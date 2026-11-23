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
| `AI:OpenAI:MaxOutputTokens` | `4096` |
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

Free-tier access and limits depend on the model and Google project. Rate or quota limits can reject requests, and a project with billing enabled may incur charges. An agent turn can make several model requests. JobPilot never switches providers or models automatically. When Gemini is selected, prompts and relevant profile-derived data can be sent to Google, including resume tool results during agent chat. Check Google's applicable data-use terms before using a real profile. [API keys](https://ai.google.dev/gemini-api/docs/api-key) · [Rate limits](https://ai.google.dev/gemini-api/docs/rate-limits) · [Pricing and data use](https://ai.google.dev/gemini-api/docs/pricing)

## Master Profile Updates

Open **Master CV** from the sidebar to maintain verified profile information. Job Analyzer can also add explicitly confirmed skills. Both workflows use the API's local `Data/candidate-profile.json`. For a read-only deployment directory, copy the profile to a private, writable location outside the repository and configure the API with the `CandidateProfile__Path` environment variable.

Existing profiles load directly. Master CV shows legacy education and project text as editable entries. When you explicitly save, structured details are written alongside the existing text fields for compatibility. The API uses the latest file for matching and resume generation. No SQL migration or import is needed.

`GET /api/profile` returns an ETag. A full update via `PUT /api/profile` requires that value in `If-Match`. If Skill Review or another editor saves first, the stale update receives HTTP 412. Copy any unsaved edits you want to keep, then reload the latest profile. The resume format uses verified skills, role details, education, projects, and certifications. Contact information stays in Master CV.

## Review a job and save a resume

In **Job Analyzer**, analyze the listing and confirm any corrections you can support. Choose **Generate Tailored Resume** to open the editor. **Back to matching review** keeps the current draft and corrections while you switch views. Save the job and edited resume, or download the current draft as Word. Saved applications list their resume versions in the **Resume** tab. Saving an unchanged latest version reuses it. An unsaved Analyzer draft is held in the current page and is lost on reload.

The profile update endpoint is intended for the existing single-user, loopback deployment. Keep both applications bound to localhost for this workflow. A shared deployment needs user authentication and per-user authorization before exposing profile updates.

If your local API runs on a different port, set `JobPilotApi__BaseAddress` in the Web app environment to that loopback URL.

## Project Status

**Active Development**

JobPilot is an evolving learning and engineering project. Planned improvements include persistent agent conversation memory, enhanced agent workflows, human-in-the-loop approval, and more advanced application management capabilities.

## Project Goal

To demonstrate how AI capabilities can be integrated into real-world .NET applications while maintaining control over business logic, data integrity, and application architecture.
