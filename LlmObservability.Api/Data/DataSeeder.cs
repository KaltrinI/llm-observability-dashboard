using LlmObservability.Api.Models;

namespace LlmObservability.Api.Data;

/// <summary>
/// Seeds ~60 days of synthetic but internally consistent telemetry:
/// latency follows model and output length, failures carry real error types (and aren't billed for output),
/// thumbs ratings agree with sentiment, completions answer their prompts, traffic grows over time,
/// and one incident is waiting to be found: a traffic spike <see cref="IncidentDaysAgo"/> days ago
/// during which gpt-4o mostly timed out.
/// Uses a fixed seed, so a fresh database always gets the same data.
/// </summary>
public static class DataSeeder
{
    private const int InteractionCount = 500;
    private const int HistoryDays      = 60;
    private const int IncidentDaysAgo  = 9;
    private const int RetiredModelDays = 30;   // gpt-4o-mini was switched off this many days ago

    private const string TimeoutError       = "upstream_timeout: provider did not respond within 30s";
    private const string RateLimitError     = "rate_limit_exceeded: 429 Too Many Requests from provider";
    private const string ContentFilterError = "content_filtered: response blocked by the provider's safety filter";

    private sealed record PromptSpec(string Prompt, string[] Tags, string Completion);

    private sealed record ModelProfile(double BaseLatencyMs, double MsPerOutputToken, double Weight);

    private sealed record Category(
        (int Min, int Max) InputTokens,
        (int Min, int Max) OutputTokens,
        double ToolUseRate,
        string[][] ToolSets,
        PromptSpec[] Prompts);

