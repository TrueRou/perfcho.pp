using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Perfcho.Performance.Configuration;
using Perfcho.Performance.Contracts;

namespace Perfcho.Performance.Services;

public sealed record ValidatedMetadata(Uri BeatmapUri, double Accuracy, bool IsLegacyScore);

public sealed class MetadataValidator(IOptions<CalculatorOptions> configured)
{
    private static readonly Regex modAcronym = new("^[A-Z0-9]{1,8}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> rulesets = new(CalculatorOptions.SupportedRulesets, StringComparer.Ordinal);
    private static readonly HashSet<string> clients = ["stable", "lazer", "web", "api"];
    private static readonly HashSet<string> outcomes = ["abandoned", "failed", "passed"];

    private readonly CalculatorOptions options = configured.Value;

    public ValidatedMetadata Validate(PerformanceMetadata metadata)
    {
        if (metadata.SchemaVersion != 1)
            Invalid("schema_version must be 1.");
        if (metadata.FormulaId == Guid.Empty || metadata.ReleaseId == Guid.Empty ||
            metadata.DifficultyFormulaId == Guid.Empty || metadata.DifficultyReleaseId == Guid.Empty)
        {
            Invalid("UUID fields must be non-empty UUIDs.");
        }
        if (metadata.ScoreId < 1 || metadata.BeatmapRevisionId < 1)
            Invalid("Identifiers must be positive.");

        VerifyIdentity(metadata.Calculator, options.Code, "calculator");
        VerifyIdentity(metadata.FormulaCode, options.FormulaCode, "formula_code");
        VerifyIdentity(metadata.ReleaseVersion, options.ReleaseVersion, "release_version");
        VerifyIdentity(metadata.DifficultyFormulaCode, options.DifficultyFormulaCode, "difficulty_formula_code");
        VerifyIdentity(metadata.DifficultyReleaseVersion, options.DifficultyReleaseVersion, "difficulty_release_version");

        if (!CalculatorOptions.IsDigest(metadata.InputDigest) || !CalculatorOptions.IsDigest(metadata.BeatmapSha256) ||
            !CalculatorOptions.IsDigest(metadata.ModsDigest))
        {
            Invalid("input_digest, beatmap_sha256, and mods_digest must be lowercase SHA-256 hex strings.");
        }
        if (metadata.Ruleset is null || !rulesets.Contains(metadata.Ruleset))
            Invalid("ruleset is not supported.");
        if (metadata.ClientFamily is null || !clients.Contains(metadata.ClientFamily))
            Invalid("client_family is not supported.");

        if (!Uri.TryCreate(metadata.BeatmapUrl, UriKind.Absolute, out Uri? beatmapUri) ||
            beatmapUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(beatmapUri.UserInfo))
        {
            Invalid("beatmap_url must be an absolute HTTP(S) URL without user information.");
        }
        ValidateConfigurations(metadata);
        ValidateMods(metadata.Mods);
        double accuracy = ValidateScore(metadata.Score);
        string scoreSystem = metadata.ReleaseConfiguration!.Value<string>("score_system") ??
                             (metadata.ClientFamily == "stable" ? "classic" : "lazer");
        return new ValidatedMetadata(beatmapUri!, accuracy, scoreSystem == "classic");
    }

    private void ValidateConfigurations(PerformanceMetadata metadata)
    {
        if (metadata.ReleaseConfiguration is null || metadata.DifficultyReleaseConfiguration is null)
            Invalid("release configurations must be JSON objects.");

        ValidateConfiguration(metadata.ReleaseConfiguration!, "performance", metadata.Ruleset!, "release_configuration");
        ValidateConfiguration(metadata.DifficultyReleaseConfiguration!, "difficulty", metadata.Ruleset!, "difficulty_release_configuration");
    }

    private void ValidateConfiguration(JObject configuration, string expectedKind, string ruleset, string name)
    {
        foreach (JProperty property in configuration.Properties())
        {
            switch (property.Name)
            {
                case "score_system":
                    if (property.Value.Type != JTokenType.String || property.Value.Value<string>() is not ("lazer" or "classic"))
                        Invalid($"{name}.score_system must be lazer or classic.");
                    break;
                case "source":
                    if (property.Value.Type != JTokenType.String || property.Value.Value<string>() != options.FormulaCode)
                        Invalid($"{name}.source does not match this deployment.");
                    break;
                case "calculator":
                    if (property.Value.Type != JTokenType.String || property.Value.Value<string>() != options.Code)
                        Invalid($"{name}.calculator does not match this deployment.");
                    break;
                case "kind":
                    if (property.Value.Type != JTokenType.String || property.Value.Value<string>() != expectedKind)
                        Invalid($"{name}.kind is invalid.");
                    break;
                case "ruleset":
                    if (property.Value.Type != JTokenType.String || property.Value.Value<string>() != ruleset)
                        Invalid($"{name}.ruleset does not match the requested ruleset.");
                    break;
                case "bootstrap_version":
                    if (property.Value.Type != JTokenType.Integer || property.Value.Value<int>() != 1)
                        Invalid($"{name}.bootstrap_version is invalid.");
                    break;
                default:
                    Invalid($"Unsupported {name} setting: {property.Name}.");
                    break;
            }
        }
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
