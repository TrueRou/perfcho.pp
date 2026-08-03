# perfcho.pp

A .NET 8 service that calculates osu! performance points and difficulty attributes through the official osu!lazer ruleset packages. Its HTTP contract matches the external calculator interface used by `perfcho.py`.

## Algorithm Scope

- Pins `ppy.osu.Game` and the Osu, Taiko, Catch, and Mania ruleset packages to `2026.702.1`.
- Supports `osu`, `taiko`, `fruits`, and `mania`, including valid cross-ruleset beatmap conversion.
- Supports the `vanilla`, `relax`, and osu!standard `autopilot` variants.
- Supports lazer mod JSON and mod settings. Unknown mods, unsupported settings, out-of-range values, and incompatible combinations return `422`.
- Supports `lazer` and `classic` through `release_configuration.score_system`. Classic scores use the official stable-to-standardised score migration path.

This project does not copy or reimplement the PP formulas. Star ratings and PP are produced by the pinned official `DifficultyCalculator` and `PerformanceCalculator` implementations.

## HTTP API

```http
POST /v1/performance/calculate
Content-Type: multipart/form-data
```

The request must contain exactly one file part named `metadata`, with filename `metadata.json` and content type `application/json`. The metadata and response schemas follow `perfcho.py/.agent-space/docs/performance-calculation.md`.

Operational endpoints:

- `GET /healthz`
- `GET /v1/capabilities`

## Caching And Concurrency

Raw beatmaps are cached by SHA-256 in memory and under `CACHE_DIRECTORY/beatmaps`. Every downloaded object is verified against `beatmap_sha256` before it is decoded or persisted.

The difficulty cache identity includes:

```text
beatmap SHA-256
osu! package version and DifficultyCalculator.Version
difficulty formula, release, version, and artifact digest
target ruleset and variant
resolved mod acronyms and all non-default settings
```

Difficulty attributes are stored in process memory and optionally in Redis. Concurrent misses for the same key are coalesced into one calculation per process. CPU work is bounded by a `SemaphoreSlim`; the default concurrency is the logical processor count, and saturation returns `429` when no queue timeout is configured.

The calculator does not cache authoritative score or user PP results. `perfcho.py` remains responsible for persisting PP by score and release.

## Configuration

Application settings use explicit uppercase environment variables with single underscores. ASP.NET Core framework variables such as `ASPNETCORE_URLS` keep their standard names. Automatic section-style environment binding is disabled for application settings.

| Environment variable | Description |
| --- | --- |
| `CALCULATOR_CODE` | Calculator identity; defaults to `perfcho-pp` |
| `FORMULA_CODE` | Performance formula code; defaults to `official` |
| `RELEASE_VERSION` | Performance release version; defaults to `2026.07.1` |
| `ARTIFACT_DIGEST` | Performance artifact SHA-256 fallback for every ruleset |
| `ARTIFACT_DIGEST_{OSU,TAIKO,FRUITS,MANIA}` | Optional per-ruleset performance artifact SHA-256 values |
| `DIFFICULTY_FORMULA_CODE` | Difficulty formula code; defaults to `official-difficulty` |
| `DIFFICULTY_RELEASE_VERSION` | Difficulty release version; defaults to `2026.07.1-difficulty` |
| `DIFFICULTY_ARTIFACT_DIGEST` | Difficulty artifact SHA-256 fallback for every ruleset |
| `DIFFICULTY_ARTIFACT_DIGEST_{OSU,TAIKO,FRUITS,MANIA}` | Optional per-ruleset difficulty artifact SHA-256 values |
| `MAXIMUM_CONCURRENT_CALCULATIONS` | CPU calculation limit; `0` uses the logical processor count |
| `CALCULATION_QUEUE_TIMEOUT_MILLISECONDS` | Time to wait for calculation capacity; `0` returns `429` immediately |
| `CACHE_DIRECTORY` | Content-addressed beatmap cache directory |
| `CACHE_MEMORY_SIZE_BYTES` | Total process memory cache size |
| `CACHE_MAXIMUM_BEATMAP_BYTES` | Maximum size of one beatmap |
| `CACHE_MAXIMUM_DISK_BYTES` | Disk cache limit; LRU entries are removed to 90% after the limit is crossed |
| `CACHE_MAXIMUM_CONCURRENT_DOWNLOADS` | Maximum concurrent beatmap downloads |
| `BEATMAP_DOWNLOAD_TIMEOUT_SECONDS` | Timeout covering beatmap headers and response body |
| `DIFFICULTY_CACHE_TTL_HOURS` | Difficulty cache lifetime |
| `BEATMAP_ALLOWED_HOSTS` | Optional comma-separated exact host allowlist for beatmap URLs |
| `REDIS_CONNECTION_STRING` | Optional Redis connection string |
| `REDIS_INSTANCE_NAME` | Redis key prefix |

The Development configuration contains `a...a` and `d...d` digests for local contract testing only. Production does not provide default artifact digests and refuses to start until valid values are configured. When the perfcho Bootstrap catalog is used, configure the four per-ruleset performance and difficulty digests generated for the corresponding active releases. Production releases must use the actual artifact digests registered by the matching perfcho formula releases.

The service does not follow redirects from `beatmap_url`. Production deployments should set `BEATMAP_ALLOWED_HOSTS` and expose the calculator only on an internal network accessible by perfcho workers.

## Local Development

```bash
dotnet restore Perfcho.Performance.sln --locked-mode
dotnet test Perfcho.Performance.sln --configuration Release --no-restore
dotnet run --project src/Perfcho.Performance
```

The Development launch profile listens on `http://127.0.0.1:6001`.

Run with Redis:

```bash
REDIS_CONNECTION_STRING=127.0.0.1:6379 \
  dotnet run --project src/Perfcho.Performance
```

## Docker

```bash
docker build -t perfcho-pp .
docker run --rm -p 6001:6001 \
  -e ARTIFACT_DIGEST=<sha256> \
  -e DIFFICULTY_ARTIFACT_DIGEST=<sha256> \
  -e BEATMAP_ALLOWED_HOSTS=minio.internal \
  perfcho-pp
```