    public static void Seed(AppDbContext ctx)
    {
        if (ctx.ModelConfigurations.Any()) return;

        var rng = new Random(42);
        var now = DateTime.UtcNow;

        // ── Models ──────────────────────────────────────────────────────────
        var models = new[]
        {
            new ModelConfiguration { Id = 1, Name = "claude-sonnet-4-6", Provider = "Anthropic",
                CostPer1KInputTokens = 0.003, CostPer1KOutputTokens = 0.015, MaxContextWindow = 200000, IsActive = true },
            new ModelConfiguration { Id = 2, Name = "claude-haiku-4-5", Provider = "Anthropic",
                CostPer1KInputTokens = 0.001, CostPer1KOutputTokens = 0.005, MaxContextWindow = 200000, IsActive = true },
            new ModelConfiguration { Id = 3, Name = "gpt-4o", Provider = "OpenAI",
                CostPer1KInputTokens = 0.0025, CostPer1KOutputTokens = 0.01, MaxContextWindow = 128000, IsActive = true },
            new ModelConfiguration { Id = 4, Name = "gpt-4o-mini", Provider = "OpenAI",
                CostPer1KInputTokens = 0.00015, CostPer1KOutputTokens = 0.0006, MaxContextWindow = 128000, IsActive = false },
        };

        var profiles = new Dictionary<int, ModelProfile>
        {
            [1] = new(BaseLatencyMs: 600, MsPerOutputToken: 11, Weight: 0.35),
            [2] = new(BaseLatencyMs: 250, MsPerOutputToken: 4,  Weight: 0.35),
            [3] = new(BaseLatencyMs: 700, MsPerOutputToken: 12, Weight: 0.20),
            [4] = new(BaseLatencyMs: 300, MsPerOutputToken: 5,  Weight: 0.10),
        };

        // ── Prompt templates ────────────────────────────────────────────────
        var templates = new[]
        {
            new PromptTemplate { Id = 1, Name = "Document Summariser", Category = "summarization",
                SystemPrompt = "You summarise long documents concisely, highlighting key points, metrics, and actionable insights.",
                Tags = "summarize,document,text", CreatedAt = now.AddDays(-90), IsActive = true },
            new PromptTemplate { Id = 2, Name = "Entity Extractor", Category = "extraction",
                SystemPrompt = "You extract named entities (people, places, organisations, dates) from text with high precision.",
                Tags = "extract,entities,ner", CreatedAt = now.AddDays(-85), IsActive = true },
            new PromptTemplate { Id = 3, Name = "Structured Data Extractor", Category = "extraction",
                SystemPrompt = "You extract structured data from unstructured text and return valid JSON.",
                Tags = "extract,data,structured,json", CreatedAt = now.AddDays(-80), IsActive = true },
            new PromptTemplate { Id = 4, Name = "Conversational Assistant", Category = "chat",
                SystemPrompt = "You are a friendly and helpful conversational assistant.",
                Tags = "chat,conversation,assistant", CreatedAt = now.AddDays(-75), IsActive = true },
            new PromptTemplate { Id = 5, Name = "Customer Support Assistant", Category = "support",
                SystemPrompt = "You help customers with billing, refunds and account questions. Be concise and empathetic.",
                Tags = "support,billing,customer", CreatedAt = now.AddDays(-70), IsActive = true },
        };

        ctx.ModelConfigurations.AddRange(models);
        ctx.PromptTemplates.AddRange(templates);
        ctx.SaveChanges();

        var categories = BuildCategories();

        // ── Interactions ────────────────────────────────────────────────────
        var userIds     = Enumerable.Range(1, 12).Select(i => $"user-{i:D3}").ToArray();
        var lastSession = new Dictionary<string, (string Id, int DaysAgo)>();
        var interactions = new List<LlmInteraction>(InteractionCount);

        while (interactions.Count < InteractionCount)
        {
            var daysAgo   = SampleDaysAgo(rng, now);
            var createdAt = now.Date.AddDays(-daysAgo)
                .AddHours(SampleHour(rng)).AddMinutes(rng.Next(60)).AddSeconds(rng.Next(60));
            if (createdAt > now) continue;   // later today hasn't happened yet

            var model   = PickModel(rng, models, profiles, daysAgo);
            var profile = profiles[model.Id];

            // 20 % of calls don't use a template; those are free-form chat
            PromptTemplate? template = rng.NextDouble() < 0.2 ? null : templates[rng.Next(templates.Length)];
            var category = categories[template?.Category ?? "chat"];
            var spec     = category.Prompts[rng.Next(category.Prompts.Length)];

            // Users follow a long tail: user-001 is the heaviest user. Same-day calls often continue a session.
            var userId = PickWeighted(rng, userIds, (_, i) => 1.0 / (i + 1));
            var sessionId = lastSession.TryGetValue(userId, out var last) && last.DaysAgo == daysAgo && rng.NextDouble() < 0.6
                ? last.Id
                : $"session-{rng.Next(100000, 999999)}";
            lastSession[userId] = (sessionId, daysAgo);

            var inputTokens  = rng.Next(category.InputTokens.Min,  category.InputTokens.Max + 1);
            var outputTokens = rng.Next(category.OutputTokens.Min, category.OutputTokens.Max + 1);
            var tools = rng.NextDouble() < category.ToolUseRate
                ? category.ToolSets[rng.Next(category.ToolSets.Length)]
                : null;

            var inputCost = inputTokens / 1000.0 * model.CostPer1KInputTokens;
            var toolDelay = tools is null ? 0 : rng.Next(800, 2500);
            var latency   = (profile.BaseLatencyMs + profile.MsPerOutputToken * outputTokens + toolDelay)
                            * (0.85 + rng.NextDouble() * 0.4)
                            * (rng.NextDouble() < 0.05 ? 2 : 1);   // occasional slow tail
            var cost      = inputCost + outputTokens / 1000.0 * model.CostPer1KOutputTokens;

            // ── Failures: baseline ~5 %, plus the incident ──────────────────
            string? error = null;
            var roll = rng.NextDouble();
            if (daysAgo == IncidentDaysAgo && model.Id == 3 && roll < 0.75) error = TimeoutError;
            else if (roll < 0.020) error = TimeoutError;
            else if (roll < 0.035) error = RateLimitError;
            else if (roll < 0.042) error = ContentFilterError;
            else if (tools is not null && roll < 0.060) error = $"tool_error: {tools[0]} failed with HTTP 503";

            switch (error)
            {
                case null:
                    break;
                case TimeoutError:
                    (outputTokens, latency, cost) = (0, 30000 + rng.Next(500), 0);
                    break;
                case RateLimitError:
                    (outputTokens, latency, cost) = (0, rng.Next(40, 250), 0);
                    break;
                default:   // content filter / tool error: the prompt was processed, so input is billed
                    (outputTokens, latency, cost) = (0, profile.BaseLatencyMs + toolDelay + rng.Next(200, 900), inputCost);
                    break;
            }

            interactions.Add(new LlmInteraction
            {
                UserId               = userId,
                SessionId            = sessionId,
                UserPrompt           = spec.Prompt,
                Completion           = error is null ? spec.Completion : string.Empty,
                Tags                 = [.. spec.Tags],
                InputTokens          = inputTokens,
                OutputTokens         = outputTokens,
                LatencyMs            = Math.Round(latency),
                Cost                 = Math.Round(cost, 6),
                Success              = error is null,
                ErrorMessage         = error,
                ToolCallsUsed        = tools is null ? null : "[" + string.Join(",", tools.Select(t => $"\"{t}\"")) + "]",
                CreatedAt            = createdAt,
                ModelConfigurationId = model.Id,
                PromptTemplateId     = template?.Id,
            });
        }

        // Ids in chronological order, like a real log
        interactions = [.. interactions.OrderBy(i => i.CreatedAt)];
        for (var i = 0; i < interactions.Count; i++) interactions[i].Id = i + 1;

        ctx.LlmInteractions.AddRange(interactions);
        ctx.SaveChanges();

        ctx.UserFeedbacks.AddRange(BuildFeedback(rng, interactions, now));
        ctx.SaveChanges();
    }

