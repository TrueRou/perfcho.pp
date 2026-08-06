using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using Perfcho.Performance.Configuration;

namespace Perfcho.Performance.Services;

public sealed class DifficultyCache
{
    private readonly IMemoryCache memoryCache;
    private readonly IDistributedCache distributedCache;
    private readonly CalculationConcurrencyLimiter concurrencyLimiter;
    private readonly ILogger<DifficultyCache> logger;
    private readonly TimeSpan ttl;
    private readonly JsonSerializerOptions serializerOptions;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> pending = new(StringComparer.Ordinal);

    public DifficultyCache(
        IMemoryCache memoryCache,
        IDistributedCache distributedCache,
        CalculationConcurrencyLimiter concurrencyLimiter,
        IOptions<CacheOptions> options,
        ILogger<DifficultyCache> logger)
    {
        this.memoryCache = memoryCache;
        this.distributedCache = distributedCache;
        this.concurrencyLimiter = concurrencyLimiter;
        this.logger = logger;
        ttl = TimeSpan.FromHours(options.Value.DifficultyTtlHours);

        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (!typeof(DifficultyAttributes).IsAssignableFrom(typeInfo.Type))
                return;

            JsonPropertyInfo? mods = typeInfo.Properties.FirstOrDefault(property => property.Name == nameof(DifficultyAttributes.Mods));
            if (mods is not null)
                typeInfo.Properties.Remove(mods);
        });
        serializerOptions = new JsonSerializerOptions { TypeInfoResolver = resolver };
    }

    public async Task<DifficultyAttributes> GetOrCreateAsync(
        string key,
        Type attributesType,
        Mod[] mods,
        Func<DifficultyAttributes> factory,
        CancellationToken cancellationToken)
    {
        string cacheKey = $"difficulty:v1:{key}";
        if (memoryCache.TryGetValue(cacheKey, out string? cached) && cached is not null)
        {
            try
            {
                DifficultyAttributes attributes = Materialize(cached, attributesType, mods);
                logger.LogInformation(
                    "Difficulty cache hit in local memory. CacheKey={CacheKey}, AttributesType={AttributesType}, PayloadLength={PayloadLength}.",
                    cacheKey,
                    attributesType.FullName,
                    cached.Length);
                return attributes;
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Invalid local difficulty cache entry was evicted.");
                memoryCache.Remove(cacheKey);
            }
        }

        Lazy<Task<string>> lazy = pending.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<string>>(
                () => LoadOrCreateAsync(cacheKey, attributesType, factory),
                LazyThreadSafetyMode.ExecutionAndPublication));
        logger.LogInformation(
            "Difficulty was not found in local memory; loading, calculating, or joining an in-flight request. CacheKey={CacheKey}, AttributesType={AttributesType}.",
            cacheKey,
            attributesType.FullName);
        Task<string> task = lazy.Value;
        _ = task.ContinueWith(
            _ => pending.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(cacheKey, lazy)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        string payload = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Materialize(payload, attributesType, mods);
    }

    private async Task<string> LoadOrCreateAsync(string cacheKey, Type attributesType, Func<DifficultyAttributes> factory)
    {
        try
        {
            string? distributed = await distributedCache.GetStringAsync(cacheKey).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(distributed))
            {
                try
                {
                    _ = Materialize(distributed, attributesType, []);
                    StoreMemory(cacheKey, distributed);
                    logger.LogInformation(
                        "Difficulty cache hit in distributed cache. CacheKey={CacheKey}, AttributesType={AttributesType}, PayloadLength={PayloadLength}.",
                        cacheKey,
                        attributesType.FullName,
                        distributed.Length);
                    return distributed;
                }
                catch (JsonException exception)
                {
                    logger.LogWarning(exception, "Invalid distributed difficulty cache entry was evicted.");
                    await distributedCache.RemoveAsync(cacheKey).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Difficulty distributed-cache read failed; calculating locally.");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation(
            "Calculating difficulty after cache miss. CacheKey={CacheKey}, AttributesType={AttributesType}.",
            cacheKey,
            attributesType.FullName);
        DifficultyAttributes attributes = await concurrencyLimiter.RunAsync(factory).ConfigureAwait(false);
        string payload = JsonSerializer.Serialize(attributes, attributes.GetType(), serializerOptions);
        logger.LogInformation(
            "Difficulty calculation finished. CacheKey={CacheKey}, RuntimeType={RuntimeType}, StarRating={StarRating:R}, MaxCombo={MaxCombo}, PayloadLength={PayloadLength}, ElapsedMilliseconds={ElapsedMilliseconds}.",
            cacheKey,
            attributes.GetType().FullName,
            attributes.StarRating,
            attributes.MaxCombo,
            payload.Length,
            stopwatch.ElapsedMilliseconds);
        StoreMemory(cacheKey, payload);

        try
        {
            await distributedCache.SetStringAsync(
                cacheKey,
                payload,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }).ConfigureAwait(false);
            logger.LogInformation(
                "Difficulty cache entry stored in distributed cache. CacheKey={CacheKey}, TtlHours={TtlHours}.",
                cacheKey,
                ttl.TotalHours);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Difficulty distributed-cache write failed; result remains in local memory.");
        }

        return payload;
    }

    private DifficultyAttributes Materialize(string payload, Type attributesType, Mod[] mods)
    {
        try
        {
            var attributes = JsonSerializer.Deserialize(payload, attributesType, serializerOptions) as DifficultyAttributes;
            if (attributes is null)
                throw new JsonException("Difficulty cache payload has the wrong type.");
            attributes.Mods = mods;
            return attributes;
        }
        catch (JsonException)
        {
            throw;
        }
    }

    private void StoreMemory(string cacheKey, string payload)
    {
        memoryCache.Set(cacheKey, payload, new MemoryCacheEntryOptions
        {
            Size = payload.Length * sizeof(char),
            AbsoluteExpirationRelativeToNow = ttl
        });
    }
}
