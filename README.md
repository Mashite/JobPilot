# JobPilot — AI-Powered Job Application Assistant

JobPilot is an AI-powered job application management platform built with **.NET 10, Blazor, and SQL Server**. It uses OpenAI by default, with local Ollama and Google Gemini available by configuration.

The project explores how **local Large Language Models (LLMs), AI agents, and traditional software engineering** can work together to create practical, intelligent applications.

JobPilot analyzes job descriptions, compares requirements against a candidate's professional profile, generates tailored resumes based on verified experience, and tracks job applications.

## Key Features

- **AI-Powered Job Analysis:** Extracts job requirements and evaluates compatibility with a candidate's skills and experience.
- **Intelligent Skill Matching:** Combines LLM-based requirement extraction with deterministic C# matching logic.
- **Tailored Resume Generation:** Creates editable resumes using verified candidate information.
- **Word Document Export:** Generates downloadable DOCX resumes using the Open XML SDK.
- **AI Agent with Tool Calling:** Uses the configured AI provider to select and orchestrate job analysis and resume generation.
- **Interactive AI Assistant:** Provides a conversational interface built with Blazor.
- **Application Tracking:** Stores job applications, matching scores, statuses, and resume drafts in SQL Server.
- **Local AI Processing:** Runs with Ollama without requiring paid cloud AI APIs.

## Technology Stack

| Layer | Technologies |
| --- | --- |
| Backend | C#, ASP.NET Core 10 Minimal API |
| Frontend | Blazor Web App (Interactive Server) |
| AI | OpenAI GPT-6 Luna, Ollama with Qwen 2.5, or Google Gemini |
| Database | SQL Server, Entity Framework Core 10 |
| Document Generation | Open XML SDK |
| Testing | xUnit |
| Architecture | Dependency Injection, REST APIs, Service-Based Design |

## AI Engineering Concepts

JobPilot is also a hands-on exploration of modern AI application development, including:

- LLM integration and prompt engineering
- Structured AI outputs
- AI tool calling and multi-step orchestration
- Combining probabilistic AI outputs with deterministic business logic
- Grounding generated content in verified candidate data
- Local AI deployment and integration with .NET applications

## Configure the AI provider

Set `AI:Provider` in API configuration to `OpenAI`, `Ollama`, or `Gemini`, then restart the API. The following PowerShell examples use environment variables in the shell that starts the API. JobPilot uses the selected provider for job analysis, skill extraction, resume drafting, and agent tool calling.

### OpenAI

The default provider is OpenAI, using the Responses API with `gpt-6-luna`. Use the existing `AI:OpenAI:ApiKey` in JobPilot.Api User Secrets. In other environments, supply `AI__OpenAI__ApiKey` through the API process environment. A missing key prevents the API from starting when OpenAI is selected.

| API setting | Default |
| --- | --- |
| `AI:Provider` | `OpenAI` |
| `AI:OpenAI:Model` | `gpt-6-luna` |
| `AI:OpenAI:MaxOutputTokens` | `16384` (application ceiling) |
| `AI:OpenAI:TokenLimits:Agent` | `4096` |
| `AI:OpenAI:TokenLimits:JobAnalysis` | `8192` |
| `AI:OpenAI:TokenLimits:SemanticMatching` | `8192` |
| `AI:OpenAI:TokenLimits:ResumeGeneration` | `8192` |
| `AI:OpenAI:TokenLimits:FinalResponse` | `2048` |
| `AI:OpenAI:RequestTimeoutSeconds` | `60` |

