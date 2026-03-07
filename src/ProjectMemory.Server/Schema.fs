namespace ProjectMemory

type QueryResult = {
    Columns: string array
    Rows: Map<string, obj> array
}

module Schema =
    let currentVersion = 3

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

    /// Migrations keyed by target version. Each runs when upgrading from version-1 to version.
    let migrations: (int * string) list =
        [
            (2, "CREATE UNIQUE INDEX IF NOT EXISTS uq_session_injections ON session_injections(session_id, item_type, item_id)")
            // FTS5 virtual table for lessons — enables fast candidate pre-filtering
            // in fuzzy dedup (RecordLesson) and Consolidate, bounding Jaccard comparisons
            // to a LIMIT 50 candidate set instead of a full O(n) / O(n²) scan.
            // The content= option keeps lessons_fts as a content table backed by lessons.
            // Explicit triggers keep the FTS index in sync on INSERT/UPDATE/DELETE.
            (3, """CREATE VIRTUAL TABLE IF NOT EXISTS lessons_fts USING fts5(lesson_text, content=lessons, content_rowid=id);
CREATE TRIGGER IF NOT EXISTS lessons_fts_insert AFTER INSERT ON lessons BEGIN
  INSERT INTO lessons_fts(rowid, lesson_text) VALUES (new.id, new.lesson_text);
END;
CREATE TRIGGER IF NOT EXISTS lessons_fts_update AFTER UPDATE ON lessons BEGIN
  INSERT INTO lessons_fts(lessons_fts, rowid, lesson_text) VALUES ('delete', old.id, old.lesson_text);
  INSERT INTO lessons_fts(rowid, lesson_text) VALUES (new.id, new.lesson_text);
END;
CREATE TRIGGER IF NOT EXISTS lessons_fts_delete AFTER DELETE ON lessons BEGIN
  INSERT INTO lessons_fts(lessons_fts, rowid, lesson_text) VALUES ('delete', old.id, old.lesson_text);
END""")
        ]
