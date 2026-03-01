namespace ProjectMemory

open System
open System.ComponentModel
open System.Runtime.InteropServices
open ModelContextProtocol.Server

[<McpServerToolType>]
type KnowledgeTools(db: ProjectMemoryDb) =

    [<McpServerTool(Name = "get_context")>]
    [<Description("Get relevant project knowledge and lessons. Call at session start and when switching codebase areas.")>]
    member _.GetContext
        (
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("File path or glob to filter by scope. Omit for all knowledge.")>]
            scope: string
        ) =
        let scope = if String.IsNullOrEmpty(scope) then None else Some scope
        db.GetContext(scope, 20)

    [<McpServerTool(Name = "project_query")>]
    [<Description("Run read-only SQL against the project memory database. Tables: knowledge(id,category,scope,content,confidence,source,session_count), lessons(id,lesson_text,trigger,scope,recurrence,confidence,status)")>]
    member _.ProjectQuery
        (
            [<Description("SQL SELECT query")>]
            sql: string
        ) =
        let trimmed = sql.TrimStart()
        if not (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) then
            "Error: Only SELECT queries allowed. Use project_store, project_forget, or record_lesson for writes."
        else
            try
                let result = db.Query(sql)
                if result.Rows.Length = 0 then
                    "No results."
                else
                    let sb = Text.StringBuilder()
                    sb.AppendLine(String.concat " | " result.Columns) |> ignore
                    sb.AppendLine(String.concat " | " (result.Columns |> Array.map (fun _ -> "---"))) |> ignore
                    for row in result.Rows do
                        let values =
                            result.Columns
                            |> Array.map (fun col ->
                                match row.TryFind(col) with
                                | Some v when not (isNull v) -> string v
                                | _ -> "NULL")
                        sb.AppendLine(String.concat " | " values) |> ignore
                    sb.ToString().TrimEnd()
            with ex ->
                $"Error: {ex.Message}"

    [<McpServerTool(Name = "project_store")>]
    [<Description("Store project knowledge. Categories: convention, decision, known_issue, file_note, preference. Auto-deduplicates — storing same content bumps confidence.")>]
    member _.ProjectStore
        (
            [<Description("Category: convention, decision, known_issue, file_note, or preference")>]
            category: string,
            [<Description("The knowledge to remember (natural language)")>]
            content: string,
            [<Optional; DefaultParameterValue("*")>]
            [<Description("File glob this applies to, or '*' for project-wide")>]
            scope: string
        ) =
        try
            let scope = if String.IsNullOrEmpty(scope) then "*" else scope
            let id = db.StoreKnowledge(category, content, scope, "user_explicit")
            $"Stored with id: {id}"
        with ex ->
            $"Error: {ex.Message}"

    [<McpServerTool(Name = "project_forget")>]
    [<Description("Remove a knowledge entry by ID. Use when stored knowledge is wrong or outdated.")>]
    member _.ProjectForget
        (
            [<Description("Knowledge entry ID to remove")>]
            id: string
        ) =
        if db.ForgetKnowledge(id) then $"Removed: {id}"
        else $"Not found: {id}"

    [<McpServerTool(Name = "record_lesson")>]
    [<Description("Record a lesson learned this session. Call when: user corrects you, build fails and you discover why, you find a non-obvious pattern. Format: 'When [situation], [action], because [reason]'")>]
    member _.RecordLesson
        (
            [<Description("Lesson: 'When [situation], [action], because [reason]'")>]
            lesson: string,
            [<Description("Trigger: user_correction, build_failure, repeated_pattern, discovery, or explicit")>]
            trigger: string,
            [<Optional; DefaultParameterValue(null: string)>]
            [<Description("Agent role that made the mistake")>]
            agentRole: string,
            [<Optional; DefaultParameterValue("*")>]
            [<Description("File glob this lesson applies to")>]
            scope: string
        ) =
        try
            let scope = if String.IsNullOrEmpty(scope) then "*" else scope
            let confidence =
                match trigger with
                | "user_correction" | "explicit" -> 0.7
                | _ -> 0.3
            let id = db.RecordLesson(lesson, trigger, agentRole, scope, confidence, null)
            $"Lesson recorded with id: {id}"
        with ex ->
            $"Error: {ex.Message}"

    [<McpServerTool(Name = "mark_useful")>]
    [<Description("Feedback on whether injected knowledge was useful. Improves future context selection.")>]
    member _.MarkUseful
        (
            [<Description("Session identifier")>]
            sessionId: string,
            [<Description("Item ID (knowledge or lesson)")>]
            itemId: string,
            [<Description("Was this useful?")>]
            useful: bool
        ) =
        try
            let affected =
                db.Execute(
                    "UPDATE session_injections SET was_useful = @useful WHERE session_id = @sid AND item_id = @iid",
                    [ ("@useful", box (if useful then 1 else 0))
                      ("@sid", box sessionId)
                      ("@iid", box itemId) ]
                )
            if affected > 0 then $"Feedback recorded for {itemId}"
            else $"No injection record found for session={sessionId}, item={itemId}"
        with ex ->
            $"Error: {ex.Message}"
