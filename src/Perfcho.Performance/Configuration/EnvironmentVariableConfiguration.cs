using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace Perfcho.Performance.Configuration;

public static class EnvironmentVariableConfiguration
{
    private static readonly IReadOnlyDictionary<string, string> mappings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CALCULATOR_CODE"] = "Calculator:Code",
        ["FORMULA_CODE"] = "Calculator:FormulaCode",
        ["RELEASE_VERSION"] = "Calculator:ReleaseVersion",
        ["DIFFICULTY_FORMULA_CODE"] = "Calculator:DifficultyFormulaCode",
        ["DIFFICULTY_RELEASE_VERSION"] = "Calculator:DifficultyReleaseVersion",
        ["MAXIMUM_CONCURRENT_CALCULATIONS"] = "Calculator:MaximumConcurrentCalculations",
        ["CALCULATION_QUEUE_TIMEOUT_MILLISECONDS"] = "Calculator:CalculationQueueTimeoutMilliseconds",
        ["CACHE_DIRECTORY"] = "Cache:Directory",
        ["CACHE_DISK_ENABLED"] = "Cache:DiskCacheEnabled",
        ["CACHE_MAXIMUM_MEMORY_CACHE_BYTES"] = "Cache:MaximumMemoryCacheBytes",
        ["CACHE_MAXIMUM_BEATMAP_BYTES"] = "Cache:MaximumBeatmapBytes",
        ["CACHE_MAXIMUM_DISK_BYTES"] = "Cache:MaximumDiskCacheBytes",
        ["CACHE_MAXIMUM_CONCURRENT_DOWNLOADS"] = "Cache:MaximumConcurrentBeatmapDownloads",
        ["BEATMAP_DOWNLOAD_TIMEOUT_SECONDS"] = "Cache:BeatmapDownloadTimeoutSeconds",
        ["DIFFICULTY_CACHE_TTL_HOURS"] = "Cache:DifficultyTtlHours",
        ["REDIS_CONNECTION_STRING"] = "Cache:RedisConnectionString",
        ["REDIS_INSTANCE_NAME"] = "Cache:RedisInstanceName"
    };

    public static void AddPerfchoEnvironmentVariables(this ConfigurationManager configuration)
    {
        for (int index = configuration.Sources.Count - 1; index >= 0; index--)
        {
            if (configuration.Sources[index] is EnvironmentVariablesConfigurationSource { Prefix: null })
                configuration.Sources.RemoveAt(index);
        }

        configuration.AddInMemoryCollection(Build(Environment.GetEnvironmentVariable));
    }

    public static IReadOnlyDictionary<string, string?> Build(Func<string, string?> read)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string environmentName, string configurationKey) in mappings)
        {
            string? value = read(environmentName);
            if (value is not null)
                values[configurationKey] = value;
        }

        return values;
    }
}
