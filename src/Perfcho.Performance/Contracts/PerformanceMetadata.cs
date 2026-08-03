using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Perfcho.Performance.Contracts;

[JsonObject(MemberSerialization.OptIn)]
public sealed class PerformanceMetadata
{
    [JsonProperty("schema_version", Required = Required.Always)] public int SchemaVersion { get; init; }
    [JsonProperty("job_id", Required = Required.Always)] public Guid JobId { get; init; }
    [JsonProperty("score_id", Required = Required.Always)] public long ScoreId { get; init; }
    [JsonProperty("formula_id", Required = Required.Always)] public Guid FormulaId { get; init; }
    [JsonProperty("formula_code", Required = Required.Always)] public string? FormulaCode { get; init; }
    [JsonProperty("calculator", Required = Required.Always)] public string? Calculator { get; init; }
    [JsonProperty("release_id", Required = Required.Always)] public Guid ReleaseId { get; init; }
    [JsonProperty("release_version", Required = Required.Always)] public string? ReleaseVersion { get; init; }
    [JsonProperty("release_configuration", Required = Required.Always)] public JObject? ReleaseConfiguration { get; init; }
    [JsonProperty("difficulty_formula_id", Required = Required.Always)] public Guid DifficultyFormulaId { get; init; }
    [JsonProperty("difficulty_formula_code", Required = Required.Always)] public string? DifficultyFormulaCode { get; init; }
    [JsonProperty("difficulty_release_id", Required = Required.Always)] public Guid DifficultyReleaseId { get; init; }
    [JsonProperty("difficulty_release_version", Required = Required.Always)] public string? DifficultyReleaseVersion { get; init; }
    [JsonProperty("difficulty_release_configuration", Required = Required.Always)] public JObject? DifficultyReleaseConfiguration { get; init; }
    [JsonProperty("input_digest", Required = Required.Always)] public string? InputDigest { get; init; }
    [JsonProperty("beatmap_revision_id", Required = Required.Always)] public long BeatmapRevisionId { get; init; }
    [JsonProperty("beatmap_sha256", Required = Required.Always)] public string? BeatmapSha256 { get; init; }
    [JsonProperty("beatmap_url", Required = Required.Always)] public string? BeatmapUrl { get; init; }
    [JsonProperty("ruleset", Required = Required.Always)] public string? Ruleset { get; init; }
    [JsonProperty("variant", Required = Required.Always)] public string? Variant { get; init; }
    [JsonProperty("mod_set_id", Required = Required.Always)] public long ModSetId { get; init; }
    [JsonProperty("mods", Required = Required.Always)] public List<CanonicalMod>? Mods { get; init; }
    [JsonProperty("client_family", Required = Required.Always)] public string? ClientFamily { get; init; }
    [JsonProperty("score", Required = Required.Always)] public ScoreInput? Score { get; init; }
}

[JsonObject(MemberSerialization.OptIn)]
public sealed class CanonicalMod
{
    [JsonProperty("acronym", Required = Required.Always)] public string? Acronym { get; init; }
    [JsonProperty("settings")] public JObject? Settings { get; init; }
}

[JsonObject(MemberSerialization.OptIn)]
public sealed class ScoreInput
{
    [JsonProperty("total_score", Required = Required.Always)] public long TotalScore { get; init; }
    [JsonProperty("classic_score", Required = Required.Always)] public long ClassicScore { get; init; }
    [JsonProperty("accuracy", Required = Required.Always)] public string? Accuracy { get; init; }
    [JsonProperty("max_combo", Required = Required.Always)] public int MaxCombo { get; init; }
    [JsonProperty("outcome", Required = Required.Always)] public string? Outcome { get; init; }
    [JsonProperty("hits", Required = Required.Always)] public List<HitStatisticInput>? Hits { get; init; }
}

[JsonObject(MemberSerialization.OptIn)]
public sealed class HitStatisticInput
{
    [JsonProperty("hit_result", Required = Required.Always)] public string? HitResult { get; init; }
    [JsonProperty("actual", Required = Required.Always)] public int Actual { get; init; }
    [JsonProperty("maximum", Required = Required.AllowNull)] public int? Maximum { get; init; }
}
