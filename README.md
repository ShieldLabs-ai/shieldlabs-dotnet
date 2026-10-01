# ShieldLabs for .NET

Read identification verdicts, verify signed webhooks and turn a Risk Score into a decision in your .NET backend.

[![CI](https://github.com/ShieldLabs-ai/shieldlabs-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/ShieldLabs-ai/shieldlabs-dotnet/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/ShieldLabs-ai/shieldlabs-dotnet/blob/main/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/ShieldLabs.svg)](https://www.nuget.org/packages/ShieldLabs)

ShieldLabs identifies visitors and scores risk with device intelligence and 300+ risk signals. This
package is the server half of an integration. New to ShieldLabs? [Start free](https://app.shieldlabs.ai).

## How it fits

1. **Browser.** The ShieldLabs agent runs an identification on your page (with `@shieldlabs-ai/js` or a
   framework binding) and gives the page a `requestId`. The browser never sees a Risk Score.
2. **Your backend.** The page sends the `requestId` along with the protected action (signup, login,
   checkout). Your backend reads the verdict for it from the History API with this SDK, or receives
   it as a signed `identification.scored` webhook.
3. **Decision.** Your backend acts on the Risk Score, its band (trusted 0-29, suspicious 30-59,
   dangerous 60-100), the detection flags and the identifiers, for example how many accounts one
   device ID has used.

## Install

```sh
dotnet add package ShieldLabs
```

The package targets `net8.0` and `netstandard2.0`. Keys come from the analytics dashboard under
**Integration > API keys**; keep them on your server.

## Quick start

Both server-side halves in one ASP.NET Core app. Create it with `dotnet new web`, run
`dotnet add package ShieldLabs`, replace `Program.cs` with the code below, set
`SHIELDLABS_API_KEY` (Private API Key, `sec_...`) and `SHIELDLABS_WEBHOOK_SECRET` (endpoint signing
secret, `whsec_...`), then `dotnet run`.

```csharp
using System.Collections.Concurrent;
using ShieldLabs;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// One client per domain, shared by every request: it is safe for concurrent use.
var shieldlabs = new ShieldLabsClient(new ShieldLabsClientOptions
{
    ApiKey = Environment.GetEnvironmentVariable("SHIELDLABS_API_KEY"), // sec_your_private_key
});
var webhookSecret = Environment.GetEnvironmentVariable("SHIELDLABS_WEBHOOK_SECRET") ?? ""; // whsec_your_signing_secret

// One identification authorizes one action. This in-memory store keeps the example short; in
// production, claim request IDs atomically in a shared store (Redis SET NX, a unique key).
var usedRequestIds = new ConcurrentDictionary<string, DateTimeOffset>();

// 1. The page posts the requestId it received from the browser agent with the signup form.
app.MapPost("/signup", async (SignupForm form, CancellationToken cancellationToken) =>
{
    Identification? identification;
    try
    {
        // Scoring is asynchronous: this polls until the verdict is stored (10-second budget by default).
        identification = await shieldlabs.Identifications.GetAsync(form.RequestId ?? "", cancellationToken: cancellationToken);
    }
    catch (ValidationException)
    {
        return Results.BadRequest(new { error = "requestId must be a UUID" });
    }
    catch (ShieldLabsException)
    {
        // Network, key or rate-limit problem: the action stays unverified.
        return Results.Json(new { error = "unverified" }, statusCode: 503);
    }

    // 2. Missing, reused, stale, rate-limited, automated or dangerous: refuse.
    var firstUse = identification is not null && usedRequestIds.TryAdd(identification.RequestId, DateTimeOffset.UtcNow);
    var evaluation = Risk.Evaluate(identification, new EvaluateOptions { IsReplay = _ => !firstUse });
    if (!evaluation.Ok)
    {
        return Results.Json(new { error = "refused", reason = evaluation.Reason }, statusCode: 403);
    }

    // Create the account here.
    return Results.Ok(new { ok = true, band = evaluation.Band });
});

// 3. Verify each webhook delivery over the raw body before reading it.
app.MapPost("/webhooks/shieldlabs", async (HttpRequest request) =>
{
    using var body = new MemoryStream();
    await request.Body.CopyToAsync(body);

    WebhookEvent evt;
    try
    {
        evt = WebhookEvents.ConstructEvent(body.ToArray(), request.Headers[WebhookSignature.HeaderName], webhookSecret);
    }
    catch (SignatureVerificationException)
    {
        return Results.Unauthorized();
    }
    catch (WebhookParseException)
    {
        return Results.BadRequest();
    }

    if (evt is IdentificationScoredEvent scored)
    {
        // Handle each request ID once: future retries resend identical bytes.
        app.Logger.LogInformation("{RequestId}: risk score {RiskScore}", scored.Data.RequestId, scored.Data.RiskScore);
    }

    return Results.Ok();
});

app.Run();

record SignupForm(string? RequestId, string? Email);
```

`POST /signup` with `{"requestId":"..."}` answers `200` with the band, `403` with a `reason`
(`missing`, `replayed`, `stale`, `rate_limited`, `no_device_signals`, `blocked_flag`,
`blocked_band`), `400` for a malformed request ID and `503` when the verdict could not be read. A
fuller app with the same routes lives in [`examples/MinimalApi`](https://github.com/ShieldLabs-ai/shieldlabs-dotnet/tree/main/examples/MinimalApi).

## Guide

### Wait for the verdict

The History row of an identification appears about 1 to 3 seconds after the browser call and can be
refined for up to about 10 seconds while follow-up checks finish (network checks such as the local
IP arrive in later versions of the row). Start the identification when the user begins the action,
for example when the signup form gets focus, so the verdict is usually stored by the time the form
is submitted. `Identifications.GetAsync` polls the History API by `request_id` until the row
appears and returns the first version it sees. `Timeout` (10 seconds by default) is the total time
budget of the call:

- the first lookup is immediate, then the waits grow: 1, 2, 4, 6 and then 8 times `PollInterval`,
  each capped at 2 s, or at `PollInterval` when that is longer (with the default 250 ms: 250 ms,
  500 ms, 1 s, 1.5 s, then every 2 s; 1 s waits 1, 2, 2, 2 s; 3 s polls every 3 s). A wait that
  would pass the deadline is cut short, so the last lookup runs at the deadline;
- each lookup is exactly one HTTP attempt (the client retries do not apply) with the timeout
  min(client `Timeout`, max(time left, 1 s)), so the call returns at most about 1 second after
  `Timeout`;
- a 429, a 5xx, a timeout or a connection error does not end the wait: the next lookup follows the
  schedule. Inside the wait a 429 is always followed by at least 1 second before the next lookup:
  the longest of the scheduled wait, 1 second and `Retry-After` capped at 10 seconds
  (`Retry-After: 0` or a date in the past counts as 0, so it still waits 1 second), cut short at the
  deadline like any other wait; when the capped `Retry-After` is longer than the time left, the
  `RateLimitException` is thrown at once. Calls outside the wait follow `Retry-After` as sent (see
  [Errors and retries](#errors-and-retries));
- a 400, 401, 403 or 404 stops at once with `BadRequestException`, `AuthenticationException` or
  `NotFoundException`: a wrong key or base URL does not heal.

```csharp
var identification = await shieldlabs.Identifications.GetAsync(
    requestId,
    new GetIdentificationOptions { Timeout = TimeSpan.FromSeconds(5) },
    cancellationToken);

// One lookup, no waiting (for example in a background job), with the client retries:
var latest = await shieldlabs.Identifications.GetAsync(requestId, new GetIdentificationOptions { Wait = false });
```

When time runs out and the last lookup failed (for example with a 429 or a 5xx), that exception is
thrown instead of returning `null`. `null` means no identification was found in time: treat it as
unverified, never as clean. It also covers an identification that was never stored, for example
when the visitor's IP went over the per-IP rate limit of identifications: the browser still gets a
request ID, but no row is written for it. To read the refined row later, call `GetAsync` with
`Wait = false` again after about 10 seconds.

### Decide with the risk helpers

`Risk.Evaluate` applies the guard logic most integrations need, in this order: missing identification,
replayed request ID, older than `MaxAge` (5 minutes), rate-limit marker, no usable device signals,
blocked flags (`browser_automation`, `javascript_disabled`), blocked bands (`Dangerous`). The
defaults are a starting point: tune them to your product.

```csharp
// Claim the request ID first with an atomic insert-if-absent (Redis SET NX, a unique key), so two
// concurrent requests with the same ID cannot both pass.
bool claimed = identification is not null && await replayStore.TryClaimAsync(identification.RequestId);

var evaluation = Risk.Evaluate(identification, new EvaluateOptions
{
    MaxAge = TimeSpan.FromMinutes(2),
    BlockBands = new[] { RiskBand.Dangerous },
    BlockFlags = new[] { DetectionFlagNames.BrowserAutomation, DetectionFlagNames.AntiDetectBrowser },
    IsReplay = _ => !claimed,
});

switch (evaluation.Reason)
{
    case null: /* allow */ break;
    case EvaluationReason.BlockedBand: /* step up or review */ break;
    default: /* refuse */ break;
}
```

`IsReplay` answers synchronously, so with an asynchronous store claim the request ID first and pass
the result, as above. A lookup followed by a separate write lets two concurrent requests with the
same ID both pass. `BlockFlags` takes the names in `DetectionFlagNames`; any other name throws
`ValidationException`, so a typo never switches a block off.

Working with the fields directly:

- `Risk.Band(score)` returns `Trusted` (0-29), `Suspicious` (30-59) or `Dangerous` (60-100). A value
  above 100 (999) is a rate-limit marker, never a score: `Risk.Band` returns `RateLimited` and
  `Risk.IsRateLimited(score)` is true.
- Branch on `DetectionFlags` (19 booleans such as `Vpn`, `Proxy`, `Tor`, `AntiDetectBrowser`,
  `BrowserAutomation`) and on `RiskScore`. `Signals` explain the score for display and logging;
  weights can be negative, so never sum them yourself.
- A `DeviceId` of `00000000-0000-0000-0000-000000000000` means "no usable device signals".
- Country values are English country names, such as `Germany` or `United States`.

### Search history for account-abuse checks

`History.SearchAsync` reads one page of identifications by one identifier, newest first.
`History.IterateAsync` pages lazily through all of them and removes rows repeated by offset paging.

```csharp
// User HID values that do not name one of your accounts (null is skipped as well).
var notAnAccount = new HashSet<string>(StringComparer.Ordinal) { "anonymous", "fail", "-1", "unknown" };

// How many accounts has this device been used with? The all-zero device ID groups identifications
// without device signals, so skip it.
if (identification.DeviceId != "00000000-0000-0000-0000-000000000000")
{
    var accounts = new HashSet<string>(StringComparer.Ordinal);
    await foreach (var row in shieldlabs.History.IterateAsync(
        LookupType.DeviceId, identification.DeviceId, new HistoryIterateOptions { MaxItems = 500 }))
    {
        if (row.UserHid is { } hid && !notAnAccount.Contains(hid))
        {
            accounts.Add(hid);
        }
    }

    if (accounts.Count >= 3)
    {
        // Route the signup to review.
    }
}

// One page by User HID, IP or visitor:
HistoryPage page = await shieldlabs.History.SearchAsync(LookupType.UserHid, userHid, new HistorySearchOptions { Limit = 50 });
Console.WriteLine($"{page.Total} identifications for this account");
```

Lookup types: `Ip` (dotted IPv4), `UserHid`, `VisitorId`, `RequestId`, `DeviceId`, `SessionId`,
`CookieId` (UUIDs, any version, sent lowercase). Arguments are validated on the client before
anything is sent, so a mistyped identifier fails fast with `ValidationException` instead of
returning unrelated rows. `Limit` is 1 to 100 (default 20) and `Offset` is 0 or more. Each request
counts toward the History API rate limit.

A User HID is sent as one URL path segment in the canonical form the History API matches, so
values with `@`, `+`, `=` or spaces match exactly. A value that contains `/` (standard base64 text
can), and the values `.` and `..`, cannot be searched and throw `ValidationException`. User HIDs
from `UserHid.FromUserId` are 64 hex characters and always work.

### Receive webhooks

ShieldLabs signs every delivery with the endpoint secret: `X-Shield-Signature: sha256=<hex HMAC-SHA256>`
over the raw body. `WebhookEvents.ConstructEvent` verifies the signature, then returns one of:

| Type | When |
|---|---|
| `IdentificationScoredEvent` | `identification.scored`: `Data` is an `Identification`, the same model the History API returns |
| `WebhookPingEvent` | `webhook.ping`, sent by **Verify** in the analytics dashboard |
| `UnknownWebhookEvent` | any other event type: acknowledge it with a 2xx and ignore it |

Rules for a reliable receiver:

- Verify the raw bytes as received. Never parse and re-serialize the JSON before verifying.
- Today each identification is delivered once per enabled endpoint: one attempt with a 1-second
  timeout and no retries. Respond with a 2xx quickly and do slow work in the background.
- Future retries will resend identical bytes, so make the handler idempotent on `Data.RequestId`
  (`data.request_id` in the body) now: store it and skip deliveries you have already handled. The
  signature header is the only ShieldLabs header, so the idempotency key comes from the body.
- For guaranteed reads, use the History API: a missed delivery is not sent again, and a History row
  can be refined after its webhook was sent.
- To rotate the signing secret without downtime, pass both secrets:
  `WebhookEvents.ConstructEvent(body, header, newSecret, oldSecret)`.

`WebhookSignature.Verify(body, header, secret)` returns a boolean when you only need the check.
Both accept the body as `byte[]` or `string`.

### Read the domain profile

The Management API returns the profile of one registered domain, including the remaining included
identifications of the account (negative when the account is over its included volume).

```csharp
var management = new ManagementClient(new ManagementClientOptions
{
    SecretKey = Environment.GetEnvironmentVariable("SHIELDLABS_SECRET_KEY"),
    Domain = Environment.GetEnvironmentVariable("SHIELDLABS_DOMAIN"), // example.com
});

DomainProfile profile = await management.GetProfileAsync();
Console.WriteLine($"{profile.Domain}: {profile.RemainingIdentifications} identifications left");
```

The domain is normalized before sending (lowercase, no scheme, path or leading `www.`), because the
server matches the registered domain exactly. Call this API sparingly and cache the result (see
[Rate limits](#rate-limits)).

### Create a User HID

Pass a stable, irreversible identifier to the browser agent instead of an email address or a raw
account ID:

```csharp
// userHidSecret: a random secret you generate once and keep on your server.
string userHid = UserHid.FromUserId(user.Id, userHidSecret);
```

It is HMAC-SHA256 of the user ID keyed with your secret, as 64 lowercase hex characters. Keep the
secret stable: changing it changes every User HID. Search the history of that account later with
`LookupType.UserHid`.

### HTTP client and dependency injection

Both clients are safe for concurrent use: create one per domain and reuse it. Without an
`HttpClient` argument they share one process-wide `HttpClient`. To use `IHttpClientFactory`
(proxies, handlers, logging), register the client as a typed client:

```csharp
builder.Services.AddHttpClient<ShieldLabsClient, ShieldLabsClient>((http, services) => new ShieldLabsClient(
    new ShieldLabsClientOptions { ApiKey = builder.Configuration["SHIELDLABS_API_KEY"] },
    http));
```

The SDK never disposes an `HttpClient` you pass in. For development and tests, point the clients
at another origin with `BaseUrl` (for example from `SHIELDLABS_API_BASE_URL` or
`SHIELDLABS_MANAGEMENT_BASE_URL`). Base URLs must use https, because every request carries a key;
plain http is accepted only for `localhost`, `127.0.0.1` and `::1`. The History API origin is
`https://account.shieldlabs.ai`; a trailing `/api` in `BaseUrl` is removed, because request paths
start with `/api/v1/`. Keys and the Management domain must be ASCII (use the punycode form of an
internationalized domain).

### Rate limits

| API | Limit | What the SDK does |
|---|---|---|
| History API | about 15 requests per second per domain, shared by all callers of that domain | retries a 429 after `Retry-After` as sent (up to 10 s; `Retry-After: 0` or a date in the past retries at once), or after at least 1 s when there is no `Retry-After`; while `GetAsync` waits, a 429 means "wait longer": at least 1 s, longer when the schedule or `Retry-After` asks (up to 10 s) |
| Management API | about 15 requests per minute per caller IP; the request over the limit blocks that IP for 10 minutes | never retries a 429: the IP stays blocked for 10 minutes, so a retry only fails again |

## Reference

| Member | Returns | Notes |
|---|---|---|
| `new ShieldLabsClient(ShieldLabsClientOptions, HttpClient?)` | client | `ApiKey` required; `BaseUrl` (default `https://account.shieldlabs.ai`), `Timeout` (10 s per attempt), `MaxRetries` (2) |
| `client.Identifications.GetAsync(requestId, GetIdentificationOptions?, CancellationToken)` | `Task<Identification?>` | `Wait` (true), `Timeout` (10 s, total budget; the last lookup runs at the deadline), `PollInterval` (250 ms; the waits are 1, 2, 4, 6, then 8 times it, each at most 2 s, or at most `PollInterval` when that is longer) |
| `client.History.SearchAsync(LookupType, value, HistorySearchOptions?, CancellationToken)` | `Task<HistoryPage>` | `Limit` 1-100 (20), `Offset` (0); `HistoryPage.Data`, `HistoryPage.Total` |
| `client.History.IterateAsync(LookupType, value, HistoryIterateOptions?, CancellationToken)` | `IAsyncEnumerable<Identification>` | `PageSize` 1-100 (100), `MaxItems`; deduplicates on request ID |
| `new ManagementClient(ManagementClientOptions, HttpClient?)` | client | `SecretKey`, `Domain` required; `BaseUrl` (default `https://api.shieldlabs.ai`) |
| `management.GetProfileAsync(CancellationToken)` | `Task<DomainProfile>` | `Domain`, `RemainingIdentifications`, `PublicKeyMasked`, `SecretKeyMasked`, `CreatedAt`, `Raw` |
| `WebhookSignature.Verify(payload, header, params secrets)` | `bool` | payload as `byte[]` or `string`; never throws |
| `WebhookEvents.ConstructEvent(payload, header, params secrets)` | `WebhookEvent` | throws `SignatureVerificationException` or `WebhookParseException` |
| `Risk.Band(int)` | `RiskBand` | `Trusted`, `Suspicious`, `Dangerous`, `RateLimited` |
| `Risk.IsRateLimited(int)` | `bool` | true above 100 |
| `Risk.Evaluate(Identification?, EvaluateOptions?)` | `Evaluation` | `Ok`, `Reason`, `Band`, `Flag`; unknown `BlockFlags` names throw `ValidationException` |
| `UserHid.FromUserId(userId, secret)` | `string` | 64 lowercase hex characters |

`Identification` properties (JSON names follow the webhook contract): `RequestId`, `VisitorId`,
`DeviceId`, `SessionId`, `CookieId`, `UserHid`, `Domain`, `PublicIp` and `LocalIp` (`Ip`, `Country`),
`ConnectionType` (see `ConnectionTypes`), `Os`, `Browser`, `DeviceType`, `TrafficSource` (`Channel`,
`ReferrerDomain`, `LandingUrl`, `ClickIdType`, `UtmSource`, `UtmMedium`, `UtmCampaign`,
`UtmContent`, `UtmTerm`), `RiskScore`, `Signals` (`Name`, `Weight`, `Description`; known names in
`SignalNames`), `DetectionFlags` (19 booleans; wire names in `DetectionFlagNames`), `ObservedAt`
(UTC, millisecond precision, serialized as `2026-09-30T12:34:56.789Z`), `Source` (`History` or
`Webhook`) and `Raw` (the original JSON object).

## Errors and retries

Every exception derives from `ShieldLabsException`:

| Exception | Cause | Retried |
|---|---|---|
| `ValidationException` | invalid argument or option; nothing was sent | no |
| `BadRequestException` | HTTP 400 | no |
| `AuthenticationException` | HTTP 401 or 403: wrong key, or wrong domain and Secret Key pair | no |
| `QuotaExceededException` | HTTP 402: no identifications left in the included volume | no |
| `NotFoundException` | HTTP 404, usually a wrong base URL | no |
| `RateLimitException` | HTTP 429; `RetryAfter` when the server sent it (zero for `Retry-After: 0` or a date in the past) | History API only |
| `ServerException` | HTTP 5xx | yes |
| `ApiException` | any other HTTP status; base class of the HTTP errors above, with `StatusCode`, `Body`, `Error` and `Headers` | no |
| `ApiConnectionException` | the server could not be reached | yes |
| `ApiTimeoutException` | an attempt exceeded `Timeout` | yes |
| `SignatureVerificationException` | the webhook signature is missing, malformed or does not match | n/a |
| `WebhookParseException` | a verified webhook body is not a valid event | n/a |

Retries apply to these GET requests only, with exponential backoff and jitter (0.5 s base, doubling,
at most 8 s). A `Retry-After` header is followed as sent, capped at 10 s: `Retry-After: 0` or a date
in the past retries at once. A 429 without it is retried after the backoff, but never sooner than
1 s, because the History API counts requests per second. `MaxRetries` (default 2) sets how many times
a request is retried. While `Identifications.GetAsync` waits for a verdict,
each lookup is one attempt and the polling schedule does the retrying; there a 429 is followed by at
least 1 s whatever `Retry-After` says (see [Wait for the verdict](#wait-for-the-verdict)).
Cancelling the `CancellationToken` stops at once and throws `OperationCanceledException`. The SDK
never logs keys or bodies and sends no telemetry.

## Compatibility

- .NET 8 and later use the `net8.0` build; CI runs the tests on .NET 8, 9 and 10.
- Runtimes that implement .NET Standard 2.0 (for example .NET Framework 4.7.2 and later) use the
  `netstandard2.0` build, which depends on `System.Text.Json` and `Microsoft.Bcl.AsyncInterfaces`.
  CI runs the same tests against that build.
- `await foreach` over `IterateAsync` needs C# 8 or later.
- The SDK follows Semantic Versioning. Webhook events use `schema_version` `2026-06-01`; other
  versions are accepted.

Documentation: [docs.shieldlabs.ai](https://docs.shieldlabs.ai). Support: [contact@shieldlabs.ai](mailto:contact@shieldlabs.ai).

## Development

Refresh the generated client when the API description changes. This does not replace the supported library in this repository.

```bash
./sync.sh      # download the current OpenAPI description into resources/
./generate.sh  # rebuild generated/ from that file
```


Requirements: the .NET 8 SDK (or Docker).

```sh
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
dotnet pack src/ShieldLabs -c Release -o artifacts
```

The same with Docker:

```sh
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  sh -c "dotnet build -c Release -warnaserror && dotnet test -c Release --no-build"
```

`tests/ShieldLabs.Tests/data` holds the shared test fixtures that every ShieldLabs server SDK
passes. See [CONTRIBUTING.md](https://github.com/ShieldLabs-ai/shieldlabs-dotnet/blob/main/CONTRIBUTING.md).

## License

[MIT](https://github.com/ShieldLabs-ai/shieldlabs-dotnet/blob/main/LICENSE). Copyright (c) 2026 ShieldLabs Inc.
