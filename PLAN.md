# ProjectMemory — Implementation Plan

## What's Built (Phase 1 + partial Phase 2)

- MCP server with STDIO transport, DI, SQLite database
- 6 tools: `get_context`, `project_query`, `project_store`, `project_forget`, `record_lesson`, `mark_useful`
- Schema: `knowledge`, `lessons`, `session_injections`, `graduations` tables
- Knowledge deduplication (same category+scope+content bumps confidence)
- Lesson deduplication (exact text match bumps recurrence)
- Scope-filtered context injection for knowledge
- 15 tests passing
- Wired up to AutonomousAgents via `.github/mcp.json` and `copilot-instructions.md`

## Remaining Work

### Phase 2: Lesson Capture (finish)

- [ ] **Fuzzy text similarity dedup for lessons** — Currently exact match only. Implement token overlap (split on whitespace, compute Jaccard similarity). If >70% overlap with existing active lesson, merge instead of creating new. Add to `RecordLesson` in Database.fs.

- [ ] **Scope-aware lesson filtering in `get_context`** — Lessons currently return all active lessons regardless of scope. Filter like knowledge does: match lesson scope against requested scope using GLOB.

- [ ] **Post-session extraction** — This is the hardest piece. Needs an LLM call to analyze the session transcript and extract lessons. Options:
  - A) Standalone CLI command (`dotnet run -- extract <transcript-path>`) that reads a transcript file and calls an LLM
  - B) A 7th MCP tool (`extract_lessons(transcript: string)`) the model calls on `/compact` or session end
  - C) Hook into Copilot CLI's session lifecycle (if supported)
  - **Recommendation:** Start with option B — it's the simplest and works today. The model can call it when prompted or at session end.

### Phase 3: Consolidation + Graduation

- [ ] **Consolidation logic** — Periodic cleanup of the lessons table. Rules:
  - Two lessons with >80% text similarity → merge (keep more specific, sum recurrence)
  - Lesson with recurrence ≥ 5 AND confidence ≥ 0.7 → promote to `knowledge` table as `source='learned'`
  - Lesson not seen in 30+ days AND recurrence = 1 → mark `status='superseded'`
  - Contradicting lessons → keep higher confidence, supersede other
  - Trigger: new MCP tool `consolidate()` or automatic on every Nth `record_lesson` call

- [ ] **Auto-graduation** — When knowledge entry reaches `confidence ≥ 0.9 AND session_count ≥ 10`:
  - Format as concise instruction
  - Append to `.github/copilot-instructions.md` under `## Learned Conventions` section
  - Record in `graduations` table
  - Mark lesson `status='graduated'`
  - Safety: never modify user-written sections, only append to auto-generated section

- [ ] **Session injection audit trail** — When `get_context()` returns items, record them in `session_injections` table. Requires a session ID — either passed as parameter or auto-generated.

- [ ] **Wire up `mark_useful` end-to-end** — Currently the tool exists but there are no injection records to update. Once audit trail is populated, this closes the feedback loop.

### Phase 4: Polish

- [ ] **Token budget for `get_context`** — Currently returns up to N items. Should estimate token count and stop when budget (default 2000 tokens) is reached. Prioritize by: confidence × recency × scope relevance.

- [ ] **Import/export** — `export_knowledge()` → JSON dump, `import_knowledge(json)` → merge into DB. Useful for sharing across team members or seeding new repos.

- [ ] **Better error messages** — Currently raw exception messages. Add user-friendly error text.

- [ ] **Startup validation** — Verify DB schema version on start, run migrations if needed. Add a `schema_version` table.

## Architecture Notes

```
ProjectMemory.Server/
├── Database.fs    # SQLite schema, CRUD, context formatting, dedup
├── Tools.fs       # MCP tool definitions (thin wrappers over Database)
└── Program.fs     # Host setup, STDIO transport, DI

ProjectMemory.Tests/
└── Tests.fs       # Database unit tests (temp DB per test)
```

Key design decisions:
- **Database.fs does all logic** — Tools.fs is a thin MCP adapter layer
- **Dedup is on write** — No background jobs, everything happens synchronously
- **STDIO transport** — Client launches server as subprocess, communicates via stdin/stdout
- **DB path** — `.project-memory/memory.db` in working directory, override via `PROJECT_MEMORY_DB` env var
- **WAL mode** — For concurrent read/write safety
