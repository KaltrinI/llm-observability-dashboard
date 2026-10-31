using LlmObservability.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LlmObservability.Api.Controllers;

/// <summary>
/// Pre-aggregated dashboard statistics endpoint.
/// Useful for rendering KPI cards and charts without requiring OData $apply support.
/// </summary>
[ApiController]
[Route("api/stats")]
public class StatsController(AppDbContext db) : ControllerBase
{
    /// <summary>
    /// Returns summary statistics for the LLM observability dashboard:
    /// KPI totals, per-model breakdown, and daily interaction counts for the last 30 days.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var interactions = await db.LlmInteractions
            .Include(i => i.ModelConfiguration)
            .AsNoTracking()
            .ToListAsync();

        var total        = interactions.Count;
        var successCount = interactions.Count(i => i.Success);
        var avgCost      = total > 0 ? interactions.Average(i => i.Cost)      : 0.0;
        var avgLatency   = total > 0 ? interactions.Average(i => i.LatencyMs) : 0.0;
        var totalCost    = interactions.Sum(i => i.Cost);

        var byModel = interactions
            .GroupBy(i => new { i.ModelConfiguration.Name, i.ModelConfiguration.Provider })
            .Select(g => new
            {
                model        = g.Key.Name,
                provider     = g.Key.Provider,
                count        = g.Count(),
                successCount = g.Count(i => i.Success),
                avgCostUsd   = Math.Round(g.Average(i => i.Cost),      6),
                avgLatencyMs = Math.Round(g.Average(i => i.LatencyMs), 1)
            })
            .OrderByDescending(x => x.count)
            .ToList();

        var cutoff    = DateTime.UtcNow.AddDays(-30).Date;
        var dailyStats = interactions
            .Where(i => i.CreatedAt.Date >= cutoff)
            .GroupBy(i => i.CreatedAt.Date)
            .Select(g => new
            {
                date         = g.Key.ToString("yyyy-MM-dd"),
                count        = g.Count(),
                successCount = g.Count(i => i.Success),
                totalCost    = Math.Round(g.Sum(i => i.Cost), 6)
            })
            .OrderBy(x => x.date)
            .ToList();

        return Ok(new
        {
            totalInteractions = total,
            successCount,
            failedCount  = total - successCount,
            successRate  = total > 0 ? Math.Round((double)successCount / total * 100, 1) : 0.0,
            avgCostUsd   = Math.Round(avgCost,    6),
            avgLatencyMs = Math.Round(avgLatency, 1),
            totalCostUsd = Math.Round(totalCost,  4),
            byModel,
            dailyStats
        });
    }
}
