namespace ProjectMemory

type QueryResult = {
    Columns: string array
    Rows: Map<string, obj> array
}

module Schema =
    let currentVersion = 5

    let versionTable = """
        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER NOT NULL,
            applied_at TEXT NOT NULL
        );
    """

    let ddl = """
        CREATE TABLE IF NOT EXISTS knowledge (
            id TEXT PRIMARY KEY,
            category TEXT NOT NULL,
            scope TEXT NOT NULL DEFAULT '*',
            content TEXT NOT NULL,
            confidence REAL NOT NULL DEFAULT 0.5,
            source TEXT NOT NULL DEFAULT 'user_explicit',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            session_count INTEGER NOT NULL DEFAULT 1,
            last_session TEXT
        );

        CREATE TABLE IF NOT EXISTS lessons (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            lesson_text TEXT NOT NULL,
            agent_role TEXT,
            trigger TEXT NOT NULL,
            scope TEXT NOT NULL DEFAULT '*',
            source_ref TEXT,
            recurrence INTEGER NOT NULL DEFAULT 1,
            confidence REAL NOT NULL DEFAULT 0.3,
            status TEXT NOT NULL DEFAULT 'active',
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS session_injections (
            session_id TEXT NOT NULL,
            item_type TEXT NOT NULL,
            item_id TEXT NOT NULL,
            injected_at TEXT NOT NULL,
            was_useful INTEGER DEFAULT NULL
        );

        CREATE TABLE IF NOT EXISTS graduations (
            item_type TEXT NOT NULL DEFAULT 'lesson',
            item_id TEXT NOT NULL,
            target_file TEXT NOT NULL,
            graduated_at TEXT NOT NULL,
            instruction_text TEXT NOT NULL
        );
    """

    /// Migrations keyed by target version. Each version maps to a list of SQL statements
    /// executed together in one transaction. All statements + the version INSERT commit
    /// atomically — a crash mid-migration leaves schema_version unchanged so it re-runs.
    let migrations: (int * string list) list =
        let ftsCreate = "CREATE VIRTUAL TABLE IF NOT EXISTS lessons_fts USING fts5(lesson_text, content=lessons, content_rowid=id)"
        let ftsInsertTrigger =
            "CREATE TRIGGER IF NOT EXISTS lessons_fts_insert AFTER INSERT ON lessons BEGIN\n" +
            "  INSERT INTO lessons_fts(rowid, lesson_text) VALUES (new.id, new.lesson_text);\n" +
            "END"
        let ftsUpdateTrigger =
            "CREATE TRIGGER IF NOT EXISTS lessons_fts_update AFTER UPDATE ON lessons BEGIN\n" +
            "  INSERT INTO lessons_fts(lessons_fts, rowid, lesson_text) VALUES ('delete', old.id, old.lesson_text);\n" +
            "  INSERT INTO lessons_fts(rowid, lesson_text) VALUES (new.id, new.lesson_text);\n" +
            "END"
        let ftsDeleteTrigger =
            "CREATE TRIGGER IF NOT EXISTS lessons_fts_delete AFTER DELETE ON lessons BEGIN\n" +
            "  INSERT INTO lessons_fts(lessons_fts, rowid, lesson_text) VALUES ('delete', old.id, old.lesson_text);\n" +
            "END"
        [
            // v2: dedup constraint on session_injections
            (2, [ "CREATE UNIQUE INDEX IF NOT EXISTS uq_session_injections ON session_injections(session_id, item_type, item_id)" ])
            // v3: FTS5 virtual table for lessons — fast candidate pre-filtering in fuzzy
            // dedup (RecordLesson) and Consolidate. content= keeps it backed by lessons.
            // Explicit triggers keep the index in sync on INSERT/UPDATE/DELETE.
            // The rebuild command at the end ensures existing rows are indexed for DBs
            // that had lessons before migrating to v3. No-op on empty tables.
            (3, [ ftsCreate; ftsInsertTrigger; ftsUpdateTrigger; ftsDeleteTrigger; "INSERT INTO lessons_fts(lessons_fts) VALUES('rebuild')" ])
            // v4: confidence lifecycle columns. last_surfaced_at tracks when an item was
            // last returned by GetContextAndTrack (used by decay in Consolidate).
            // knowledge.status mirrors lessons.status — enables archiving without deletion.
            (4, [ "ALTER TABLE knowledge ADD COLUMN last_surfaced_at TEXT"
                  "ALTER TABLE lessons ADD COLUMN last_surfaced_at TEXT"
                  "ALTER TABLE knowledge ADD COLUMN status TEXT NOT NULL DEFAULT 'active'" ])
            // v5: drop vestigial session_count column. All session-based criteria now use
            // COUNT(DISTINCT session_id) from session_injections. Fresh DBs run DDL (which
            // includes session_count) then this migration drops it — both paths produce the
            // same final schema.
            (5, [ "ALTER TABLE knowledge DROP COLUMN session_count" ])
        ]
