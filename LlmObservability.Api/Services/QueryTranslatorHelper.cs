using System.Text.Json;

namespace LlmObservability.Api.Services;

internal static class QueryTranslatorHelper
{
    /// <summary>
    /// Builds the translator system prompt. The current date and tag vocabulary are appended last
    /// so the static part of the prompt stays identical across requests.
    /// </summary>
    internal static string BuildSystemPrompt(IReadOnlyCollection<string> knownTags, DateTimeOffset now)
    {
        var utcNow = now.ToUniversalTime();
        var dateLine =
            $"Current date and time (UTC): {utcNow:yyyy-MM-dd'T'HH:mm}Z, a {utcNow:dddd}. " +
            "Resolve relative dates from it: \"today\" starts at 00:00Z today, weeks start on Monday, " +
            "\"last N days\" means CreatedAt ge (now minus N days). All timestamps are UTC.";

        var tagLine = knownTags.Count == 0
            ? "Known tags: (none recorded yet — do not filter on Tags)"
            : "Known tags: " + string.Join(", ", knownTags.Take(MaxTagsInPrompt));

        return BaseSystemPrompt + "\n" + dateLine + "\n" + tagLine;
    }

    private const int MaxTagsInPrompt = 200;
    private const int DefaultTop      = 50;

    private const string BaseSystemPrompt =
        """
        You are an OData query expert. The user will describe what data they want in natural language.
        Translate it into an OData query for the following entity model:

        Entity: LlmInteractions (primary queryable collection)
        Fields: Id (int), UserId (string), SessionId (string), UserPrompt (string), Completion (string),
        InputTokens (int), OutputTokens (int), LatencyMs (double, milliseconds), Cost (double, USD),
        Success (bool), ErrorMessage (string?), ToolCallsUsed (string?), CreatedAt (DateTimeOffset),
        CreatedDay (string 'yyyy-MM-dd', the UTC day of CreatedAt — use it to group by day),
        ModelConfigurationId (int), PromptTemplateId (int?), Tags (collection of lowercase strings)
        Navigations: ModelConfiguration, PromptTemplate, Feedback — all single-valued (Feedback is null when the
        interaction has no feedback). Address their fields directly, e.g. Feedback/ThumbsRating eq -1;
        never use any()/all() on them. Tags is the only collection.

        Entity: ModelConfigurations
        Fields: Id (int), Name (string), Provider (string), CostPer1KInputTokens (double),
        CostPer1KOutputTokens (double), MaxContextWindow (int), IsActive (bool)

        Entity: PromptTemplates
        Fields: Id (int), Name (string), Category (string), Tags (string), CreatedAt (DateTime), IsActive (bool)

        Entity: UserFeedbacks
        Fields: Id (int), LlmInteractionId (int), ThumbsRating (int: 1=up, -1=down),
        SentimentScore (double?), Comment (string?), CreatedAt (DateTime)

        Base collection URL: /odata/LlmInteractions

        Filter syntax notes:
        - Boolean: Success eq true / Success eq false
        - Null check: ErrorMessage ne null / ToolCallsUsed ne null
        - DateTime: CreatedAt ge 2024-04-01T00:00:00Z
        - String equality: use single quotes e.g. UserId eq 'user-001'
        - Compound: LatencyMs gt 5000 and Success eq false
        - Negation: not (Success eq true)
        - String contains on identifier-like fields: contains(ToolCallsUsed,'web_search'), contains(UserId,'00')
        - String starts with: startswith(UserId,'user-00')

        Tags — a collection; filter with a lambda:
        - Has tag: Tags/any(t: t eq 'pricing')
        - Has any of several: Tags/any(t: t eq 'pricing' or t eq 'sales')
        - Has all of several: Tags/any(t: t eq 'pricing') and Tags/any(t: t eq 'sales')
        - Untagged: not Tags/any()
        - Tags are a controlled vocabulary: only use tags from the "Known tags" list at the end of this prompt,
          mapping the user's wording to the closest tag(s) by meaning (e.g. "money questions" → 'pricing').
          If no known tag fits, use a text search on UserPrompt instead.
        - Tags cannot be used inside groupby.

        Text and phrase search — fields UserPrompt, Completion, ErrorMessage, Feedback/Comment:
        Do NOT write contains() for these fields yourself. Add an entry to "textSearches" and put its placeholder
        {{<id>}} where the condition belongs: in filterExpression, or inside the $apply filter(...).
        The server expands each placeholder into a case-insensitive OR of contains() calls.
        - "phrase" is the shortest core wording that carries the meaning and would appear verbatim in the text
          ("feedback saying answers were too long" → phrase "too long", not "answers were too long").
        - By default the user means the idea, not the literal wording. Set "exact": false and give "variants":
          alternative wordings with the same meaning that could literally appear in the text, made by swapping key
          words for common synonyms. Example: "the best product" → ["the greatest product", "the finest product",
          "the top product", "the best item"]. 3–8 variants, lowercase, short (they are matched as substrings),
          no wildcards, do not repeat the original phrase. Matching is by substring, so add inflected forms that
          are not substrings of each other. Single words get variants too
          ("refund" → ["reimbursement", "reimbursed", "money back"]). Terms with no real synonyms
          (technical terms, names, brands) get an empty variants list.
        - Every variant must be specific enough that a match almost always means the same thing. Skip everyday
          words with other common meanings: for "refund", not "return" (it also matches "returns wrong results").
        - Putting a phrase in quotes does NOT make it exact — users quote phrases just to mark them.
        - Set "exact": true and "variants": [] ONLY when the user explicitly asks for a literal match, e.g.
          "exactly", "exact phrase", "exact match", "verbatim", "literally", "word for word", "only that phrase",
          "no synonyms", or the syntax exact:"...".
        - Use ids t0, t1, t2, ... in order. Each placeholder is a complete boolean condition.
        Examples:
          "how many prompts mention the best product" →
            "apply": "filter({{t0}})/aggregate($count as Count)",
            "textSearches": [{"id": "t0", "field": "UserPrompt", "phrase": "the best product", "exact": false,
                              "variants": ["the greatest product", "the finest product", "the top product"]}]
          "failed calls whose prompt contains exactly 'the best product'" →
            "filterExpression": "Success eq false and {{t0}}",
            "textSearches": [{"id": "t0", "field": "UserPrompt", "phrase": "the best product", "exact": true, "variants": []}]

        $apply — use for aggregations and grouping (when the user wants counts, sums, averages, or grouped results):
        - Count all: $apply=aggregate($count as Count)
        - Count matching a phrase: $apply=filter({{t0}})/aggregate($count as Count)   (plus a textSearches entry)
        - Count by tag: $apply=filter(Tags/any(t: t eq 'pricing'))/aggregate($count as Count)
        - Group by a field: $apply=groupby((Success),aggregate($count as Count))
        - Group by model — always by name so results are readable:
          $apply=groupby((ModelConfiguration/Name),aggregate($count as Count,Cost with sum as TotalCost,Cost with average as AvgCost))
        - Group by day: $apply=filter(Success eq false)/groupby((CreatedDay),aggregate($count as Failures))
        - Filter then aggregate: $apply=filter(Success eq false)/aggregate(Cost with sum as TotalWastedCost,$count as FailedCount)
        - Aggregate functions: $count, sum, average, min, max
        - Sort or limit grouped results with the separate orderBy/top fields, using the aggregate's alias:
          "which day had the most failures" → apply "filter(Success eq false)/groupby((CreatedDay),aggregate($count as Failures))",
          orderBy "Failures desc", top 1. orderby()/top() inside $apply are NOT supported.
        - Never set expand together with apply (it is ignored); group by navigation paths like ModelConfiguration/Name instead.
        - Other date functions (date(), year(), format) are not supported in groupby; use CreatedDay.

        IMPORTANT: use $apply (not $filter+$count) when the user asks for totals, averages, sums, or grouped breakdowns.
        Use $filter (not $apply) when the user wants to see the actual records.
        Never combine $apply with filterExpression in the same query — put the filter inside the $apply pipeline instead.

        Respond ONLY with a valid JSON object — no markdown, no preamble:
        {
          "filterExpression": "<$filter value or null — omit if using apply>",
          "orderBy": "<$orderby value or null>",
          "top": <integer or null>,
          "expand": "<$expand value or null>",
          "apply": "<$apply expression or null — use for aggregations/grouping>",
          "textSearches": [
            { "id": "t0", "field": "<UserPrompt|Completion|ErrorMessage|Feedback/Comment>", "phrase": "<what the user asked for>",
              "exact": <true|false>, "variants": ["<synonymous wording>", ...] }
          ],
          "explanation": "<one sentence describing what the query retrieves; mention when synonyms are included>"
        }
        Use "textSearches": [] when the query has no text search.
        """;

