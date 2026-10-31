using LlmObservability.Api.Data;
using LlmObservability.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;

namespace LlmObservability.Api.Controllers;

/// <summary>
/// OData endpoint for user feedback records.
/// <para>Example queries:</para>
/// <list type="bullet">
///   <item><c>GET /odata/UserFeedbacks?$filter=ThumbsRating eq -1</c></item>
///   <item><c>GET /odata/UserFeedbacks?$filter=SentimentScore lt 0</c></item>
///   <item><c>GET /odata/UserFeedbacks?$expand=LlmInteraction&amp;$orderby=CreatedAt desc</c></item>
///   <item><c>GET /odata/UserFeedbacks?$filter=ThumbsRating eq 1 and SentimentScore gt 0.5&amp;$count=true</c></item>
///   <item><c>GET /odata/UserFeedbacks?$select=Id,ThumbsRating,SentimentScore,Comment</c></item>
/// </list>
/// </summary>
public class UserFeedbacksController(AppDbContext db) : ODataController
{
    /// <summary>Returns all user feedback entries. Supports full OData query options.</summary>
    [EnableQuery(PageSize = 50, MaxExpansionDepth = 3)]
    public IQueryable<UserFeedback> Get() =>
        db.UserFeedbacks.Include(f => f.LlmInteraction);

    /// <summary>Returns a single user feedback entry by primary key.</summary>
    [EnableQuery]
    public async Task<IActionResult> Get([FromRoute] int key)
    {
        var entity = await db.UserFeedbacks
            .Include(f => f.LlmInteraction)
            .FirstOrDefaultAsync(f => f.Id == key);

        return entity is null ? NotFound() : Ok(entity);
    }
}
