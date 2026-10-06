using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShieldLabs;

/// <summary>Stored attribution. Provider verification does not verify a claimed agent or mode.</summary>
[JsonConverter(typeof(ClientIdentityConverter))]
public sealed class ClientIdentity
{
    private ClientIdentity(JsonElement raw) { Raw = raw.Clone(); }
    /// <summary>Original object, including unknown future fields.</summary>
    public JsonElement Raw { get; }
    internal static ClientIdentity? FromRaw(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("client_identity", out var value)) return null;
        return FromValue(value);
    }
    internal static ClientIdentity? FromValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "schema_version", "availability", "registry_revision", "observed_at" })
            if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return null;
        if (!value.TryGetProperty("classification_revision", out var revision) || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var number) || number < 1) return null;
        foreach (var name in new[] { "claims", "verified", "assessments", "evidence" })
        {
            if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in field.EnumerateArray()) if (item.ValueKind != JsonValueKind.Object) return null;
        }
        return new ClientIdentity(value);
    }
    internal static string? Text(JsonElement raw, string name) => raw.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static IReadOnlyList<string> Strings(JsonElement raw, string name)
    {
        var items = new List<string>();
        if (raw.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array)
            foreach (var value in values.EnumerateArray()) if (value.ValueKind == JsonValueKind.String) items.Add(value.GetString()!);
        return items.AsReadOnly();
    }
    private IReadOnlyList<T> Group<T>(string name, Func<JsonElement,T> create)
    {
        var items = new List<T>();
        foreach (var value in Raw.GetProperty(name).EnumerateArray()) items.Add(create(value));
        return items.AsReadOnly();
    }
    /// <summary>Stored schema_version value; strings remain open for future values.</summary>
    public string? SchemaVersion => Text(Raw, "schema_version");
    /// <summary>Stored classifier_version value; strings remain open for future values.</summary>
    public string? ClassifierVersion => Text(Raw, "classifier_version");
    /// <summary>Stored registry_revision value; strings remain open for future values.</summary>
    public string? RegistryRevision => Text(Raw, "registry_revision");
    /// <summary>Stored availability value; strings remain open for future values.</summary>
    public string? Availability => Text(Raw, "availability");
    /// <summary>Stored observed_at value; strings remain open for future values.</summary>
    public string? ObservedAt => Text(Raw, "observed_at");
    /// <summary>Stored decided_at value; strings remain open for future values.</summary>
    public string? DecidedAt => Text(Raw, "decided_at");
    /// <summary>Stored decision revision.</summary>
    public long ClassificationRevision => Raw.GetProperty("classification_revision").GetInt64();
    /// <summary>Stored claims entries; no local reclassification.</summary>
    public IReadOnlyList<ClientIdentityClaim> Claims => Group("claims", value => new ClientIdentityClaim(value));
    /// <summary>Stored verified entries; no local reclassification.</summary>
    public IReadOnlyList<ClientIdentityAttribution> Verified => Group("verified", value => new ClientIdentityAttribution(value));
    /// <summary>Stored assessments entries; no local reclassification.</summary>
    public IReadOnlyList<ClientIdentityAssessment> Assessments => Group("assessments", value => new ClientIdentityAssessment(value));
    /// <summary>Stored evidence entries; no local reclassification.</summary>
    public IReadOnlyList<ClientIdentityEvidence> Evidence => Group("evidence", value => new ClientIdentityEvidence(value));
}

/// <summary>One stored attribution record with open string values.</summary>
public sealed class ClientIdentityClaim
{
    internal ClientIdentityClaim(JsonElement raw) { Raw = raw.Clone(); }
    /// <summary>Original record, including unknown fields.</summary>
    public JsonElement Raw { get; }
    /// <summary>Stored profile_id value.</summary>
    public string? ProfileId => ClientIdentity.Text(Raw, "profile_id");
    /// <summary>Stored provider_id value.</summary>
    public string? ProviderId => ClientIdentity.Text(Raw, "provider_id");
    /// <summary>Stored provider_name value.</summary>
    public string? ProviderName => ClientIdentity.Text(Raw, "provider_name");
    /// <summary>Stored agent_name value.</summary>
    public string? AgentName => ClientIdentity.Text(Raw, "agent_name");
    /// <summary>Stored client_kind value.</summary>
    public string? ClientKind => ClientIdentity.Text(Raw, "client_kind");
    /// <summary>Stored purpose value.</summary>
    public string? Purpose => ClientIdentity.Text(Raw, "purpose");
    /// <summary>Stored source value.</summary>
    public string? Source => ClientIdentity.Text(Raw, "source");
}

