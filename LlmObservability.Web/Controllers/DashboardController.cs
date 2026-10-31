using LlmObservability.Web.Models;
using LlmObservability.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LlmObservability.Web.Controllers;

public class DashboardController(NlQueryProxyService nlQueryService, IConfiguration configuration) : Controller
{
    public async Task<IActionResult> Index()
    {
        // Pass API base URL to view so JS can call /api/stats directly
        ViewData["ApiBaseUrl"] = configuration["LlmObservabilityApi:BaseUrl"] ?? "https://localhost:5001";
        return View(new DashboardViewModel { Providers = await nlQueryService.GetProvidersAsync() });
    }
}