    internal static ODataQueryResult ParseResponse(string rawText, string originalQuery)
    {
        rawText = rawText.Trim();
        if (rawText.StartsWith("```"))
        {
            var firstNewline = rawText.IndexOf('\n');
            var lastFence    = rawText.LastIndexOf("```");
            if (firstNewline >= 0 && lastFence > firstNewline)
                rawText = rawText[(firstNewline + 1)..lastFence].Trim();
        }

        using var doc  = ParseModelJson(rawText);
        var root       = doc.RootElement;

        var filterExpression = GetStringOrEmpty(root, "filterExpression");
        var orderBy          = GetStringOrNull(root, "orderBy");
        var expand           = GetStringOrNull(root, "expand");
        var apply            = GetStringOrNull(root, "apply");
        var explanation      = GetStringOrEmpty(root, "explanation");

        int? top = null;
        if (root.TryGetProperty("top", out var topProp) && topProp.ValueKind == JsonValueKind.Number)
            top = topProp.GetInt32();

        var (expandedFilter, expandedApply, textSearches) = TextSearchExpansion.Expand(
            filterExpression, apply, ParseTextSearches(root), originalQuery);

        var url = BuildODataUrl(expandedFilter, orderBy, top, expand, expandedApply);
        return new ODataQueryResult(
            url, expandedFilter, orderBy, top, expand, expandedApply, explanation, textSearches);
    }

