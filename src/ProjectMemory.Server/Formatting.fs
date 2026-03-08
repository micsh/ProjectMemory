module ProjectMemory.Formatting

open System.Text

let private categoryTitle = function
    | "convention" -> "Conventions"
    | "decision" -> "Decisions"
    | "known_issue" -> "Known Issues"
    | "file_note" -> "File Notes"
    | "preference" -> "Preferences"
    | other -> other

let estimateTokens (text: string) = (text.Length + 3) / 4

let private formatKnowledgeItem (item: Map<string, obj>) =
    let content = string item.["content"]
    let conf = item.["confidence"] :?> double
    let itemScope = string item.["scope"]
    let scopeStr = if itemScope = "*" then "" else $" (scope: {itemScope})"
    $"- [%.2f{conf}] {content}{scopeStr}"

let private formatLessonItem (lesson: Map<string, obj>) =
    let text = string lesson.["lesson_text"]
    let recurrence = lesson.["recurrence"] :?> int64
    let trigger = string lesson.["trigger"]
    $"- {text} ({recurrence} occurrence(s), trigger: {trigger})"

let formatContext (knowledgeRows: Map<string, obj> array) (lessonRows: Map<string, obj> array) (maxTokens: int) : string =
    if knowledgeRows.Length = 0 && lessonRows.Length = 0 then
        "No project memory stored yet. Use project_store to add knowledge, or record_lesson when you learn something."
    else
        let sb = StringBuilder()
        let mutable tokens = 0
        let header = "## Project Memory"
        tokens <- tokens + estimateTokens header + 5
        let items = ResizeArray<string>()

        // Format knowledge items, respecting budget
        let grouped = knowledgeRows |> Array.groupBy (fun r -> string r.["category"])
        for category, categoryItems in grouped do
            let catHeader = $"### {categoryTitle category}"
            let catTokens = estimateTokens catHeader
            if tokens + catTokens <= maxTokens then
                items.Add(catHeader)
                tokens <- tokens + catTokens
                for item in categoryItems do
                    let line = formatKnowledgeItem item
                    let lineTokens = estimateTokens line
                    if tokens + lineTokens <= maxTokens then
                        items.Add(line)
                        tokens <- tokens + lineTokens
                items.Add("")

        // Format lesson items, respecting budget
        if lessonRows.Length > 0 && tokens < maxTokens then
            let lessonHeader = "### Active Lessons"
            tokens <- tokens + estimateTokens lessonHeader
            items.Add(lessonHeader)
            for lesson in lessonRows do
                let line = formatLessonItem lesson
                let lineTokens = estimateTokens line
                if tokens + lineTokens <= maxTokens then
                    items.Add(line)
                    tokens <- tokens + lineTokens
            items.Add("")

        let itemCount = items |> Seq.filter (fun l -> l.StartsWith("- ")) |> Seq.length
        sb.AppendLine($"## Project Memory ({itemCount} items)") |> ignore
        sb.AppendLine() |> ignore
        for line in items do
            sb.AppendLine(line) |> ignore

        sb.ToString().TrimEnd()
