using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using Perfcho.Performance.Infrastructure;
using Perfcho.Performance.Configuration;
using Xunit;

namespace Perfcho.Performance.Tests;

public sealed class ContractTests
{
    [Fact]
    public void Environment_variables_use_explicit_uppercase_single_underscore_names()
    {
        var environment = new Dictionary<string, string?>
        {
            ["ARTIFACT_DIGEST"] = new string('a', 64),
            ["REDIS_CONNECTION_STRING"] = "redis.test:6379",
            ["BEATMAP_ALLOWED_HOSTS"] = "s3.test, minio.test",
            ["Calculator__Code"] = "legacy-name-must-not-bind"
        };

        IReadOnlyDictionary<string, string?> mapped = EnvironmentVariableConfiguration.Build(
            name => environment.GetValueOrDefault(name));

        Assert.Equal(new string('a', 64), mapped["Calculator:ArtifactDigest"]);
        Assert.Equal("redis.test:6379", mapped["Cache:RedisConnectionString"]);
        Assert.Equal("s3.test", mapped["Cache:AllowedBeatmapHosts:0"]);
        Assert.Equal("minio.test", mapped["Cache:AllowedBeatmapHosts:1"]);
        Assert.DoesNotContain("Calculator:Code", mapped);
    }

    [Fact]
    public async Task Calculate_accepts_perfcho_multipart_contract_and_returns_official_result()
    {
        byte[] beatmap = Encoding.UTF8.GetBytes(TestBeatmap);
        using var factory = new CalculatorFactory(beatmap);
        using HttpClient client = factory.CreateClient();
        JObject metadata = CreateMetadata(beatmap);

        using HttpResponseMessage first = await client.PostAsync("/v1/performance/calculate", CreateRequest(metadata));
        string firstJson = await first.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using JsonDocument response = JsonDocument.Parse(firstJson);
        Assert.Equal("osu-lazer-dotnet", response.RootElement.GetProperty("calculator").GetString());
        Assert.Equal(metadata.Value<string>("input_digest"), response.RootElement.GetProperty("input_digest").GetString());
        Assert.True(decimal.Parse(response.RootElement.GetProperty("difficulty").GetProperty("star_rating").GetString()!) >= 0);
        Assert.True(decimal.Parse(response.RootElement.GetProperty("performance").GetProperty("pp").GetString()!) >= 0);
        Assert.Equal(3, response.RootElement.GetProperty("difficulty").GetProperty("max_combo").GetInt32());

        using HttpResponseMessage second = await client.PostAsync("/v1/performance/calculate", CreateRequest(metadata));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, factory.BeatmapRequests);
    }

    [Theory]
    [InlineData("taiko", "great")]
    [InlineData("fruits", "great")]
    [InlineData("mania", "perfect")]
    public async Task Calculate_supports_all_official_rulesets(string ruleset, string bestHitResult)
    {
        byte[] beatmap = Encoding.UTF8.GetBytes(TestBeatmap);
        using var factory = new CalculatorFactory(beatmap);
        using HttpClient client = factory.CreateClient();
        JObject metadata = CreateMetadata(beatmap, ruleset, bestHitResult);

        using HttpResponseMessage response = await client.PostAsync("/v1/performance/calculate", CreateRequest(metadata));
        string json = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, json);
        using JsonDocument payload = JsonDocument.Parse(json);
        Assert.True(decimal.Parse(payload.RootElement.GetProperty("difficulty").GetProperty("star_rating").GetString()!) >= 0);
        Assert.True(decimal.Parse(payload.RootElement.GetProperty("performance").GetProperty("pp").GetString()!) >= 0);
    }

    [Fact]
    public async Task Calculate_supports_classic_score_migration()
    {
        byte[] beatmap = Encoding.UTF8.GetBytes(TestBeatmap);
        using var factory = new CalculatorFactory(beatmap);
        using HttpClient client = factory.CreateClient();
        JObject metadata = CreateMetadata(beatmap);
        metadata["client_family"] = "stable";
        metadata["release_configuration"] = JObject.FromObject(new { score_system = "classic" });

        using HttpResponseMessage response = await client.PostAsync("/v1/performance/calculate", CreateRequest(metadata));
        string json = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, json);
    }

    [Fact]
    public async Task Difficulty_mods_are_not_applied_twice_to_performance_score()
    {
        byte[] beatmap = Encoding.UTF8.GetBytes(TestBeatmap);
        using var factory = new CalculatorFactory(beatmap);
        using HttpClient client = factory.CreateClient();
        JObject metadata = CreateMetadata(beatmap);
        metadata["mods"] = new JArray(new JObject { ["acronym"] = "HR" });

        using HttpResponseMessage response = await client.PostAsync("/v1/performance/calculate", CreateRequest(metadata));
        string json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, json);

        using JsonDocument payload = JsonDocument.Parse(json);
        double actual = double.Parse(payload.RootElement.GetProperty("performance").GetProperty("pp").GetString()!);

        var workingBeatmap = new ProcessorWorkingBeatmap(beatmap);
        var ruleset = new OsuRuleset();
        var hardRock = ruleset.CreateModFromAcronym("HR")!;
        var difficulty = ruleset.CreateDifficultyCalculator(workingBeatmap).Calculate([hardRock]);
        var score = new ScoreInfo(workingBeatmap.BeatmapInfo, ruleset.RulesetInfo)
        {
            Mods = [hardRock],
            Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 3, [HitResult.Miss] = 0 },
            Accuracy = 1,
            MaxCombo = 3,
            TotalScore = 1_000_000,
            Passed = true
        };
        LegacyScoreDecoder.PopulateMaximumStatistics(score, workingBeatmap);
        double expected = ruleset.CreatePerformanceCalculator()!.Calculate(score, difficulty).Total;

        Assert.Equal(expected, actual, precision: 10);
    }

    [Fact]
    public async Task Seeded_mods_require_an_explicit_seed_for_deterministic_cache_identity()
    {
        byte[] beatmap = Encoding.UTF8.GetBytes(TestBeatmap);
        using var factory = new CalculatorFactory(beatmap);
        using HttpClient client = factory.CreateClient();
        JObject metadata = CreateMetadata(beatmap);
        metadata["mods"] = new JArray(new JObject { ["acronym"] = "RD" });

        using HttpResponseMessage response = await client.PostAsync("/v1/performance/calculate", CreateRequest(metadata));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, factory.BeatmapRequests);
    }

    [Fact]
    public async Task Nullable_seed_setting_is_rejected_as_a_client_error()
    {
        byte[] beatmap = Encoding.UTF8.GetBytes(TestBeatmap);
        using var factory = new CalculatorFactory(beatmap);
        using HttpClient client = factory.CreateClient();
        JObject metadata = CreateMetadata(beatmap);
        metadata["mods"] = new JArray(new JObject
        {
            ["acronym"] = "RD",
            ["settings"] = new JObject { ["seed"] = JValue.CreateNull() }
        });

        using HttpResponseMessage response = await client.PostAsync("/v1/performance/calculate", CreateRequest(metadata));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Lazer_mod_settings_are_applied_by_the_official_bindable()
    {
        byte[] beatmap = Encoding.UTF8.GetBytes(TestBeatmap);
        using var factory = new CalculatorFactory(beatmap);
        using HttpClient client = factory.CreateClient();
        JObject metadata = CreateMetadata(beatmap);
        metadata["mods"] = new JArray(new JObject
        {
            ["acronym"] = "DT",
            ["settings"] = new JObject { ["speed_change"] = 1.25 }
        });

        using HttpResponseMessage response = await client.PostAsync("/v1/performance/calculate", CreateRequest(metadata));
        string json = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, json);
    }

    [Fact]
    public async Task Calculate_rejects_release_fingerprint_mismatch_without_fetching_beatmap()
    {
        byte[] beatmap = Encoding.UTF8.GetBytes(TestBeatmap);
        using var factory = new CalculatorFactory(beatmap);
        using HttpClient client = factory.CreateClient();
        JObject metadata = CreateMetadata(beatmap);
        metadata["release_version"] = "wrong";

        using HttpResponseMessage response = await client.PostAsync("/v1/performance/calculate", CreateRequest(metadata));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, factory.BeatmapRequests);
    }

    [Fact]
    public async Task Calculate_rejects_additional_multipart_parts()
    {
        byte[] beatmap = Encoding.UTF8.GetBytes(TestBeatmap);
        using var factory = new CalculatorFactory(beatmap);
        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent request = CreateRequest(CreateMetadata(beatmap));
        request.Add(new StringContent("unexpected"), "extra");

        using HttpResponseMessage response = await client.PostAsync("/v1/performance/calculate", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.BeatmapRequests);
    }

    private static MultipartFormDataContent CreateRequest(JObject metadata)
    {
        var request = new MultipartFormDataContent();
        var content = new StringContent(metadata.ToString(Formatting.None), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Add(content, "metadata", "metadata.json");
        return request;
    }

    private static JObject CreateMetadata(byte[] beatmap, string ruleset = "osu", string bestHitResult = "great")
    {
        string beatmapDigest = Convert.ToHexString(SHA256.HashData(beatmap)).ToLowerInvariant();
        return JObject.FromObject(new
        {
            schema_version = 1,
            job_id = Guid.NewGuid(),
            score_id = 1,
            formula_id = Guid.NewGuid(),
            formula_code = "official",
            calculator = "osu-lazer-dotnet",
            release_id = Guid.NewGuid(),
            release_version = "2026.07.1",
            artifact_digest = new string('a', 64),
            release_configuration = new { score_system = "lazer" },
            difficulty_formula_id = Guid.NewGuid(),
            difficulty_formula_code = "official-difficulty",
            difficulty_release_id = Guid.NewGuid(),
            difficulty_release_version = "2026.07.1-difficulty",
            difficulty_artifact_digest = new string('d', 64),
            difficulty_release_configuration = new { },
            input_digest = new string('1', 64),
            beatmap_revision_id = 1,
            beatmap_sha256 = beatmapDigest,
            beatmap_url = "https://beatmaps.test/map.osu?signature=secret",
            ruleset,
            variant = "vanilla",
            mod_set_id = 1,
            mods = Array.Empty<object>(),
            client_family = "lazer",
            score = new
            {
                total_score = 1_000_000,
                classic_score = 1_000_000,
                accuracy = "1.0",
                max_combo = 3,
                outcome = "passed",
                hits = new object[]
                {
                    new { hit_result = bestHitResult, actual = 3, maximum = (int?)null },
                    new { hit_result = "miss", actual = 0, maximum = (int?)null }
                }
            }
        });
    }

    private const string TestBeatmap = """
        osu file format v14

        [General]
        AudioFilename: audio.mp3
        Mode: 0

        [Metadata]
        Title:Contract Test
        Artist:perfcho
        Creator:tests
        Version:Normal

        [Difficulty]
        HPDrainRate:5
        CircleSize:4
        OverallDifficulty:5
        ApproachRate:5
        SliderMultiplier:1.4
        SliderTickRate:1

        [TimingPoints]
        0,500,4,2,1,50,1,0

        [HitObjects]
        128,192,1000,1,0,0:0:0:0:
        256,192,1500,1,0,0:0:0:0:
        384,192,2000,1,0,0:0:0:0:
        """;

    private sealed class CalculatorFactory : WebApplicationFactory<Program>
    {
        private readonly CountingHandler handler;
        private readonly string cacheDirectory = Path.Combine(Path.GetTempPath(), $"perfcho-pp-tests-{Guid.NewGuid():N}");

        public CalculatorFactory(byte[] beatmap)
        {
            handler = new CountingHandler(beatmap);
        }

        public int BeatmapRequests => handler.RequestCount;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:Directory"] = cacheDirectory,
                ["Calculator:MaximumConcurrentCalculations"] = "2"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(handler));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Directory.Exists(cacheDirectory))
                Directory.Delete(cacheDirectory, recursive: true);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CountingHandler(byte[] beatmap) : HttpMessageHandler
    {
        private int requestCount;
        public int RequestCount => requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(beatmap)
            });
        }
    }
}
