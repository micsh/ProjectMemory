namespace ProjectMemory.Tests

open System
open System.IO
open Xunit
open ProjectMemory

type DatabaseTests() =
    let dbPath = Path.Combine(Path.GetTempPath(), $"pm-test-{Guid.NewGuid()}.db")
    let db = ProjectMemoryDb(dbPath)
    // DomainService for tests that need a fixed instructions path (Graduate/Consolidate)
    let defaultInstrPath = Path.Combine(Path.GetTempPath(), $"pm-test-{Guid.NewGuid()}", ".github", "copilot-instructions.md")
    let svc = DomainService(db, defaultInstrPath)

    interface IDisposable with
        member _.Dispose() =
            try if File.Exists(dbPath) then File.Delete(dbPath) with _ -> ()
            try
                let wal = dbPath + "-wal"
                let shm = dbPath + "-shm"
                if File.Exists(wal) then File.Delete(wal)
                if File.Exists(shm) then File.Delete(shm)
            with _ -> ()

    [<Fact>]
    member _.``Creates schema tables``() =
        let result = db.Query("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")
        let tables = result.Rows |> Array.map (fun r -> string r.["name"])
        Assert.Contains("knowledge", tables)
        Assert.Contains("lessons", tables)
        Assert.Contains("session_injections", tables)
        Assert.Contains("graduations", tables)
        Assert.Contains("schema_version", tables)

    [<Fact>]
    member _.``Schema version is recorded``() =
        let result = db.Query("SELECT MAX(version) as v FROM schema_version")
        Assert.Equal(int64 Schema.currentVersion, result.Rows.[0].["v"] :?> int64)

    [<Fact>]
    member _.``Stores and retrieves knowledge``() =
        let id = db.StoreKnowledge("convention", "Use tabs not spaces", "*", "user_explicit")
        let result = db.Query("SELECT content, confidence, session_count FROM knowledge WHERE id = @id", [ ("@id", box id) ])
        Assert.Equal(1, result.Rows.Length)
        Assert.Equal("Use tabs not spaces", string result.Rows.[0].["content"])
        Assert.Equal(0.5, result.Rows.[0].["confidence"] :?> double)
        Assert.Equal(1L, result.Rows.[0].["session_count"] :?> int64)

    [<Fact>]
    member _.``Deduplicates knowledge - same content bumps session count``() =
        let id1 = db.StoreKnowledge("convention", "Always run tests", "*", "user_explicit")
        let id2 = db.StoreKnowledge("convention", "Always run tests", "*", "user_explicit")
        Assert.Equal(id1, id2)
        let result = db.Query("SELECT session_count, confidence FROM knowledge WHERE id = @id", [ ("@id", box id1) ])
        Assert.Equal(2L, result.Rows.[0].["session_count"] :?> int64)
        Assert.True((result.Rows.[0].["confidence"] :?> double) > 0.5)

    [<Fact>]
    member _.``Different scope produces different id``() =
        let id1 = db.StoreKnowledge("convention", "Same content", "*", "user_explicit")
        let id2 = db.StoreKnowledge("convention", "Same content", "src/*", "user_explicit")
        Assert.NotEqual<string>(id1, id2)

    [<Fact>]
    member _.``Forgets existing knowledge``() =
        let id = db.StoreKnowledge("decision", "Use JWT auth", "*", "user_explicit")
        Assert.True(db.ForgetKnowledge(id))
        let result = db.Query("SELECT id FROM knowledge WHERE id = @id", [ ("@id", box id) ])
        Assert.Equal(0, result.Rows.Length)

    [<Fact>]
    member _.``Forget returns false for non-existent id``() =
        Assert.False(db.ForgetKnowledge("nonexistent"))

    [<Fact>]
    member _.``Records a lesson``() =
        let id = db.RecordLesson("When X happens, do Y, because Z", "user_correction", null, "*", 0.7, null)
        Assert.True(id > 0)
        let result = db.Query("SELECT lesson_text, trigger, confidence, recurrence FROM lessons WHERE id = @id", [ ("@id", box (int64 id)) ])
        Assert.Equal(1, result.Rows.Length)
        Assert.Equal("When X happens, do Y, because Z", string result.Rows.[0].["lesson_text"])
        Assert.Equal(1L, result.Rows.[0].["recurrence"] :?> int64)

    [<Fact>]
    member _.``Deduplicates lessons - same text bumps recurrence``() =
        let id1 = db.RecordLesson("Always check nulls", "build_failure", null, "*", 0.3, null)
        let id2 = db.RecordLesson("Always check nulls", "user_correction", null, "*", 0.5, null)
        Assert.Equal(id1, id2)
        let result = db.Query("SELECT recurrence FROM lessons WHERE id = @id", [ ("@id", box (int64 id1)) ])
        Assert.Equal(2L, result.Rows.[0].["recurrence"] :?> int64)

    [<Fact>]
    member _.``GetContext returns empty message for fresh database``() =
        let freshPath = Path.Combine(Path.GetTempPath(), $"pm-test-{Guid.NewGuid()}.db")
        try
            let freshDb = ProjectMemoryDb(freshPath)
            let context = freshDb.GetContext(None, 20)
            Assert.Contains("No project memory stored yet", context)
        finally
            try File.Delete(freshPath) with _ -> ()

    [<Fact>]
    member _.``GetContext returns formatted markdown``() =
        db.StoreKnowledge("convention", "Use staging build", "*", "user_explicit") |> ignore
        db.StoreKnowledge("known_issue", "Test FR-284 is flaky", "tests/*", "user_explicit") |> ignore
        let context = db.GetContext(None, 20)
        Assert.Contains("Project Memory", context)
        Assert.Contains("Conventions", context)
        Assert.Contains("Use staging build", context)
        Assert.Contains("Known Issues", context)
        Assert.Contains("Test FR-284 is flaky", context)

    [<Fact>]
    member _.``GetContext includes lessons``() =
        db.RecordLesson("Check assembly references", "discovery", null, "*", 0.5, null) |> ignore
        let context = db.GetContext(None, 20)
        Assert.Contains("Active Lessons", context)
        Assert.Contains("Check assembly references", context)

    [<Fact>]
    member _.``GetContext filters by scope``() =
        db.StoreKnowledge("convention", "Global rule", "*", "user_explicit") |> ignore
        db.StoreKnowledge("file_note", "Auth specific", "src/Auth/*", "user_explicit") |> ignore
        db.StoreKnowledge("file_note", "UI specific", "src/UI/*", "user_explicit") |> ignore
        let context = db.GetContext(Some "src/Auth/Login.fs", 20)
        Assert.Contains("Global rule", context)
        Assert.Contains("Auth specific", context)
        Assert.DoesNotContain("UI specific", context)

    [<Fact>]
    member _.``GenerateId is deterministic``() =
        let id1 = ProjectMemoryDb.GenerateId [ "a"; "b"; "c" ]
        let id2 = ProjectMemoryDb.GenerateId [ "a"; "b"; "c" ]
        Assert.Equal(id1, id2)

    [<Fact>]
    member _.``GenerateId differs for different input``() =
        let id1 = ProjectMemoryDb.GenerateId [ "a"; "b" ]
        let id2 = ProjectMemoryDb.GenerateId [ "a"; "c" ]
        Assert.NotEqual<string>(id1, id2)

    [<Fact>]
    member _.``Query with read-only SQL works``() =
        db.StoreKnowledge("convention", "Test query", "*", "user_explicit") |> ignore
        let result = db.Query("SELECT COUNT(*) as cnt FROM knowledge")
        Assert.True((result.Rows.[0].["cnt"] :?> int64) >= 1L)

    [<Fact>]
    member _.``Fuzzy dedup merges similar lessons``() =
        let id1 = db.RecordLesson("When deploying to staging, always run integration tests first", "user_correction", null, "*", 0.7, null)
        let id2 = db.RecordLesson("When deploying to staging, always run the integration tests first", "discovery", null, "*", 0.3, null)
        Assert.Equal(id1, id2)
        let result = db.Query("SELECT recurrence FROM lessons WHERE id = @id", [ ("@id", box (int64 id1)) ])
        Assert.Equal(2L, result.Rows.[0].["recurrence"] :?> int64)

    [<Fact>]
    member _.``Fuzzy dedup does not merge dissimilar lessons``() =
        let id1 = db.RecordLesson("Always check null references before accessing properties", "build_failure", null, "*", 0.3, null)
        let id2 = db.RecordLesson("Use async/await for all database calls", "discovery", null, "*", 0.3, null)
        Assert.NotEqual(id1, id2)

    [<Fact>]
    member _.``JaccardSimilarity returns 1 for identical strings``() =
        let sim = Similarity.jaccard "hello world" "hello world"
        Assert.Equal(1.0, sim)

    [<Fact>]
    member _.``JaccardSimilarity returns 0 for completely different strings``() =
        let sim = Similarity.jaccard "alpha beta gamma" "delta epsilon zeta"
        Assert.Equal(0.0, sim)

    [<Fact>]
    member _.``GetContext filters lessons by scope``() =
        db.RecordLesson("Global lesson applies everywhere", "discovery", null, "*", 0.5, null) |> ignore
        db.RecordLesson("Auth lesson for auth files", "user_correction", null, "src/Auth/*", 0.7, null) |> ignore
        db.RecordLesson("UI lesson for UI files", "user_correction", null, "src/UI/*", 0.7, null) |> ignore
        let context = db.GetContext(Some "src/Auth/Login.fs", 20)
        Assert.Contains("Global lesson applies everywhere", context)
        Assert.Contains("Auth lesson for auth files", context)
        Assert.DoesNotContain("UI lesson for UI files", context)

    [<Fact>]
    member _.``Consolidate merges near-duplicate lessons``() =
        // Insert directly to bypass RecordLesson's 70% fuzzy dedup
        let now = DateTime.UtcNow.ToString("o")
        db.Execute(
            "INSERT INTO lessons (lesson_text, trigger, scope, recurrence, confidence, status, created_at, updated_at) VALUES (@t, 'discovery', '*', 2, 0.3, 'active', @now, @now)",
            [ ("@t", box "Always run tests before pushing code to the remote repository"); ("@now", box now) ]
        ) |> ignore
        db.Execute(
            "INSERT INTO lessons (lesson_text, trigger, scope, recurrence, confidence, status, created_at, updated_at) VALUES (@t, 'discovery', '*', 1, 0.3, 'active', @now, @now)",
            [ ("@t", box "Always run the tests before pushing code to the remote repo"); ("@now", box now) ]
        ) |> ignore
        let before = db.Query("SELECT COUNT(*) as cnt FROM lessons WHERE status = 'active'")
        let result = svc.Consolidate()
        let after = db.Query("SELECT COUNT(*) as cnt FROM lessons WHERE status = 'active'")
        Assert.True((after.Rows.[0].["cnt"] :?> int64) < (before.Rows.[0].["cnt"] :?> int64))
        Assert.Contains("Merged", result)

    [<Fact>]
    member _.``Consolidate promotes high-recurrence lessons to knowledge``() =
        let id = db.RecordLesson("Use dependency injection everywhere", "user_correction", null, "*", 0.7, null)
        // Bump recurrence to 5
        for _ in 1..4 do
            db.RecordLesson("Use dependency injection everywhere", "user_correction", null, "*", 0.7, null) |> ignore
        let lesson = db.Query("SELECT recurrence, confidence FROM lessons WHERE id = @id", [ ("@id", box (int64 id)) ])
        Assert.True((lesson.Rows.[0].["recurrence"] :?> int64) >= 5L)
        svc.Consolidate() |> ignore
        let promoted = db.Query("SELECT status FROM lessons WHERE id = @id", [ ("@id", box (int64 id)) ])
        Assert.Equal("graduated", string promoted.Rows.[0].["status"])
        let knowledge = db.Query("SELECT id FROM knowledge WHERE content = 'Use dependency injection everywhere' AND source = 'learned'")
        Assert.True(knowledge.Rows.Length > 0)

    [<Fact>]
    member _.``Consolidate returns no-op message when nothing to do``() =
        let result = svc.Consolidate()
        Assert.Equal("No consolidation actions needed.", result)

    [<Fact>]
    member _.``Graduate writes to instructions file``() =
        let instrPath = Path.Combine(Path.GetTempPath(), $"pm-test-{Guid.NewGuid()}", ".github", "copilot-instructions.md")
        try
            // Create knowledge with high confidence and session count
            let id = db.StoreKnowledge("convention", "Always use strict mode", "*", "user_explicit")
            db.Execute(
                "UPDATE knowledge SET confidence = 0.95, session_count = 12 WHERE id = @id",
                [ ("@id", box id) ]
            ) |> ignore
            let testSvc = DomainService(db, instrPath)
            let result = testSvc.Graduate()
            Assert.Contains("Graduated", result)
            let content = File.ReadAllText(instrPath)
            Assert.Contains("## Learned Conventions", content)
            Assert.Contains("Always use strict mode", content)
            Assert.Contains("Auto-managed by ProjectMemory", content)
        finally
            let dir = Path.GetDirectoryName(instrPath) |> Path.GetDirectoryName
            try if Directory.Exists(dir) then Directory.Delete(dir, true) with _ -> ()

    [<Fact>]
    member _.``Graduate is idempotent``() =
        let instrPath = Path.Combine(Path.GetTempPath(), $"pm-test-{Guid.NewGuid()}", ".github", "copilot-instructions.md")
        try
            let id = db.StoreKnowledge("convention", "Idempotent test rule", "*", "user_explicit")
            db.Execute(
                "UPDATE knowledge SET confidence = 0.95, session_count = 15 WHERE id = @id",
                [ ("@id", box id) ]
            ) |> ignore
            let testSvc = DomainService(db, instrPath)
            testSvc.Graduate() |> ignore
            let result2 = testSvc.Graduate()
            Assert.Equal("No knowledge entries ready for graduation.", result2)
            let content = File.ReadAllText(instrPath)
            let count = content.Split("Idempotent test rule").Length - 1
            Assert.Equal(1, count)
        finally
            let dir = Path.GetDirectoryName(instrPath) |> Path.GetDirectoryName
            try if Directory.Exists(dir) then Directory.Delete(dir, true) with _ -> ()

    [<Fact>]
    member _.``GetContext records session injections when sessionId provided``() =
        db.StoreKnowledge("convention", "Injection test rule", "*", "user_explicit") |> ignore
        db.RecordLesson("Injection test lesson", "discovery", null, "*", 0.5, null) |> ignore
        db.GetContextAndTrack(None, 20, "test-session-123") |> ignore
        let injections = db.Query("SELECT item_type, item_id FROM session_injections WHERE session_id = 'test-session-123'")
        Assert.True(injections.Rows.Length >= 2)
        let types = injections.Rows |> Array.map (fun r -> string r.["item_type"])
        Assert.Contains("knowledge", types)
        Assert.Contains("lesson", types)

    [<Fact>]
    member _.``GetContext does not record injections without sessionId``() =
        db.StoreKnowledge("convention", "No injection rule", "*", "user_explicit") |> ignore
        db.GetContext(None, 20) |> ignore
        let injections = db.Query("SELECT COUNT(*) as cnt FROM session_injections")
        Assert.Equal(0L, injections.Rows.[0].["cnt"] :?> int64)

    [<Fact>]
    member _.``MarkUseful bumps knowledge confidence when useful``() =
        let id = db.StoreKnowledge("convention", "Mark useful test rule", "*", "user_explicit")
        let before = (db.Query("SELECT confidence FROM knowledge WHERE id = @id", [ ("@id", box id) ])).Rows.[0].["confidence"] :?> double
        db.GetContextAndTrack(None, 20, "mark-useful-session") |> ignore
        let result = db.MarkUseful("mark-useful-session", id, true)
        Assert.Contains("Feedback recorded", result)
        let after = (db.Query("SELECT confidence FROM knowledge WHERE id = @id", [ ("@id", box id) ])).Rows.[0].["confidence"] :?> double
        Assert.True(after > before)

    [<Fact>]
    member _.``MarkUseful decreases confidence when not useful``() =
        let id = db.StoreKnowledge("convention", "Not useful test rule", "*", "user_explicit")
        db.GetContextAndTrack(None, 20, "mark-notuseful-session") |> ignore
        // Capture confidence after surfacing (which bumps +0.05) so we measure the
        // MarkUseful(false) delta (-0.05) in isolation.
        let before = (db.Query("SELECT confidence FROM knowledge WHERE id = @id", [ ("@id", box id) ])).Rows.[0].["confidence"] :?> double
        db.MarkUseful("mark-notuseful-session", id, false) |> ignore
        let after = (db.Query("SELECT confidence FROM knowledge WHERE id = @id", [ ("@id", box id) ])).Rows.[0].["confidence"] :?> double
        Assert.True(after < before)

    [<Fact>]
    member _.``MarkUseful returns not found for missing injection``() =
        let result = db.MarkUseful("nonexistent-session", "nonexistent-id", true)
        Assert.Contains("No injection record found", result)

    [<Fact>]
    member _.``GetContext respects token budget``() =
        for i in 1..20 do
            db.StoreKnowledge("convention", $"This is a long convention rule number {i} that takes up token space in the context window", "*", "user_explicit") |> ignore
        let small = db.GetContext(None, 100, maxTokens = 100)
        let large = db.GetContext(None, 100, maxTokens = 5000)
        Assert.True(small.Length < large.Length)

    [<Fact>]
    member _.``estimateTokens approximates correctly``() =
        Assert.Equal(1, Formatting.estimateTokens "hi")
        Assert.True(Formatting.estimateTokens (String.replicate 100 "word ") > 100)

    [<Fact>]
    member _.``Export produces valid JSON with knowledge and lessons``() =
        db.StoreKnowledge("convention", "Export test rule", "*", "user_explicit") |> ignore
        db.RecordLesson("Export test lesson", "discovery", null, "*", 0.3, null) |> ignore
        let json = db.Export()
        Assert.Contains("\"knowledge\"", json)
        Assert.Contains("\"lessons\"", json)
        Assert.Contains("Export test rule", json)
        Assert.Contains("Export test lesson", json)

    [<Fact>]
    member _.``Import round-trips through Export``() =
        db.StoreKnowledge("decision", "Round trip rule", "*", "user_explicit") |> ignore
        db.RecordLesson("Round trip lesson", "explicit", null, "src/*", 0.7, null) |> ignore
        let json = db.Export()
        let freshPath = Path.Combine(Path.GetTempPath(), $"pm-test-{Guid.NewGuid()}.db")
        try
            let freshDb = ProjectMemoryDb(freshPath)
            let result = freshDb.Import(json)
            Assert.Contains("1 knowledge entries", result)
            Assert.Contains("1 lessons", result)
            let knowledge = freshDb.Query("SELECT content FROM knowledge WHERE content = 'Round trip rule'")
            Assert.Equal(1, knowledge.Rows.Length)
            let lesson = freshDb.Query("SELECT lesson_text FROM lessons WHERE lesson_text = 'Round trip lesson'")
            Assert.Equal(1, lesson.Rows.Length)
        finally
            try File.Delete(freshPath) with _ -> ()

    // --- Resource tests ---

    [<Fact>]
    member _.``Resource returns full context``() =
        db.StoreKnowledge("convention", "Resource test convention", "*", "user_explicit") |> ignore
        db.RecordLesson("Resource test lesson", "explicit", null, "*", 0.5, null) |> ignore
        let resources = MemoryResources(db)
        let result = resources.GetFullContext()
        Assert.Contains("Resource test convention", result)
        Assert.Contains("Resource test lesson", result)

    [<Fact>]
    member _.``Resource returns scoped context``() =
        db.StoreKnowledge("convention", "Global rule", "*", "user_explicit") |> ignore
        db.StoreKnowledge("file_note", "FS-only rule", "src/*.fs", "user_explicit") |> ignore
        let resources = MemoryResources(db)
        let scoped = resources.GetScopedContext("src/Main.fs")
        Assert.Contains("Global rule", scoped)
        Assert.Contains("FS-only rule", scoped)

