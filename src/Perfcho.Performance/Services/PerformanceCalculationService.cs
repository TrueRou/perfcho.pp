using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
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
    CalculationConcurrencyLimiter concurrencyLimiter,
    ILogger<PerformanceCalculationService> logger)
{
    public async Task<CalculationResult> CalculateAsync(PerformanceMetadata metadata, CancellationToken cancellationToken)
    {
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ScoreId"] = metadata.ScoreId,
            ["BeatmapRevisionId"] = metadata.BeatmapRevisionId,
            ["Ruleset"] = metadata.Ruleset
        });
        var totalStopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Starting performance calculation. InputDigest={InputDigest}, BeatmapSha256={BeatmapSha256}, BeatmapUrl={BeatmapUrl}, " +
            "Formula={FormulaCode}/{ReleaseVersion}, DifficultyFormula={DifficultyFormulaCode}/{DifficultyReleaseVersion}, " +
            "ClientFamily={ClientFamily}, ModsDigest={ModsDigest}.",
            metadata.InputDigest,
            metadata.BeatmapSha256,
            metadata.BeatmapUrl,
            metadata.FormulaCode,
            metadata.ReleaseVersion,
            metadata.DifficultyFormulaCode,
            metadata.DifficultyReleaseVersion,
            metadata.ClientFamily,
            metadata.ModsDigest);
        logger.LogInformation("Full calculation metadata: {CalculationMetadata}.", JsonConvert.SerializeObject(metadata));

        ValidatedMetadata validated = metadataValidator.Validate(metadata);
        logger.LogInformation(
            "Metadata validated. Accuracy={Accuracy:R}, IsLegacyScore={IsLegacyScore}, BeatmapUri={BeatmapUri}.",
            validated.Accuracy,
            validated.IsLegacyScore,
            validated.BeatmapUri);

        Ruleset ruleset = RulesetFactory.Create(metadata.Ruleset!);
        ResolvedMods resolvedMods = ModResolver.Resolve(ruleset, metadata, validated.IsLegacyScore);
        logger.LogInformation(
            "Ruleset and mods resolved. RulesetName={RulesetName}, RulesetShortName={RulesetShortName}, Mods={CanonicalMods}.",
            ruleset.Description,
            ruleset.ShortName,
            resolvedMods.CanonicalJson);

        var stageStopwatch = Stopwatch.StartNew();
        byte[] beatmapBytes = await beatmapStore.GetAsync(metadata.BeatmapSha256!, validated.BeatmapUri, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Beatmap content loaded. ByteCount={BeatmapByteCount}, ElapsedMilliseconds={ElapsedMilliseconds}.",
            beatmapBytes.LongLength,
            stageStopwatch.ElapsedMilliseconds);

        ProcessorWorkingBeatmap workingBeatmap;
        stageStopwatch.Restart();
        try
        {
            workingBeatmap = await concurrencyLimiter.RunAsync(() => new ProcessorWorkingBeatmap(beatmapBytes)).ConfigureAwait(false);
            logger.LogInformation(
                "Beatmap decoded. OnlineBeatmapId={OnlineBeatmapId}, OnlineBeatmapSetId={OnlineBeatmapSetId}, " +
                "RulesetOnlineId={BeatmapRulesetOnlineId}, ElapsedMilliseconds={ElapsedMilliseconds}.",
                workingBeatmap.BeatmapInfo.OnlineID,
                workingBeatmap.BeatmapInfo.BeatmapSet?.OnlineID,
                workingBeatmap.BeatmapInfo.Ruleset.OnlineID,
                stageStopwatch.ElapsedMilliseconds);
        }
        catch (CalculatorException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Beatmap decoding failed after {ElapsedMilliseconds} ms.", stageStopwatch.ElapsedMilliseconds);
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "beatmap_invalid", "Beatmap content cannot be decoded.", exception);
        }

        DifficultyCalculator calculator = ruleset.CreateDifficultyCalculator(workingBeatmap);
        string difficultyKey = BuildDifficultyKey(metadata, calculator.Version, resolvedMods.CanonicalJson);
        Type attributesType = GetDifficultyAttributesType(metadata.Ruleset!);
        logger.LogInformation(
            "Difficulty calculation prepared. CalculatorType={CalculatorType}, CalculatorVersion={CalculatorVersion}, " +
            "AttributesType={AttributesType}, DifficultyKey={DifficultyKey}.",
            calculator.GetType().FullName,
            calculator.Version,
            attributesType.FullName,
            difficultyKey);

        DifficultyAttributes difficulty;
        stageStopwatch.Restart();
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
            logger.LogError(exception, "Difficulty calculation failed after {ElapsedMilliseconds} ms. DifficultyKey={DifficultyKey}.", stageStopwatch.ElapsedMilliseconds, difficultyKey);
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "difficulty_calculation_failed", "Beatmap difficulty calculation failed for the supplied ruleset and mods.", exception);
        }

        IReadOnlyDictionary<string, object?> difficultyBreakdown = BuildDifficultyBreakdown(difficulty);
        logger.LogInformation(
            "Difficulty calculated. AttributeRuntimeType={AttributeRuntimeType}, StarRating={StarRating:R}, MaxCombo={MaxCombo}, " +
            "Breakdown={DifficultyBreakdown}, ElapsedMilliseconds={ElapsedMilliseconds}.",
            difficulty.GetType().FullName,
            difficulty.StarRating,
            difficulty.MaxCombo,
            JsonConvert.SerializeObject(difficultyBreakdown),
            stageStopwatch.ElapsedMilliseconds);

        ScoreInfo score = CreateScore(metadata, validated, ruleset, workingBeatmap.BeatmapInfo, resolvedMods.Values);
        logger.LogInformation(
            "Score created. Accuracy={Accuracy:R}, MaxCombo={MaxCombo}, TotalScore={TotalScore}, LegacyTotalScore={LegacyTotalScore}, " +
            "TotalScoreVersion={TotalScoreVersion}, IsLegacyScore={IsLegacyScore}, Passed={Passed}, Statistics={Statistics}.",
            score.Accuracy,
            score.MaxCombo,
            score.TotalScore,
            score.LegacyTotalScore,
            score.TotalScoreVersion,
            score.IsLegacyScore,
            score.Passed,
            FormatStatistics(score.Statistics));

        PopulateMaximumStatistics(score, workingBeatmap, difficulty, validated.IsLegacyScore, metadata.Score!.Hits!);
        logger.LogInformation("Maximum score statistics populated. MaximumStatistics={MaximumStatistics}.", FormatStatistics(score.MaximumStatistics));

        if (validated.IsLegacyScore)
        {
            stageStopwatch.Restart();
            logger.LogInformation(
                "Starting classic score migration. PreMigrationTotalScore={TotalScore}, PreMigrationLegacyTotalScore={LegacyTotalScore}, " +
                "PreMigrationAccuracy={Accuracy:R}, PreMigrationStatistics={Statistics}, PreMigrationMaximumStatistics={MaximumStatistics}.",
                score.TotalScore,
                score.LegacyTotalScore,
                score.Accuracy,
                FormatStatistics(score.Statistics),
                FormatStatistics(score.MaximumStatistics));
            await MigrateLegacyScoreAsync(score, workingBeatmap).ConfigureAwait(false);
            logger.LogInformation(
                "Classic score migration completed. TotalScore={TotalScore}, LegacyTotalScore={LegacyTotalScore}, Accuracy={Accuracy:R}, " +
                "MaxCombo={MaxCombo}, TotalScoreVersion={TotalScoreVersion}, Statistics={Statistics}, MaximumStatistics={MaximumStatistics}, " +
                "ElapsedMilliseconds={ElapsedMilliseconds}.",
                score.TotalScore,
                score.LegacyTotalScore,
                score.Accuracy,
                score.MaxCombo,
                score.TotalScoreVersion,
                FormatStatistics(score.Statistics),
                FormatStatistics(score.MaximumStatistics),
                stageStopwatch.ElapsedMilliseconds);
        }

        PerformanceCalculator performanceCalculator = ruleset.CreatePerformanceCalculator() ??
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "performance_unsupported", "The requested ruleset has no performance calculator.");
        logger.LogInformation("Performance calculation prepared. CalculatorType={CalculatorType}.", performanceCalculator.GetType().FullName);

        PerformanceAttributes performance;
        stageStopwatch.Restart();
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
            logger.LogError(
                exception,
                "Performance calculation failed after {ElapsedMilliseconds} ms. Score={ScoreSnapshot}, Difficulty={DifficultySnapshot}.",
                stageStopwatch.ElapsedMilliseconds,
                FormatScore(score),
                FormatDifficulty(difficulty, difficultyBreakdown));
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "performance_calculation_failed", "Performance calculation failed for the supplied score statistics.", exception);
        }

        IReadOnlyDictionary<string, object?> performanceBreakdown = BuildPerformanceBreakdown(performance);
        logger.LogInformation(
            "Performance calculated. AttributeRuntimeType={AttributeRuntimeType}, TotalPp={TotalPp:R}, Breakdown={PerformanceBreakdown}, " +
            "ElapsedMilliseconds={ElapsedMilliseconds}.",
            performance.GetType().FullName,
            performance.Total,
            JsonConvert.SerializeObject(performanceBreakdown),
            stageStopwatch.ElapsedMilliseconds);

        ValidateOutput(difficulty, performance);
        var result = new CalculationResult(
            difficulty.StarRating,
            difficulty.MaxCombo,
            difficultyBreakdown,
            performance.Total,
            performanceBreakdown);
        logger.LogInformation(
            "Performance calculation completed. StarRating={StarRating:R}, MaxCombo={MaxCombo}, PerformancePoints={PerformancePoints:R}, " +
            "TotalElapsedMilliseconds={TotalElapsedMilliseconds}.",
            result.StarRating,
            result.MaxCombo,
            result.PerformancePoints,
            totalStopwatch.ElapsedMilliseconds);
        return result;
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

    private static string FormatStatistics(IReadOnlyDictionary<HitResult, int> statistics) =>
        JsonConvert.SerializeObject(statistics
            .OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key.ToString(), pair => pair.Value, StringComparer.Ordinal));

    private static string FormatScore(ScoreInfo score) => JsonConvert.SerializeObject(new
    {
        score.Accuracy,
        score.MaxCombo,
        score.TotalScore,
        score.LegacyTotalScore,
        score.IsLegacyScore,
        score.TotalScoreVersion,
        score.Passed,
        statistics = JsonConvert.DeserializeObject(FormatStatistics(score.Statistics)),
        maximum_statistics = JsonConvert.DeserializeObject(FormatStatistics(score.MaximumStatistics)),
        mods = score.Mods.Select(mod => mod.Acronym).ToArray()
    });

    private static string FormatDifficulty(
        DifficultyAttributes difficulty,
        IReadOnlyDictionary<string, object?> breakdown) => JsonConvert.SerializeObject(new
        {
            runtime_type = difficulty.GetType().FullName,
            difficulty.StarRating,
            difficulty.MaxCombo,
            breakdown
        });

    private static void ValidateOutput(DifficultyAttributes difficulty, PerformanceAttributes performance)
    {
        if (!double.IsFinite(difficulty.StarRating) || difficulty.StarRating < 0 || difficulty.MaxCombo < 0 ||
            !double.IsFinite(performance.Total) || performance.Total < 0)
        {
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "calculation_not_finite", "The supplied score produced an invalid calculation result.");
        }
    }
}
