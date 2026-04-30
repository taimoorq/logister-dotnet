using System.Text.Json.Serialization;

namespace Logister;

public sealed record LogisterResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("legacy_id")] long? LegacyId,
    [property: JsonPropertyName("status")] string? Status);
