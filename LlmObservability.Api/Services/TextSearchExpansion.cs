using System.Text.RegularExpressions;

namespace LlmObservability.Api.Services;

/// <summary>
/// One phrase the user wants to find in a free-text field.
/// Unless <see cref="Exact"/> is set, the phrase also matches each of its <see cref="Variants"/>
/// (synonymous wordings such as "the finest product" for "the best product").
/// </summary>
public record TextSearchTerm(
    string Id,
    string Field,
    string Phrase,
    bool Exact,
    IReadOnlyList<string> Variants);

/// <summary>
/// Turns the translator's <c>{{id}}</c> placeholders into OData <c>contains()</c> clauses.
/// The LLM decides what the user meant (which phrase, which field, exact or not, which synonyms);
/// this class owns how that becomes OData, so escaping, casing and limits are enforced in one place.
/// </summary>
internal static partial class TextSearchExpansion
{
    internal const int MaxVariants = 10;

    // Free-text fields where synonym matching makes sense, keyed by the names the LLM may use.
    private static readonly Dictionary<string, string> s_fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UserPrompt"]       = "UserPrompt",
        ["Completion"]       = "Completion",
        ["ErrorMessage"]     = "ErrorMessage",
        ["Feedback/Comment"] = "Feedback/Comment",
        ["Comment"]          = "Feedback/Comment",
    };

    [GeneratedRegex(@"\{\{\s*(\w+)\s*\}\}")]
    private static partial Regex PlaceholderRegex();

    // Deterministic opt-out the user can type: exact:"the best product"
    [GeneratedRegex(@"exact:\s*[""“”]([^""“”]+)[""“”]", RegexOptions.IgnoreCase)]
    private static partial Regex ExactSyntaxRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Replaces every <c>{{id}}</c> in <paramref name="filter"/> and <paramref name="apply"/> with the
    /// expanded clause for that term. Returns only the terms that were actually used, so callers can
    /// show the user exactly which wordings were searched.
    /// </summary>
    internal static (string Filter, string? Apply, IReadOnlyList<TextSearchTerm> Applied) Expand(
        string filter,
        string? apply,
        IReadOnlyList<TextSearchTerm> rawTerms,
        string originalQuery)
    {
        var forcedExact = ExactSyntaxRegex().Matches(originalQuery)
            .Select(m => NormalizePhrase(m.Groups[1].Value))
            .ToHashSet();

        var terms = rawTerms
            .Select(t => Normalize(t, forcedExact))
            .Where(t => t is not null)
            .Select(t => t!)
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var used = new List<TextSearchTerm>();

        string Substitute(string expression) =>
            PlaceholderRegex().Replace(expression, match =>
            {
                var id = match.Groups[1].Value;
                if (!terms.TryGetValue(id, out var term))
                    throw new InvalidOperationException(
                        $"The translated query references an unknown text search '{{{{{id}}}}}'.");

                if (!used.Contains(term)) used.Add(term);
                return BuildClause(term);
            });

        var expandedFilter = string.IsNullOrEmpty(filter) ? filter : Substitute(filter);
        var expandedApply  = string.IsNullOrEmpty(apply)  ? apply  : Substitute(apply);

        return (expandedFilter, expandedApply, used);
    }

    internal static string BuildClause(TextSearchTerm term)
    {
        var field   = s_fields[term.Field];
        var clauses = new[] { term.Phrase }
            .Concat(term.Variants)
            .Select(p => $"contains(tolower({field}),'{EscapeLiteral(p)}')")
            .ToList();

        return clauses.Count == 1 ? clauses[0] : "(" + string.Join(" or ", clauses) + ")";
    }

    private static TextSearchTerm? Normalize(TextSearchTerm raw, IReadOnlySet<string> forcedExact)
    {
        var phrase = NormalizePhrase(raw.Phrase);
        if (phrase.Length == 0 || string.IsNullOrWhiteSpace(raw.Id))
            return null;

        var fieldKey = string.IsNullOrWhiteSpace(raw.Field) ? "UserPrompt" : raw.Field.Trim();
        if (!s_fields.TryGetValue(fieldKey, out var field))
            throw new InvalidOperationException(
                $"Text search is not supported on field '{raw.Field}'. " +
                $"Supported fields: {string.Join(", ", s_fields.Values.Distinct())}.");

        var exact = raw.Exact || forcedExact.Contains(phrase);

        var variants = exact
            ? []
            : raw.Variants
                .Select(NormalizePhrase)
                .Where(v => v.Length > 0 && v != phrase)
                .Distinct()
                .Take(MaxVariants)
                .ToList();

        return new TextSearchTerm(raw.Id.Trim(), field, phrase, exact, variants);
    }

    // Matching is done against tolower(field), so phrases are lowercased to line up.
    private static string NormalizePhrase(string? value) =>
        WhitespaceRegex().Replace(value ?? string.Empty, " ").Trim().ToLowerInvariant();

    // OData string literals escape a single quote by doubling it.
    private static string EscapeLiteral(string value) => value.Replace("'", "''");
}
