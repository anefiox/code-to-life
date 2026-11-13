using CodeToLife.Insights;
using CodeToLife.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// codetolife-mcp [<path>]: an MCP server over stdio that answers questions about a C# codebase
// (who calls what, what a change breaks, which tests reach it, whether the architecture rules
// hold) and drives netcoredbg so an assistant can watch the code run.
if (args is ["-h" or "--help"])
{
    Console.Error.WriteLine("""
        Usage: codetolife-mcp [<path>]

        An MCP server (stdio) for a C# codebase. <path> is a .sln, .slnx, .csproj or folder;
        the default is the current directory, which is the project folder when an MCP client
        such as Claude Code starts it. Register it with:

          claude mcp add codetolife -- codetolife-mcp
        """);
    return;
}

var path = args.FirstOrDefault(a => !a.StartsWith('-')) ?? Environment.CurrentDirectory;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
// stdout is the protocol: everything else goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(new CodebaseHost(path));
builder.Services.AddSingleton<DebugHost>();
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "codetolife", Version = "0.1.0" })
    .WithStdioServerTransport()
    .WithTools<CodeTools>()
    .WithTools<DebugTools>();

var app = builder.Build();
// Start reading the code straight away so the first question doesn't wait for it.
_ = app.Services.GetRequiredService<CodebaseHost>().ReadyAsync();
await app.RunAsync(); // the container disposes the debug session (and the program it runs) on the way out