The Responses client uses `store: false` and replays tool context within each agent request. OpenAI receives prompts and any profile-derived data needed for the selected operation. The API logs the model, outcome, elapsed time, and available input, output, and total token counts for each attempt. It omits prompts, CV contents, and API keys. API access and usage charges depend on your project. [GPT-6 Luna model ID, capabilities and limits](https://developers.openai.com/api/docs/models/gpt-6-luna)

### Ollama

Start Ollama and install the default model with `ollama pull qwen2.5:3b`. The defaults work with the standard local Ollama installation. To override them, set:

```powershell
$env:AI__Provider = 'Ollama'
$env:AI__Ollama__Model = 'qwen2.5:3b'
$env:AI__Ollama__BaseUrl = 'http://localhost:11434/'
dotnet run --project JobPilot.Api
```

### Gemini

Create a Gemini Developer API key in Google AI Studio. Choose a text model available to your project that supports structured output and function calling. Store the key in .NET User Secrets for local development:

```powershell
$jobPilotGeminiKey = Read-Host 'Gemini API key' -MaskInput
dotnet user-secrets set 'AI:Gemini:ApiKey' $jobPilotGeminiKey --project JobPilot.Api | Out-Null
Remove-Variable jobPilotGeminiKey
$env:AI__Provider = 'Gemini'
$env:AI__Gemini__Model = 'gemini-3.1-flash-lite'
dotnet run --project JobPilot.Api
```

JobPilot calls Gemini through the official Google GenAI .NET SDK. The default Gemini model is `gemini-3.1-flash-lite`. User Secrets load when the API runs in the Development environment. For other environments, supply `AI__Gemini__ApiKey` through the API process environment. Keep the key out of repository files. Switch back by setting `AI__Provider` to `Ollama` and restarting the API.

### Test the selected provider

Run the API in Development, then call the loopback diagnostic from PowerShell:

```powershell
Invoke-RestMethod -Method Post -Uri 'http://localhost:5287/api/ai/test-connection' -SkipHttpErrorCheck
```

The response includes the configured provider and model, elapsed milliseconds, and either a short reply or a diagnostic code. For OpenAI or Gemini errors, `providerStatusCode` reports the original provider HTTP status, even if JobPilot returns a gateway status. A 401/403 indicates a credential or access issue, a 404 indicates an unavailable model, a 429 indicates a quota or rate limit, and 500/502/503/504 indicate a temporary provider failure. Transient requests can retry up to twice. JobPilot honors `Retry-After` and returns the error if the requested delay exceeds ten seconds. OpenAI insufficient-quota errors return immediately. API logs omit raw upstream response bodies.

The route is unavailable outside Development and rejects non-loopback and browser-origin requests. It sends a fixed text prompt without profile data. To check tool calling separately, use AI Assistant with a synthetic profile and ask it to analyze a listing and draft a resume. Verify that both tool results appear.

Free-tier access and limits depend on the model and Google project. Rate or quota limits can reject requests, and a project with billing enabled may incur charges. An agent turn can make several model requests. JobPilot never switches providers or models automatically. When Gemini is selected, prompts and relevant profile excerpts can be sent to Google. [API keys](https://ai.google.dev/gemini-api/docs/api-key) · [Rate limits](https://ai.google.dev/gemini-api/docs/rate-limits) · [Pricing and data use](https://ai.google.dev/gemini-api/docs/pricing)

## Master Profile Updates

Master CV, matching and resume generation use the same SQL Server profile. Apply the additive `20261123120000_AddCareerProfile` migration to the configured JobPilot database with the EF 10 CLI. If an older global tool is installed, use a project-local tool from PowerShell.

```powershell
dotnet tool install dotnet-ef --tool-path ./JobPilot.Api/obj/ef-tools --version 10.0.15
./JobPilot.Api/obj/ef-tools/dotnet-ef.exe database update --project JobPilot.Api
```

Until a profile is saved, the API reads the previous private JSON file at `JobPilot.Api/Data/candidate-profile.json` or `CandidateProfile__Path`. The first explicit save or confirmed merge writes to SQL Server and keeps that file as a local backup. The database stores a versioned snapshot of structured experience, competencies, evidence, and sections flagged for review in the `CareerProfiles` table.

1. Open **Master CV** and choose the private DOCX directly. For a server-side local file, place it in `JobPilot.Api/Data/Import/MasterCV.docx` and choose **Preview local import file**. Git ignores that directory.
2. Review the full **Professional Profile**, each role's **Used Technologies** and projects, the education entries, and the source sections marked for review. Edit or remove incorrect entries and uncheck evidence you want excluded.
3. If the document's professional profile should replace the saved text, select **Replace my saved Professional Profile** in the preview. Confirm the merge after review. Other conflicting values stay in Master CV for you to resolve. A stale preview requires reopening the import and reviewing the latest profile.

DOCX extraction and preview use Open XML locally. Only explicitly documented evidence accepted in the preview and competencies you attest to become verified. Related competencies, uncertain sections, and saved evidence stay visible in Master CV.

`GET /api/profile` returns an ETag. Profile saves and import confirmations send that value in `If-Match`. If another editor saves first, the stale update receives HTTP 412. Copy any unsaved edits you want to keep, then reload or preview again.

## Review a job and save a resume

With OpenAI selected, Job Analyzer checks exact names and aliases locally, then evaluates unresolved broad capabilities in batches using selected Master CV evidence. Review the supporting excerpts under **AI Semantic Match**. Exact, equivalent, and supported semantic matches earn full credit, partial matches earn half, and required items have three times the weight of preferred items. If semantic evaluation is unavailable, use **Retry semantic evaluation**.

Apply the additive `AddJobMatchReviews` migration with `dotnet ef database update --project JobPilot.Api` (or the project-local EF 10 CLI above). **Save job review** stores manual decisions for the analyzed listing without changing Master CV. **Save to Master Profile** is a separate action for verified competencies. To retrieve the job review, analyze the same listing again.

### Run an AI Assistant workflow

**Candidate Intelligence** evaluates the saved Master CV as a separate, versioned assessment. Choose **Evaluate Master CV** to create one or refresh it after changes to your profile. Job matching reuses its evidence references for the current profile version. In **Experience discovery**, review grouped questions and explicitly confirm your answers. Professional and personal project experience with supporting explanations become verified evidence. Familiarity, learning, and no-experience answers are retained for your review.

Apply the additive `AddCandidateIntelligence` migration before using this page. Job analysis shows both weighted requirement coverage and equally weighted competency-area coverage. Each option in an explicit alternative stays visible. A valid Master CV and job listing allow resume drafting even when analysis is provisional or some requirements are missing.

Apply the additive `AddAgentWorkflows` migration before running a chat workflow with a job listing. Paste a listing and ask the assistant to analyze it, or ask for analysis and a CV. The assistant saves analysis before drafting and stores the completed draft privately so you can open it in Resume Editor or export DOCX. Attaching it as a saved application resume remains an explicit action. **Retry unfinished step** reuses the recorded intent and completed analysis.

OpenAI retries a step once when its response reports `max_output_tokens`, subject to the `AI:OpenAI:TokenRetryLimit` and application ceiling. Set per-step reasoning effort under `AI:OpenAI:ReasoningEffort`. For GPT-6 Luna, semantic matching defaults to `low` and the other steps default to `none`. Use `AI:OpenAI:ModelOutputLimit` to configure the documented ceiling when selecting another model. Logs include the stage, response ID, incomplete reason and token counts without prompt or CV contents.

Semantic matching runs sequential batches of six by default. Configure `AI:SemanticMatching:BatchSize` (1–16), `AI:SemanticMatching:MaxOutputTokens` (defaults to the existing 8,192 semantic budget), and `AI:SemanticMatching:MaxRetryAttempts` (0 or 1). A token-limited batch splits in two for its retry, or gets a larger budget for a single requirement when the configured cap allows it. Validated results are kept. The Assistant saves them after each batch and shows a provisional score while requirements remain pending. The standalone analyzer retains them in its 30-minute cache. Retrying evaluates the pending requirements for the same saved profile and listing. Logs include batch sizes, token usage, reasoning effort, response status, incomplete reason, and validation counts without CV text.

In **Job Analyzer**, review exact, equivalent, related and missing requirements. Exact and equivalent matches count toward the score. Related and missing review decisions stay on the page. Explicitly confirmed competencies can be saved with evidence. Choose **Include all Master CV positions** before generating if you want the full employment history in the draft. **Back to matching review** keeps the current draft and corrections while you switch views. Save the job and edited resume, or download the current draft as Word. Saved applications list their resume versions in the **Resume** tab. Saving an unchanged latest version reuses it. An unsaved Analyzer draft is held in the current page and is lost on reload.

Job requirement extraction is cached in memory for 15 minutes, with up to 64 listings. Matching, scoring and corrections run locally. Resume generation sends a bounded shortlist of skills and role highlights. The editor assembles employers, dates, and other verified details locally. In Development, refresh **Development diagnostics** in Job Analyzer to see aggregate matching results and OpenAI requests and tokens. To estimate cost, set `AI__OpenAI__InputUsdPerMillionTokens` and `AI__OpenAI__OutputUsdPerMillionTokens` to your configured model's current USD rates. The estimate uses reported token totals and resets with the API process.

The profile update endpoint is intended for the existing single-user, loopback deployment. Keep both applications bound to localhost for this workflow. A shared deployment needs user authentication and per-user authorization before exposing profile updates.

If your local API runs on a different port, set `JobPilotApi__BaseAddress` in the Web app environment to that loopback URL.

## Project Status

**Active Development**

JobPilot is an evolving learning and engineering project. Planned improvements include persistent agent conversation memory, enhanced agent workflows, human-in-the-loop approval, and more advanced application management capabilities.

## Project Goal

To demonstrate how AI capabilities can be integrated into real-world .NET applications while maintaining control over business logic, data integrity, and application architecture.
