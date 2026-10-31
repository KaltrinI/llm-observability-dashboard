using LlmObservability.Api.Data;
using LlmObservability.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;

namespace LlmObservability.Api.Controllers;

/// <summary>
/// OData endpoint for prompt templates.
/// <para>Example queries:</para>
/// <list type="bullet">
///   <item><c>GET /odata/PromptTemplates?$filter=Category eq 'summarization'</c></item>
///   <item><c>GET /odata/PromptTemplates?$filter=IsActive eq true&amp;$expand=Interactions</c></item>
///   <item><c>GET /odata/PromptTemplates?$select=Id,Name,Category,Tags</c></item>
/// </list>
/// </summary>
public class PromptTemplatesController(AppDbContext db) : ODataController
{
    /// <summary>Returns all prompt templates. Supports full OData query options.</summary>
    [EnableQuery(PageSize = 50, MaxExpansionDepth = 3)]
    public IQueryable<PromptTemplate> Get() =>
        db.PromptTemplates.Include(p => p.Interactions);

    /// <summary>Returns a single prompt template by primary key.</summary>
    [EnableQuery]
    public async Task<IActionResult> Get([FromRoute] int key)
    {
        var entity = await db.PromptTemplates
            .Include(p => p.Interactions)
            .FirstOrDefaultAsync(p => p.Id == key);

        return entity is null ? NotFound() : Ok(entity);
    }
}
