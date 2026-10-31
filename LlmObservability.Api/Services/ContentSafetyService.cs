using Azure;
using Azure.AI.ContentSafety;

namespace LlmObservability.Api.Services;

public record ContentSafetyResult(bool IsSafe, string? BlockReason);

public class ContentSafetyService(IConfiguration configuration, ILogger<ContentSafetyService> logger)
{
    public async Task<ContentSafetyResult> AnalyzeAsync(string text)
    {
        var endpoint = configuration["AzureAI:ContentSafety:Endpoint"];
        var apiKey   = configuration["AzureAI:ContentSafety:ApiKey"];

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey)
            || endpoint.StartsWith("YOUR_"))
        {
            logger.LogDebug("Content Safety not configured — skipping check.");
            return new ContentSafetyResult(true, null);
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            throw new InvalidOperationException(
                $"AzureAI:ContentSafety:Endpoint isn't a valid URL (got '{endpoint}'). Expected https://<resource>.cognitiveservices.azure.com/");

        // A misconfigured check should say so, not surface as a bare HTTP 500 on every question.
        Response<AnalyzeTextResult> response;
        try
        {
            var client = new ContentSafetyClient(endpointUri, new AzureKeyCredential(apiKey));
            response   = await client.AnalyzeTextAsync(new AnalyzeTextOptions(text));
        }
        catch (RequestFailedException ex)
        {
            throw new InvalidOperationException(
                $"Azure Content Safety check failed (HTTP {ex.Status}, {ex.ErrorCode ?? "no error code"}). " +
                "Check AzureAI:ContentSafety:Endpoint and ApiKey.", ex);
        }
        catch (AggregateException ex)   // Azure SDK gives up after its retries, e.g. a misspelled resource name
        {
            throw new InvalidOperationException(
                $"Couldn't reach Azure Content Safety: {ex.InnerExceptions[0].Message}. Check AzureAI:ContentSafety:Endpoint.", ex);
        }

        var flagged = response.Value.CategoriesAnalysis
            .Where(c => c.Severity > 2)
            .Select(c => $"{c.Category} (severity {c.Severity})")
            .ToList();

        if (flagged.Count > 0)
        {
            logger.LogWarning("Content Safety flagged query: {Flags}", string.Join(", ", flagged));
            return new ContentSafetyResult(false, $"Query blocked by Azure Content Safety: {string.Join(", ", flagged)}");
        }

        logger.LogInformation("Content Safety check passed for query.");
        return new ContentSafetyResult(true, null);
    }
}
