# Changelog

All notable changes to this package are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `sync.sh` downloads the OpenAPI description and `generate.sh` rebuilds `generated/` from it. The supported client is unchanged.

## [1.0.0] - 2026-09-30

First release of the ShieldLabs server SDK for .NET (NuGet package `ShieldLabs`, targets `net8.0`
and `netstandard2.0`).

### Added

- `ShieldLabsClient` for the History API (`https://account.shieldlabs.ai`): `History.SearchAsync`
  reads one page by IP, User HID, visitor, request, device, session or cookie ID;
  `History.IterateAsync` pages lazily and deduplicates on request ID.
- `Identifications.GetAsync` reads one identification by request ID and waits for the verdict on a
  total time budget (`Timeout`, 10 s by default). The first lookup is immediate, then the waits are
  1, 2, 4, 6 and then 8 times `PollInterval`, each capped at 2 s, or at `PollInterval` when that is
  longer (250 ms, 500 ms, 1 s, 1.5 s, then 2 s with the default; 3 s polls every 3 s), and the last
  lookup runs at the deadline. Each lookup is one HTTP attempt with the timeout min(client timeout,
  max(time left, 1 s)). A 429, a 5xx, a timeout or a connection error keeps it polling, and the error
  of the last lookup is thrown when time is up. After a 429 the next wait is at least 1 s: the
  longest of the scheduled wait, 1 s and `Retry-After` up to 10 s (`Retry-After: 0` or a past date
  counts as 0); a capped `Retry-After` longer than the time left throws at once. A 400, 401, 403 or
  404 stops the wait at once. With `Wait = false` it makes one lookup with the client retries.
- Client-side validation of lookup types, UUIDs, IPv4 addresses, User HIDs, limits and offsets
  before any request is sent. User HIDs are percent-encoded in the canonical form the History API
  matches; values that contain `/`, and the values `.` and `..`, throw `ValidationException`.
- Base URLs must use https (plain http only for `localhost`, `127.0.0.1` and `::1`); keys and the
  Management domain must be ASCII.
- `ManagementClient` for the Management API (`https://api.shieldlabs.ai`) with `GetProfileAsync`,
  domain normalization for `X-Shield-Domain`, and no retries on 429.
- `WebhookSignature.Verify` and `WebhookEvents.ConstructEvent` with one or several signing secrets
  (rotation without downtime), returning `IdentificationScoredEvent`, `WebhookPingEvent` or
  `UnknownWebhookEvent`.
- One `Identification` model for History rows and webhook data, with 19 detection flags, risk
  signals, traffic source, UTC `ObservedAt` (serialized as `2026-09-30T12:34:56.789Z`) and the
  original JSON in `Raw`.
- `Risk.Band`, `Risk.IsRateLimited` (999 marker) and `Risk.Evaluate`, a guard policy for missing,
  replayed, stale, rate-limited, no-device-signal, flagged and dangerous identifications; unknown
  `BlockFlags` names throw `ValidationException`.
- `UserHid.FromUserId` to create a User HID with HMAC-SHA256 on the server.
- Exception hierarchy under `ShieldLabsException`, including `QuotaExceededException` for HTTP 402,
  retries with exponential backoff and jitter, per-attempt timeouts, `Retry-After` followed as sent
  (up to 10 s; `Retry-After: 0` or a past date retries at once), at least 1 s before retrying a 429
  without `Retry-After`, and a `User-Agent` of `shieldlabs-dotnet/<version>`.
- ASP.NET Core minimal API example with a guarded signup route and a webhook receiver.

[1.0.0]: https://github.com/ShieldLabs-ai/shieldlabs-dotnet/releases/tag/v1.0.0
