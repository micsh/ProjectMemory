module ProjectMemory.Program

open System
open System.IO
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol.Server

[<EntryPoint>]
let main args =
    let dbPath =
        match Environment.GetEnvironmentVariable("PROJECT_MEMORY_DB") with
        | null | "" ->
            Path.Combine(Directory.GetCurrentDirectory(), ".project-memory", "memory.db")
        | path -> path

    let builder = Host.CreateApplicationBuilder(args)
    builder.Logging.SetMinimumLevel(LogLevel.Warning) |> ignore
    builder.Logging.AddConsole(fun o -> o.LogToStandardErrorThreshold <- LogLevel.Trace) |> ignore
    builder.Services.AddSingleton<ProjectMemoryDb>(fun _ -> ProjectMemoryDb(dbPath)) |> ignore
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
    |> ignore

    builder.Build().RunAsync().GetAwaiter().GetResult()
    0
