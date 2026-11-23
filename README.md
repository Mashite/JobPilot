# JobPilot — AI-Powered Job Application Assistant

JobPilot is an AI-powered job application management platform built with **.NET 10, Blazor, SQL Server, and Ollama**.

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
| AI | Ollama, Qwen 2.5, LLM Tool Calling |
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
