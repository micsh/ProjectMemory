namespace ProjectMemory

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Microsoft.Data.Sqlite

type QueryResult = {
    Columns: string array
    Rows: Map<string, obj> array
}

type ProjectMemoryDb(dbPath: string) =
    let connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate"

    let schema = """
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
            lesson_id INTEGER NOT NULL,
            target_file TEXT NOT NULL,
            graduated_at TEXT NOT NULL,
            instruction_text TEXT NOT NULL
        );
    """

    let withConnection f =
        use conn = new SqliteConnection(connectionString)
        conn.Open()
        f conn

    let addParams (cmd: SqliteCommand) (parameters: (string * obj) list) =
        for name, value in parameters do
            let v = if isNull value then box DBNull.Value else value
            cmd.Parameters.AddWithValue(name, v) |> ignore

    do
        let dir = Path.GetDirectoryName(dbPath)
        if not (String.IsNullOrWhiteSpace(dir)) then
            Directory.CreateDirectory(dir) |> ignore
        withConnection (fun conn ->
            use pragma = conn.CreateCommand()
            pragma.CommandText <- "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;"
            pragma.ExecuteNonQuery() |> ignore
            use cmd = conn.CreateCommand()
            cmd.CommandText <- schema
            cmd.ExecuteNonQuery() |> ignore
        )

    static member GenerateId(parts: string list) =
        let input = String.Join("|", parts)
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(input))
        Convert.ToHexString(hash, 0, 6).ToLowerInvariant()

    member _.Query(sql: string, ?parameters: (string * obj) list) : QueryResult =
        let parameters = defaultArg parameters []
        withConnection (fun conn ->
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql
            addParams cmd parameters
            use reader = cmd.ExecuteReader()
            let columns = [| for i in 0 .. reader.FieldCount - 1 -> reader.GetName(i) |]
            let rows = ResizeArray()
            while reader.Read() do
                let row =
                    columns
                    |> Array.map (fun col ->
                        let v = reader.[col]
                        col, (if v :? DBNull then null else v))
                    |> Map.ofArray
                rows.Add(row)
            { Columns = columns; Rows = rows.ToArray() }
        )

    member _.Execute(sql: string, ?parameters: (string * obj) list) : int =
        let parameters = defaultArg parameters []
        withConnection (fun conn ->
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql
            addParams cmd parameters
            cmd.ExecuteNonQuery()
        )

    member this.StoreKnowledge(category: string, content: string, scope: string, source: string) : string =
        let now = DateTime.UtcNow.ToString("o")
        let id = ProjectMemoryDb.GenerateId [ category; scope; content ]
        let existing =
            this.Query("SELECT id FROM knowledge WHERE id = @id", [ ("@id", box id) ])
        if existing.Rows.Length > 0 then
            this.Execute(
                "UPDATE knowledge SET session_count = session_count + 1, confidence = MIN(1.0, confidence + 0.1), updated_at = @now WHERE id = @id",
                [ ("@id", box id); ("@now", box now) ]
            ) |> ignore
        else
            this.Execute(
                "INSERT INTO knowledge (id, category, scope, content, source, created_at, updated_at) VALUES (@id, @cat, @scope, @content, @source, @now, @now)",
                [ ("@id", box id); ("@cat", box category); ("@scope", box scope)
                  ("@content", box content); ("@source", box source); ("@now", box now) ]
            ) |> ignore
        id

    member this.ForgetKnowledge(id: string) : bool =
        this.Execute("DELETE FROM knowledge WHERE id = @id", [ ("@id", box id) ]) > 0

    member this.RecordLesson
        (lessonText: string, trigger: string, agentRole: string,
         scope: string, confidence: float, sourceRef: string) : int =
        let now = DateTime.UtcNow.ToString("o")
        let scope = if String.IsNullOrEmpty(scope) then "*" else scope
        let existing =
            this.Query(
                "SELECT id, recurrence FROM lessons WHERE lesson_text = @text AND status = 'active' LIMIT 1",
                [ ("@text", box lessonText) ]
            )
        if existing.Rows.Length > 0 then
            let existingId = existing.Rows.[0].["id"] :?> int64
            this.Execute(
                "UPDATE lessons SET recurrence = recurrence + 1, confidence = MIN(1.0, confidence + 0.1), updated_at = @now WHERE id = @id",
                [ ("@id", box existingId); ("@now", box now) ]
            ) |> ignore
            int existingId
        else
            this.Execute(
                "INSERT INTO lessons (lesson_text, agent_role, trigger, scope, source_ref, confidence, created_at, updated_at) VALUES (@text, @role, @trigger, @scope, @ref, @conf, @now, @now)",
                [ ("@text", box lessonText)
                  ("@role", if String.IsNullOrEmpty(agentRole) then box DBNull.Value else box agentRole)
                  ("@trigger", box trigger)
                  ("@scope", box scope)
                  ("@ref", if String.IsNullOrEmpty(sourceRef) then box DBNull.Value else box sourceRef)
                  ("@conf", box confidence)
                  ("@now", box now) ]
            ) |> ignore
            let result = this.Query("SELECT last_insert_rowid() as id")
            result.Rows.[0].["id"] :?> int64 |> int

    member this.GetContext(scope: string option, limit: int) : string =
        let knowledgeResult =
            match scope with
            | Some s ->
                this.Query(
                    "SELECT category, content, confidence, session_count, scope FROM knowledge WHERE scope = '*' OR @scope GLOB scope ORDER BY confidence DESC, session_count DESC LIMIT @limit",
                    [ ("@scope", box s); ("@limit", box limit) ]
                )
            | None ->
                this.Query(
                    "SELECT category, content, confidence, session_count, scope FROM knowledge ORDER BY confidence DESC, session_count DESC LIMIT @limit",
                    [ ("@limit", box limit) ]
                )

        let lessonResult =
            this.Query(
                "SELECT lesson_text, recurrence, confidence, trigger FROM lessons WHERE status = 'active' ORDER BY recurrence DESC, confidence DESC LIMIT @limit",
                [ ("@limit", box limit) ]
            )

        if knowledgeResult.Rows.Length = 0 && lessonResult.Rows.Length = 0 then
            "No project memory stored yet. Use project_store to add knowledge, or record_lesson when you learn something."
        else
            let sb = StringBuilder()
            let total = knowledgeResult.Rows.Length + lessonResult.Rows.Length
            sb.AppendLine($"## Project Memory ({total} items)") |> ignore
            sb.AppendLine() |> ignore

            if knowledgeResult.Rows.Length > 0 then
                let grouped = knowledgeResult.Rows |> Array.groupBy (fun r -> string r.["category"])
                for category, items in grouped do
                    let title =
                        match category with
                        | "convention" -> "Conventions"
                        | "decision" -> "Decisions"
                        | "known_issue" -> "Known Issues"
                        | "file_note" -> "File Notes"
                        | "preference" -> "Preferences"
                        | other -> other
                    sb.AppendLine($"### {title}") |> ignore
                    for item in items do
                        let content = string item.["content"]
                        let conf = item.["confidence"] :?> double
                        let sessions = item.["session_count"] :?> int64
                        let itemScope = string item.["scope"]
                        let scopeStr = if itemScope = "*" then "" else $" (scope: {itemScope})"
                        sb.AppendLine($"- [%.2f{conf}] {content}{scopeStr} — {sessions} session(s)") |> ignore
                    sb.AppendLine() |> ignore

            if lessonResult.Rows.Length > 0 then
                sb.AppendLine("### Active Lessons") |> ignore
                for lesson in lessonResult.Rows do
                    let text = string lesson.["lesson_text"]
                    let recurrence = lesson.["recurrence"] :?> int64
                    let trigger = string lesson.["trigger"]
                    sb.AppendLine($"- {text} ({recurrence} occurrence(s), trigger: {trigger})") |> ignore
                sb.AppendLine() |> ignore

            sb.ToString().TrimEnd()
