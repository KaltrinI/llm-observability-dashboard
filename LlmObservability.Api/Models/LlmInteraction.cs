using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace LlmObservability.Api.Models;

public class LlmInteraction
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string UserPrompt { get; set; } = string.Empty;
    public string Completion { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double LatencyMs { get; set; }
    public double Cost { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ToolCallsUsed { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC calendar day of <see cref="CreatedAt"/> ("yyyy-MM-dd"), computed by the database. Group by it for per-day stats.</summary>
    public string CreatedDay { get; set; } = string.Empty;

    /// <summary>
    /// Free-form labels for the interaction (lowercase, e.g. "pricing", "product").
    /// Query with a lambda: <c>$filter=Tags/any(t: t eq 'pricing')</c>.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    public int ModelConfigurationId { get; set; }
    public int? PromptTemplateId { get; set; }

    // Ingestion only sends ModelConfigurationId; don't require the navigation object in the request body.
    [ValidateNever]
    public ModelConfiguration ModelConfiguration { get; set; } = null!;
    public PromptTemplate? PromptTemplate { get; set; }
    public UserFeedback? Feedback { get; set; }
}
