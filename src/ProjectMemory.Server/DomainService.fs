namespace ProjectMemory

open System
open System.Text

/// Owns the domain-level operations that orchestrate across persistence and file I/O:
/// Consolidate (merge/promote/prune lessons) and Graduate (promote knowledge to
/// copilot-instructions.md). Also wraps RecordLesson to own the auto-consolidation trigger.
///
/// Depends on ProjectMemoryDb for all persistence. ProjectMemoryDb has no knowledge of
/// DomainService — dependency flows one way: DomainService → ProjectMemoryDb.
type DomainService(db: ProjectMemoryDb, instructionsPath: string) =

    /// Record a lesson and trigger auto-consolidation when the lesson count crosses a
    /// threshold (>30 active lessons, every 5th addition). The just-inserted lesson is
    /// excluded from the consolidation pass it triggers so it cannot be immediately
    /// superseded by the run it initiates.
    ///
    /// Note: bulk import of many lessons fires this at each qualifying count.
    /// Latency cost is bounded until C3 (FTS5 pre-filter) lands.
    member this.RecordLesson(lessonText, trigger, agentRole, scope, confidence, sourceRef) : int =
        let newId = db.RecordLesson(lessonText, trigger, agentRole, scope, confidence, sourceRef)
        let countResult = db.Query("SELECT COUNT(*) as cnt FROM lessons WHERE status = 'active'")
        let count = countResult.Rows.[0].["cnt"] :?> int64
        if count > 30L && count % 5L = 0L then
            this.Consolidate(excludeId = int64 newId) |> ignore
        newId

    /// Consolidate active lessons: merge near-duplicates (>80% Jaccard), promote
    /// high-recurrence lessons to knowledge, and prune stale entries.
    ///
    /// ?excludeId: lesson rowid to exclude from this pass (used when the trigger
    /// comes from RecordLesson — prevents the just-inserted lesson being superseded
    /// by the consolidation it triggered).
    member _.Consolidate(?excludeId: int64) : string =
        let now = DateTime.UtcNow.ToString("o")
        let sb = StringBuilder()
        let activeLessons =
            match excludeId with
            | Some eid ->
                db.Query(
                    "SELECT id, lesson_text, recurrence, confidence, scope, updated_at FROM lessons WHERE status = 'active' AND id != @excludeId",
                    [ ("@excludeId", box eid) ]
                )
            | None ->
                db.Query("SELECT id, lesson_text, recurrence, confidence, scope, updated_at FROM lessons WHERE status = 'active'")

        // Merge near-duplicates using FTS5 pre-filtering + Jaccard verification.
        // For each active lesson, retrieve up to 50 FTS candidates with overlapping
        // tokens, then Jaccard-score only those pairs — bounding the work to O(n × 50)
        // rather than O(n²). Threshold: >80% Jaccard similarity.
        let mutable merged = Set.empty<int64>
        let rows = activeLessons.Rows
        for i in 0 .. rows.Length - 1 do
            let idI = rows.[i].["id"] :?> int64
            if not (Set.contains idI merged) then
                let textI = string rows.[i].["lesson_text"]
                let ftsQuery =
                    textI.ToLowerInvariant().Split([| ' '; '\t'; '\n'; '\r'; ','; '.'; ';'; ':' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun t -> $"\"{t}\"")
                    |> fun tokens ->
                        if tokens.Length = 0 then None
                        else Some (String.concat " OR " tokens)
                let candidateIds =
                    match ftsQuery with
                    | Some q ->
                        let r = db.Query(
                            "SELECT l.id FROM lessons l JOIN lessons_fts f ON l.id = f.rowid WHERE f.lesson_text MATCH @q AND l.status = 'active' AND l.id != @self LIMIT 50",
                            [ ("@q", box q); ("@self", box idI) ]
                        )
                        r.Rows |> Array.map (fun row -> row.["id"] :?> int64)
                    | None -> [||]
                // Only compare against candidates that are also in our active snapshot
                // and haven't already been merged in this pass.
                let snapshotById = rows |> Array.map (fun r -> r.["id"] :?> int64, r) |> Map.ofArray
                for idJ in candidateIds do
                    if not (Set.contains idJ merged) && idJ <> idI then
                        match Map.tryFind idJ snapshotById with
                        | None -> ()
                        | Some rowJ ->
                            let textJ = string rowJ.["lesson_text"]
                            if Similarity.jaccard textI textJ > 0.8 then
                                let recI = rows.[i].["recurrence"] :?> int64
                                let recJ = rowJ.["recurrence"] :?> int64
                                let keepId, supersededId =
                                    if recI >= recJ then idI, idJ else idJ, idI
                                db.Execute(
                                    "UPDATE lessons SET recurrence = recurrence + @addRec, confidence = MIN(1.0, confidence + 0.1), updated_at = @now WHERE id = @id",
                                    [ ("@addRec", box (if keepId = idI then recJ else recI))
                                      ("@now", box now); ("@id", box keepId) ]
                                ) |> ignore
                                db.Execute(
                                    "UPDATE lessons SET status = 'superseded', updated_at = @now WHERE id = @id",
                                    [ ("@now", box now); ("@id", box supersededId) ]
                                ) |> ignore
                                merged <- Set.add supersededId merged
                                sb.AppendLine($"Merged lesson {supersededId} into {keepId}") |> ignore

        // Promote high-recurrence lessons to knowledge
        let promotable =
            db.Query("SELECT id, lesson_text, scope FROM lessons WHERE status = 'active' AND recurrence >= 5 AND confidence >= 0.7")
        for row in promotable.Rows do
            let lessonId = row.["id"] :?> int64
            let text = string row.["lesson_text"]
            let scope = string row.["scope"]
            db.StoreKnowledge("convention", text, scope, "learned") |> ignore
            db.Execute(
                "UPDATE lessons SET status = 'graduated', updated_at = @now WHERE id = @id",
                [ ("@now", box now); ("@id", box lessonId) ]
            ) |> ignore
            sb.AppendLine($"Promoted lesson {lessonId} to knowledge") |> ignore

        // Prune stale lessons (not updated in 30+ days, recurrence = 1)
        let pruned =
            db.Execute(
                "UPDATE lessons SET status = 'superseded', updated_at = @now WHERE status = 'active' AND recurrence = 1 AND updated_at < @cutoff",
                [ ("@now", box now); ("@cutoff", box (DateTime.UtcNow.AddDays(-30.0).ToString("o"))) ]
            )
        if pruned > 0 then
            sb.AppendLine($"Pruned {pruned} stale lesson(s)") |> ignore

        // Confidence decay: items not surfaced in the last 5 distinct sessions lose
        // 0.03 confidence per Consolidate call. Uses a named CTE to identify recent
        // sessions — never an inline correlated subquery.
        // Edge cases: fewer than 5 sessions → CTE returns however many exist, all
        // unseen items decay. Zero sessions → all active items decay (correct — nothing
        // has been surfaced yet). Confidence floor is 0.0.
        let recentSessionsCte =
            "WITH recent_sessions AS (SELECT session_id FROM session_injections GROUP BY session_id ORDER BY MAX(injected_at) DESC LIMIT 5)"

        let knowledgeDecayed =
            db.Execute(
                $"{recentSessionsCte} UPDATE knowledge SET confidence = MAX(confidence - 0.03, 0.0), updated_at = @now WHERE status = 'active' AND id NOT IN (SELECT DISTINCT item_id FROM session_injections WHERE item_type = 'knowledge' AND session_id IN (SELECT session_id FROM recent_sessions))",
                [ ("@now", box now) ]
            )
        let lessonDecayed =
            db.Execute(
                $"{recentSessionsCte} UPDATE lessons SET confidence = MAX(confidence - 0.03, 0.0), updated_at = @now WHERE status = 'active' AND id NOT IN (SELECT DISTINCT CAST(item_id AS INTEGER) FROM session_injections WHERE item_type = 'lesson' AND session_id IN (SELECT session_id FROM recent_sessions))",
                [ ("@now", box now) ]
            )
        let totalDecayed = knowledgeDecayed + lessonDecayed

        // Auto-archive items whose confidence fell below 0.2 (threshold is strict <).
        // Item at exactly 0.2 is preserved. Archived items are excluded from
        // GetContext results but remain in the DB for audit / manual recovery.
        // Note: an item may graduate (to copilot-instructions.md) and later be archived
        // in the DB — the instructions file has no removal mechanism (known asymmetry).
        let knowledgeArchived =
            db.Execute(
                "UPDATE knowledge SET status = 'archived', updated_at = @now WHERE status = 'active' AND confidence < 0.2",
                [ ("@now", box now) ]
            )
        let lessonArchived =
            db.Execute(
                "UPDATE lessons SET status = 'archived', updated_at = @now WHERE status = 'active' AND confidence < 0.2",
                [ ("@now", box now) ]
            )
        let totalArchived = knowledgeArchived + lessonArchived

        if totalDecayed > 0 then
            sb.AppendLine($"Decayed {totalDecayed} item(s)") |> ignore
        if totalArchived > 0 then
            sb.AppendLine($"Archived {totalArchived} item(s) below confidence threshold") |> ignore

        let result = sb.ToString().TrimEnd()
        if String.IsNullOrWhiteSpace(result) then "No consolidation actions needed."
        else result

    /// Graduate high-confidence knowledge entries to copilot-instructions.md.
    /// File is written FIRST; DB graduation records are inserted only on success —
    /// so a file-write failure leaves items eligible for the next graduation run.
    member _.Graduate() : string =
        let now = DateTime.UtcNow.ToString("o")
        let sb = StringBuilder()

        let candidates =
            db.Query(
                "SELECT id, content, scope FROM knowledge WHERE confidence >= 0.9 AND session_count >= 10 AND id NOT IN (SELECT item_id FROM graduations WHERE item_type = 'knowledge')"
            )

        if candidates.Rows.Length = 0 then
            "No knowledge entries ready for graduation."
        else
            let existingGraduated =
                db.Query("SELECT instruction_text FROM graduations ORDER BY graduated_at")
            let existingInstructions =
                existingGraduated.Rows |> Array.map (fun r -> string r.["instruction_text"]) |> Array.toList

            // Collect all graduation records before any writes
            let pending =
                candidates.Rows |> Array.map (fun row ->
                    let knowledgeId = string row.["id"]
                    let content = string row.["content"]
                    let scope = string row.["scope"]
                    let instruction =
                        if scope = "*" then $"- {content}"
                        else $"- {content} (applies to: {scope})"
                    knowledgeId, content, instruction)

            // Write to file FIRST — if this throws, no DB records are written
            // and the items remain eligible for graduation on the next run.
            let allInstructions = existingInstructions @ (pending |> Array.map (fun (_, _, instr) -> instr) |> Array.toList)
            let sectionContent = InstructionsFile.buildSection allInstructions
            InstructionsFile.mergeIntoFile instructionsPath sectionContent

            // File write succeeded — record graduations in DB
            for knowledgeId, content, instruction in pending do
                db.Execute(
                    "INSERT INTO graduations (item_type, item_id, target_file, graduated_at, instruction_text) VALUES ('knowledge', @kid, @file, @now, @text)",
                    [ ("@kid", box knowledgeId); ("@file", box instructionsPath)
                      ("@now", box now); ("@text", box instruction) ]
                ) |> ignore
                sb.AppendLine($"Graduated knowledge {knowledgeId}: {content}") |> ignore

            sb.ToString().TrimEnd()
