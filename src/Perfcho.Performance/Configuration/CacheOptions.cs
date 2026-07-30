namespace Perfcho.Performance.Configuration;

public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    public string Directory { get; init; } = "cache";
    public long MemorySizeBytes { get; init; } = 256 * 1024 * 1024;
    public int MaximumBeatmapBytes { get; init; } = 16 * 1024 * 1024;
    public long MaximumDiskCacheBytes { get; init; } = 5L * 1024 * 1024 * 1024;
    public int MaximumConcurrentBeatmapDownloads { get; init; } = 16;
    public int BeatmapDownloadTimeoutSeconds { get; init; } = 15;
    public int DifficultyTtlHours { get; init; } = 24 * 30;
    public string[] AllowedBeatmapHosts { get; init; } = [];
    public string? RedisConnectionString { get; init; }
    public string RedisInstanceName { get; init; } = "perfcho-pp:";

    public static void Validate(CacheOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Directory))
            throw new InvalidOperationException("Cache directory must be configured.");
        if (options.MemorySizeBytes < 1024 * 1024 || options.MaximumBeatmapBytes < 1024 ||
            options.MaximumDiskCacheBytes < options.MaximumBeatmapBytes ||
            options.MaximumConcurrentBeatmapDownloads < 1 || options.BeatmapDownloadTimeoutSeconds < 1 ||
            options.DifficultyTtlHours < 1)
        {
            throw new InvalidOperationException("Cache limits must be positive and usable.");
        }
        if (options.AllowedBeatmapHosts.Any(host => string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown))
            throw new InvalidOperationException("Allowed beatmap hosts must contain exact valid host names.");
    }
}
