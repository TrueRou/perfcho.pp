using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Distributed;
using Perfcho.Performance.Configuration;
using Perfcho.Performance.Infrastructure;
using Perfcho.Performance.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddPerfchoEnvironmentVariables();

builder.Services.AddOptions<CalculatorOptions>()
       .Bind(builder.Configuration.GetSection(CalculatorOptions.SectionName))
       .ValidateOnStart();
builder.Services.AddOptions<CacheOptions>()
       .Bind(builder.Configuration.GetSection(CacheOptions.SectionName))
       .ValidateOnStart();

CalculatorOptions.Validate(builder.Configuration.GetSection(CalculatorOptions.SectionName).Get<CalculatorOptions>() ?? new());
CacheOptions cacheOptions = builder.Configuration.GetSection(CacheOptions.SectionName).Get<CacheOptions>() ?? new();
CacheOptions.Validate(cacheOptions);

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 512 * 1024;
    options.ValueLengthLimit = 512 * 1024;
    options.MultipartHeadersLengthLimit = 8 * 1024;
});
builder.Services.AddMemoryCache(options => options.SizeLimit = cacheOptions.MemorySizeBytes);

if (!string.IsNullOrWhiteSpace(cacheOptions.RedisConnectionString))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = cacheOptions.RedisConnectionString;
        options.InstanceName = cacheOptions.RedisInstanceName;
    });
}
else
{
    builder.Services.AddSingleton<IDistributedCache, NullDistributedCache>();
}

builder.Services.AddHttpClient(BeatmapStore.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(cacheOptions.BeatmapDownloadTimeoutSeconds);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<BeatmapStore>();
builder.Services.AddSingleton<DifficultyCache>();
builder.Services.AddSingleton<CalculationConcurrencyLimiter>();
builder.Services.AddSingleton<MetadataValidator>();
builder.Services.AddSingleton<PerformanceCalculationService>();
builder.Services.AddControllers();

WebApplication app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/v1/capabilities", (Microsoft.Extensions.Options.IOptions<CalculatorOptions> configured) =>
{
    CalculatorOptions options = configured.Value;
    return Results.Ok(new
    {
        calculator = options.Code,
        formula_code = options.FormulaCode,
        release_version = options.ReleaseVersion,
        artifact_digest = options.ArtifactDigest,
        difficulty_formula_code = options.DifficultyFormulaCode,
        difficulty_release_version = options.DifficultyReleaseVersion,
        difficulty_artifact_digest = options.DifficultyArtifactDigest,
        rulesets = new[] { "osu", "taiko", "fruits", "mania" },
        variants = new[] { "vanilla", "relax", "autopilot" },
        osu_package_version = CalculatorOptions.OsuPackageVersion
    });
});
app.MapControllers();
app.Run();

public partial class Program;
