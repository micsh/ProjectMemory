# Copilot Instructions for ProjectMemory

## Project Memory — READ THIS FIRST

You have access to a project memory MCP server. Follow these rules:

1. **SESSION START:** Call `get_context()` BEFORE doing any work. This loads project knowledge from prior sessions.
2. **AFTER EVERY USER CORRECTION:** Stop. Call `record_lesson()` with what you learned BEFORE continuing with the fix. Format: "When [situation], [action], because [reason]".
3. **WHEN YOU DISCOVER A PATTERN:** Call `project_store()` to remember conventions, decisions, or known issues.
4. **WHEN KNOWLEDGE IS WRONG:** Call `project_forget()` to remove it.
5. **SESSION END:** Before the user exits, review the session. Call `record_lesson()` for each non-obvious thing you learned.

## Project Overview
ProjectMemory is an MCP (Model Context Protocol) server that gives AI coding assistants persistent memory across sessions. It stores knowledge, lessons learned, and conventions in a local SQLite database.

## Architecture
- **F# (.NET 10)** with STDIO transport
- `Schema.fs` — DDL and schema versioning definitions
- `Similarity.fs` — Jaccard text similarity (pure function)
- `Formatting.fs` — Context output formatting with token budgeting (pure function)
- `InstructionsFile.fs` — Graduation file I/O for copilot-instructions.md (pure function)
- `Database.fs` — `ProjectMemoryDb` class with all DB operations
- `Tools.fs` — Thin MCP tool adapter layer with input validation
- `Program.fs` — Host setup, DI, STDIO transport

## Key Conventions
- All business logic belongs in `Database.fs` or the pure modules — `Tools.fs` is only for MCP wiring and validation
- Deduplication happens on write, not via background jobs
- DB path defaults to `.project-memory/memory.db` in the working directory (override via `PROJECT_MEMORY_DB` env var)
- WAL mode is enabled for concurrent read/write safety
- Schema changes require a migration entry in `Schema.migrations` with a bumped `currentVersion`
- Logging uses stderr only — stdout is reserved for the MCP JSON-RPC protocol
- Use `--no-build -c Release` when launching via MCP config to avoid exe locking

## Testing
- Tests are in `tests/ProjectMemory.Tests/Tests.fs` using xUnit
- Each test gets a fresh temp DB via `ProjectMemoryDb(tempPath)`
- Run with `dotnet test`
- Build Release for MCP with `dotnet build -c Release`

## Learned Conventions
<!-- Auto-managed by ProjectMemory. Do not edit this section manually. -->
