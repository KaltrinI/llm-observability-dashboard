# LLM Observability — OData + AI Demo

A full-stack .NET 8 demo that combines **OData v4** with **Claude**. Ask questions about your LLM
usage in plain English: Claude turns them into an OData query you can read and check, and the
dashboard shows the answer as a number, a chart, or a table.

```
"which day had the most failures in the last 2 weeks"
        ↓  Claude (knows today's date, the schema and the tag vocabulary)
$apply=filter(CreatedAt ge 2026-10-17T00:00:00Z and Success eq false)/groupby((CreatedDay),aggregate($count as Failures))
$orderby=Failures desc&$top=1
        ↓  OData API → SQLite
2026-10-22 | 13 failures
```

---

## Architecture

```
Browser
  └─► LlmObservability.Web  (ASP.NET Core MVC, https://localhost:5002)
         └─► LlmObservability.Api  (ASP.NET Core OData v4, https://localhost:5001)
                 ├─► SQLite  (llmobservability.db — 500 seeded interactions over 60 days)
                 ├─► Anthropic API  (Claude Haiku 5.5: NL → OData translation, auto-tagging)
                 └─► Microsoft Foundry  (optional: Phi-4 or any deployed model as an alternative translator)
```

---

## Quick Start

### 1. Add your keys to `.env`

Keys never go in `appsettings.json`. Copy the template and fill it in; `.env` is git-ignored and loaded at startup:

```bash
cp LlmObservability.Api/.env.example LlmObservability.Api/.env
```

```bash
# LlmObservability.Api/.env
Anthropic__ApiKey=sk-ant-...          # required
AzureAI__Foundry__Endpoint=           # optional: Microsoft Foundry model (see below)
AzureAI__Foundry__ApiKey=
AzureAI__Foundry__Model=
AzureAI__ContentSafety__Endpoint=     # optional: Content Safety
AzureAI__ContentSafety__ApiKey=
```

Non-secret settings stay in `appsettings.json` (`Anthropic:Model`, `TranslatorEffort`, `TaggerEffort`, `AutoTagging`).

Claude Haiku 5.5 thinks before answering. `effort` (`low` | `medium` | `high` | `xhigh` | `max`) sets how much:
`low` translates in about 2 s and handles the demo questions. Try `medium` if complex questions come out wrong.
Leave both effort settings empty if you switch `Model` back to `claude-haiku-4-5`, which doesn't support effort.

### 2. Run both projects

```bash
# Terminal 1 — API (OData + NL translation + auto-tagging)
cd LlmObservability.Api
dotnet run

# Terminal 2 — Web dashboard
cd LlmObservability.Web
dotnet run
```

| URL | What it is |
|-----|------------|
| `https://localhost:5002` | Dashboard — ask questions, see answers, try live auto-tagging |
| `https://localhost:5001/swagger` | Swagger UI — browse all endpoints |
| `https://localhost:5001/odata/LlmInteractions?$count=true&$top=5` | Raw OData feed |

### 3. Reset the data

The database is created and seeded on first start. Data persists across restarts, including
anything you add. To start fresh:

```bash
rm LlmObservability.Api/llmobservability.db   # reseeded on next start
```

Seeding is relative to the current date (the last 60 days), so reseed on the day you demo.

---

## Demo Script

Reseed the morning of the demo, run both projects, and do one dry run: the model can phrase
things slightly differently between runs. Exact numbers depend on the day you seed. The story doesn't change.

| # | Ask (or click the chip) | What it shows |
|---|---|---|
| 1 | *(dashboard on load)* | KPIs, model mix, and a **spike on the daily chart 9 days ago** where failures jump. That's the incident to investigate. |
| 2 | `failures per model in the last 2 weeks` | Bar chart: **gpt-4o** dominates failures. Claude turned "last 2 weeks" into a real date. |
| 3 | `which day had the most failures in the last 2 weeks` | Pinpoints the incident day. Groups by day, sorts by the aggregate, top 1. |
| 4 | `total cost per model` | Bar chart; claude-sonnet is the biggest spender. |
| 5 | `how many prompts call something the best product` | Big number. The **Phrase matching** panel shows the synonyms searched ("the greatest / finest / top product"). |
| 6 | `how many prompts contain exactly "the best product"` | Smaller number. Saying "exactly" switches synonyms off. |
| 7 | `how many customers asked for a refund` | Maps "refund" onto the `refunds` tag. Depending on the run, the model counts prompts or **distinct customers**; the explanation says which. |
| 8 | `feedback complaining that answers were too long` | Searches feedback comments: "too verbose", "too wordy", "too lengthy"… |
| 9 | `interactions tagged pricing` | Tag filter (`Tags/any(...)`). |
| 10 | **Live auto-tagging** panel: `Can I get my money back if I downgrade mid-month?` | Saved instantly, tagged ~2 s later with existing tags (`billing`, `refunds`). Try something off-topic (a haiku request) to see a new tag created. |