    // ── Feedback that agrees with what happened ──────────────────────────────
    private static List<UserFeedback> BuildFeedback(Random rng, List<LlmInteraction> interactions, DateTime now)
    {
        string[] positive   = ["Very helpful!", "Quick and accurate.", "Exactly what I needed.", "Great explanation!",
                               "Saved me hours of work.", "Clear and concise.", "Would use again."];
        string[] failed     = ["It just errored out.", "No answer at all, it timed out.", "Failed twice before I gave up."];
        string[] slow       = ["Took forever to respond.", "Way too slow.", "Good answer, but the wait was painful."];
        string[] verbose    = ["Too verbose for a simple question.", "Way too long, I only needed the gist.",
                               "Too wordy.", "The answer was too lengthy."];
        string[] inaccurate = ["Missed some key details.", "Some of the numbers were wrong.",
                               "Response was too generic.", "Not quite what I needed."];

        var feedbacks = new List<UserFeedback>();
        foreach (var interaction in interactions)
        {
            if (rng.NextDouble() >= (interaction.Success ? 0.45 : 0.35)) continue;

            var isSlow    = interaction.LatencyMs > 6000;
            var isVerbose = interaction.OutputTokens > 550;
            var negativeChance = !interaction.Success
                ? 0.85
                : 0.12 + (isSlow ? 0.30 : 0) + (isVerbose ? 0.15 : 0);
            var isNegative = rng.NextDouble() < negativeChance;

            string? comment = null;
            if (rng.NextDouble() < 0.5)
            {
                // A long answer is usually also a slow one; complain about either, like real users do.
                var reasons = new List<string[]>();
                if (!isNegative)               reasons.Add(positive);
                else if (!interaction.Success) reasons.Add(failed);
                else
                {
                    if (isSlow)    reasons.Add(slow);
                    if (isVerbose) reasons.Add(verbose);
                    if (reasons.Count == 0) reasons.Add(inaccurate);
                }

                var pool = reasons[rng.Next(reasons.Count)];
                comment = pool[rng.Next(pool.Length)];
            }

            var sentiment = isNegative
                ? -0.2 - rng.NextDouble() * 0.75    // -0.95 … -0.20
                : 0.25 + rng.NextDouble() * 0.73;   //  0.25 …  0.98

            var createdAt = interaction.CreatedAt.AddMinutes(rng.Next(1, 31));
            feedbacks.Add(new UserFeedback
            {
                Id               = feedbacks.Count + 1,
                LlmInteractionId = interaction.Id,
                ThumbsRating     = isNegative ? -1 : 1,
                SentimentScore   = Math.Round(sentiment, 2),
                Comment          = comment,
                CreatedAt        = createdAt > now ? now : createdAt,
            });
        }

        return feedbacks;
    }

    // ── Sampling helpers ─────────────────────────────────────────────────────

    /// <summary>Traffic grows ~3× over the period, halves at weekends, and spikes on the incident day.</summary>
    private static int SampleDaysAgo(Random rng, DateTime now)
    {
        const double maxWeight = 3.0 * 2.5;
        while (true)
        {
            var daysAgo = rng.Next(HistoryDays);
            var day     = now.Date.AddDays(-daysAgo).DayOfWeek;

            var weight = 1 + 2.0 * (HistoryDays - daysAgo) / HistoryDays;
            if (day is DayOfWeek.Saturday or DayOfWeek.Sunday) weight *= 0.5;
            if (daysAgo == IncidentDaysAgo) weight *= 2.5;

            if (rng.NextDouble() * maxWeight < weight) return daysAgo;
        }
    }

    /// <summary>Mostly business hours (UTC).</summary>
    private static int SampleHour(Random rng) =>
        rng.NextDouble() < 0.8 ? rng.Next(8, 19) : rng.Next(24);

