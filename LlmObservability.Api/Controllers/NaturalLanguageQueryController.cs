using System.ClientModel;
using LlmObservability.Api.Data;
using LlmObservability.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace LlmObservability.Api.Controllers;

[ApiController]
[Route("api/naturallanguagequery")]
public class NaturalLanguageQueryController(
    [FromKeyedServices("anthropic")] IQueryTranslatorService anthropicTranslator,
    [FromKeyedServices("foundry")]   IQueryTranslatorService foundryTranslator,
    AnthropicMessagesClient anthropic,
    ContentSafetyService contentSafety,
    IConfiguration configuration,
    AppDbContext db) : ControllerBase
{
    public record NlQueryRequest(string Query, string Provider = "anthropic");

    public record ProviderInfo(string Id, string Label, string Model, bool Configured);

    /// <summary>The translators this API can use, with their model names and whether they're configured.</summary>
    [HttpGet("providers")]
    public IEnumerable<ProviderInfo> Providers() =>
    [
        new("anthropic", "Claude", anthropic.Model, anthropic.IsConfigured),
        new("foundry", "Microsoft Foundry",
            FoundryQueryTranslatorService.ModelName(configuration),
            FoundryQueryTranslatorService.IsConfigured(configuration)),
    ];

    /// <summary>
    /// Translates a natural language query into an OData query URL and filter expression.
    /// </summary>
    /// <remarks>
    /// Example request body:
    /// <code>{ "query": "show me all failed interactions from last week that took more than 3 seconds", "provider": "anthropic" }</code>
    /// Set <c>provider</c> to <c>"foundry"</c> to use the model deployed in Microsoft Foundry (AzureAI:Foundry:Model).
    /// <para>
    /// Phrase searches match synonyms by default ("the best product" also matches "the finest product").
    /// Ask for a literal match in words ("exactly", "verbatim") or with the syntax <c>exact:"the best product"</c>.
    /// The response's <c>textSearches</c> lists every wording that was searched.
    /// </para>
    /// </remarks>
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] NlQueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest("Query cannot be null or whitespace.");

        var useFoundry = request.Provider?.Equals("foundry", StringComparison.OrdinalIgnoreCase) == true;
        if (useFoundry && !FoundryQueryTranslatorService.IsConfigured(configuration))
            return UnprocessableEntity(new { error = "Microsoft Foundry is not configured. Set AzureAI:Foundry:Endpoint, ApiKey and Model in the API's appsettings." });

        // Azure AI Content Safety — gracefully skipped when not configured
        ContentSafetyResult safetyResult;
        try
        {
            safetyResult = await contentSafety.AnalyzeAsync(request.Query);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }

        if (!safetyResult.IsSafe)
            return UnprocessableEntity(new { error = safetyResult.BlockReason });

        var translator = useFoundry ? foundryTranslator : anthropicTranslator;
        var knownTags  = await TagVocabulary.GetKnownTagsAsync(db);

        // Model/config problems come back as readable messages instead of a bare 500.
        try
        {
            return Ok(await translator.TranslateAsync(request.Query, knownTags));
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { error = ex.Message });
        }
        catch (ClientResultException ex) when (ex.Status == 400 && ex.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase))
        {
            // Azure applies its own content filter to model deployments
            return UnprocessableEntity(new { error = "Azure's content filter on the model deployment blocked this request." });
        }
        catch (ClientResultException ex)   // Microsoft Foundry (OpenAI SDK): bad key, unknown deployment, …
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"Microsoft Foundry returned HTTP {ex.Status}: {ex.Message}" });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is ClientResultException or HttpRequestException))
        {
            // The OpenAI SDK wraps network failures this way after its own retries (e.g. a wrong resource name)
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"Couldn't reach Microsoft Foundry: {ex.InnerExceptions[0].Message}" });
        }
        catch (HttpRequestException ex)    // Anthropic API error or network failure
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
        catch (System.Text.Json.JsonException ex)
        {
            return UnprocessableEntity(new { error = $"The model's reply wasn't valid JSON: {ex.Message}" });
        }
    }
}
