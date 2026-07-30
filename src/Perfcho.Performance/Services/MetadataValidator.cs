using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Perfcho.Performance.Configuration;
using Perfcho.Performance.Contracts;

namespace Perfcho.Performance.Services;

public sealed record ValidatedMetadata(Uri BeatmapUri, double Accuracy, bool IsLegacyScore);

public sealed class MetadataValidator(IOptions<CalculatorOptions> configured, IOptions<CacheOptions> cacheConfigured)
{
    private static readonly Regex modAcronym = new("^[A-Z0-9]{1,8}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> rulesets = ["osu", "taiko", "fruits", "mania"];
    private static readonly HashSet<string> variants = ["vanilla", "relax", "autopilot"];
    private static readonly HashSet<string> clients = ["stable", "lazer", "web", "api"];
    private static readonly HashSet<string> outcomes = ["abandoned", "failed", "passed"];

    private readonly CalculatorOptions options = configured.Value;
    private readonly CacheOptions cacheOptions = cacheConfigured.Value;

    public ValidatedMetadata Validate(PerformanceMetadata metadata)
    {
        if (metadata.SchemaVersion != 1)
            Invalid("schema_version must be 1.");
        if (metadata.JobId == Guid.Empty || metadata.FormulaId == Guid.Empty || metadata.ReleaseId == Guid.Empty ||
            metadata.DifficultyFormulaId == Guid.Empty || metadata.DifficultyReleaseId == Guid.Empty)
        {
            Invalid("UUID fields must be non-empty UUIDs.");
        }
        if (metadata.ScoreId < 1 || metadata.BeatmapRevisionId < 1 || metadata.ModSetId < 1)
            Invalid("Identifiers must be positive.");

        VerifyIdentity(metadata.Calculator, options.Code, "calculator");
        VerifyIdentity(metadata.FormulaCode, options.FormulaCode, "formula_code");
        VerifyIdentity(metadata.ReleaseVersion, options.ReleaseVersion, "release_version");
        VerifyIdentity(metadata.ArtifactDigest, options.ArtifactDigest, "artifact_digest");
        VerifyIdentity(metadata.DifficultyFormulaCode, options.DifficultyFormulaCode, "difficulty_formula_code");
        VerifyIdentity(metadata.DifficultyReleaseVersion, options.DifficultyReleaseVersion, "difficulty_release_version");
        VerifyIdentity(metadata.DifficultyArtifactDigest, options.DifficultyArtifactDigest, "difficulty_artifact_digest");

        if (!CalculatorOptions.IsDigest(metadata.InputDigest) || !CalculatorOptions.IsDigest(metadata.BeatmapSha256))
            Invalid("input_digest and beatmap_sha256 must be lowercase SHA-256 hex strings.");
        if (metadata.Ruleset is null || !rulesets.Contains(metadata.Ruleset))
            Invalid("ruleset is not supported.");
        if (metadata.Variant is null || !variants.Contains(metadata.Variant))
            Invalid("variant is not supported.");
        if (metadata.ClientFamily is null || !clients.Contains(metadata.ClientFamily))
            Invalid("client_family is not supported.");
        if (metadata.Variant == "relax" && metadata.Ruleset == "mania")
            Invalid("mania does not support the relax variant.");
        if (metadata.Variant == "autopilot" && metadata.Ruleset != "osu")
            Invalid("autopilot is only supported by osu.");

        if (!Uri.TryCreate(metadata.BeatmapUrl, UriKind.Absolute, out Uri? beatmapUri) ||
            beatmapUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(beatmapUri.UserInfo))
        {
            Invalid("beatmap_url must be an absolute HTTP(S) URL without user information.");
        }
        if (cacheOptions.AllowedBeatmapHosts.Length > 0 &&
            !cacheOptions.AllowedBeatmapHosts.Contains(beatmapUri!.IdnHost, StringComparer.OrdinalIgnoreCase))
        {
            Invalid("beatmap_url host is not allowed by this deployment.");
        }

        ValidateConfigurations(metadata);
        ValidateMods(metadata.Mods);
        double accuracy = ValidateScore(metadata.Score);
        string scoreSystem = metadata.ReleaseConfiguration!.Value<string>("score_system") ??
                             (metadata.ClientFamily == "stable" ? "classic" : "lazer");
        return new ValidatedMetadata(beatmapUri!, accuracy, scoreSystem == "classic");
    }

    private static void ValidateConfigurations(PerformanceMetadata metadata)
    {
        if (metadata.ReleaseConfiguration is null || metadata.DifficultyReleaseConfiguration is null)
            Invalid("release configurations must be JSON objects.");
        if (metadata.DifficultyReleaseConfiguration!.HasValues)
            Invalid("difficulty_release_configuration is not supported by this release.");

        foreach (JProperty property in metadata.ReleaseConfiguration!.Properties())
        {
            if (property.Name != "score_system")
                Invalid($"Unsupported release_configuration setting: {property.Name}.");
        }

        JToken? scoreSystem = metadata.ReleaseConfiguration["score_system"];
        if (scoreSystem is not null && (scoreSystem.Type != JTokenType.String || scoreSystem.Value<string>() is not ("lazer" or "classic")))
            Invalid("release_configuration.score_system must be lazer or classic.");
    }

    private static void ValidateMods(List<CanonicalMod>? mods)
    {
        if (mods is null || mods.Count > 32)
            Invalid("mods must contain at most 32 entries.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (CanonicalMod mod in mods!)
        {
            if (mod.Acronym is null || !modAcronym.IsMatch(mod.Acronym))
                Invalid("Mod acronyms must contain 1-8 uppercase ASCII letters or digits.");
            if (!seen.Add(mod.Acronym!))
                Invalid($"Duplicate mod acronym: {mod.Acronym}.");
            if (mod.Acronym is "RX" or "AP")
                Invalid("Assistance mods must be represented by variant, not mods.");
            if (mod.Acronym is "AT" or "CN")
                Invalid("Automatic-play mods cannot be used for submitted scores.");
            if (mod.Settings is not null && mod.Settings.Properties().Count() > 32)
                Invalid($"Mod {mod.Acronym} contains too many settings.");
        }
    }

    private static double ValidateScore(ScoreInput? score)
    {
        if (score is null || score.TotalScore < 0 || score.ClassicScore < 0 || score.MaxCombo < 0)
            Invalid("Score totals and max_combo must be nonnegative.");
        if (score!.Outcome is null || !outcomes.Contains(score.Outcome))
            Invalid("score.outcome is not supported.");
        if (score.Hits is null || score.Hits.Count is < 1 or > 32)
            Invalid("score.hits must contain 1-32 entries.");
        if (!decimal.TryParse(score.Accuracy, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal accuracy) ||
            accuracy is < 0 or > 1)
        {
            Invalid("score.accuracy must be a decimal string between zero and one.");
        }

        var hitNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (HitStatisticInput hit in score.Hits!)
        {
            if (string.IsNullOrWhiteSpace(hit.HitResult) || !Regex.IsMatch(hit.HitResult, "^[a-z0-9_]{1,32}$", RegexOptions.CultureInvariant))
                Invalid("hit_result must be a snake-case name.");
            if (!hitNames.Add(hit.HitResult!))
                Invalid($"Duplicate hit_result: {hit.HitResult}.");
            if (hit.Actual < 0 || hit.Maximum < hit.Actual)
                Invalid("Hit counts must be nonnegative and maximum must not be below actual.");
        }

        return (double)accuracy;
    }

    private static void VerifyIdentity(string? actual, string expected, string name)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new CalculatorException(StatusCodes.Status409Conflict, "release_identity_mismatch", $"{name} does not match this deployment.");
    }

    private static void Invalid(string message) =>
        throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "invalid_metadata", message);
}
