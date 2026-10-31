namespace LlmObservability.Api.Models;

public class UserFeedback
{
    public int Id { get; set; }
    public int LlmInteractionId { get; set; }

    /// <summary>1 = thumbs up, -1 = thumbs down.</summary>
    public int ThumbsRating { get; set; }

    /// <summary>Sentiment score in the range [-1.0, 1.0].</summary>
    public double? SentimentScore { get; set; }

    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }

    public LlmInteraction LlmInteraction { get; set; } = null!;
}
