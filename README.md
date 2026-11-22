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

## Project Status

**Active Development**

JobPilot is an evolving learning and engineering project. Planned improvements include persistent agent conversation memory, enhanced agent workflows, human-in-the-loop approval, and more advanced application management capabilities.

## Project Goal

To demonstrate how AI capabilities can be integrated into real-world .NET applications while maintaining control over business logic, data integrity, and application architecture.
