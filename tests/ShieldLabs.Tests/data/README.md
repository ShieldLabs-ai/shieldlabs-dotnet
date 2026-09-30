# Shared test fixtures

Shared test fixtures that every ShieldLabs server SDK passes: the same files in every SDK, so all
SDKs produce the same results. Do not edit them by hand: they are updated together with the public
API.

| File | What the tests check |
|---|---|
| `history-page.json`, `history-empty.json` | History API response bodies: five realistic rows, and an empty page |
| `normalization-cases.json` | History rows and webhook `data` objects with the exact `Identification` each must produce |
| `signal-slug-cases.json` | Score detail descriptions and the signal name each maps to |
| `risk-band-cases.json` | Risk Score to risk band, including the 999 rate-limit marker |
| `webhook-identification-scored.json`, `.raw.txt` | A scored event, pretty printed and as the exact bytes sent |
| `webhook-rate-limited.json` | A scored event carrying the 999 rate-limit marker |
| `webhook-ping.json`, `.raw.txt` | The Verify ping, pretty printed and as the exact bytes sent |
| `webhook-test-delivery.json` | The analytics dashboard Test delivery: 17 of the 19 flags, second-precision timestamps |
| `webhook-signature-vectors.json` | 21 signature vectors with one secret or a list of secrets |
| `management-profile.json`, `management-profile-expected.json` | A Management API profile body and the `DomainProfile` it maps to |
| `error-responses.json` | Error bodies per API and status, the expected exception and whether it is retried |
