using LlmObservability.Api.Data;
using LlmObservability.Api.Models;
using LlmObservability.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;

namespace LlmObservability.Api.Controllers;

/// <summary>
/// OData endpoint for LLM interactions.
/// <para>Example queries:</para>
/// <list type="bullet">
///   <item><c>GET /odata/LlmInteractions?$filter=Success eq false</c></item>
///   <item><c>GET /odata/LlmInteractions?$filter=LatencyMs gt 5000 and Success eq true</c></item>
///   <item><c>GET /odata/LlmInteractions?$expand=ModelConfiguration,Feedback</c></item>
///   <item><c>GET /odata/LlmInteractions?$select=Id,UserId,Cost,Success,LatencyMs</c></item>
///   <item><c>GET /odata/LlmInteractions?$orderby=Cost desc&amp;$top=10</c></item>
///   <item><c>GET /odata/LlmInteractions?$count=true</c></item>
///   <item><c>GET /odata/LlmInteractions?$filter=ToolCallsUsed ne null&amp;$orderby=CreatedAt desc</c></item>
///   <item><c>GET /odata/LlmInteractions?$filter=Tags/any(t: t eq 'product')</c></item>
/// </list>
/// </summary>
public class LlmInteractionsController(AppDbContext db, TaggingQueue taggingQueue) : ODataController
{
    /// <summary>
    /// Returns all LLM interactions. Supports full OData query options including $apply for aggregations.
    /// <para>$apply examples:</para>
    /// <list type="bullet">
    ///   <item><c>GET /odata/LlmInteractions?$apply=aggregate($count as Total)</c></item>
    ///   <item><c>GET /odata/LlmInteractions?$apply=filter(contains(tolower(UserPrompt),'please'))/aggregate($count as Count)</c></item>
    ///   <item><c>GET /odata/LlmInteractions?$apply=groupby((Success),aggregate($count as Count,Cost with average as AvgCost))</c></item>
    ///   <item><c>GET /odata/LlmInteractions?$apply=groupby((ModelConfigurationId),aggregate($count as Count,Cost with sum as TotalCost))</c></item>
    /// </list>
    /// </summary>
    // No PageSize: OData would also page the Tags collection inside each row, which SQLite can't
    // translate. Use $top (max 100); natural-language queries default to $top=50.
    // No .Include(): OData composes $expand/$select into the query itself.
    [EnableQuery(MaxExpansionDepth = 3, AllowedQueryOptions = AllowedQueryOptions.All)]
    public IQueryable<LlmInteraction> Get() => db.LlmInteractions;

    /// <summary>Returns a single LLM interaction by primary key.</summary>
    [EnableQuery]
    public async Task<IActionResult> Get([FromRoute] int key)
    {
        var entity = await db.LlmInteractions
            .Include(i => i.ModelConfiguration)
            .Include(i => i.PromptTemplate)
            .Include(i => i.Feedback)
            .FirstOrDefaultAsync(i => i.Id == key);

        return entity is null ? NotFound() : Ok(entity);
    }

    /// <summary>
    /// Ingests a new LLM interaction record into the observability store.
    /// </summary>
    /// <remarks>
    /// Use this endpoint to record a real LLM call from your application.
    /// Example request body:
    /// <code>
    /// {
    ///   "userId": "user-001",
    ///   "sessionId": "session-abc123",
    ///   "userPrompt": "Summarise this document",
    ///   "completion": "The document covers...",
    ///   "inputTokens": 512,
    ///   "outputTokens": 128,
    ///   "latencyMs": 1240,
    ///   "cost": 0.00314,
    ///   "success": true,
    ///   "modelConfigurationId": 1,
    ///   "tags": ["summarization", "legal"]
    /// }
    /// </code>
    /// Omit <c>tags</c> to have them generated: the interaction is saved immediately and tagged
    /// in the background a moment later (when <c>AutoTagging:Enabled</c> and an Anthropic key are set).
    /// </remarks>
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] LlmInteraction interaction)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // Strip navigation properties — only FK values are needed for insert
        interaction.ModelConfiguration = null!;
        interaction.PromptTemplate     = null;
        interaction.Feedback           = null;
        interaction.CreatedAt          = DateTime.UtcNow;
        interaction.Tags               = TagVocabulary.Normalize(interaction.Tags);

        db.LlmInteractions.Add(interaction);
        await db.SaveChangesAsync();

        if (interaction.Tags.Count == 0)
            taggingQueue.TryEnqueue(interaction.Id);

        return Created(interaction);
    }
}
