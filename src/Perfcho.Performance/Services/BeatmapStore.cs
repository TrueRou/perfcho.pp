using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Perfcho.Performance.Configuration;

namespace Perfcho.Performance.Services;

public sealed class BeatmapStore
{
    public const string HttpClientName = "beatmap-store";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IMemoryCache memoryCache;
    private readonly CacheOptions options;
    private readonly ILogger<BeatmapStore> logger;
    private readonly string beatmapDirectory;
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim trimSemaphore = new(1, 1);
    private readonly SemaphoreSlim downloadSemaphore;

    public BeatmapStore(
        IHttpClientFactory httpClientFactory,
        IMemoryCache memoryCache,
        IOptions<CacheOptions> options,
        IHostEnvironment environment,
        ILogger<BeatmapStore> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.memoryCache = memoryCache;
        this.options = options.Value;
        downloadSemaphore = new SemaphoreSlim(this.options.MaximumConcurrentBeatmapDownloads);
        this.logger = logger;
        string root = Path.IsPathRooted(this.options.Directory)
            ? this.options.Directory
            : Path.Combine(environment.ContentRootPath, this.options.Directory);
        beatmapDirectory = Path.Combine(root, "beatmaps");
        if (this.options.DiskCacheEnabled)
            Directory.CreateDirectory(beatmapDirectory);
    }

    public async Task<byte[]> GetAsync(string sha256, Uri source, CancellationToken cancellationToken)
    {
        string memoryKey = $"beatmap:{sha256}";
        if (memoryCache.TryGetValue(memoryKey, out byte[]? cached) && cached is not null)
            return cached;

        Lazy<Task<byte[]>> lazy = pending.GetOrAdd(
            sha256,
            _ => new Lazy<Task<byte[]>>(() => LoadAsync(sha256, source), LazyThreadSafetyMode.ExecutionAndPublication));
        Task<byte[]> task = lazy.Value;
        _ = task.ContinueWith(
            _ => pending.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(sha256, lazy)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        byte[] bytes = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        memoryCache.Set(memoryKey, bytes, new MemoryCacheEntryOptions
        {
            Size = bytes.LongLength,
            SlidingExpiration = TimeSpan.FromHours(6)
        });
        return bytes;
    }

    private async Task<byte[]> LoadAsync(string sha256, Uri source)
    {
        string path = Path.Combine(beatmapDirectory, $"{sha256}.osu");
        byte[]? local = options.DiskCacheEnabled
            ? await ReadLocalAsync(path, sha256).ConfigureAwait(false)
            : null;
        if (local is not null)
            return local;

        byte[] downloaded = await DownloadAsync(source).ConfigureAwait(false);
        string actual = Convert.ToHexString(SHA256.HashData(downloaded)).ToLowerInvariant();
        if (!string.Equals(actual, sha256, StringComparison.Ordinal))
            throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "beatmap_digest_mismatch", "Beatmap content does not match beatmap_sha256.");

        if (options.DiskCacheEnabled)
            await WriteLocalAsync(path, downloaded).ConfigureAwait(false);
        return downloaded;
    }

    private async Task<byte[]?> ReadLocalAsync(string path, string expectedDigest)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var info = new FileInfo(path);
            if (info.Length > options.MaximumBeatmapBytes)
            {
                File.Delete(path);
                return null;
            }

            byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (string.Equals(actual, expectedDigest, StringComparison.Ordinal))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return bytes;
            }

            File.Delete(path);
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Failed to read beatmap cache entry {DigestPrefix}.", expectedDigest[..12]);
        }

        return null;
    }

    private async Task<byte[]> DownloadAsync(Uri source)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.BeatmapDownloadTimeoutSeconds));
        bool entered = false;
        try
        {
            await downloadSemaphore.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            using HttpRequestMessage request = new(HttpMethod.Get, source);
            using HttpResponseMessage response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "beatmap_not_found", "Beatmap object does not exist.");
            if (!response.IsSuccessStatusCode)
                throw new CalculatorException(StatusCodes.Status502BadGateway, "beatmap_upstream_error", "Beatmap storage is temporarily unavailable.");
            if (response.Content.Headers.ContentLength > options.MaximumBeatmapBytes)
                throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "beatmap_too_large", "Beatmap object exceeds the configured size limit.");

            await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[81920];
            while (true)
            {
                int read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (output.Length + read > options.MaximumBeatmapBytes)
                    throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "beatmap_too_large", "Beatmap object exceeds the configured size limit.");
                output.Write(buffer, 0, read);
            }

            if (output.Length == 0)
                throw new CalculatorException(StatusCodes.Status422UnprocessableEntity, "beatmap_empty", "Beatmap object is empty.");
            return output.ToArray();
        }
        catch (CalculatorException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            throw new CalculatorException(StatusCodes.Status502BadGateway, "beatmap_upstream_error", "Beatmap storage is temporarily unavailable.", exception);
        }
        finally
        {
            if (entered)
                downloadSemaphore.Release();
        }
    }

    private async Task WriteLocalAsync(string path, byte[] content)
    {
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: false);
            TrimDiskCacheIfNeeded();
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process populated the same immutable content-addressed entry.
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Failed to persist beatmap cache entry.");
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private void TrimDiskCacheIfNeeded()
    {
        trimSemaphore.Wait();

        try
        {
            FileInfo[] files = new DirectoryInfo(beatmapDirectory).GetFiles("*.osu");
            long totalBytes = files.Sum(file => file.Length);
            if (totalBytes <= options.MaximumDiskCacheBytes)
                return;

            long targetBytes = (long)(options.MaximumDiskCacheBytes * 0.9);
            foreach (FileInfo file in files.OrderBy(file => file.LastWriteTimeUtc))
            {
                try
                {
                    long length = file.Length;
                    file.Delete();
                    totalBytes -= length;
                    if (totalBytes <= targetBytes)
                        break;
                }
                catch (IOException exception)
                {
                    logger.LogWarning(exception, "Failed to evict beatmap cache entry {FileName}.", file.Name);
                }
            }
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Failed to inspect beatmap disk cache size.");
        }
        finally
        {
            trimSemaphore.Release();
        }
    }
}
