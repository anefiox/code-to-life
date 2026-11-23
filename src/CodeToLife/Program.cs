using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeToLife;
using CodeToLife.Insights;
using CodeToLife.Runtime.Dap;
using CodeToLife.Runtime.Live;

if (args is ["check", .. var checkArgs]) return CheckCommand.Run(checkArgs);
if (args is ["explain", .. var explainArgs]) return await ExplainCommand.RunAsync(explainArgs);
if (args is ["guide", .. var guideArgs]) return await GuideCommand.RunAsync(guideArgs);

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine("""
        Code to Life: see a C# codebase as a living map, and watch it run.

        Usage: codetolife [<path>] [options]
               codetolife check [<path>] [--format text|github]
               codetolife explain [<path>] --ai openrouter|URL [--ai-model MODEL] [--limit N]
               codetolife guide [<path>] [--out FILE]

          <path>          A .sln, .slnx, .csproj or folder (default: current directory)
          -p, --port <n>  Port to serve on (default: 5170, or the next free one)
              --no-browser  Don't open a browser
              --ai <x>    Write explanations with an AI: "openrouter" (needs OPENROUTER_API_KEY) or the
                          URL of an OpenAI-compatible server (key, if any, in CODETOLIFE_AI_KEY)
              --ai-model <m>  The model to ask (default: openrouter/auto, or the server's own default)
          -h, --help      Show this help

        Debugging needs netcoredbg: run tools/Get-Netcoredbg.ps1, put it on PATH,
        or set CODETOLIFE_NETCOREDBG to its path.

        check: verify the architecture rules in .codetolife/rules.json (exit 1 when broken).
        explain: write plain-English explanations for up to N types and methods (default 10).
        guide: write a Markdown onboarding guide (tools, how to run it, folders, the tour, pages,
               settings, how-to recipes, explanations and hotspots) to FILE or the console.
        """);
    return 0;
}

AiConfig? aiConfig;
try { aiConfig = AiConfig.From(options.Ai, options.AiModel, Environment.GetEnvironmentVariable); }
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Logging.SetMinimumLevel(LogLevel.Warning).AddFilter("CodeToLife", LogLevel.Information);
builder.WebHost.UseKestrel(k => k.ListenLocalhost(options.Port));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = json.PropertyNamingPolicy;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton(json);
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<Workspace>();
builder.Services.AddSingleton<RuntimeHub>();
builder.Services.AddSingleton<PlanStore>();
builder.Services.AddSingleton(new AiSettings(aiConfig));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

// Only this machine's browser should drive a debugger: refuse requests that come from other sites.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Headers.Origin is { Count: > 0 } origin
        && !Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var o) | o is { IsLoopback: false })
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

var workspace = app.Services.GetRequiredService<Workspace>();
var events = app.Services.GetRequiredService<EventHub>();
var runtime = app.Services.GetRequiredService<RuntimeHub>();
var plans = app.Services.GetRequiredService<PlanStore>();

var api = app.MapGroup("/api");

// ---- The codebase ----
api.MapGet("/status", () => new
{
    path = workspace.Path,
    root = workspace.RootDirectory,
    loaded = workspace.IsLoaded,
    analyzing = workspace.IsAnalyzing,
    error = workspace.LastError,
    debugger = runtime.Debugger,
    changesAvailable = ChangesOverlay.IsAvailable,
    debug = new { state = runtime.Debug.State, program = runtime.Debug.Program, frames = runtime.Debug.Frames, breakpoints = runtime.Debug.Breakpoints },
    live = runtime.Sampler is { } s ? new { processId = s.ProcessId } : null,
});

api.MapGet("/graph", () => workspace.IsLoaded ? Results.Ok(workspace.Graph) : Results.Problem(workspace.LastError ?? "Still analyzing.", statusCode: 503));

