using System.Text.Json.Serialization;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using LlmObservability.Api;
using LlmObservability.Api.Data;
using LlmObservability.Api.OData;
using LlmObservability.Api.Services;
using Microsoft.AspNetCore.OData;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Local secrets from .env (git-ignored); see .env in this folder ──────────
DotEnv.AddTo(builder.Configuration, Path.Combine(builder.Environment.ContentRootPath, ".env"));

// ── Azure Monitor / AI Foundry Tracing (skipped until a real connection string is set) ──
var appInsights = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(appInsights) && !appInsights.StartsWith("YOUR_"))
{
    builder.Services.AddOpenTelemetry()
        .UseAzureMonitor()
        .WithTracing(t => t.AddSource("LlmObservability.NLQuery", "LlmObservability.Tagging"));
}

// ── Data (SQLite file; delete it to reseed) ─────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=llmobservability.db"));

// ── Named HTTP client for Anthropic API ─────────────────────────────────────
builder.Services.AddHttpClient("anthropic", client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com");
    client.Timeout     = TimeSpan.FromSeconds(60);   // adaptive thinking can lengthen replies
});

// ── Controllers + OData ─────────────────────────────────────────────────────
builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles)
    .AddOData(opt =>
    {
        opt.Select().Filter().OrderBy().Expand().Count().SetMaxTop(100)
           .AddRouteComponents("odata", EdmModelBuilder.Build());

        // Timestamps are stored in UTC; without this OData treats them as server-local time.
        opt.TimeZone = TimeZoneInfo.Utc;
    });

// ── Anthropic + auto-tagging ─────────────────────────────────────────────────
builder.Services.AddSingleton<AnthropicMessagesClient>();
builder.Services.AddScoped<IInteractionTagger, AnthropicInteractionTagger>();
builder.Services.AddSingleton<TaggingQueue>();
builder.Services.AddHostedService<AutoTaggingWorker>();

// ── Azure AI services ────────────────────────────────────────────────────────
builder.Services.AddScoped<ContentSafetyService>();
builder.Services.AddKeyedScoped<IQueryTranslatorService, ODataQueryTranslatorService>("anthropic");
builder.Services.AddKeyedScoped<IQueryTranslatorService, FoundryQueryTranslatorService>("foundry");

// ── Swagger ──────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title       = "LLM Observability API",
        Version     = "v1",
        Description = "OData v4 API for querying LLM telemetry. Use the " +
                      "/api/naturallanguagequery endpoint to translate plain English into OData."
    });
    c.ResolveConflictingActions(a => a.First());

    var xmlFile = Path.Combine(AppContext.BaseDirectory, "LlmObservability.Api.xml");
    if (File.Exists(xmlFile))
        c.IncludeXmlComments(xmlFile);
});

// ── CORS ─────────────────────────────────────────────────────────────────────
builder.Services.AddCors(opt =>
    opt.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    DataSeeder.Seed(db);
}

// ── Middleware pipeline ───────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "LLM Observability API v1");
        c.DocumentTitle = "LLM Observability — API Explorer";
    });
}

app.UseCors();
app.UseRouting();
app.MapControllers();
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.Run();
