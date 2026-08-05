using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Perfcho.Performance.Configuration;
using Perfcho.Performance.Services;
using Xunit;

namespace Perfcho.Performance.Tests;

public sealed class BeatmapStoreTests
{
    [Fact]
    public async Task Disabled_disk_cache_does_not_create_or_persist_beatmap_files()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            byte[] content = Encoding.UTF8.GetBytes("osu file format v14\n");
            var handler = new CountingHandler(content);
            BeatmapStore store = CreateStore(root, diskCacheEnabled: false, handler);

            byte[] result = await store.GetAsync(Digest(content), new Uri("https://beatmaps.test/map.osu"), CancellationToken.None);

            Assert.Equal(content, result);
            Assert.Equal(1, handler.RequestCount);
            Assert.False(Directory.Exists(Path.Combine(root, "beatmaps")));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Enabled_disk_cache_reuses_beatmap_after_memory_cache_is_recreated()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            byte[] content = Encoding.UTF8.GetBytes("osu file format v14\n");
            string digest = Digest(content);
            var firstHandler = new CountingHandler(content);
            BeatmapStore firstStore = CreateStore(root, diskCacheEnabled: true, firstHandler);

            await firstStore.GetAsync(digest, new Uri("https://beatmaps.test/map.osu"), CancellationToken.None);

            var secondHandler = new CountingHandler(content);
            BeatmapStore secondStore = CreateStore(root, diskCacheEnabled: true, secondHandler);
            byte[] result = await secondStore.GetAsync(digest, new Uri("https://beatmaps.test/map.osu"), CancellationToken.None);

            Assert.Equal(content, result);
            Assert.Equal(1, firstHandler.RequestCount);
            Assert.Equal(0, secondHandler.RequestCount);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Defaults_disable_disk_cache_and_limit_shared_memory_cache_to_128_mib()
    {
        var options = new CacheOptions();

        Assert.False(options.DiskCacheEnabled);
        Assert.Equal(128 * 1024 * 1024, options.MaximumMemoryCacheBytes);
    }

    private static BeatmapStore CreateStore(string root, bool diskCacheEnabled, CountingHandler handler)
    {
        var memory = new MemoryCache(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });
        return new BeatmapStore(
            new StubHttpClientFactory(handler),
            memory,
            Options.Create(new CacheOptions
            {
                Directory = root,
                DiskCacheEnabled = diskCacheEnabled
            }),
            new TestHostEnvironment(root),
            NullLogger<BeatmapStore>.Instance);
    }

    private static string Digest(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string CreateTemporaryDirectory() =>
        Path.Combine(Path.GetTempPath(), $"perfcho-pp-beatmap-tests-{Guid.NewGuid():N}");

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CountingHandler(byte[] content) : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = typeof(BeatmapStoreTests).Assembly.GetName().Name!;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