api.MapPost("/open", async (OpenRequest request) =>
{
    try { await workspace.OpenAsync(request.Path); }
    catch (FileNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
    return Results.Ok(new { path = workspace.Path });
});

api.MapPost("/reload", async () => { await workspace.AnalyzeAsync(); return Results.Ok(); });

api.MapGet("/source", (string file) =>
{
    if (!workspace.Contains(file)) return Results.NotFound(new { error = "Only files in the analyzed code can be shown." });
    return Results.Text(File.ReadAllText(file), "text/plain; charset=utf-8");
});

api.MapPost("/editor", (EditorRequest request) =>
{
    if (!workspace.Contains(request.File)) return Results.NotFound();
    var editor = Environment.GetEnvironmentVariable("CODETOLIFE_EDITOR") ?? "code";
    try
    {
        var info = new ProcessStartInfo(editor) { UseShellExecute = OperatingSystem.IsWindows(), CreateNoWindow = true };
        info.ArgumentList.Add("--goto");
        info.ArgumentList.Add($"{request.File}:{Math.Max(1, request.Line)}");
        Process.Start(info);
        return Results.Ok();
    }
    catch (Exception ex) { return Results.BadRequest(new { error = $"Couldn't start '{editor}': {ex.Message}. Set CODETOLIFE_EDITOR." }); }
});

api.MapGet("/events", (HttpContext context) => events.StreamAsync(context));

// ---- What this branch changed (PR Buddy) ----
api.MapGet("/changes", (string? @base, bool? uncommitted) =>
    workspace.IsLoaded ? Results.Ok(ChangesOverlay.Build(workspace, @base, uncommitted ?? false)) : Results.Problem("Still analyzing.", statusCode: 503));

api.MapInsights(workspace, events);
api.MapLearn(workspace, app.Services.GetRequiredService<AiSettings>());

// ---- Planning ----
api.MapGet("/plan", () => plans.Load());
api.MapPut("/plan", (Plan plan) => { plans.Save(plan); events.Publish("plan.changed", new { }); return Results.Ok(); });
api.MapGet("/plan/stubs", () => Results.Text(plans.Stubs(plans.Load()), "text/plain; charset=utf-8"));

// ---- Building, so the debugger runs the code you're looking at ----
api.MapPost("/build", async (BuildRequest request) =>
{
    var project = workspace.Graph.Projects.FirstOrDefault(p => p.Name == request.Project);
    if (project is null) return Results.NotFound(new { error = "No such project." });
    var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in new[] { "build", project.Path, "-c", "Debug", "-nologo", "-v", "q", "-clp:NoSummary" }) info.ArgumentList.Add(a);
    using var process = Process.Start(info)!;
    var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    events.Publish("debug.output", new { category = "build", output = output.Length > 0 ? output : $"Built {project.Name}.\n" });
    if (process.ExitCode == 0) await workspace.AnalyzeAsync();
    return Results.Ok(new { success = process.ExitCode == 0, output });
});

// ---- Debugger ----
api.MapGet("/debug/targets", () => workspace.IsLoaded
    ? workspace.Graph.Projects.Where(p => p.OutputKind == "exe").Select(p => new { project = p.Name, program = p.BuiltAssembly, path = p.Path })
    : []);

api.MapPost("/debug/launch", async (LaunchRequest request) =>
{
    if (runtime.Debugger is not { } adapter) return NoDebugger();
    if (!File.Exists(request.Program)) return Results.BadRequest(new { error = $"'{request.Program}' doesn't exist. Build the project first." });
    return await Run(() => runtime.Debug.LaunchAsync(adapter, request));
});

api.MapPost("/debug/attach", async (AttachRequest request) =>
    runtime.Debugger is { } adapter ? await Run(() => runtime.Debug.AttachAsync(adapter, request.ProcessId)) : NoDebugger());

api.MapPost("/debug/continue", () => Run(runtime.Debug.ContinueAsync));
api.MapPost("/debug/next", () => Run(runtime.Debug.StepOverAsync));
api.MapPost("/debug/stepIn", () => Run(runtime.Debug.StepInAsync));
api.MapPost("/debug/stepOut", () => Run(runtime.Debug.StepOutAsync));
api.MapPost("/debug/pause", () => Run(runtime.Debug.PauseAsync));
api.MapPost("/debug/stop", () => Run(runtime.Debug.StopAsync));
api.MapPost("/debug/animate", (AnimateRequest request) =>
{
    _ = runtime.Debug.AnimateAsync(Math.Clamp(request.Steps, 1, 5000), TimeSpan.FromMilliseconds(Math.Clamp(request.DelayMs, 0, 5000)), request.StepOver);
    return Results.Ok();
});
api.MapPost("/debug/animate/stop", () => { runtime.Debug.StopAnimation(); return Results.Ok(); });
api.MapPost("/debug/breakpoints", (BreakpointRequest request) =>
    workspace.Contains(request.File) ? Run(() => runtime.Debug.SetBreakpointsAsync(Path.GetFullPath(request.File), request.Lines)) : Task.FromResult(Results.NotFound()));
