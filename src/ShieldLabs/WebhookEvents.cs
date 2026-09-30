using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using ShieldLabs.Internal;

namespace ShieldLabs;

/// <summary>Known webhook <c>event_type</c> values.</summary>
public static class WebhookEventTypes
{
    /// <summary>A verdict for one identification.</summary>
    public const string IdentificationScored = "identification.scored";

    /// <summary>The "Verify" ping sent from the analytics dashboard.</summary>
    public const string Ping = "webhook.ping";
}

/// <summary>
/// Verifies and parses webhook deliveries into typed events.
/// </summary>
/// <remarks>
/// Today ShieldLabs sends one delivery per identification and endpoint: one attempt with a
/// 1-second timeout and no retries, so respond with a 2xx quickly. Future retries will resend
/// identical bytes: make the handler idempotent on <see cref="Identification.RequestId"/>
/// (<c>data.request_id</c>). Use the History API for guaranteed reads and for the latest state of an
/// identification.
/// </remarks>
public static class WebhookEvents
{
    /// <summary>The <c>schema_version</c> this SDK was built for. Other values are accepted.</summary>
    public const string SchemaVersion = "2026-06-01";

    private static int _schemaWarningIssued;

    /// <summary>Verifies the signature of a delivery, then parses it into a typed event.</summary>
    /// <param name="payload">The raw request body, decoded as UTF-8.</param>
    /// <param name="signatureHeader">The <c>X-Shield-Signature</c> header value.</param>
    /// <param name="secrets">One or more endpoint signing secrets (<c>whsec_…</c>).</param>
    /// <returns>
    /// An <see cref="IdentificationScoredEvent"/>, a <see cref="WebhookPingEvent"/>, or an
    /// <see cref="UnknownWebhookEvent"/> for any other event type.
    /// </returns>
    /// <exception cref="SignatureVerificationException">The signature is missing, malformed or does not match.</exception>
    /// <exception cref="WebhookParseException">The verified body is not a valid event.</exception>
    public static WebhookEvent ConstructEvent(string payload, string? signatureHeader, params string[] secrets)
        => ConstructEvent(payload is null ? null! : Encoding.UTF8.GetBytes(payload), signatureHeader, secrets);

    /// <summary>Verifies the signature of a delivery, then parses it into a typed event.</summary>
    /// <param name="payload">The raw request body bytes.</param>
    /// <param name="signatureHeader">The <c>X-Shield-Signature</c> header value.</param>
    /// <param name="secrets">One or more endpoint signing secrets (<c>whsec_…</c>).</param>
    /// <returns>
    /// An <see cref="IdentificationScoredEvent"/>, a <see cref="WebhookPingEvent"/>, or an
    /// <see cref="UnknownWebhookEvent"/> for any other event type.
    /// </returns>
    /// <exception cref="SignatureVerificationException">The signature is missing, malformed or does not match.</exception>
    /// <exception cref="WebhookParseException">The verified body is not a valid event.</exception>
    public static WebhookEvent ConstructEvent(byte[] payload, string? signatureHeader, params string[] secrets)
    {
        switch (WebhookSignature.Check(payload, signatureHeader, secrets))
        {
            case SignatureCheck.Valid:
                return Parse(payload);
            case SignatureCheck.MissingPayload:
                throw new SignatureVerificationException("No webhook payload was given.");
            case SignatureCheck.MissingHeader:
                throw new SignatureVerificationException($"The {WebhookSignature.HeaderName} header is missing or empty.");
            case SignatureCheck.MalformedHeader:
                throw new SignatureVerificationException($"The {WebhookSignature.HeaderName} header is not in the form sha256=<64 hex characters>.");
            case SignatureCheck.NoSecret:
                throw new SignatureVerificationException("No webhook signing secret was given: pass the whsec_ secret of the endpoint.");
            default:
                throw new SignatureVerificationException("The webhook signature does not match the payload for any of the given secrets.");
        }
    }

