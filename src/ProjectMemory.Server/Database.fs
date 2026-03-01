namespace ProjectMemory

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.Data.Sqlite

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

            // Run pending migrations
            for targetVersion, sql in Schema.migrations do
                if targetVersion > dbVersion then
                    use migrate = conn.CreateCommand()
                    migrate.CommandText <- sql
                    migrate.ExecuteNonQuery() |> ignore
                    use record = conn.CreateCommand()
                    record.CommandText <- "INSERT INTO schema_version (version, applied_at) VALUES (@v, @now)"
                    record.Parameters.AddWithValue("@v", targetVersion) |> ignore
                    record.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o")) |> ignore
                    record.ExecuteNonQuery() |> ignore

            // Ensure current version is recorded
            if dbVersion < Schema.currentVersion then
                use setVer = conn.CreateCommand()
                setVer.CommandText <- "INSERT INTO schema_version (version, applied_at) SELECT @v, @now WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = @v)"
                setVer.Parameters.AddWithValue("@v", Schema.currentVersion) |> ignore
                setVer.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o")) |> ignore
                setVer.ExecuteNonQuery() |> ignore
        )

    static member GenerateId(parts: string list) =
        let input = String.Join("|", parts)
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(input))
        Convert.ToHexString(hash, 0, 6).ToLowerInvariant()

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

    member this.MarkUseful(sessionId: string, itemId: string, useful: bool) : string =
        let affected =
            this.Execute(
                "UPDATE session_injections SET was_useful = @useful WHERE session_id = @sid AND item_id = @iid",
                [ ("@useful", box (if useful then 1 else 0))
                  ("@sid", box sessionId)
                  ("@iid", box itemId) ]
            )
        if affected = 0 then
            $"No injection record found for session={sessionId}, item={itemId}"
        else
            let injection =
                this.Query(
                    "SELECT item_type FROM session_injections WHERE session_id = @sid AND item_id = @iid LIMIT 1",
                    [ ("@sid", box sessionId); ("@iid", box itemId) ]
                )
            let delta = if useful then 0.05 else -0.05
            let now = DateTime.UtcNow.ToString("o")
            if injection.Rows.Length > 0 then
                match string injection.Rows.[0].["item_type"] with
                | "knowledge" ->
                    this.Execute(
                        "UPDATE knowledge SET confidence = MAX(0.1, MIN(1.0, confidence + @delta)), updated_at = @now WHERE id = @id",
                        [ ("@delta", box delta); ("@now", box now); ("@id", box itemId) ]
                    ) |> ignore
                | "lesson" ->
                    this.Execute(
                        "UPDATE lessons SET confidence = MAX(0.1, MIN(1.0, confidence + @delta)), updated_at = @now WHERE id = @id",
                        [ ("@delta", box delta); ("@now", box now); ("@id", box (int64 (int itemId))) ]
                    ) |> ignore
                | _ -> ()
            $"Feedback recorded for {itemId}"

    // --- Lessons ---

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
            let activeLessons =
                this.Query("SELECT id, lesson_text FROM lessons WHERE status = 'active'")
            let fuzzyMatch =
                activeLessons.Rows
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
                let countResult = this.Query("SELECT COUNT(*) as cnt FROM lessons WHERE status = 'active'")
                let count = countResult.Rows.[0].["cnt"] :?> int64
                if count > 0L && count % 10L = 0L then
                    this.Consolidate() |> ignore
                newId

    // --- Context ---

    member this.GetContext(scope: string option, limit: int, ?sessionId: string, ?maxTokens: int) : string =
        let knowledgeResult =
            match scope with
            | Some s ->
                this.Query(
                    "SELECT id, category, content, confidence, session_count, scope FROM knowledge WHERE scope = '*' OR @scope GLOB scope ORDER BY confidence DESC, session_count DESC LIMIT @limit",
                    [ ("@scope", box s); ("@limit", box limit) ]
                )
            | None ->
                this.Query(
                    "SELECT id, category, content, confidence, session_count, scope FROM knowledge ORDER BY confidence DESC, session_count DESC LIMIT @limit",
                    [ ("@limit", box limit) ]
                )

        let lessonResult =
            match scope with
            | Some s ->
                this.Query(
                    "SELECT id, lesson_text, recurrence, confidence, trigger FROM lessons WHERE status = 'active' AND (scope = '*' OR @scope GLOB scope) ORDER BY recurrence DESC, confidence DESC LIMIT @limit",
                    [ ("@scope", box s); ("@limit", box limit) ]
                )
            | None ->
                this.Query(
                    "SELECT id, lesson_text, recurrence, confidence, trigger FROM lessons WHERE status = 'active' ORDER BY recurrence DESC, confidence DESC LIMIT @limit",
                    [ ("@limit", box limit) ]
                )

        match sessionId with
        | Some sid when knowledgeResult.Rows.Length > 0 || lessonResult.Rows.Length > 0 ->
            let now = DateTime.UtcNow.ToString("o")
            for row in knowledgeResult.Rows do
                this.Execute(
                    "INSERT INTO session_injections (session_id, item_type, item_id, injected_at) VALUES (@sid, 'knowledge', @iid, @now)",
                    [ ("@sid", box sid); ("@iid", box (string row.["id"])); ("@now", box now) ]
                ) |> ignore
            for row in lessonResult.Rows do
                this.Execute(
                    "INSERT INTO session_injections (session_id, item_type, item_id, injected_at) VALUES (@sid, 'lesson', @iid, @now)",
                    [ ("@sid", box sid); ("@iid", box (string (row.["id"] :?> int64))); ("@now", box now) ]
                ) |> ignore
        | _ -> ()

        Formatting.formatContext knowledgeResult.Rows lessonResult.Rows (defaultArg maxTokens 2000)

    // --- Consolidation ---

    member this.Consolidate() : string =
        let now = DateTime.UtcNow.ToString("o")
        let sb = StringBuilder()
        let activeLessons =
            this.Query("SELECT id, lesson_text, recurrence, confidence, scope, updated_at FROM lessons WHERE status = 'active'")

        // Merge near-duplicates (>80% Jaccard similarity)
        let mutable merged = Set.empty<int64>
        let rows = activeLessons.Rows
        for i in 0 .. rows.Length - 1 do
            let idI = rows.[i].["id"] :?> int64
            if not (Set.contains idI merged) then
                for j in i + 1 .. rows.Length - 1 do
                    let idJ = rows.[j].["id"] :?> int64
                    if not (Set.contains idJ merged) then
                        let textI = string rows.[i].["lesson_text"]
                        let textJ = string rows.[j].["lesson_text"]
                        if Similarity.jaccard textI textJ > 0.8 then
                            let recI = rows.[i].["recurrence"] :?> int64
                            let recJ = rows.[j].["recurrence"] :?> int64
                            let keepId, supersededId =
                                if recI >= recJ then idI, idJ else idJ, idI
                            this.Execute(
                                "UPDATE lessons SET recurrence = recurrence + @addRec, confidence = MIN(1.0, confidence + 0.1), updated_at = @now WHERE id = @id",
                                [ ("@addRec", box (if keepId = idI then recJ else recI))
                                  ("@now", box now); ("@id", box keepId) ]
                            ) |> ignore
                            this.Execute(
                                "UPDATE lessons SET status = 'superseded', updated_at = @now WHERE id = @id",
                                [ ("@now", box now); ("@id", box supersededId) ]
                            ) |> ignore
                            merged <- Set.add supersededId merged
                            sb.AppendLine($"Merged lesson {supersededId} into {keepId}") |> ignore

        // Promote high-recurrence lessons to knowledge
        let promotable =
            this.Query("SELECT id, lesson_text, scope FROM lessons WHERE status = 'active' AND recurrence >= 5 AND confidence >= 0.7")
        for row in promotable.Rows do
            let lessonId = row.["id"] :?> int64
            let text = string row.["lesson_text"]
            let scope = string row.["scope"]
            this.StoreKnowledge("convention", text, scope, "learned") |> ignore
            this.Execute(
                "UPDATE lessons SET status = 'graduated', updated_at = @now WHERE id = @id",
                [ ("@now", box now); ("@id", box lessonId) ]
            ) |> ignore
            sb.AppendLine($"Promoted lesson {lessonId} to knowledge") |> ignore

        // Prune stale lessons (not updated in 30+ days, recurrence = 1)
        let pruned =
            this.Execute(
                "UPDATE lessons SET status = 'superseded', updated_at = @now WHERE status = 'active' AND recurrence = 1 AND updated_at < @cutoff",
                [ ("@now", box now); ("@cutoff", box (DateTime.UtcNow.AddDays(-30.0).ToString("o"))) ]
            )
        if pruned > 0 then
            sb.AppendLine($"Pruned {pruned} stale lesson(s)") |> ignore

        let result = sb.ToString().TrimEnd()
        if String.IsNullOrWhiteSpace(result) then "No consolidation actions needed."
        else result

    // --- Graduation ---

    member this.Graduate(instructionsPath: string) : string =
        let now = DateTime.UtcNow.ToString("o")
        let sb = StringBuilder()

        let candidates =
            this.Query(
                "SELECT id, content, scope FROM knowledge WHERE confidence >= 0.9 AND session_count >= 10 AND id NOT IN (SELECT item_id FROM graduations WHERE item_type = 'knowledge')"
            )

        if candidates.Rows.Length = 0 then
            "No knowledge entries ready for graduation."
        else
            let existingGraduated =
                this.Query("SELECT instruction_text FROM graduations ORDER BY graduated_at")
            let existingInstructions =
                existingGraduated.Rows |> Array.map (fun r -> string r.["instruction_text"]) |> Array.toList

            let newInstructions = ResizeArray<string>()
            for row in candidates.Rows do
                let knowledgeId = string row.["id"]
                let content = string row.["content"]
                let scope = string row.["scope"]
                let instruction =
                    if scope = "*" then $"- {content}"
                    else $"- {content} (applies to: {scope})"
                newInstructions.Add(instruction)
                this.Execute(
                    "INSERT INTO graduations (item_type, item_id, target_file, graduated_at, instruction_text) VALUES ('knowledge', @kid, @file, @now, @text)",
                    [ ("@kid", box knowledgeId); ("@file", box instructionsPath)
                      ("@now", box now); ("@text", box instruction) ]
                ) |> ignore
                sb.AppendLine($"Graduated knowledge {knowledgeId}: {content}") |> ignore

            let allInstructions = existingInstructions @ (newInstructions |> Seq.toList)
            let sectionContent = InstructionsFile.buildSection allInstructions
            InstructionsFile.mergeIntoFile instructionsPath sectionContent
            sb.ToString().TrimEnd()

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

        if doc.RootElement.TryGetProperty("knowledge") |> fst then
            for item in doc.RootElement.GetProperty("knowledge").EnumerateArray() do
                let category = item.GetProperty("category").GetString()
                let content = item.GetProperty("content").GetString()
                let scope = if item.TryGetProperty("scope") |> fst then item.GetProperty("scope").GetString() else "*"
                let source = if item.TryGetProperty("source") |> fst then item.GetProperty("source").GetString() else "imported"
                this.StoreKnowledge(category, content, scope, source) |> ignore
                knowledgeCount <- knowledgeCount + 1

        if doc.RootElement.TryGetProperty("lessons") |> fst then
            for item in doc.RootElement.GetProperty("lessons").EnumerateArray() do
                let text = item.GetProperty("lesson_text").GetString()
                let trigger = if item.TryGetProperty("trigger") |> fst then item.GetProperty("trigger").GetString() else "explicit"
                let scope = if item.TryGetProperty("scope") |> fst then item.GetProperty("scope").GetString() else "*"
                let confidence = if item.TryGetProperty("confidence") |> fst then item.GetProperty("confidence").GetDouble() else 0.3
                this.RecordLesson(text, trigger, null, scope, confidence, null) |> ignore
                lessonCount <- lessonCount + 1

        $"Imported {knowledgeCount} knowledge entries, {lessonCount} lessons (duplicates merged automatically)."
