// ShieldLabs example: an ASP.NET Core minimal API with both server-side halves of an integration.
//
//   POST /signup                reads the requestId sent by the browser, waits for the verdict and
//                               applies a policy before creating the account.
//   POST /webhooks/shieldlabs   verifies and logs identification.scored and webhook.ping deliveries.
//
// Configuration (environment variables):
//   SHIELDLABS_API_KEY          Private API Key of the domain (sec_...). Required for /signup.
//   SHIELDLABS_WEBHOOK_SECRET   Signing secret of the webhook endpoint (whsec_...). Required for the
//                               webhook route. During a rotation, pass both secrets separated by a comma.
//   SHIELDLABS_API_BASE_URL     Optional History API origin override (development and tests).

using System.Collections.Concurrent;
using ShieldLabs;

var builder = WebApplication.CreateBuilder(args);

var apiKey = Environment.GetEnvironmentVariable("SHIELDLABS_API_KEY");
var webhookSecrets = (Environment.GetEnvironmentVariable("SHIELDLABS_WEBHOOK_SECRET") ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

// One client per domain, shared by every request: it is safe for concurrent use.
builder.Services.AddSingleton(_ => new ShieldLabsClient(new ShieldLabsClientOptions
{
    ApiKey = apiKey,
    BaseUrl = Environment.GetEnvironmentVariable("SHIELDLABS_API_BASE_URL"),
}));
builder.Services.AddSingleton<UsedRequestIds>();
builder.Services.AddSingleton<ProcessedDeliveries>();

var app = builder.Build();

app.MapPost("/signup", async (SignupRequest body, ShieldLabsClient shieldlabs, UsedRequestIds usedRequestIds, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(body.RequestId))
    {
        return Results.BadRequest(new { error = "requestId is required" });
    }

    Identification? identification;
    try
    {
        // Scoring is asynchronous: the History row appears about 1 to 3 seconds after the browser
        // call. This polls until it does, within a total budget of 10 seconds by default.
        identification = await shieldlabs.Identifications.GetAsync(body.RequestId, cancellationToken: cancellationToken);
    }
    catch (ValidationException)
    {
        return Results.BadRequest(new { error = "requestId must be a UUID" });
    }
    catch (ShieldLabsException error)
    {
        // Could not read the verdict (network, key or rate limit): the action stays unverified.
        logger.LogWarning(error, "ShieldLabs lookup failed");
        return Results.Json(new { ok = false, reason = "unverified" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // One identification authorizes one action: claim the request ID first (an atomic
    // insert-if-absent), then pass the result to the policy.
    var firstUse = identification is not null && usedRequestIds.TryClaim(identification.RequestId);

    // Policy: missing, replayed, stale (5 minutes), rate-limited (999 marker), no device signals,
    // browser automation or JavaScript disabled, or the dangerous band (60-100) refuse the signup.
    var evaluation = Risk.Evaluate(identification, new EvaluateOptions { IsReplay = _ => !firstUse });
    if (!evaluation.Ok)
    {
        logger.LogInformation("Signup refused: {Reason} (band {Band}, flag {Flag})", evaluation.Reason, evaluation.Band, evaluation.Flag);
        return Results.Json(new { ok = false, reason = evaluation.Reason, band = evaluation.Band, flag = evaluation.Flag }, statusCode: StatusCodes.Status403Forbidden);
    }

    // Create the account here. Store identification!.DeviceId with it for later account-abuse checks.
    return Results.Ok(new { ok = true, band = evaluation.Band });
});

app.MapPost("/webhooks/shieldlabs", async (HttpRequest request, ProcessedDeliveries processed, ILogger<Program> logger) =>
{
    // Verify the raw bytes exactly as received; never re-serialize parsed JSON before verifying.
    byte[] payload;
    using (var buffer = new MemoryStream())
    {
        await request.Body.CopyToAsync(buffer);
        payload = buffer.ToArray();
    }

    WebhookEvent evt;
    try
    {
        evt = WebhookEvents.ConstructEvent(payload, request.Headers[WebhookSignature.HeaderName], webhookSecrets);
    }
    catch (SignatureVerificationException)
    {
        return Results.Unauthorized();
    }
    catch (WebhookParseException)
    {
        return Results.BadRequest();
    }

    switch (evt)
    {
        case IdentificationScoredEvent scored:
            // Today each identification is delivered once (1-second timeout, no retries). Future
            // retries resend identical bytes, so handle each request ID once. Respond fast and do
            // slow work in the background; read the History API for guaranteed reads.
            if (processed.TryMark(scored.Data.RequestId))
            {
                logger.LogInformation(
                    "identification.scored {RequestId}: risk score {RiskScore} ({Band})",
                    scored.Data.RequestId,
                    scored.Data.RiskScore,
                    Risk.Band(scored.Data.RiskScore));
            }

            break;
        case WebhookPingEvent:
            logger.LogInformation("webhook.ping received");
            break;
        default:
            logger.LogInformation("Ignoring webhook event type {EventType}", evt.EventType);
            break;
    }

    return Results.Ok();
});

app.Run();

/// <summary>The body the signup form posts: the request ID from the browser agent plus the form fields.</summary>
internal sealed record SignupRequest(string? RequestId, string? Email);

/// <summary>
/// Remembers request IDs that already authorized an action (one identification per action).
/// In-memory for the example: use a shared store (for example Redis SET NX with a TTL) in production.
/// </summary>
internal sealed class UsedRequestIds
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);

    /// <summary>Records the request ID and returns true the first time only (atomic).</summary>
    public bool TryClaim(string requestId) => _used.TryAdd(requestId, DateTimeOffset.UtcNow);
}

/// <summary>Request IDs whose webhook was already handled.</summary>
internal sealed class ProcessedDeliveries
{
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    public bool TryMark(string requestId) => _seen.TryAdd(requestId, 0);
}