The model toggle above the search box lists the translators the API offers. **Microsoft Foundry** stays greyed out until
it's configured (see below). Once it is, ask the same question with each model and compare the generated OData.

---

## Microsoft Foundry (optional)

Microsoft Foundry (formerly Azure AI Foundry) hosts Phi-4 and many other models (DeepSeek, Llama, Mistral,
Azure OpenAI GPT models, …). The API calls it through Foundry's **OpenAI-compatible v1 endpoint** with the
`OpenAI` NuGet package, which replaced the retired Azure AI Inference SDK. Any chat model you deploy works,
and switching models only takes a config change.

### 1. Deploy a model

1. Go to <https://ai.azure.com> and create a **project**. This also creates a Foundry resource in your Azure subscription.
2. Open the **model catalog**, pick a model (e.g. **Phi-4**), and choose **Deploy**.
   *Global Standard* (pay per token) is the simplest deployment type.
3. Note the **deployment name** (e.g. `Phi-4`) and copy the resource's **key**.
   The endpoint you need is `https://<resource>.openai.azure.com/openai/v1/`, where `<resource>` is your Foundry resource name.

### 2. Configure the API

Fill in `LlmObservability.Api/.env`. It's git-ignored and loaded at startup, so keys stay out of `appsettings.json`:

```bash
AzureAI__Foundry__Endpoint=https://<resource>.openai.azure.com/openai/v1/
AzureAI__Foundry__ApiKey=<key>
AzureAI__Foundry__Model=demogpt-4.1-mini   # your deployment name
```

Empty values fall back to `appsettings.json`, and real environment variables override the file.
Restart the API. The dashboard toggle then shows **Microsoft Foundry (demogpt-4.1-mini)**.

> The old Azure AI Inference style of endpoint (`https://<resource>.services.ai.azure.com/models`) is rejected
> with a message telling you to use the `/openai/v1/` endpoint instead.

Smaller models like Phi-4 follow the long translation prompt less reliably than Claude. Expect more failed or
wrong queries; failed ones show the OData error on the dashboard.

### 3. Optional: Content Safety and tracing

| Setting | Where to get it | Effect |
|---|---|---|
| `AzureAI:ContentSafety:Endpoint` + `ApiKey` | Your Foundry resource includes Content Safety: use `https://<resource>.cognitiveservices.azure.com/` and the same key. If that's rejected, create a standalone *Azure AI Content Safety* resource. | Every question is screened before it reaches a model; harmful ones are blocked with a message. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` (both apps) | Create an *Application Insights* resource (or connect one under **Tracing** in your Foundry project) and copy its connection string. | Request traces plus the `TranslateNLQuery.*` and `SuggestTags.*` spans (model, query, generated URL), viewable in App Insights and Foundry tracing. |

Each Azure setting switches itself on only once its `YOUR_...` placeholder is replaced.

---

## The Demo Data

`DataSeeder` generates 60 days of synthetic telemetry that holds together, so questions have real answers:

- **Completions answer their prompts.** Each prompt has its own tags and a matching completion.
- **Latency follows the model and output length** (haiku is fastest; tool calls add time).
- **Failures look real:** timeouts (~30 s), rate limits, content-filter blocks and tool errors. Failed calls
  produce no output tokens; only content-filter and tool errors bill the input.
- **Feedback agrees with what happened:** thumbs-down has negative sentiment, and comments match the cause
  ("Took forever to respond.", "Too wordy.", "It just errored out.").
- **Traffic grows over time and dips at weekends.** gpt-4o-mini was retired 30 days ago.
- **One incident:** 9 days before seeding, a traffic spike sent half the calls to gpt-4o, and most of them timed out.

