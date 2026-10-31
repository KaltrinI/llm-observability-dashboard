namespace LlmObservability.Api.Models;

public class ModelConfiguration
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public double CostPer1KInputTokens { get; set; }
    public double CostPer1KOutputTokens { get; set; }
    public int MaxContextWindow { get; set; }
    public bool IsActive { get; set; }

    public ICollection<LlmInteraction> Interactions { get; set; } = new List<LlmInteraction>();
}
