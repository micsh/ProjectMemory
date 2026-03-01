namespace ProjectMemory

open System.ComponentModel
open ModelContextProtocol.Server

[<McpServerResourceType>]
type MemoryResources(db: ProjectMemoryDb) =

    [<McpServerResource(UriTemplate = "memory://context", Name = "project-memory-context", Title = "Project Memory Context", MimeType = "text/markdown")>]
    [<Description("All project knowledge and active lessons. Attach at session start for automatic context injection.")>]
    member _.GetFullContext() =
        db.GetContext(None, 20)

    [<McpServerResource(UriTemplate = "memory://context/{scope}", Name = "project-memory-scoped", Title = "Scoped Project Memory", MimeType = "text/markdown")>]
    [<Description("Project knowledge and lessons filtered by file scope. Use a file path or glob pattern.")>]
    member _.GetScopedContext(scope: string) =
        db.GetContext(Some scope, 20)
