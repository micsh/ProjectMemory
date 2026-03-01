# ProjectMemory

A persistent project knowledge store that gives AI assistants memory across sessions. Delivered as an MCP (Model Context Protocol) server.

## What It Does

ProjectMemory maintains a SQLite database of project-specific knowledge — conventions, decisions, known issues, lessons learned. AI assistants read from it at session start and write to it when they learn something new. Over time, the database builds a growing understanding of your project.

## Tools

| Tool | Purpose |
|------|---------|
| `get_context` | Get relevant knowledge for current work context |
| `project_query` | Query the knowledge database with SQL |
| `project_store` | Store a knowledge entry (convention, decision, known issue, etc.) |
| `project_forget` | Remove wrong or outdated knowledge |
| `record_lesson` | Record something learned during a session |
| `mark_useful` | Feedback on whether injected knowledge helped |
| `consolidate` | Merge near-duplicate lessons, promote high-recurrence lessons, prune stale entries |
| `graduate` | Promote high-confidence knowledge to copilot-instructions.md |
| `export_knowledge` | Export all knowledge and lessons as JSON |
| `import_knowledge` | Import knowledge from JSON (deduplicates automatically) |

## Setup

### Option 1: Self-Contained Executable (no SDK required)

Publish a single-file executable:

```bash
dotnet publish src/ProjectMemory.Server -c Release -r win-x64 -o publish/win-x64
# Also available: linux-x64, osx-arm64
```

Configure in `~/.copilot/mcp-config.json` (user-level) or `.github/mcp.json` (repo-level):

```json
{
  "mcpServers": {
    "project-memory": {
      "command": "C:/path/to/ProjectMemory.Server.exe",
      "args": []
    }
  }
}
```

### Option 2: NuGet Tool (requires .NET 10 runtime)

Pack and install as a global dotnet tool:

```bash
dotnet pack src/ProjectMemory.Server -c Release -o nupkg
dotnet tool install -g --add-source ./nupkg ProjectMemory
```

Then configure:

```json
{
  "mcpServers": {
    "project-memory": {
      "command": "project-memory",
      "args": []
    }
  }
}
```

Update later with:

```bash
dotnet tool update -g --add-source ./nupkg ProjectMemory
```

### Option 3: From Source (requires .NET 10 SDK)

```json
{
  "mcpServers": {
    "project-memory": {
      "command": "dotnet",
      "args": ["run", "--no-build", "-c", "Release", "--project", "/path/to/ProjectMemory/src/ProjectMemory.Server"]
    }
  }
}
```

Build Release first: `dotnet build -c Release`

### Database Location

The database is created at `.project-memory/memory.db` in the working directory. Override with `PROJECT_MEMORY_DB` environment variable.

### System Prompt Addition

For best results, add this to your copilot instructions:

```
You have access to a project memory database via the project-memory MCP server.
- At the start of work, call get_context() to load project-specific knowledge.
- When the user corrects you, call record_lesson() with what you learned.
- When you discover a project-specific pattern, call project_store() to remember it.
- When you find stored knowledge is wrong, call project_forget() to remove it.
```

## Knowledge Categories

| Category | Use For |
|----------|---------|
| `convention` | How things are done (build commands, naming patterns, code style) |
| `decision` | Why something was chosen (JWT over sessions, specific library picks) |
| `known_issue` | Bugs, flaky tests, workarounds |
| `file_note` | File-specific context (purpose, quirks, dependencies) |
| `preference` | User preferences (code style, approach preferences) |

## Scope Filtering

Knowledge and lessons can be scoped to specific files using glob patterns:

- `*` — project-wide (default)
- `src/Auth/*` — everything in Auth directory
- `tests/**/*Integration*` — integration test files
- `*.fsproj` — all F# project files

When `get_context(scope: "src/Auth/Login.fs")` is called, it returns:
1. All project-wide (`*`) knowledge and lessons
2. Items scoped to patterns matching that path (e.g., `src/Auth/*`)

## Learning Pipeline

1. **Lesson capture** — The assistant records lessons when corrected. Fuzzy deduplication (Jaccard similarity >70%) prevents near-duplicate entries.
2. **Consolidation** — Merges similar lessons (>80% similarity), promotes lessons with high recurrence (≥5) and confidence (≥0.7) to knowledge, prunes stale entries.
3. **Graduation** — Knowledge entries reaching confidence ≥0.9 and session count ≥10 are appended to `.github/copilot-instructions.md` under an auto-managed section. Configurable via `PROJECT_MEMORY_INSTRUCTIONS_FILE` env var.

## Build & Test

```bash
dotnet build
dotnet test
dotnet build -c Release          # For MCP / distribution
```

## Architecture

```
ProjectMemory.Server/
├── Schema.fs           # DDL and schema versioning
├── Similarity.fs       # Jaccard text similarity (pure function)
├── Formatting.fs       # Context output formatting with token budgeting (pure function)
├── InstructionsFile.fs # Graduation file I/O (pure function)
├── Database.fs         # ProjectMemoryDb class — all DB operations
├── Tools.fs            # MCP tool adapter layer with input validation
└── Program.fs          # Host setup, DI, STDIO transport
```

The server uses STDIO transport — the MCP client (Copilot CLI, VS Code) launches it as a subprocess and communicates via stdin/stdout. All logging goes to stderr.
