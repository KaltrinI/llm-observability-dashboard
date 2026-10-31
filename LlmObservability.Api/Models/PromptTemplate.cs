namespace LlmObservability.Api.Models;

public class PromptTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }

    public ICollection<LlmInteraction> Interactions { get; set; } = new List<LlmInteraction>();
}
