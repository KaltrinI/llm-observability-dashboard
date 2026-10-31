using Azure.Monitor.OpenTelemetry.AspNetCore;
using LlmObservability.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Azure Monitor / AI Foundry Tracing (skipped until a real connection string is set) ──
var appInsights = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(appInsights) && !appInsights.StartsWith("YOUR_"))
    builder.Services.AddOpenTelemetry().UseAzureMonitor();

builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();

builder.Services.AddScoped<NlQueryProxyService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.Run();
