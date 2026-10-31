namespace LlmObservability.Api.Services;

public interface IQueryTranslatorService
{
    /// <param name="naturalLanguageQuery">The user's request in plain language.</param>
    /// <param name="knownTags">Tags currently in use, so the model can map wording onto real tags.</param>
    Task<ODataQueryResult> TranslateAsync(string naturalLanguageQuery, IReadOnlyCollection<string> knownTags);
}
