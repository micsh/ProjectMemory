namespace ProjectMemory

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.Data.Sqlite

/// Domain constants shared by the DB layer and callers.
/// These are the single source of truth — Tools.fs delegates to the DB
/// rather than re-declaring its own copies.
module private Domain =
    let validCategories = set [ "convention"; "decision"; "known_issue"; "file_note"; "preference" ]
    let validTriggers = set [ "user_correction"; "build_failure"; "repeated_pattern"; "discovery"; "explicit" ]

type ProjectMemoryDb(dbPath: string) =
    let connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate"

    let withConnection f =
        use conn = new SqliteConnection(connectionString)
        conn.Open()
        use bt = conn.CreateCommand()
        bt.CommandText <- "PRAGMA busy_timeout=5000;"
        bt.ExecuteNonQuery() |> ignore
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
            pragma.CommandText <- "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"
            pragma.ExecuteNonQuery() |> ignore

            // Create schema version table and base schema
            use verCmd = conn.CreateCommand()
            verCmd.CommandText <- Schema.versionTable
            verCmd.ExecuteNonQuery() |> ignore

            use cmd = conn.CreateCommand()
            cmd.CommandText <- Schema.ddl
            cmd.ExecuteNonQuery() |> ignore

            // Check current version
            use getVer = conn.CreateCommand()
            getVer.CommandText <- "SELECT COALESCE(MAX(version), 0) FROM schema_version"
            let dbVersion = getVer.ExecuteScalar() :?> int64 |> int

            // Run pending migrations — each DDL + version INSERT is one atomic unit.
            // If a crash occurs mid-migration the transaction rolls back, leaving
            // schema_version at the previous value so the migration re-runs cleanly.
            for targetVersion, sql in Schema.migrations do
                if targetVersion > dbVersion then
                    use tx = conn.BeginTransaction()
                    use migrate = conn.CreateCommand()
                    migrate.Transaction <- tx
                    migrate.CommandText <- sql
                    migrate.ExecuteNonQuery() |> ignore
                    use record = conn.CreateCommand()
                    record.Transaction <- tx
                    record.CommandText <- "INSERT INTO schema_version (version, applied_at) VALUES (@v, @now)"
                    record.Parameters.AddWithValue("@v", targetVersion) |> ignore
                    record.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o")) |> ignore
                    record.ExecuteNonQuery() |> ignore
                    tx.Commit()

            // Ensure current version is recorded (safety net for empty migrations list)
            if dbVersion < Schema.currentVersion then
                use tx = conn.BeginTransaction()
                use setVer = conn.CreateCommand()
                setVer.Transaction <- tx
                setVer.CommandText <- "INSERT INTO schema_version (version, applied_at) SELECT @v, @now WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = @v)"
                setVer.Parameters.AddWithValue("@v", Schema.currentVersion) |> ignore
                setVer.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o")) |> ignore
                setVer.ExecuteNonQuery() |> ignore
                tx.Commit()
        )

    static member GenerateId(parts: string list) =
        let input = String.Join("|", parts)
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(input))
        // 8 bytes = 16-char hex (64-bit space). DBs created before this change
        // contain 12-char IDs (6 bytes); both lengths coexist in the same DB.
        // Deduplication works correctly for each format — old entries keep their
        // 12-char IDs and continue to be found by hash; new entries get 16-char IDs.
        Convert.ToHexString(hash, 0, 8).ToLowerInvariant()

    // --- Low-level DB access ---

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

    // --- Knowledge ---

    member this.StoreKnowledge(category: string, content: string, scope: string, source: string) : string =
        if not (Domain.validCategories.Contains(category)) then
            raise (ArgumentException($"Invalid category '{category}'. Valid categories: convention, decision, known_issue, file_note, preference."))
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

    // --- Feedback ---

    member _.MarkUseful(sessionId: string, itemId: string, useful: bool) : string =
        withConnection (fun conn ->
            // (1) SELECT item_type — verify the injection record exists
            let itemTypeOpt =
                use selectCmd = conn.CreateCommand()
                selectCmd.CommandText <- "SELECT item_type FROM session_injections WHERE session_id = @sid AND item_id = @iid LIMIT 1"
                selectCmd.Parameters.AddWithValue("@sid", sessionId) |> ignore
                selectCmd.Parameters.AddWithValue("@iid", itemId) |> ignore
                use reader = selectCmd.ExecuteReader()
                if reader.Read() then Some (reader.GetString(0)) else None

            match itemTypeOpt with
            | None -> $"No injection record found for session={sessionId}, item={itemId}"
            | Some itemType ->
                // (2) UPDATE session_injections
                use updateInj = conn.CreateCommand()
                updateInj.CommandText <- "UPDATE session_injections SET was_useful = @useful WHERE session_id = @sid AND item_id = @iid"
                updateInj.Parameters.AddWithValue("@useful", if useful then 1 else 0) |> ignore
                updateInj.Parameters.AddWithValue("@sid", sessionId) |> ignore
                updateInj.Parameters.AddWithValue("@iid", itemId) |> ignore
                updateInj.ExecuteNonQuery() |> ignore

                // (3) UPDATE confidence on the underlying item
                let delta = if useful then 0.05 else -0.05
                let now = DateTime.UtcNow.ToString("o")
                match itemType with
                | "knowledge" ->
                    use updateK = conn.CreateCommand()
                    updateK.CommandText <- "UPDATE knowledge SET confidence = MAX(0.1, MIN(1.0, confidence + @delta)), updated_at = @now WHERE id = @id"
                    updateK.Parameters.AddWithValue("@delta", delta) |> ignore
                    updateK.Parameters.AddWithValue("@now", now) |> ignore
                    updateK.Parameters.AddWithValue("@id", itemId) |> ignore
                    updateK.ExecuteNonQuery() |> ignore
                | "lesson" ->
                    use updateL = conn.CreateCommand()
                    updateL.CommandText <- "UPDATE lessons SET confidence = MAX(0.1, MIN(1.0, confidence + @delta)), updated_at = @now WHERE id = @id"
                    updateL.Parameters.AddWithValue("@delta", delta) |> ignore
                    updateL.Parameters.AddWithValue("@now", now) |> ignore
                    updateL.Parameters.AddWithValue("@id", int64 itemId) |> ignore
                    updateL.ExecuteNonQuery() |> ignore
                | _ -> ()
                $"Feedback recorded for {itemId}"
        )

    // --- Lessons ---

    member this.RecordLesson
        (lessonText: string, trigger: string, agentRole: string,
         scope: string, confidence: float, sourceRef: string) : int =
        if not (Domain.validTriggers.Contains(trigger)) then
            raise (ArgumentException($"Invalid trigger '{trigger}'. Valid triggers: user_correction, build_failure, repeated_pattern, discovery, explicit."))
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
            // FTS5 pre-filter: retrieve up to 50 candidate lessons whose text
            // overlaps with the query before running the more expensive Jaccard
            // comparison. Falls back to full scan if the query token list is empty.
            let ftsQuery =
                lessonText.ToLowerInvariant().Split([| ' '; '\t'; '\n'; '\r'; ','; '.'; ';'; ':' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun t -> $"\"{t}\"")
                |> fun tokens ->
                    if tokens.Length = 0 then None
                    else Some (String.concat " OR " tokens)
            let candidates =
                match ftsQuery with
                | Some q ->
                    this.Query(
                        "SELECT l.id, l.lesson_text FROM lessons l JOIN lessons_fts f ON l.id = f.rowid WHERE f.lesson_text MATCH @q AND l.status = 'active' LIMIT 50",
                        [ ("@q", box q) ]
                    )
                | None ->
                    this.Query("SELECT id, lesson_text FROM lessons WHERE status = 'active'")
            let fuzzyMatch =
                candidates.Rows
                |> Array.tryFind (fun r ->
                    Similarity.jaccard lessonText (string r.["lesson_text"]) > 0.7)
            match fuzzyMatch with
            | Some row ->
                let existingId = row.["id"] :?> int64
                this.Execute(
                    "UPDATE lessons SET recurrence = recurrence + 1, confidence = MIN(1.0, confidence + 0.1), updated_at = @now WHERE id = @id",
                    [ ("@id", box existingId); ("@now", box now) ]
                ) |> ignore
                int existingId
            | None ->
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
                let newId = result.Rows.[0].["id"] :?> int64 |> int
                newId

    // --- Context ---

    member this.GetContext(scope: string option, limit: int, ?maxTokens: int) : string =
        let knowledgeResult =
            match scope with
            | Some s ->
                this.Query(
                    "SELECT id, category, content, confidence, session_count, scope FROM knowledge WHERE scope = '*' OR @scope GLOB scope ORDER BY CASE WHEN scope != '*' THEN 1 ELSE 0 END DESC, confidence DESC, updated_at DESC LIMIT @limit",
                    [ ("@scope", box s); ("@limit", box limit) ]
                )
            | None ->
                this.Query(
                    "SELECT id, category, content, confidence, session_count, scope FROM knowledge ORDER BY CASE WHEN scope != '*' THEN 1 ELSE 0 END DESC, confidence DESC, updated_at DESC LIMIT @limit",
                    [ ("@limit", box limit) ]
                )

        let lessonResult =
            match scope with
            | Some s ->
                this.Query(
                    "SELECT id, lesson_text, recurrence, confidence, trigger FROM lessons WHERE status = 'active' AND (scope = '*' OR @scope GLOB scope) ORDER BY CASE WHEN scope != '*' THEN 1 ELSE 0 END DESC, confidence DESC, updated_at DESC LIMIT @limit",
                    [ ("@scope", box s); ("@limit", box limit) ]
                )
            | None ->
                this.Query(
                    "SELECT id, lesson_text, recurrence, confidence, trigger FROM lessons WHERE status = 'active' ORDER BY CASE WHEN scope != '*' THEN 1 ELSE 0 END DESC, confidence DESC, updated_at DESC LIMIT @limit",
                    [ ("@limit", box limit) ]
                )

        Formatting.formatContext knowledgeResult.Rows lessonResult.Rows (defaultArg maxTokens 2000)

    member this.GetContextAndTrack(scope: string option, limit: int, sessionId: string, ?maxTokens: int) : string =
        let knowledgeResult =
            match scope with
            | Some s ->
                this.Query(
                    "SELECT id, category, content, confidence, session_count, scope FROM knowledge WHERE scope = '*' OR @scope GLOB scope ORDER BY CASE WHEN scope != '*' THEN 1 ELSE 0 END DESC, confidence DESC, updated_at DESC LIMIT @limit",
                    [ ("@scope", box s); ("@limit", box limit) ]
                )
            | None ->
                this.Query(
                    "SELECT id, category, content, confidence, session_count, scope FROM knowledge ORDER BY CASE WHEN scope != '*' THEN 1 ELSE 0 END DESC, confidence DESC, updated_at DESC LIMIT @limit",
                    [ ("@limit", box limit) ]
                )

        let lessonResult =
            match scope with
            | Some s ->
                this.Query(
                    "SELECT id, lesson_text, recurrence, confidence, trigger FROM lessons WHERE status = 'active' AND (scope = '*' OR @scope GLOB scope) ORDER BY CASE WHEN scope != '*' THEN 1 ELSE 0 END DESC, confidence DESC, updated_at DESC LIMIT @limit",
                    [ ("@scope", box s); ("@limit", box limit) ]
                )
            | None ->
                this.Query(
                    "SELECT id, lesson_text, recurrence, confidence, trigger FROM lessons WHERE status = 'active' ORDER BY CASE WHEN scope != '*' THEN 1 ELSE 0 END DESC, confidence DESC, updated_at DESC LIMIT @limit",
                    [ ("@limit", box limit) ]
                )

        if knowledgeResult.Rows.Length > 0 || lessonResult.Rows.Length > 0 then
            let now = DateTime.UtcNow.ToString("o")
            for row in knowledgeResult.Rows do
                this.Execute(
                    "INSERT OR IGNORE INTO session_injections (session_id, item_type, item_id, injected_at) VALUES (@sid, 'knowledge', @iid, @now)",
                    [ ("@sid", box sessionId); ("@iid", box (string row.["id"])); ("@now", box now) ]
                ) |> ignore
            for row in lessonResult.Rows do
                this.Execute(
                    "INSERT OR IGNORE INTO session_injections (session_id, item_type, item_id, injected_at) VALUES (@sid, 'lesson', @iid, @now)",
                    [ ("@sid", box sessionId); ("@iid", box (string (row.["id"] :?> int64))); ("@now", box now) ]
                ) |> ignore

        Formatting.formatContext knowledgeResult.Rows lessonResult.Rows (defaultArg maxTokens 2000)

    // --- Consolidation and Graduation are owned by DomainService ---
    // See DomainService.fs for Consolidate() and Graduate().

    // --- Import/Export ---

    member this.Export() : string =
        let knowledge =
            this.Query("SELECT id, category, scope, content, confidence, source, session_count FROM knowledge ORDER BY category, content")
        let lessons =
            this.Query("SELECT lesson_text, trigger, scope, recurrence, confidence, status FROM lessons ORDER BY status, recurrence DESC")

        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
        writer.WriteStartObject()

        writer.WriteStartArray("knowledge")
        for row in knowledge.Rows do
            writer.WriteStartObject()
            writer.WriteString("id", string row.["id"])
            writer.WriteString("category", string row.["category"])
            writer.WriteString("scope", string row.["scope"])
            writer.WriteString("content", string row.["content"])
            writer.WriteNumber("confidence", row.["confidence"] :?> double)
            writer.WriteString("source", string row.["source"])
            writer.WriteNumber("session_count", row.["session_count"] :?> int64)
            writer.WriteEndObject()
        writer.WriteEndArray()

        writer.WriteStartArray("lessons")
        for row in lessons.Rows do
            writer.WriteStartObject()
            writer.WriteString("lesson_text", string row.["lesson_text"])
            writer.WriteString("trigger", string row.["trigger"])
            writer.WriteString("scope", string row.["scope"])
            writer.WriteNumber("recurrence", row.["recurrence"] :?> int64)
            writer.WriteNumber("confidence", row.["confidence"] :?> double)
            writer.WriteString("status", string row.["status"])
            writer.WriteEndObject()
        writer.WriteEndArray()

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    member this.Import(json: string) : string =
        let doc = JsonDocument.Parse(json)
        let mutable knowledgeCount = 0
        let mutable lessonCount = 0
        let mutable skippedCount = 0

        if doc.RootElement.TryGetProperty("knowledge") |> fst then
            for item in doc.RootElement.GetProperty("knowledge").EnumerateArray() do
                let category = item.GetProperty("category").GetString()
                let content = item.GetProperty("content").GetString()
                if String.IsNullOrWhiteSpace(category) || String.IsNullOrWhiteSpace(content) then
                    skippedCount <- skippedCount + 1
                else
                    let scope =
                        if item.TryGetProperty("scope") |> fst then
                            let s = item.GetProperty("scope").GetString()
                            if String.IsNullOrWhiteSpace(s) then "*" else s
                        else "*"
                    let source =
                        if item.TryGetProperty("source") |> fst then
                            let s = item.GetProperty("source").GetString()
                            if String.IsNullOrWhiteSpace(s) then "imported" else s
                        else "imported"
                    try
                        this.StoreKnowledge(category, content, scope, source) |> ignore
                        knowledgeCount <- knowledgeCount + 1
                    with :? ArgumentException ->
                        skippedCount <- skippedCount + 1

        if doc.RootElement.TryGetProperty("lessons") |> fst then
            for item in doc.RootElement.GetProperty("lessons").EnumerateArray() do
                let text = item.GetProperty("lesson_text").GetString()
                if String.IsNullOrWhiteSpace(text) then
                    skippedCount <- skippedCount + 1
                else
                    let trigger =
                        if item.TryGetProperty("trigger") |> fst then
                            let s = item.GetProperty("trigger").GetString()
                            if String.IsNullOrWhiteSpace(s) then "explicit" else s
                        else "explicit"
                    let scope =
                        if item.TryGetProperty("scope") |> fst then
                            let s = item.GetProperty("scope").GetString()
                            if String.IsNullOrWhiteSpace(s) then "*" else s
                        else "*"
                    let confidence =
                        if item.TryGetProperty("confidence") |> fst then item.GetProperty("confidence").GetDouble()
                        else 0.3
                    try
                        this.RecordLesson(text, trigger, null, scope, confidence, null) |> ignore
                        lessonCount <- lessonCount + 1
                    with :? ArgumentException ->
                        skippedCount <- skippedCount + 1

        if skippedCount > 0 then
            $"Imported {knowledgeCount} knowledge entries, {lessonCount} lessons; {skippedCount} items skipped (null/invalid fields)."
        else
            $"Imported {knowledgeCount} knowledge entries, {lessonCount} lessons (duplicates merged automatically)."