    private static ModelConfiguration PickModel(
        Random rng, ModelConfiguration[] models, Dictionary<int, ModelProfile> profiles, int daysAgo)
    {
        // The incident: a launch-day spike routed half the traffic to gpt-4o
        if (daysAgo == IncidentDaysAgo && rng.NextDouble() < 0.5) return models[2];

        var available = models.Where(m => m.IsActive || daysAgo > RetiredModelDays).ToArray();
        return PickWeighted(rng, available, (m, _) => profiles[m.Id].Weight);
    }

    private static T PickWeighted<T>(Random rng, IReadOnlyList<T> items, Func<T, int, double> weight)
    {
        var weights = items.Select(weight).ToArray();
        var roll    = rng.NextDouble() * weights.Sum();
        for (var i = 0; i < items.Count; i++)
        {
            roll -= weights[i];
            if (roll <= 0) return items[i];
        }
        return items[^1];
    }

    // ── Prompt catalogue: each prompt has its own tags and a completion that answers it ──
    private static Dictionary<string, Category> BuildCategories() => new()
    {
        ["summarization"] = new(
            InputTokens: (1500, 6000), OutputTokens: (150, 500), ToolUseRate: 0.10,
            ToolSets: [["file_read"]],
            Prompts:
            [
                new("Summarise the Q3 2024 earnings report, highlighting revenue growth and margin changes.",
                    ["finance", "reports"],
                    "Q3 revenue grew 15% to $2.4B. Operating margin expanded 200 bps to 18.5%, and free cash flow rose 40% YoY to $320M. Full-year guidance was raised."),
                new("Give me a two-paragraph summary of the attached research paper on transformer architectures.",
                    ["research", "machine-learning"],
                    "The paper proposes a sparse-attention mechanism that cuts attention cost from O(n²) to O(n log n).\n\nIt matches baseline accuracy on GLUE (94.2%) while running inference 3× faster, and the authors release code and weights."),
                new("Condense this 50-page legal brief into an executive summary under 500 words.",
                    ["legal"],
                    "The brief argues the non-compete clause is unenforceable: it has no geographic limit and runs for 36 months. Three appellate precedents support this. Recommendation: settle before discovery."),
                new("Summarise the key decisions from today's board meeting transcript.",
                    ["meetings"],
                    "Decisions: (1) approve the FY25 budget, (2) pause EMEA hiring until Q2, (3) start the search for a new CFO."),
                new("Create a TL;DR for this product specification document.",
                    ["product", "documentation"],
                    "TL;DR: v2 adds SSO, audit logs and a usage-based pricing tier. Launch is planned for March after a two-week beta."),
                new("Summarise the customer reviews for our new headphones and tell me if people think it's the best product in its price range.",
                    ["product", "reviews"],
                    "Most of the 1,240 reviews are positive (4.4★). Buyers praise battery life and comfort, and many call it the best product under $150. Complaints focus on the case hinge."),
            ]),

        ["extraction"] = new(
            InputTokens: (800, 3000), OutputTokens: (100, 600), ToolUseRate: 0.30,
            ToolSets: [["file_read"], ["file_read", "code_interpreter"]],
            Prompts:
            [
                new("Extract all named entities (people, organisations, locations) from this contract.",
                    ["legal", "ner"],
                    "People: John Smith (signatory). Organisations: Acme Corp (counterparty). Locations: London (jurisdiction)."),
                new("Convert the following CSV data into structured JSON with nested address objects.",
                    ["data-transformation"],
                    "Converted 1,247 rows. Nested 'address' objects were created for city, postcode and country. 12 rows were skipped because required fields were missing."),
                new("Pull out all dates, deadlines, and deliverables from this project charter.",
                    ["project-management"],
                    "Kick-off 2024-03-01; design sign-off 2024-04-15; MVP delivery 2024-06-30; final report 2024-07-31."),
                new("Identify and list every financial figure mentioned in this annual report.",
                    ["finance", "reports"],
                    "Revenue $9.1B; net income $1.2B; R&D spend $1.4B; dividend $0.84/share; capex $650M."),
                new("Extract the action items and owners from this meeting transcript.",
                    ["meetings"],
                    "(1) Alice: deliver the API spec by Fri 19 Apr. (2) Bob: schedule UAT by 26 Apr. (3) Carol: confirm budget approval by month end."),
                new("Pull every refund request out of these support emails and list the order numbers.",
                    ["support", "refunds"],
                    "Found 4 refund requests: #10231, #10288, #10302 and #10417. Two of them mention a double charge."),
            ]),

        ["chat"] = new(
            InputTokens: (100, 800), OutputTokens: (150, 900), ToolUseRate: 0.33,
            ToolSets: [["web_search"], ["code_interpreter"], ["web_search", "code_interpreter"]],
            Prompts:
            [
                new("Help me debug this Python function, it returns wrong results for edge cases.",
                    ["coding", "debugging"],
                    "Root cause: an off-by-one on line 23. For n=0 the loop returns -1 instead of 0. I fixed the range bound and all 47 tests pass."),
                new("Explain the difference between OData and GraphQL for a technical audience.",
                    ["coding", "apis"],
                    "OData standardises query options ($filter, $orderby, $expand) on REST URLs, so any HTTP client can query it. GraphQL uses one endpoint and a typed query language, so clients ask for exactly the fields they need."),
                new("Write three variations of ad copy for a fitness app targeting adults aged 25–45.",
                    ["marketing"],
                    "A: 'Transform your body in 30 days.' B: 'Your fitness journey starts here.' C: 'Small habits, big results. Join 2M members today.'"),
                new("What are the best practices for securing a REST API in a microservices architecture?",
                    ["security", "apis"],
                    "Use OAuth 2.0 with short-lived JWTs, enforce TLS 1.3, rate-limit per client, validate all input at the edge, and scan dependencies weekly."),
                new("Draft a polite follow-up email to a prospect who hasn't responded in two weeks.",
                    ["sales", "email"],
                    "Subject: Quick follow-up\n\nHi [Name], just checking in since we last spoke. Would a 15-minute call this week work for you? Best, [Sender]"),
                new("Write a short review explaining why this is the best product we have shipped this year.",
                    ["product", "reviews"],
                    "This release is the best product we've shipped this year: setup takes two minutes, it's twice as fast as v1, and support tickets dropped 30%."),
                new("Is this really the finest product for sensitive skin, or is that just marketing?",
                    ["product", "reviews"],
                    "Partly marketing. It is fragrance-free and dermatologist-tested, but independent reviews rate two cheaper options just as highly for sensitive skin."),
                new("Draft a landing page headline saying we make the greatest product in our category.",
                    ["product", "marketing"],
                    "\"The greatest product in its class, built for teams who refuse to settle.\""),
                new("Compare these two vacuums and tell me which is the top product for pet hair.",
                    ["product", "comparison"],
                    "The Model X is the top product for pet hair: stronger suction (220 AW vs 150 AW), a tangle-free brush and a larger bin."),
                new("How should we price the premium tier compared to our competitors?",
                    ["pricing", "product"],
                    "Price the premium tier at $49/user/month, 10–15% above the main competitors. SSO and audit logs justify the premium; offer 20% off annual plans."),
            ]),

        ["support"] = new(
            InputTokens: (100, 500), OutputTokens: (80, 400), ToolUseRate: 0.40,
            ToolSets: [["kb_search"], ["kb_search", "order_lookup"]],
            Prompts:
            [
                new("How do I get a refund for my annual plan? I cancelled after two weeks.",
                    ["support", "refunds", "billing"],
                    "Annual plans cancelled within 30 days get a full refund. I've opened request #R-2291, and the money will be back on your card in 5–7 business days."),
                new("I was charged twice this month and I want my money back for the duplicate charge.",
                    ["support", "billing", "refunds"],
                    "Sorry about that. I can see two charges on 3 Oct. The duplicate has been reversed and will disappear from your statement within 3 business days."),
                new("Can I get reimbursed for the hours we lost during yesterday's outage?",
                    ["support", "refunds", "outage"],
                    "We're applying a 10% service credit for the outage to your next invoice. Cash reimbursement isn't available under the current SLA."),
                new("My API key stopped working after I rotated it. What should I check?",
                    ["support", "apis", "troubleshooting"],
                    "New keys take up to 60 seconds to propagate. Make sure the old key is gone from your environment and the new key has the read and write scopes."),
                new("What's the difference between the Team and Business plans?",
                    ["support", "pricing"],
                    "Business adds SSO, audit logs and a 99.9% SLA on top of Team. It costs $49 per user per month versus $19 for Team."),
                new("How do I export all my data before closing my account?",
                    ["support", "data-export"],
                    "Go to Settings → Data → Export. You'll get a ZIP of CSV files by email within an hour, and the link expires after 7 days."),
            ]),
    };
}