---

## OData Entity Model

```
LlmInteractions  ──────────────────────────────────────────────
  Id, UserId, SessionId, UserPrompt, Completion
  InputTokens, OutputTokens, LatencyMs, Cost
  Success, ErrorMessage, ToolCallsUsed
  CreatedAt (UTC), CreatedDay ("yyyy-MM-dd", computed by the database)
  Tags (collection of lowercase strings, e.g. ["product","reviews"])
  → ModelConfiguration (many-to-one)
  → PromptTemplate     (many-to-one, optional)
  → Feedback           (one-to-one, optional)

ModelConfigurations  ──────────────────────────────────────────
  Id, Name, Provider, CostPer1KInputTokens, CostPer1KOutputTokens, MaxContextWindow, IsActive

PromptTemplates  ──────────────────────────────────────────────
  Id, Name, Category, Tags, SystemPrompt, CreatedAt, IsActive

UserFeedbacks  ────────────────────────────────────────────────
  Id, LlmInteractionId, ThumbsRating (1=up, -1=down), SentimentScore, Comment, CreatedAt
```

All entity sets support `$filter` `$select` `$orderby` `$expand` `$top` `$skip` `$count` `$apply`.

---

## How the Translation Works

The translator prompt describes the schema and query patterns, and includes **today's date** (so
"last 2 weeks" and "this month" resolve correctly) and the **current tag list**. Claude replies with
structured JSON (`filterExpression`, `apply`, `orderBy`, `top`, `expand`, `textSearches`, `explanation`).
The API builds the OData URL from that and the dashboard runs it.

The dashboard then picks how to show the answer:

- **A single aggregate** (e.g. a count) → big-number tiles
- **Grouped results** → bar chart (with a metric picker when there are several aggregates) + table
- **Records** → table (first 50; nested objects like `ModelConfiguration/Name` become columns)
- **A query that fails** → the OData error message, instead of an empty result

### Phrase search with synonyms

Searching free text (`UserPrompt`, `Completion`, `ErrorMessage`, `Feedback/Comment`) matches
**the idea, not just the wording**. Asking for prompts that mention *"the best product"* also
counts *"the greatest product"*, *"the finest product"*, *"the top product"*, and so on.

**Exact match is opt-in.** Ask for it explicitly:

- in words: *"exactly"*, *"exact phrase"*, *"verbatim"*, *"literally"*, *"no synonyms"*, …
- or with the syntax `exact:"the best product"`, which the server always enforces, whatever the model returns

Putting a phrase in quotes on its own does **not** make it exact, because people quote phrases just to mark them.

How it works:

1. Claude doesn't write `contains()` for text fields. It returns a `textSearches` entry
   (field, phrase, `exact`, synonym `variants`) and a `{{t0}}` placeholder where the condition belongs.
2. `TextSearchExpansion` swaps each placeholder for a case-insensitive OR of `contains()` calls.
   It escapes quotes, lowercases, removes duplicates, caps variants at 10, and checks the field against an allowlist.
3. The response includes `textSearches`, so the dashboard shows exactly which wordings were searched.

Matching is by substring, so a variant only matches text that contains it literally.

### Tags and auto-tagging

Tags are stored in one form: trimmed, lowercase, spaces → `-`, no duplicates. Query them with a lambda:

```
GET /odata/LlmInteractions?$filter=Tags/any(t: t eq 'product')
GET /odata/LlmInteractions?$apply=filter(Tags/any(t: t eq 'pricing'))/aggregate($count as Count)
```

`POST /odata/LlmInteractions` **without** `tags` saves the interaction immediately and queues it.
A background worker asks Claude (structured outputs) for 1–4 tags and writes them back about a second later.

- **Reuses the existing vocabulary**, so you get `pricing` rather than `fees`, `prices` and `pricing`.
  A new tag is created only when nothing fits.
- **Never blocks or breaks ingestion.** 429/5xx/network errors are retried up to 3 times. Other failures
  are logged and the interaction stays untagged (`$filter=not Tags/any()`).
- **Tags you send win.** If the request includes `tags`, they're stored (normalized) and Claude isn't called.

```json
"AutoTagging": {
  "Enabled": true,        // false = never call Claude for tags
  "MaxPromptChars": 8000  // only the start of very long prompts is sent for tagging
}
```

