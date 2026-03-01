namespace ProjectMemory

type QueryResult = {
    Columns: string array
    Rows: Map<string, obj> array
}

module Schema =
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
