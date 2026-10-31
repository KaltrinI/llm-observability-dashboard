using System.Diagnostics;

namespace LlmObservability.Api.Services;

public record ODataQueryResult(
    string ODataUrl,
    string FilterExpression,
    string? OrderBy,
    int? Top,
    string? Expand,
    string? Apply,
    string Explanation,
    IReadOnlyList<TextSearchTerm> TextSearches);

public class ODataQueryTranslatorService(
    AnthropicMessagesClient anthropic,
    IConfiguration configuration,
    ILogger<ODataQueryTranslatorService> logger) : IQueryTranslatorService
{
    private static readonly ActivitySource s_activity = new("LlmObservability.NLQuery");

    public async Task<ODataQueryResult> TranslateAsync(
        string naturalLanguageQuery, IReadOnlyCollection<string> knownTags)
    {
        using var activity = s_activity.StartActivity("TranslateNLQuery.Anthropic");
        activity?.SetTag("ai.provider", "anthropic");
        activity?.SetTag("ai.model", anthropic.Model);
        activity?.SetTag("query.text", naturalLanguageQuery);

        logger.LogInformation("Sending NL query to Anthropic (model={Model}): {Query}", anthropic.Model, naturalLanguageQuery);

        var rawText = await anthropic.CreateMessageAsync(
            QueryTranslatorHelper.BuildSystemPrompt(knownTags, DateTimeOffset.UtcNow),
            naturalLanguageQuery,
            maxTokens: 16000,
            effort: configuration["Anthropic:TranslatorEffort"]);

        var result = QueryTranslatorHelper.ParseResponse(rawText, naturalLanguageQuery);

        activity?.SetTag("odata.url", result.ODataUrl);
        logger.LogInformation("Generated OData URL: {Url}", result.ODataUrl);

        return result;
    }
}
