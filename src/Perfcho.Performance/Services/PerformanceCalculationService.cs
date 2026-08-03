using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch.Difficulty;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mania.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Scoring.Legacy;
using osu.Game.Rulesets.Taiko.Difficulty;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using Perfcho.Performance.Configuration;
using Perfcho.Performance.Contracts;
using Perfcho.Performance.Infrastructure;

namespace Perfcho.Performance.Services;

public sealed class PerformanceCalculationService(
    MetadataValidator metadataValidator,
    BeatmapStore beatmapStore,
    DifficultyCache difficultyCache,
    CalculationConcurrencyLimiter concurrencyLimiter)
{
    public async Task<CalculationResult> CalculateAsync(PerformanceMetadata metadata, CancellationToken cancellationToken)
    {
        ValidatedMetadata validated = metadataValidator.Validate(metadata);
        Ruleset ruleset = RulesetFactory.Create(metadata.Ruleset!);
        ResolvedMods resolvedMods = ModResolver.Resolve(ruleset, metadata, validated.IsLegacyScore);
        byte[] beatmapBytes = await beatmapStore.GetAsync(metadata.BeatmapSha256!, validated.BeatmapUri, cancellationToken).ConfigureAwait(false);

        ProcessorWorkingBeatmap workingBeatmap;
        try
        {
            workingBeatmap = await concurrencyLimiter.RunAsync(() => new ProcessorWorkingBeatmap(beatmapBytes)).ConfigureAwait(false);
        }
        catch (CalculatorException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "beatmap_invalid", "Beatmap content cannot be decoded.", exception);
        }

        DifficultyCalculator calculator = ruleset.CreateDifficultyCalculator(workingBeatmap);
        string difficultyKey = BuildDifficultyKey(metadata, calculator.Version, resolvedMods.CanonicalJson);
        Type attributesType = GetDifficultyAttributesType(metadata.Ruleset!);
        DifficultyAttributes difficulty;
        try
        {
            difficulty = await difficultyCache.GetOrCreateAsync(
                difficultyKey,
                attributesType,
                resolvedMods.Values,
                () => calculator.Calculate(resolvedMods.Values),
                cancellationToken).ConfigureAwait(false);
        }
        catch (CalculatorException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "difficulty_calculation_failed", "Beatmap difficulty calculation failed for the supplied ruleset and mods.", exception);
        }

        ScoreInfo score = CreateScore(metadata, validated, ruleset, workingBeatmap.BeatmapInfo, resolvedMods.Values);
        PopulateMaximumStatistics(score, workingBeatmap, difficulty, validated.IsLegacyScore, metadata.Score!.Hits!);
        if (validated.IsLegacyScore)
            await MigrateLegacyScoreAsync(score, workingBeatmap).ConfigureAwait(false);

        PerformanceCalculator performanceCalculator = ruleset.CreatePerformanceCalculator() ??
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "performance_unsupported", "The requested ruleset has no performance calculator.");
        PerformanceAttributes performance;
        try
        {
            performance = await concurrencyLimiter.RunAsync(
                () => performanceCalculator.Calculate(score, difficulty)).ConfigureAwait(false);
        }
        catch (CalculatorException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "performance_calculation_failed", "Performance calculation failed for the supplied score statistics.", exception);
        }

        ValidateOutput(difficulty, performance);
        return new CalculationResult(
            difficulty.StarRating,
            difficulty.MaxCombo,
            BuildDifficultyBreakdown(difficulty),
            performance.Total,
            BuildPerformanceBreakdown(performance));
    }

    private static ScoreInfo CreateScore(
        PerformanceMetadata metadata,
        ValidatedMetadata validated,
        Ruleset ruleset,
        BeatmapInfo beatmapInfo,
        Mod[] mods)
    {
        var statistics = new Dictionary<HitResult, int>();
        foreach (HitStatisticInput hit in metadata.Score!.Hits!)
        {
            HitResult result = HitResultMapper.Parse(hit.HitResult!);
            statistics[result] = hit.Actual;
        }

        long totalScore = validated.IsLegacyScore ? metadata.Score.ClassicScore : metadata.Score.TotalScore;
        return new ScoreInfo(beatmapInfo, ruleset.RulesetInfo)
        {
            Mods = mods,
            Statistics = statistics,
            Accuracy = validated.Accuracy,
            MaxCombo = metadata.Score.MaxCombo,
            TotalScore = totalScore,
            LegacyTotalScore = validated.IsLegacyScore ? metadata.Score.ClassicScore : null,
            IsLegacyScore = validated.IsLegacyScore,
            TotalScoreVersion = validated.IsLegacyScore ? 30000001 : LegacyScoreEncoder.LATEST_VERSION,
            Passed = metadata.Score.Outcome == "passed"
        };
    }

    private static void PopulateMaximumStatistics(
        ScoreInfo score,
        ProcessorWorkingBeatmap workingBeatmap,
        DifficultyAttributes difficulty,
        bool isLegacyScore,
        IReadOnlyList<HitStatisticInput> suppliedStatistics)
    {
        // Avoid the official helper recalculating difficulty solely to determine legacy combo padding.
        score.IsLegacyScore = false;
        LegacyScoreDecoder.PopulateMaximumStatistics(score, workingBeatmap);
        score.IsLegacyScore = isLegacyScore;

        foreach (HitStatisticInput supplied in suppliedStatistics)
        {
            if (supplied.Maximum is not null)
                score.MaximumStatistics[HitResultMapper.Parse(supplied.HitResult!)] = supplied.Maximum.Value;
        }

        if (!isLegacyScore)
            return;

#pragma warning disable CS0618
        int maximumComboFromStatistics = score.MaximumStatistics
            .Where(pair => pair.Key.AffectsCombo())
            .Sum(pair => pair.Value);
        if (difficulty.MaxCombo > maximumComboFromStatistics)
            score.MaximumStatistics[HitResult.LegacyComboIncrease] = difficulty.MaxCombo - maximumComboFromStatistics;
#pragma warning restore CS0618
    }

    private async Task MigrateLegacyScoreAsync(
        ScoreInfo score,
        ProcessorWorkingBeatmap workingBeatmap)
    {
        try
        {
            await concurrencyLimiter.RunAsync(() =>
            {
                StandardisedScoreMigrationTools.UpdateToLatestScoring(score, workingBeatmap);
                return true;
            }).ConfigureAwait(false);
        }
        catch (CalculatorException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "legacy_score_invalid", "Classic score migration failed for the supplied statistics.", exception);
        }
    }

    private static string BuildDifficultyKey(PerformanceMetadata metadata, int calculatorVersion, string canonicalMods)
    {
        string identity = string.Join(
            '\n',
            "v1",
            CalculatorOptions.OsuPackageVersion,
            calculatorVersion.ToString(CultureInfo.InvariantCulture),
            metadata.BeatmapSha256,
            metadata.DifficultyFormulaId,
            metadata.DifficultyFormulaCode,
            metadata.DifficultyReleaseId,
            metadata.DifficultyReleaseVersion,
            metadata.Ruleset,
            metadata.Variant,
            canonicalMods);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static Type GetDifficultyAttributesType(string ruleset) => ruleset switch
    {
        "osu" => typeof(OsuDifficultyAttributes),
        "taiko" => typeof(TaikoDifficultyAttributes),
        "fruits" => typeof(CatchDifficultyAttributes),
        "mania" => typeof(ManiaDifficultyAttributes),
        _ => throw new UnreachableException()
    };

    private static IReadOnlyDictionary<string, object?> BuildDifficultyBreakdown(DifficultyAttributes attributes)
    {
        var result = new Dictionary<string, object?>();
        switch (attributes)
        {
            case OsuDifficultyAttributes osu:
                result["aim"] = osu.AimDifficulty;
                result["speed"] = osu.SpeedDifficulty;
                result["flashlight"] = osu.FlashlightDifficulty;
                result["reading"] = osu.ReadingDifficulty;
                result["slider_factor"] = osu.SliderFactor;
                result["speed_note_count"] = osu.SpeedNoteCount;
                result["aim_difficult_slider_count"] = osu.AimDifficultSliderCount;
                result["aim_difficult_strain_count"] = osu.AimDifficultStrainCount;
                result["speed_difficult_strain_count"] = osu.SpeedDifficultStrainCount;
                result["reading_difficult_note_count"] = osu.ReadingDifficultNoteCount;
                result["hit_circle_count"] = osu.HitCircleCount;
                result["slider_count"] = osu.SliderCount;
                result["spinner_count"] = osu.SpinnerCount;
                break;
            case TaikoDifficultyAttributes taiko:
                result["mechanical"] = taiko.MechanicalDifficulty;
                result["rhythm"] = taiko.RhythmDifficulty;
                result["reading"] = taiko.ReadingDifficulty;
                result["colour"] = taiko.ColourDifficulty;
                result["stamina"] = taiko.StaminaDifficulty;
                result["mono_stamina_factor"] = taiko.MonoStaminaFactor;
                result["consistency_factor"] = taiko.ConsistencyFactor;
                result["stamina_top_strains"] = taiko.StaminaTopStrains;
                break;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, object?> BuildPerformanceBreakdown(PerformanceAttributes attributes)
    {
        var result = new Dictionary<string, object?>();
        switch (attributes)
        {
            case OsuPerformanceAttributes osu:
                result["aim"] = osu.Aim;
                result["speed"] = osu.Speed;
                result["accuracy"] = osu.Accuracy;
                result["flashlight"] = osu.Flashlight;
                result["reading"] = osu.Reading;
                result["effective_miss_count"] = osu.EffectiveMissCount;
                result["speed_deviation"] = osu.SpeedDeviation;
                result["combo_based_estimated_miss_count"] = osu.ComboBasedEstimatedMissCount;
                result["score_based_estimated_miss_count"] = osu.ScoreBasedEstimatedMissCount;
                result["aim_estimated_slider_breaks"] = osu.AimEstimatedSliderBreaks;
                result["speed_estimated_slider_breaks"] = osu.SpeedEstimatedSliderBreaks;
                break;
            case TaikoPerformanceAttributes taiko:
                result["difficulty"] = taiko.Difficulty;
                result["accuracy"] = taiko.Accuracy;
                result["estimated_unstable_rate"] = taiko.EstimatedUnstableRate;
                break;
            case ManiaPerformanceAttributes mania:
                result["difficulty"] = mania.Difficulty;
                break;
        }
        return result;
    }

    private static void ValidateOutput(DifficultyAttributes difficulty, PerformanceAttributes performance)
    {
        if (!double.IsFinite(difficulty.StarRating) || difficulty.StarRating < 0 || difficulty.MaxCombo < 0 ||
            !double.IsFinite(performance.Total) || performance.Total < 0)
        {
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "calculation_not_finite", "The supplied score produced an invalid calculation result.");
        }
    }
}