---

## Ingest a New Interaction (POST)

```bash
curl -X POST https://localhost:5001/odata/LlmInteractions \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "user-demo",
    "sessionId": "session-live-001",
    "userPrompt": "Explain transformer attention mechanisms",
    "completion": "Attention allows the model to weigh the relevance of...",
    "inputTokens": 420,
    "outputTokens": 310,
    "latencyMs": 1850,
    "cost": 0.00591,
    "success": true,
    "modelConfigurationId": 1
  }'
```

Leave out `tags` (as above) to have them generated, or send `"tags": ["machine-learning"]` to set them yourself.

---

## Project Structure

```
LlmObservability.sln
├── LlmObservability.Api/
│   ├── Controllers/
│   │   ├── LlmInteractionsController.cs      OData GET + POST (queues auto-tagging)
│   │   ├── ModelConfigurationsController.cs  OData GET
│   │   ├── PromptTemplatesController.cs      OData GET
│   │   ├── UserFeedbacksController.cs        OData GET
│   │   ├── StatsController.cs                GET /api/stats (dashboard KPIs and charts)
│   │   └── NaturalLanguageQueryController.cs POST /api/naturallanguagequery
│   ├── Data/
│   │   ├── AppDbContext.cs                   SQLite; CreatedDay computed column
│   │   └── DataSeeder.cs                     60 days of consistent synthetic telemetry
│   ├── Models/                               LlmInteraction, ModelConfiguration, ...
│   ├── OData/EdmModelBuilder.cs
│   └── Services/
│       ├── AnthropicMessagesClient.cs        Shared Anthropic Messages API call
│       ├── ODataQueryTranslatorService.cs    NL → OData via Claude
│       ├── FoundryQueryTranslatorService.cs  NL → OData via any Microsoft Foundry model (optional)
│       ├── QueryTranslatorHelper.cs          System prompt + response parsing + URL building
│       ├── TextSearchExpansion.cs            {{t0}} placeholders → synonym-aware contains() clauses
│       ├── InteractionTagger.cs              Claude suggests tags for a prompt
│       ├── AutoTagging.cs                    Tagging queue + background worker
│       ├── TagVocabulary.cs                  Tag normalization + current tag list
│       └── ContentSafetyService.cs           Azure AI Content Safety check (optional)
└── LlmObservability.Web/
    ├── Controllers/
    │   ├── DashboardController.cs
    │   └── QueryController.cs                NL query → OData → answer + results table
    ├── Services/NlQueryProxyService.cs
    └── Views/Dashboard/Index.cshtml          KPIs, charts, query, answer, live auto-tagging
```

---

## Key Technical Decisions

| Decision | Rationale |
|----------|-----------|
| OData v4 via `Microsoft.AspNetCore.OData` | `[EnableQuery]` on `IQueryable<T>`: filters, grouping and aggregates are pushed down to the database with no hand-written query code |
| SQLite | A single file, no setup, and data that survives restarts. Money and latency are `double` because SQLite can't sum or average `decimal` |
| `CreatedDay` computed column | OData's date functions don't work in `groupby` on SQLite, so per-day stats group by a stored `yyyy-MM-dd` column |
| LLM proposes, server composes text search | Claude decides meaning (phrase, synonyms, exact or not); the server owns escaping, casing and limits |
| Today's date and tag list in the prompt | Relative dates resolve correctly, and wording maps onto real tags |
| Auto-tagging off the request path | Ingestion stays fast and never fails because of the LLM |
| Foundry via the OpenAI SDK + `/openai/v1/` | Microsoft's replacement for the retired Azure AI Inference SDK; one client for every Foundry model |
| Translators listed by the API (`/api/naturallanguagequery/providers`) | The dashboard shows real model names and disables translators that aren't configured |
| Optional Azure services skip themselves | App Insights, Content Safety and Phi-4 only switch on when real settings replace the `YOUR_...` placeholders |

## Known Limitations (demo scope)

- No authentication, rate limiting or spend caps on the API — run it locally only.
- Text matching is by substring: no word boundaries and no meaning-based search.
- No automated tests; translation quality is checked by hand with the demo script.
- `LlmInteractions` has no server-side paging (OData would also page the `Tags` collection, which SQLite can't do), so use `$top` (max 100).