/// <summary>One stored attribution record with open string values.</summary>
public sealed class ClientIdentityAttribution
{
    internal ClientIdentityAttribution(JsonElement raw) { Raw = raw.Clone(); }
    /// <summary>Original record, including unknown fields.</summary>
    public JsonElement Raw { get; }
    /// <summary>Stored subject value.</summary>
    public string? Subject => ClientIdentity.Text(Raw, "subject");
    /// <summary>Stored value_id value.</summary>
    public string? ValueId => ClientIdentity.Text(Raw, "value_id");
    /// <summary>Stored evidence_ids value.</summary>
    public IReadOnlyList<string> EvidenceIds => ClientIdentity.Strings(Raw, "evidence_ids");
}

/// <summary>One stored attribution record with open string values.</summary>
public sealed class ClientIdentityAssessment
{
    internal ClientIdentityAssessment(JsonElement raw) { Raw = raw.Clone(); }
    /// <summary>Original record, including unknown fields.</summary>
    public JsonElement Raw { get; }
    /// <summary>Stored candidate_profile_id value.</summary>
    public string? CandidateProfileId => ClientIdentity.Text(Raw, "candidate_profile_id");
    /// <summary>Stored status value.</summary>
    public string? Status => ClientIdentity.Text(Raw, "status");
    /// <summary>Stored reason value.</summary>
    public string? Reason => ClientIdentity.Text(Raw, "reason");
}

/// <summary>One stored attribution record with open string values.</summary>
public sealed class ClientIdentityEvidence
{
    internal ClientIdentityEvidence(JsonElement raw) { Raw = raw.Clone(); }
    /// <summary>Original record, including unknown fields.</summary>
    public JsonElement Raw { get; }
    /// <summary>Stored id value.</summary>
    public string? Id => ClientIdentity.Text(Raw, "id");
    /// <summary>Stored method value.</summary>
    public string? Method => ClientIdentity.Text(Raw, "method");
    /// <summary>Stored source_id value.</summary>
    public string? SourceId => ClientIdentity.Text(Raw, "source_id");
    /// <summary>Stored source_revision value.</summary>
    public string? SourceRevision => ClientIdentity.Text(Raw, "source_revision");
    /// <summary>Stored checked_at value.</summary>
    public string? CheckedAt => ClientIdentity.Text(Raw, "checked_at");
    /// <summary>Stored evaluated_at value.</summary>
    public string? EvaluatedAt => ClientIdentity.Text(Raw, "evaluated_at");
    /// <summary>Stored covered_attributes value.</summary>
    public IReadOnlyList<string> CoveredAttributes => ClientIdentity.Strings(Raw, "covered_attributes");
    /// <summary>Stored covered_components value.</summary>
    public IReadOnlyList<string> CoveredComponents => ClientIdentity.Strings(Raw, "covered_components");
    /// <summary>Stored request_binding value.</summary>
    public string? RequestBinding => ClientIdentity.Text(Raw, "request_binding");
    /// <summary>Stored replay_policy value.</summary>
    public string? ReplayPolicy => ClientIdentity.Text(Raw, "replay_policy");
}

internal sealed class ClientIdentityConverter : JsonConverter<ClientIdentity>
{
    public override ClientIdentity? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ClientIdentity.FromValue(document.RootElement);
    }
    public override void Write(Utf8JsonWriter writer, ClientIdentity value, JsonSerializerOptions options) => value.Raw.WriteTo(writer);
}
