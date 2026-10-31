using System.Text;
using System.Text.Json;

namespace LlmObservability.Api.Services;

/// <summary>
/// Minimal client for the Anthropic Messages API, shared by the query translator and the auto-tagger.
/// </summary>
public class AnthropicMessagesClient(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<AnthropicMessagesClient> logger)
{
    public string Model => configuration["Anthropic:Model"] ?? "claude-haiku-5-5";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["Anthropic:ApiKey"]);

    /// <summary>
    /// Sends one user message and returns the text of the reply. When <paramref name="jsonSchema"/> is set,
    /// the reply is constrained to that JSON schema (structured outputs), so it always parses.
    /// <paramref name="effort"/> (low | medium | high | xhigh | max) sets how much the model thinks;
    /// leave it null for models without effort support (e.g. Claude Haiku 4.5).
    /// </summary>
    /// <exception cref="HttpRequestException">The API returned an error; <c>StatusCode</c> is set.</exception>
    public async Task<string> CreateMessageAsync(
        string system,
        string userMessage,
        int maxTokens,
        object? jsonSchema = null,
        string? effort = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = configuration["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Anthropic:ApiKey configuration is missing.");

        var body = new Dictionary<string, object>
        {
            ["model"]      = Model,
            ["max_tokens"] = maxTokens,
            ["system"]     = system,
            ["messages"]   = new[] { new { role = "user", content = userMessage } },
        };
        // Thinking tokens count toward max_tokens on models that think by default, so callers leave headroom.
        var outputConfig = new Dictionary<string, object>();
        if (jsonSchema is not null)
            outputConfig["format"] = new { type = "json_schema", schema = jsonSchema };
        if (!string.IsNullOrWhiteSpace(effort))
            outputConfig["effort"] = effort;
        if (outputConfig.Count > 0)
            body["output_config"] = outputConfig;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response     = await httpClientFactory.CreateClient("anthropic").SendAsync(request, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Anthropic API error {Status}: {Body}", (int)response.StatusCode, responseJson);
            throw new HttpRequestException(
                $"Anthropic API returned {(int)response.StatusCode}: {responseJson}", null, response.StatusCode);
        }

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        // A refused or truncated reply won't contain the expected output, so fail loudly instead of parsing it.
        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
        if (stopReason == "refusal")
            throw new InvalidOperationException("The model declined this request (safety refusal).");
        if (stopReason == "max_tokens")
            throw new InvalidOperationException("The model's reply was cut off before it finished (max_tokens).");

        return root.GetProperty("content")
            .EnumerateArray()
            .Where(block => block.GetProperty("type").GetString() == "text")
            .Select(block => block.GetProperty("text").GetString())
            .FirstOrDefault() ?? string.Empty;
    }
}