    // A reply that isn't JSON is usually the model declining ("I can't help with that…"), so show its own words.
    private static JsonDocument ParseModelJson(string rawText)
    {
        try
        {
            return JsonDocument.Parse(rawText);
        }
        catch (JsonException)
        {
            var reply = string.Join(' ', rawText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (reply.Length == 0)
                throw new InvalidOperationException("The model returned an empty reply instead of a query.");
            if (reply.Length > 200) reply = reply[..200] + "…";
            throw new InvalidOperationException($"The model declined or didn't return a query. Its reply: \"{reply}\"");
        }
    }

    private static List<TextSearchTerm> ParseTextSearches(JsonElement root)
    {
        var terms = new List<TextSearchTerm>();
        if (!root.TryGetProperty("textSearches", out var array) || array.ValueKind != JsonValueKind.Array)
            return terms;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var variants = item.TryGetProperty("variants", out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString() ?? string.Empty)
                    .ToList()
                : [];

            terms.Add(new TextSearchTerm(
                Id:       GetStringOrEmpty(item, "id"),
                Field:    GetStringOrEmpty(item, "field"),
                Phrase:   GetStringOrEmpty(item, "phrase"),
                Exact:    item.TryGetProperty("exact", out var e) && e.ValueKind == JsonValueKind.True,
                Variants: variants));
        }

        return terms;
    }

    internal static string BuildODataUrl(
        string filterExpression, string? orderBy, int? top, string? expand, string? apply)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(apply))
        {
            parts.Add($"$apply={Uri.EscapeDataString(apply)}");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(filterExpression))
                parts.Add($"$filter={Uri.EscapeDataString(filterExpression)}");
        }

        if (!string.IsNullOrWhiteSpace(orderBy))
            parts.Add($"$orderby={Uri.EscapeDataString(orderBy)}");

        // Record lists get a default page; aggregations return few rows and are left alone.
        var effectiveTop = top ?? (string.IsNullOrWhiteSpace(apply) ? DefaultTop : null);
        if (effectiveTop.HasValue)
            parts.Add($"$top={effectiveTop.Value}");

        if (!string.IsNullOrWhiteSpace(expand))
            parts.Add($"$expand={Uri.EscapeDataString(expand)}");

        parts.Add("$count=true");

        return "/odata/LlmInteractions?" + string.Join("&", parts);
    }

    private static string GetStringOrEmpty(JsonElement root, string key) =>
        root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;

    private static string? GetStringOrNull(JsonElement root, string key) =>
        root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
