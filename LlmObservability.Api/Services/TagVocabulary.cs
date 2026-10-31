using System.Text.RegularExpressions;
using LlmObservability.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LlmObservability.Api.Services;

/// <summary>Canonical tag form and the set of tags currently in use.</summary>
public static partial class TagVocabulary
{
    /// <summary>
    /// Tags are matched with <c>eq</c>, so they are stored in one form:
    /// trimmed, lowercase, inner whitespace replaced by '-', no empties or duplicates.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string?>? tags) =>
        (tags ?? [])
            .Select(t => WhitespaceRegex().Replace(t?.Trim().ToLowerInvariant() ?? string.Empty, "-"))
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();

    public static async Task<List<string>> GetKnownTagsAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        // Flattened client-side: simple, provider-agnostic, and cheap at demo scale.
        var tagLists = await db.LlmInteractions.AsNoTracking().Select(i => i.Tags).ToListAsync(cancellationToken);
        return tagLists.SelectMany(t => t).Distinct().Order().ToList();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
