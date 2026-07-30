using System.Text.RegularExpressions;

namespace Perfcho.Performance.Configuration;

public sealed class CalculatorOptions
{
    public const string SectionName = "Calculator";
    public const string OsuPackageVersion = "2026.702.1";

    public string Code { get; init; } = "osu-lazer-dotnet";
    public string FormulaCode { get; init; } = "official";
    public string ReleaseVersion { get; init; } = string.Empty;
    public string ArtifactDigest { get; init; } = string.Empty;
    public string DifficultyFormulaCode { get; init; } = "official-difficulty";
    public string DifficultyReleaseVersion { get; init; } = string.Empty;
    public string DifficultyArtifactDigest { get; init; } = string.Empty;
    public int MaximumConcurrentCalculations { get; init; }
    public int CalculationQueueTimeoutMilliseconds { get; init; }

    public int EffectiveMaximumConcurrentCalculations =>
        MaximumConcurrentCalculations > 0 ? MaximumConcurrentCalculations : Math.Max(1, Environment.ProcessorCount);

    public static void Validate(CalculatorOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Code) ||
            string.IsNullOrWhiteSpace(options.FormulaCode) ||
            string.IsNullOrWhiteSpace(options.ReleaseVersion) ||
            string.IsNullOrWhiteSpace(options.DifficultyFormulaCode) ||
            string.IsNullOrWhiteSpace(options.DifficultyReleaseVersion))
        {
            throw new InvalidOperationException("Calculator release identity must be configured.");
        }

        if (!IsDigest(options.ArtifactDigest) || !IsDigest(options.DifficultyArtifactDigest))
            throw new InvalidOperationException("Calculator artifact digests must be lowercase SHA-256 hex strings.");

        if (options.MaximumConcurrentCalculations < 0 || options.CalculationQueueTimeoutMilliseconds < 0)
            throw new InvalidOperationException("Calculator concurrency settings must be nonnegative.");
    }

    public static bool IsDigest(string? value) =>
        value is not null && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
}