api.MapGet("/debug/variables", async (int reference) => Results.Ok(await runtime.Debug.VariablesAsync(reference)));
api.MapGet("/debug/frame", async (int frameId) => Results.Ok(await runtime.Debug.FrameLocalsAsync(frameId)));
api.MapPost("/debug/evaluate", async (EvaluateRequest request) =>
{
    try { return Results.Ok(new { result = await runtime.Debug.EvaluateAsync(request.Expression, request.FrameId) }); }
    catch (Exception ex) { return Results.Ok(new { error = ex.Message }); }
});

// ---- Live view (no debugger needed) ----
api.MapGet("/live/processes", () => LiveSampler.ListProcesses());
api.MapPost("/live/start", async (LiveRequest request) => await Run(() => runtime.StartLiveAsync(request.ProcessId, request.IntervalMs ?? 250, request.Events ?? true)));
api.MapGet("/live/events", () => runtime.Tracer is { } t ? Results.Ok(t.Snapshot()) : Results.NotFound(new { error = runtime.TracerError ?? "Not watching a process with runtime events on." }));
api.MapPost("/live/stop", () => Run(runtime.StopLiveAsync));
api.MapGet("/live/totals", () => runtime.Sampler is { } s ? Results.Ok(new { totals = s.Totals() is var t ? new { members = t.Members, types = t.Types, calls = t.Calls, samples = t.Samples } : null }) : Results.NotFound());
api.MapGet("/live/heap", async (int processId) =>
{
    try
    {
        var (counts, complete, seen) = await Task.Run(() => LiveSampler.CountInstances(processId, () => workspace.Mapper, TimeSpan.FromSeconds(5)));
        return Results.Ok(new { counts, complete, objectsSeen = seen });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Analyze before serving, so the first page load has a graph to show.
var url = $"http://localhost:{options.Port}/";
Console.WriteLine($"Code to Life: analyzing {options.Path}...");
_ = workspace.OpenAsync(options.Path).ContinueWith(t =>
{
    if (t.IsFaulted) Console.Error.WriteLine($"Couldn't open {options.Path}: {t.Exception?.GetBaseException().Message}");
    else if (workspace.IsLoaded) Console.WriteLine($"  {workspace.Graph.Types.Count} types in {workspace.Graph.Projects.Count} project(s).");
    Console.WriteLine($"  Open {url}");
    if (aiConfig is not null) Console.WriteLine($"  Explanations: {aiConfig}");
    Console.WriteLine(runtime.Debugger is { } d ? $"  Debugger: {d}" : "  Debugger: netcoredbg not found (live view still works). Run tools/Get-Netcoredbg.ps1 to enable stepping.");
    if (options.OpenBrowser) OpenBrowser(url);
});

app.Lifetime.ApplicationStopping.Register(() => runtime.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)));
app.Run();
return 0;

static IResult NoDebugger() => Results.BadRequest(new
{
    error = "netcoredbg wasn't found. Run tools/Get-Netcoredbg.ps1, put netcoredbg on PATH, or set CODETOLIFE_NETCOREDBG.",
});

static async Task<IResult> Run(Func<Task> action)
{
    try
    {
        await action();
        return Results.Ok();
    }
    catch (Exception ex) when (ex is InvalidOperationException or DapException or TimeoutException or IOException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}

static void OpenBrowser(string url)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch { /* headless: the URL is printed above */ }
}

record OpenRequest(string Path);
record EditorRequest(string File, int Line);
record BuildRequest(string Project);
record AttachRequest(int ProcessId);
record AnimateRequest(int Steps = 200, int DelayMs = 400, bool StepOver = false);
record BreakpointRequest(string File, int[] Lines);
record EvaluateRequest(string Expression, int? FrameId);
record LiveRequest(int ProcessId, int? IntervalMs, bool? Events);

sealed record CliOptions(string Path, int Port, bool OpenBrowser, bool ShowHelp, string? Ai, string? AiModel)
{
    public static CliOptions Parse(string[] args)
    {
        string? path = null, ai = null, aiModel = null;
        int? port = null;
        var browser = true;
        var help = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p" or "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
                case "--no-browser": browser = false; break;
                case "--ai" when i + 1 < args.Length: ai = args[++i]; break;
                case "--ai-model" when i + 1 < args.Length: aiModel = args[++i]; break;
                case "-h" or "--help" or "-?": help = true; break;
                default: path ??= args[i]; break;
            }
        }
        return new CliOptions(System.IO.Path.GetFullPath(path ?? Environment.CurrentDirectory), port ?? FreePort(5170), browser, help, ai, aiModel);
    }

    static int FreePort(int preferred)
    {
        for (var port = preferred; port < preferred + 50; port++)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (System.Net.Sockets.SocketException) { }
        }
        return 0;
    }
}
