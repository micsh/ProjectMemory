module ProjectMemory.Formatting

open System.Text

let private categoryTitle = function
    | "convention" -> "Conventions"
    | "decision" -> "Decisions"
    | "known_issue" -> "Known Issues"
    | "file_note" -> "File Notes"
    | "preference" -> "Preferences"
    | other -> other

let formatContext (knowledgeRows: Map<string, obj> array) (lessonRows: Map<string, obj> array) : string =
    if knowledgeRows.Length = 0 && lessonRows.Length = 0 then
        "No project memory stored yet. Use project_store to add knowledge, or record_lesson when you learn something."
    else
        let sb = StringBuilder()
        let total = knowledgeRows.Length + lessonRows.Length
        sb.AppendLine($"## Project Memory ({total} items)") |> ignore
        sb.AppendLine() |> ignore

        if knowledgeRows.Length > 0 then
            let grouped = knowledgeRows |> Array.groupBy (fun r -> string r.["category"])
            for category, items in grouped do
                sb.AppendLine($"### {categoryTitle category}") |> ignore
                for item in items do
                    let content = string item.["content"]
                    let conf = item.["confidence"] :?> double
                    let sessions = item.["session_count"] :?> int64
                    let itemScope = string item.["scope"]
                    let scopeStr = if itemScope = "*" then "" else $" (scope: {itemScope})"
                    sb.AppendLine($"- [%.2f{conf}] {content}{scopeStr} — {sessions} session(s)") |> ignore
                sb.AppendLine() |> ignore

        if lessonRows.Length > 0 then
            sb.AppendLine("### Active Lessons") |> ignore
            for lesson in lessonRows do
                let text = string lesson.["lesson_text"]
                let recurrence = lesson.["recurrence"] :?> int64
                let trigger = string lesson.["trigger"]
                sb.AppendLine($"- {text} ({recurrence} occurrence(s), trigger: {trigger})") |> ignore
            sb.AppendLine() |> ignore

        sb.ToString().TrimEnd()
