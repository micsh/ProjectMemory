module ProjectMemory.InstructionsFile

open System.IO
open System.Text

let sentinel = "<!-- Auto-managed by ProjectMemory. Do not edit this section manually. -->"
let sectionHeader = "## Learned Conventions"

let buildSection (instructions: string list) =
    let sb = StringBuilder()
    sb.AppendLine(sectionHeader) |> ignore
    sb.AppendLine(sentinel) |> ignore
    for instr in instructions do
        sb.AppendLine(instr) |> ignore
    sb.ToString()

let mergeIntoFile (filePath: string) (sectionContent: string) =
    let dir = Path.GetDirectoryName(filePath)
    if not (System.String.IsNullOrWhiteSpace(dir)) then
        Directory.CreateDirectory(dir) |> ignore

    let existingContent =
        if File.Exists(filePath) then
            // Normalize line endings so section-boundary search (\n##) works on
            // files written on Windows with CRLF. Write-back is fine as-is;
            // File.WriteAllText uses the platform default.
            File.ReadAllText(filePath).Replace("\r\n", "\n")
        else ""

    let newContent =
        let sentinelIdx = existingContent.IndexOf(sentinel)
        if sentinelIdx >= 0 then
            let headerIdx = existingContent.LastIndexOf(sectionHeader, sentinelIdx)
            let sectionStart = if headerIdx >= 0 then headerIdx else sentinelIdx
            let afterSentinel = sentinelIdx + sentinel.Length
            let nextSection =
                let idx = existingContent.IndexOf("\n## ", afterSentinel)
                if idx >= 0 then idx else existingContent.Length
            existingContent.Substring(0, sectionStart) + sectionContent + existingContent.Substring(nextSection)
        else
            let separator = if existingContent.Length > 0 && not (existingContent.EndsWith("\n")) then "\n\n" else "\n"
            existingContent + separator + sectionContent

    File.WriteAllText(filePath, newContent)