    internal static WebhookEvent Parse(byte[] payload)
    {
        JsonElement root;
        try
        {
            root = JsonUtil.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new WebhookParseException("The webhook body is not valid UTF-8 JSON.", ex);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new WebhookParseException("The webhook body is not a JSON object.");
        }

        try
        {
            return ParseEnvelope(root);
        }
        catch (Exception ex) when (ex is not ShieldLabsException && ex is not OutOfMemoryException)
        {
            // Defensive: the documented contract is SignatureVerificationException or WebhookParseException.
            throw new WebhookParseException("The webhook body could not be read as an event: " + ex.Message, ex);
        }
    }

    private static WebhookEvent ParseEnvelope(JsonElement root)
    {
        var eventType = JsonUtil.StringOrNull(JsonUtil.Get(root, "event_type")) ?? string.Empty;
        var schemaVersion = JsonUtil.StringOrNull(JsonUtil.Get(root, "schema_version")) ?? string.Empty;
        var createdAt = Timestamps.ParseRfc3339(JsonUtil.StringOrNull(JsonUtil.Get(root, "created_at"))) ?? DateTimeOffset.MinValue;

        if (schemaVersion != SchemaVersion && Interlocked.Exchange(ref _schemaWarningIssued, 1) == 0)
        {
            Trace.TraceWarning($"ShieldLabs: received webhook schema_version '{schemaVersion}'; this SDK was built for {SchemaVersion}. Parsing continues.");
        }

        switch (eventType)
        {
            case WebhookEventTypes.IdentificationScored:
                if (JsonUtil.Get(root, "data") is not JsonElement data || data.ValueKind != JsonValueKind.Object)
                {
                    throw new WebhookParseException("The identification.scored event has no data object.");
                }

                return new IdentificationScoredEvent
                {
                    EventType = eventType,
                    SchemaVersion = schemaVersion,
                    CreatedAt = createdAt,
                    Raw = root,
                    Data = Normalizer.FromWebhookData(data),
                };
            case WebhookEventTypes.Ping:
                return new WebhookPingEvent { EventType = eventType, SchemaVersion = schemaVersion, CreatedAt = createdAt, Raw = root };
            default:
                return new UnknownWebhookEvent { EventType = eventType, SchemaVersion = schemaVersion, CreatedAt = createdAt, Raw = root };
        }
    }
}

/// <summary>
/// A verified webhook event. Match on the concrete type: <see cref="IdentificationScoredEvent"/>,
/// <see cref="WebhookPingEvent"/> or <see cref="UnknownWebhookEvent"/>.
/// </summary>
public abstract class WebhookEvent
{
    private protected WebhookEvent()
    {
    }

    /// <summary>The <c>event_type</c> of the envelope (empty when absent).</summary>
    [JsonPropertyName("event_type")]
    public string EventType { get; init; } = string.Empty;

    /// <summary>The <c>schema_version</c> of the envelope, for example <c>2026-06-01</c>.</summary>
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; init; } = string.Empty;

    /// <summary>
    /// When the event was created (UTC, millisecond precision), or <see cref="DateTimeOffset.MinValue"/>
    /// when the envelope had no parsable timestamp. Serialized as <c>2026-09-30T12:34:56.789Z</c>.
    /// </summary>
    [JsonPropertyName("created_at")]
    [JsonConverter(typeof(UtcTimestampConverter))]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The whole envelope as received.</summary>
    [JsonPropertyName("raw")]
    public JsonElement Raw { get; init; } = JsonUtil.EmptyObject;
}

/// <summary>An <c>identification.scored</c> event: the verdict for one identification.</summary>
public sealed class IdentificationScoredEvent : WebhookEvent
{
    /// <summary>The identification, normalized to the same model the History API returns.</summary>
    [JsonPropertyName("data")]
    public Identification Data { get; init; } = new Identification();
}

/// <summary>A <c>webhook.ping</c> event sent by "Verify" in the analytics dashboard. It carries no data.</summary>
public sealed class WebhookPingEvent : WebhookEvent
{
}

/// <summary>An event with an <c>event_type</c> this SDK does not know. Acknowledge it and ignore it.</summary>
public sealed class UnknownWebhookEvent : WebhookEvent
{
}
