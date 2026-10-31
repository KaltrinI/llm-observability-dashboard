using System.Globalization;
using System.Text.Json;
using LlmObservability.Web.Models;
using LlmObservability.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LlmObservability.Web.Controllers;

public class QueryController(
    NlQueryProxyService nlQueryService,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<QueryController> logger) : Controller
{
    [HttpPost]
    public async Task<IActionResult> Submit([FromForm] string query, [FromForm] string provider = "anthropic")
    {
        if (string.IsNullOrWhiteSpace(query))
            return RedirectToAction("Index", "Dashboard");

        var vm = new DashboardViewModel
        {
            NaturalLanguageQuery = query,
            ModelProvider        = provider,
            Providers            = await nlQueryService.GetProvidersAsync(),
        };
        ViewData["ApiBaseUrl"] = configuration["LlmObservabilityApi:BaseUrl"] ?? "https://localhost:5001";

        try
        {
            var nlResult = await nlQueryService.QueryAsync(query, provider);
            vm.FilterExpression = nlResult.FilterExpression;
            vm.ODataUrl         = nlResult.ODataUrl;
            vm.Explanation      = nlResult.Explanation;
            vm.OrderBy          = nlResult.OrderBy;
            vm.Top              = nlResult.Top;
            vm.Expand           = nlResult.Expand;
            vm.Apply            = nlResult.Apply;
            vm.TextSearches     = nlResult.TextSearches ?? [];

            // Fetch the actual OData results to display in the explorer table
            (vm.QueryResults, vm.QueryResultCount, var queryError) = await FetchODataResultsAsync(nlResult.ODataUrl);
            if (queryError is not null)
                vm.ErrorMessage = $"The generated query couldn't be run: {queryError}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process NL query: {Query}", query);
            vm.ErrorMessage = $"Failed to process query: {ex.Message}";
        }

        return View("~/Views/Dashboard/Index.cshtml", vm);
    }

    private async Task<(List<Dictionary<string, string>> Items, int Count, string? Error)> FetchODataResultsAsync(
        string odataRelativeUrl)
    {
        var baseUrl = configuration["LlmObservabilityApi:BaseUrl"];
        if (string.IsNullOrEmpty(baseUrl))
            return ([], 0, "LlmObservabilityApi:BaseUrl is not configured.");

        try
        {
            var client  = httpClientFactory.CreateClient();
            var fullUrl = baseUrl.TrimEnd('/') + odataRelativeUrl;

            var response = await client.GetAsync(fullUrl);
            var json     = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return ([], 0, ReadODataError(json) ?? $"HTTP {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int count = 0;
            if (root.TryGetProperty("@odata.count", out var countProp)
                && countProp.ValueKind == JsonValueKind.Number)
                count = countProp.GetInt32();

            var items = new List<Dictionary<string, string>>();

            if (root.TryGetProperty("value", out var valueProp))
            {
                foreach (var item in valueProp.EnumerateArray())
                {
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    Flatten(item, prefix: "", dict);
                    items.Add(dict);
                }
            }

            return (items, count, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch OData results for display.");
            return ([], 0, ex.Message);
        }
    }

    // OData errors look like {"error":{"message":"..."}}; anything else (e.g. a stack trace) is summarised.
    private static string? ReadODataError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("error").GetProperty("message").GetString();
        }
        catch (Exception)
        {
            var firstLine = body.Split('\n', 2)[0];
            return firstLine.Length > 300 ? firstLine[..300] + "…" : firstLine;
        }
    }

    /// <summary>
    /// Turns one OData row into table columns. Nested objects ($expand, or groupby on a navigation such as
    /// ModelConfiguration/Name) become "Parent/Child" columns; primitive arrays such as Tags are joined.
    /// </summary>
    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> into)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name.StartsWith('@')) continue;   // OData metadata
            var key = prefix + prop.Name;

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Flatten(prop.Value, key + "/", into);
                    break;
                case JsonValueKind.Array:
                    var elements = prop.Value.EnumerateArray().ToList();
                    if (elements.All(e => e.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)))
                        into[key] = string.Join(", ", elements.Select(e => e.ToString()));
                    break;
                case JsonValueKind.Null:
                    into[key] = "";
                    break;
                case JsonValueKind.Number when !prop.Value.TryGetInt64(out _):
                    // Keep costs readable: 1.7730810000000001 → 1.773081
                    into[key] = Math.Round(prop.Value.GetDouble(), 6).ToString("0.######", CultureInfo.InvariantCulture);
                    break;
                default:
                    into[key] = prop.Value.ToString();
                    break;
            }
        }
    }
}
