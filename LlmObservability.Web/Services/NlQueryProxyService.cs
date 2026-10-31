using System.Text;
using System.Text.Json;

namespace LlmObservability.Web.Services;

public record NlQueryResult(
    string ODataUrl,
    string FilterExpression,
    string? OrderBy,
    int? Top,
    string? Expand,
    string? Apply,
    string Explanation,
    List<TextSearchInfo>? TextSearches);

/// <summary>A translator the API can use (Claude or a Microsoft Foundry deployment).</summary>
public record ProviderInfo(string Id, string Label, string Model, bool Configured);

/// <summary>A phrase search the API ran, with the synonymous wordings it also matched.</summary>
public record TextSearchInfo(string Field, string Phrase, bool Exact, List<string> Variants);

public class NlQueryProxyService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<NlQueryProxyService> logger)
{
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Asks the API which translators exist; falls back to Claude only if the API can't be reached.</summary>
    public async Task<List<ProviderInfo>> GetProvidersAsync()
    {
        try
        {
            var baseUrl = configuration["LlmObservabilityApi:BaseUrl"];
            var json    = await httpClientFactory.CreateClient().GetStringAsync($"{baseUrl}/api/naturallanguagequery/providers");
            return JsonSerializer.Deserialize<List<ProviderInfo>>(json, s_json) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load translator providers from the API.");
            return [new ProviderInfo("anthropic", "Claude", "", true)];
        }
    }

    public async Task<NlQueryResult> QueryAsync(string naturalLanguageQuery, string provider = "anthropic")
    {
        var baseUrl = configuration["LlmObservabilityApi:BaseUrl"]
            ?? throw new InvalidOperationException("LlmObservabilityApi:BaseUrl is not configured.");

        var httpClient = httpClientFactory.CreateClient();

        var requestBody = JsonSerializer.Serialize(new { query = naturalLanguageQuery, provider });
        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync($"{baseUrl}/api/naturallanguagequery", content);

        if (!response.IsSuccessStatusCode)
        {
            // The API explains failures as {"error": "..."}; show that rather than a status code.
            var errorBody = await response.Content.ReadAsStringAsync();
            string? message = null;
            try
            {
                using var doc = JsonDocument.Parse(errorBody);
                if (doc.RootElement.TryGetProperty("error", out var e)) message = e.GetString();
            }
            catch (JsonException) { }

            throw new InvalidOperationException(message ?? $"The API returned HTTP {(int)response.StatusCode}.");
        }

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<NlQueryResult>(json, s_json)
            ?? throw new InvalidOperationException("Failed to deserialize NL query result.");

        return result;
    }
}
