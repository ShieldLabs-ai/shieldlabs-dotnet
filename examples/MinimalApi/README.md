# ASP.NET Core minimal API example

Both server-side halves of a ShieldLabs integration in one small app:

- `POST /signup` takes the `requestId` your page received from the browser agent, waits for the
  verdict with `Identifications.GetAsync` and applies `Risk.Evaluate`. It refuses the signup when the
  identification is missing, reused, older than 5 minutes, rate-limited (999 marker), without usable
  device signals, automated (`browser_automation`, `javascript_disabled`) or in the dangerous band
  (60-100).
- `POST /webhooks/shieldlabs` verifies `X-Shield-Signature` over the raw body with
  `WebhookEvents.ConstructEvent`, answers 401 on a bad signature and 400 on a body that is not an
  event, handles each request ID once and logs `identification.scored` and `webhook.ping` events.
  Today each identification is delivered once per endpoint (1-second timeout, no retries); future
  retries resend identical bytes, which is why the handler is idempotent on the request ID.

## Run it

```sh
export SHIELDLABS_API_KEY=sec_your_private_key
export SHIELDLABS_WEBHOOK_SECRET=whsec_your_signing_secret
dotnet run --project examples/MinimalApi
```

The app listens on `http://localhost:5000` by default. During a signing secret rotation, set
`SHIELDLABS_WEBHOOK_SECRET` to both secrets separated by a comma. `SHIELDLABS_API_BASE_URL`
overrides the History API origin for development and tests (https, or plain http on `localhost`).

## Try it

```sh
# The request ID comes from identify() in the browser.
curl -s http://localhost:5000/signup \
  -H 'Content-Type: application/json' \
  -d '{"requestId":"3f2b8c1e-9d4a-4e6b-8a7c-2d1e0f9b6a53","email":"user@example.com"}'
```

Responses: `200 {"ok":true,"band":"trusted"}` when the signup may proceed, `403` with a `reason`
(`missing`, `replayed`, `stale`, `rate_limited`, `no_device_signals`, `blocked_flag`,
`blocked_band`) when it is refused, `400` for a missing or malformed request ID and `503` when the
verdict could not be read. The History row appears about 1 to 3 seconds after the browser call, so
start the identification when the user begins filling in the form.

Point a webhook endpoint in the analytics dashboard (**Integration > Webhooks**) at
`https://<your host>/webhooks/shieldlabs` and press **Verify** to receive a `webhook.ping`.

The replay store and the processed-delivery store are in memory to keep the example short. Use a
shared store (for example Redis with a TTL) when you run more than one instance.
