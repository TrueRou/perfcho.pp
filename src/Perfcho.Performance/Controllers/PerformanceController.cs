using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Perfcho.Performance.Configuration;
using Perfcho.Performance.Contracts;
using Perfcho.Performance.Services;

namespace Perfcho.Performance.Controllers;

[ApiController]
[Route("v1/performance")]
public sealed class PerformanceController(
    PerformanceCalculationService calculationService,
    IOptions<CalculatorOptions> configured,
    ILogger<PerformanceController> logger) : ControllerBase
{
    private const int maximum_metadata_bytes = 256 * 1024;

    private static readonly JsonSerializerSettings serializerSettings = new()
    {
        MissingMemberHandling = MissingMemberHandling.Error,
        DateParseHandling = DateParseHandling.None,
        FloatParseHandling = FloatParseHandling.Double,
        MaxDepth = 64
    };

    [HttpPost("calculate")]
    [Consumes("multipart/form-data")]
    [Produces("application/json")]
    public async Task<IActionResult> Calculate(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Received performance calculation request. TraceIdentifier={TraceIdentifier}, ContentType={ContentType}, ContentLength={ContentLength}.",
            HttpContext.TraceIdentifier,
            Request.ContentType,
            Request.ContentLength);
        PerformanceMetadata? metadata = null;
        try
        {
            metadata = await ReadMetadataAsync(cancellationToken).ConfigureAwait(false);
            CalculationResult result = await calculationService.CalculateAsync(metadata, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Returning successful performance calculation response. StarRating={StarRating:R}, MaxCombo={MaxCombo}, PerformancePoints={PerformancePoints:R}.",
                result.StarRating,
                result.MaxCombo,
                result.PerformancePoints);
            CalculatorOptions options = configured.Value;
            return new JsonResult(new
            {
                schema_version = 1,
                calculator = options.Code,
                release_version = options.ReleaseVersion,
                difficulty_release_version = options.DifficultyReleaseVersion,
                input_digest = metadata.InputDigest,
                difficulty = new
                {
                    star_rating = FormatDecimal(result.StarRating),
                    max_combo = result.MaxCombo,
                    attributes = result.DifficultyAttributes
                },
                performance = new
                {
                    pp = FormatDecimal(result.PerformancePoints),
                    breakdown = result.PerformanceBreakdown
                }
            });
        }
        catch (CalculatorException exception)
        {
            if (exception.StatusCode >= 500)
                logger.LogError(exception, "Calculation failed for score {ScoreId}.", metadata?.ScoreId);
            else
                logger.LogInformation("Calculation rejected for score {ScoreId}: {Code}. Reason: {Reason}", metadata?.ScoreId, exception.Code, exception.Message);

            if (exception.StatusCode == StatusCodes.Status429TooManyRequests)
                Response.Headers.RetryAfter = "1";
            var problem = new ProblemDetails
            {
                Status = exception.StatusCode,
                Title = exception.Message
            };
            problem.Extensions["code"] = exception.Code;
            return new ObjectResult(problem) { StatusCode = exception.StatusCode };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled calculation failure for score {ScoreId}.", metadata?.ScoreId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Calculator failed unexpectedly.");
        }
    }

    private async Task<PerformanceMetadata> ReadMetadataAsync(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType || Request.ContentType is null ||
            !Request.ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new CalculatorException(StatusCodes.Status400BadRequest, "invalid_multipart", "Content-Type must be multipart/form-data.");
        }

        IFormCollection form;
        try
        {
            form = await Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw new CalculatorException(StatusCodes.Status400BadRequest, "invalid_multipart", "Multipart body is invalid or too large.", exception);
        }

        if (form.Count != 0 || form.Files.Count != 1)
            throw new CalculatorException(StatusCodes.Status400BadRequest, "invalid_multipart", "Request must contain exactly one metadata file part and no fields.");

        IFormFile file = form.Files[0];
        if (file.Name != "metadata" || file.FileName != "metadata.json" ||
            !string.Equals(file.ContentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new CalculatorException(StatusCodes.Status400BadRequest, "invalid_multipart", "The only part must be metadata.json with application/json content type.");
        }
        if (file.Length is <= 0 or > maximum_metadata_bytes)
            throw new CalculatorException(StatusCodes.Status400BadRequest, "invalid_metadata", "Metadata is empty or too large.");

        try
        {
            await using Stream stream = file.OpenReadStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            string json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Received raw calculation metadata. TraceIdentifier={TraceIdentifier}, MetadataLength={MetadataLength}, Metadata={Metadata}.",
                HttpContext.TraceIdentifier,
                json.Length,
                json);
            return JsonConvert.DeserializeObject<PerformanceMetadata>(json, serializerSettings) ??
                   throw new JsonSerializationException("Metadata must be a JSON object.");
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or IOException)
        {
            throw new CalculatorException(StatusCodes.Status400BadRequest, "invalid_metadata", "Metadata is not valid contract JSON.", exception);
        }
    }

    private static string FormatDecimal(double value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);
}
