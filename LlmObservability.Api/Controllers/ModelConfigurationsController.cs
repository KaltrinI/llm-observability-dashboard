using LlmObservability.Api.Data;
using LlmObservability.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
using Microsoft.EntityFrameworkCore;

namespace LlmObservability.Api.Controllers;

/// <summary>
/// OData endpoint for model configurations.
/// <para>Example queries:</para>
/// <list type="bullet">
///   <item><c>GET /odata/ModelConfigurations?$filter=Provider eq 'Anthropic'</c></item>
///   <item><c>GET /odata/ModelConfigurations?$expand=Interactions($top=5;$orderby=CreatedAt desc)</c></item>
///   <item><c>GET /odata/ModelConfigurations?$filter=IsActive eq true&amp;$orderby=CostPer1KInputTokens asc</c></item>
/// </list>
/// </summary>
public class ModelConfigurationsController(AppDbContext db) : ODataController
{
    /// <summary>Returns all model configurations. Supports full OData query options.</summary>
    [EnableQuery(PageSize = 50, MaxExpansionDepth = 3)]
    public IQueryable<ModelConfiguration> Get() =>
        db.ModelConfigurations.Include(m => m.Interactions);

    /// <summary>Returns a single model configuration by primary key.</summary>
    [EnableQuery]
    public async Task<IActionResult> Get([FromRoute] int key)
    {
        var entity = await db.ModelConfigurations
            .Include(m => m.Interactions)
            .FirstOrDefaultAsync(m => m.Id == key);

        return entity is null ? NotFound() : Ok(entity);
    }
}
