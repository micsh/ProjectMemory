namespace ProjectMemory.Tests

open System
open System.IO
open Xunit
open ProjectMemory

type DatabaseTests() =
    let dbPath = Path.Combine(Path.GetTempPath(), $"pm-test-{Guid.NewGuid()}.db")
    let db = ProjectMemoryDb(dbPath)

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

