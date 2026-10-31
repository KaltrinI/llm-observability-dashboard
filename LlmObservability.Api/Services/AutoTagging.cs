using System.Net;
using System.Threading.Channels;
using LlmObservability.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LlmObservability.Api.Services;

/// <summary>
/// Interactions waiting to be tagged. Ingestion only enqueues an id, so saving an interaction
/// never waits on, or fails because of, the LLM.
/// </summary>
public class TaggingQueue(
    IConfiguration configuration,
    AnthropicMessagesClient anthropic,
    ILogger<TaggingQueue> logger)
{
    private readonly Channel<int> _channel = Channel.CreateBounded<int>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    public bool IsEnabled =>
        configuration.GetValue("AutoTagging:Enabled", true) && anthropic.IsConfigured;

    /// <returns><c>false</c> when auto-tagging is off or the queue is full.</returns>
    public bool TryEnqueue(int interactionId)
    {
        if (!IsEnabled) return false;

        if (_channel.Writer.TryWrite(interactionId)) return true;

        logger.LogWarning("Tagging queue is full; interaction {Id} stays untagged.", interactionId);
        return false;
    }

    internal IAsyncEnumerable<int> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>Tags queued interactions one at a time, retrying transient API failures.</summary>
public class AutoTaggingWorker(
    TaggingQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<AutoTaggingWorker> logger) : BackgroundService
{
    private const int MaxAttempts = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var id in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await TagWithRetryAsync(id, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Auto-tagging failed for interaction {Id}; it stays untagged.", id);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task TagWithRetryAsync(int id, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await TagAsync(id, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogInformation(
                    "Auto-tagging interaction {Id} failed ({Error}); retry {Attempt} in {Delay}s.",
                    id, ex.Message, attempt, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task TagAsync(int id, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db     = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tagger = scope.ServiceProvider.GetRequiredService<IInteractionTagger>();

        var interaction = await db.LlmInteractions.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

        // Gone, already tagged in the meantime, or nothing to classify.
        if (interaction is null || interaction.Tags.Count > 0 || string.IsNullOrWhiteSpace(interaction.UserPrompt))
            return;

        var knownTags = await TagVocabulary.GetKnownTagsAsync(db, cancellationToken);
        var tags      = await tagger.SuggestTagsAsync(interaction.UserPrompt, knownTags, cancellationToken);
        if (tags.Count == 0) return;

        interaction.Tags = [.. tags];
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Auto-tagged interaction {Id}: {Tags}", id, string.Join(", ", tags));
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null }       => true,  // network failure
        HttpRequestException { StatusCode: var status } => status == HttpStatusCode.TooManyRequests || (int)status >= 500,
        TaskCanceledException                           => true,  // HttpClient timeout
        _                                               => false,
    };
}
