using System.ClientModel;
using System.Diagnostics;
using OpenAI;
using OpenAI.Chat;

namespace LlmObservability.Api.Services;

/// <summary>
/// NL → OData through any chat model deployed in Microsoft Foundry (Phi-4, DeepSeek, Llama, Mistral, GPT, …).
/// Uses Foundry's OpenAI-compatible v1 endpoint with the OpenAI SDK, which replaced the retired
/// Azure AI Inference SDK. Switch models by changing <c>AzureAI:Foundry:Model</c> to another deployment name.
/// </summary>
public class FoundryQueryTranslatorService(
    IConfiguration configuration,
    ILogger<FoundryQueryTranslatorService> logger) : IQueryTranslatorService
{
    private static readonly ActivitySource s_activity = new("LlmObservability.NLQuery");

    public static bool IsConfigured(IConfiguration configuration) =>
        IsSet(configuration["AzureAI:Foundry:Endpoint"]) && IsSet(configuration["AzureAI:Foundry:ApiKey"]);

    /// <summary>The Foundry deployment name, e.g. "Phi-4".</summary>
    public static string ModelName(IConfiguration configuration) =>
        configuration["AzureAI:Foundry:Model"] is { Length: > 0 } model ? model : "Phi-4";

    public async Task<ODataQueryResult> TranslateAsync(
        string naturalLanguageQuery, IReadOnlyCollection<string> knownTags)
    {
        if (!IsConfigured(configuration))
            throw new InvalidOperationException(
                "Microsoft Foundry is not configured. Set AzureAI:Foundry:Endpoint, ApiKey and Model in the API's appsettings.");

        var endpoint = configuration["AzureAI:Foundry:Endpoint"]!;
        if (!endpoint.Contains("/openai/v1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"AzureAI:Foundry:Endpoint must be the OpenAI v1 endpoint, e.g. https://<resource>.openai.azure.com/openai/v1/ (got {endpoint}).");

        var model = ModelName(configuration);

        using var activity = s_activity.StartActivity("TranslateNLQuery.Foundry");
        activity?.SetTag("ai.provider", "microsoft-foundry");
        activity?.SetTag("ai.model", model);
        activity?.SetTag("query.text", naturalLanguageQuery);

        var client = new ChatClient(
            model: model,
            credential: new ApiKeyCredential(configuration["AzureAI:Foundry:ApiKey"]!),
            options: new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

        logger.LogInformation("Sending NL query to Microsoft Foundry ({Model}): {Query}", model, naturalLanguageQuery);

        ChatCompletion completion = await client.CompleteChatAsync(
        [
            new SystemChatMessage(QueryTranslatorHelper.BuildSystemPrompt(knownTags, DateTimeOffset.UtcNow)),
            new UserChatMessage(naturalLanguageQuery),
        ]);

        var rawText = string.Concat(completion.Content.Select(part => part.Text));
        var result  = QueryTranslatorHelper.ParseResponse(rawText, naturalLanguageQuery);

        activity?.SetTag("odata.url", result.ODataUrl);
        logger.LogInformation("Foundry ({Model}) generated OData URL: {Url}", model, result.ODataUrl);

        return result;
    }

    private static bool IsSet(string? value) => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("YOUR_");
}
