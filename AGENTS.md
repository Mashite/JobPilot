## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

For `$graphify` requests, use the project skill at `.codex/skills/graphify/SKILL.md`.

- From the solution root in PowerShell, use `./.graphify-venv/Scripts/graphify.exe` for Graphify commands. If the virtual environment is absent, a `graphify` executable on `PATH` also works.
- Use `query "<question>"` to orient codebase investigations, `path "<A>" "<B>"` to trace relationships, and `explain "<concept>"` for focused lookups. Use `graphify-out/GRAPH_REPORT.md` for an overview and verify details against the source as needed.
- After changing indexed code, refresh the graph with `$env:GRAPHIFY_BACKEND = 'none'; ./.graphify-venv/Scripts/graphify.exe update .` in PowerShell. The project uses local code extraction.
