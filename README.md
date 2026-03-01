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

## Setup

### Prerequisites

- .NET 10 SDK

### Configure in Your Repository

Add to `.github/mcp.json`:

```json
{
  "servers": {
    "project-memory": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/ProjectMemory.Server"]
    }
  }
}
```

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

Knowledge entries can be scoped to specific files using glob patterns:

- `*` — project-wide (default)
- `src/Auth/*` — everything in Auth directory
- `tests/**/*Integration*` — integration test files
- `*.fsproj` — all F# project files

When `get_context(scope: "src/Auth/Login.fs")` is called, it returns:
1. All project-wide (`*`) knowledge
2. Knowledge scoped to patterns matching that path (e.g., `src/Auth/*`)

## Learning Pipeline (Roadmap)

**Phase 1** (current): Manual knowledge store — you tell it what to remember.

**Phase 2**: Lesson capture — the assistant records lessons when corrected, and deduplicates them automatically.

**Phase 3**: Consolidation & graduation — high-confidence lessons (seen across many sessions) auto-graduate to `.github/copilot-instructions.md`.

## Build & Test

```bash
dotnet build
dotnet test
```

## Architecture

```
ProjectMemory.Server/
├── Database.fs    # SQLite schema, CRUD, context formatting
├── Tools.fs       # MCP tool definitions (thin wrappers)
└── Program.fs     # MCP server entry point (STDIO transport)
```

The server uses STDIO transport — the MCP client (Copilot CLI, VS Code) launches it as a subprocess and communicates via stdin/stdout. All logging goes to stderr.
