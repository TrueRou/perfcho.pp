using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using osu.Game.Rulesets.Osu.Difficulty;
using Perfcho.Performance.Configuration;
using Perfcho.Performance.Services;
using Xunit;

namespace Perfcho.Performance.Tests;

public sealed class DifficultyCacheTests
{
    [Fact]
    public async Task Same_key_is_calculated_once_and_full_attributes_survive_snapshotting()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 * 1024 });
        var distributed = new DictionaryDistributedCache();
        var limiter = new CalculationConcurrencyLimiter(Options.Create(new CalculatorOptions
        {
            MaximumConcurrentCalculations = 2
        }));
        var cache = new DifficultyCache(
            memory,
            distributed,
            limiter,
            Options.Create(new CacheOptions()),
            NullLogger<DifficultyCache>.Instance);
        int calculationCount = 0;

        Task<osu.Game.Rulesets.Difficulty.DifficultyAttributes>[] requests = Enumerable.Range(0, 16)
            .Select(_ => cache.GetOrCreateAsync(
                "same-key",
                typeof(OsuDifficultyAttributes),
                [],
                () =>
                {
                    Interlocked.Increment(ref calculationCount);
                    Thread.Sleep(50);
                    return new OsuDifficultyAttributes
                    {
                        StarRating = 5.5,
                        MaxCombo = 123,
                        AimDifficulty = 2.1,
                        SliderCount = 42
                    };
                },
                CancellationToken.None))
            .ToArray();

        osu.Game.Rulesets.Difficulty.DifficultyAttributes[] results = await Task.WhenAll(requests);

        Assert.Equal(1, calculationCount);
        Assert.All(results, result =>
        {
            var osu = Assert.IsType<OsuDifficultyAttributes>(result);
            Assert.Equal(5.5, osu.StarRating);
            Assert.Equal(42, osu.SliderCount);
        });
    }

    private sealed class DictionaryDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> values = new(StringComparer.Ordinal);

        public byte[]? Get(string key) => values.TryGetValue(key, out byte[]? value) ? value : null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => values.TryRemove(key, out _);
        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => values[key] = value;
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }
}
