# JobPilot — AI-Powered Job Application Assistant

JobPilot is an AI-powered job application management platform built with **.NET 10, Blazor, and SQL Server**. It uses local Ollama by default and can also use Google Gemini.

The project explores how **local Large Language Models (LLMs), AI agents, and traditional software engineering** can work together to create practical, intelligent applications.

JobPilot analyzes job descriptions, compares requirements against a candidate's professional profile, generates tailored resumes based on verified experience, and tracks job applications.

## Key Features

- **AI-Powered Job Analysis:** Extracts job requirements and evaluates compatibility with a candidate's skills and experience.
- **Intelligent Skill Matching:** Combines LLM-based requirement extraction with deterministic C# matching logic.
- **Tailored Resume Generation:** Creates editable resumes using verified candidate information.
- **Word Document Export:** Generates downloadable DOCX resumes using the Open XML SDK.
- **AI Agent with Tool Calling:** Uses Ollama to select and orchestrate application tools, including job analysis and resume generation.
- **Interactive AI Assistant:** Provides a conversational interface built with Blazor.
- **Application Tracking:** Stores job applications, matching scores, statuses, and resume drafts in SQL Server.
- **Local AI Processing:** Runs with Ollama without requiring paid cloud AI APIs.

## Technology Stack

| Layer | Technologies |
| --- | --- |
| Backend | C#, ASP.NET Core 10 Minimal API |
| Frontend | Blazor Web App (Interactive Server) |
| AI | Ollama with Qwen 2.5 or Google Gemini, LLM tool calling |
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

Set `AI:Provider` in API configuration to `Ollama` or `Gemini`, then restart the API. The following PowerShell examples use environment variables in the shell that starts the API. JobPilot uses the selected provider for job analysis, skill extraction, resume drafting, and agent tool calling.

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
$env:AI__Gemini__Model = '<your-model-id>'
dotnet run --project JobPilot.Api
```

User Secrets load when the API runs in the Development environment. For other environments, supply `AI__Gemini__ApiKey` through the API process environment. Keep the key out of repository files. Switch back by setting `AI__Provider` to `Ollama` and restarting the API.

Free-tier access and limits depend on the model and Google project. Rate or quota limits can reject requests, and a project with billing enabled may incur charges. An agent turn can make several model requests. JobPilot never switches providers or models automatically. When Gemini is selected, prompts and relevant profile-derived data can be sent to Google, including resume tool results during agent chat. Check Google's applicable data-use terms before using a real profile. [API keys](https://ai.google.dev/gemini-api/docs/api-key) · [Rate limits](https://ai.google.dev/gemini-api/docs/rate-limits) · [Pricing and data use](https://ai.google.dev/gemini-api/docs/pricing)

## Master Profile Updates

Open **Master CV** from the sidebar to maintain verified profile information. Job Analyzer can also add explicitly confirmed skills. Both workflows use the API's local `Data/candidate-profile.json`. For a read-only deployment directory, copy the profile to a private, writable location outside the repository and configure the API with the `CandidateProfile__Path` environment variable.

Existing profiles load directly. Master CV shows legacy education and project text as editable entries. When you explicitly save, structured details are written alongside the existing text fields for compatibility. The API uses the latest file for matching and resume generation. No SQL migration or import is needed.

`GET /api/profile` returns an ETag. A full update via `PUT /api/profile` requires that value in `If-Match`. If Skill Review or another editor saves first, the stale update receives HTTP 412. Copy any unsaved edits you want to keep, then reload the latest profile. The resume format uses verified skills, role details, education, and projects. Contact information and certifications stay in Master CV.

The profile update endpoint is intended for the existing single-user, loopback deployment. Keep both applications bound to localhost for this workflow. A shared deployment needs user authentication and per-user authorization before exposing profile updates.

If your local API runs on a different port, set `JobPilotApi__BaseAddress` in the Web app environment to that loopback URL.

## Project Status

**Active Development**

JobPilot is an evolving learning and engineering project. Planned improvements include persistent agent conversation memory, enhanced agent workflows, human-in-the-loop approval, and more advanced application management capabilities.

## Project Goal

To demonstrate how AI capabilities can be integrated into real-world .NET applications while maintaining control over business logic, data integrity, and application architecture.
