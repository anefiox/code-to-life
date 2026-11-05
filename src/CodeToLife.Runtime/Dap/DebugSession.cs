using System.Text.Json.Nodes;
using CodeToLife.Analysis;

namespace CodeToLife.Runtime.Dap;

public sealed record LaunchRequest(string Program, IReadOnlyList<string>? Args, string? Cwd, bool StopAtEntry = false);

public sealed record StackFrameInfo(int Id, string Name, string? File, int Line, string? TypeId, string? MemberId);

public sealed record VariableInfo(string Name, string Value, string? Type, int VariablesReference);

public enum DebugState { Idle, Starting, Running, Stopped, Exited }

/// <summary>
/// One debugging session against netcoredbg, translated into graph terms: every time the program
/// stops, the call stack is mapped to type and member nodes so the UI can light them up.
/// </summary>
public sealed class DebugSession : IAsyncDisposable
{
    readonly Action<string, object> _publish;
    readonly Func<FrameMapper> _mapper;
    readonly Dictionary<string, SortedSet<int>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();
    DapClient? _client;
    TaskCompletionSource<bool>? _nextStop;
    CancellationTokenSource? _animation;

    public DebugSession(Action<string, object> publish, Func<FrameMapper> mapper)
    {
        _publish = publish;
        _mapper = mapper;
    }

    public DebugState State { get; private set; } = DebugState.Idle;
    public int? StoppedThread { get; private set; }
    public IReadOnlyList<StackFrameInfo> Frames { get; private set; } = [];
    public string? Program { get; private set; }

    public IReadOnlyDictionary<string, int[]> Breakpoints
    {
        get { lock (_gate) return _breakpoints.ToDictionary(b => b.Key, b => b.Value.ToArray()); }
    }

    public async Task LaunchAsync(string adapter, LaunchRequest request)
    {
        await StopAsync();
        SetState(DebugState.Starting);
        Program = request.Program;
        var program = Path.GetFullPath(request.Program);
        var (exe, args) = program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? (DotnetHost(), new[] { program }.Concat(request.Args ?? []).ToArray())
            : (program, (request.Args ?? []).ToArray());

        await StartAdapterAsync(adapter, "launch", new JsonObject
        {
            ["name"] = "Code to Life",
            ["type"] = "coreclr",
            ["request"] = "launch",
            ["program"] = exe,
            ["args"] = new JsonArray(args.Select(a => (JsonNode)a).ToArray()),
            ["cwd"] = request.Cwd ?? Path.GetDirectoryName(program),
            ["stopAtEntry"] = request.StopAtEntry,
            ["justMyCode"] = true,
            ["console"] = "internalConsole",
        });
    }

    public async Task AttachAsync(string adapter, int processId)
    {
        await StopAsync();
        SetState(DebugState.Starting);
        Program = $"process {processId}";
        await StartAdapterAsync(adapter, "attach", new JsonObject
        {
            ["name"] = "Code to Life",
            ["type"] = "coreclr",
            ["request"] = "attach",
            ["processId"] = processId,
            ["justMyCode"] = true,
        });
    }

    async Task StartAdapterAsync(string adapter, string command, JsonObject arguments)
    {
        var client = DapClient.Start(adapter, ["--interpreter=vscode"]);
        _client = client;
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += (name, body) =>
        {
            if (name == "initialized") initialized.TrySetResult();
            OnEvent(client, name, body);
        };
        client.Closed += reason =>
        {
            if (_client != client) return;
            SetState(DebugState.Exited);
            _publish("debug.exited", new { reason });
        };

        await client.RequestAsync("initialize", new JsonObject
        {
            ["clientID"] = "codetolife",
            ["clientName"] = "Code to Life",
            ["adapterID"] = "coreclr",
            ["pathFormat"] = "path",
            ["linesStartAt1"] = true,
            ["columnsStartAt1"] = true,
            ["supportsVariableType"] = true,
            ["supportsRunInTerminalRequest"] = false,
        });

        // launch only answers after configurationDone, so don't await it before configuring.
        var started = client.RequestAsync(command, arguments, TimeSpan.FromSeconds(60));
        await Task.WhenAny(initialized.Task, started, Task.Delay(TimeSpan.FromSeconds(15)));
        if (started.IsFaulted) { await started; }

        foreach (var (file, lines) in Breakpoints) await SendBreakpointsAsync(client, file, lines);
        try { await client.RequestAsync("setExceptionBreakpoints", new JsonObject { ["filters"] = new JsonArray("user-unhandled") }); }
        catch (DapException) { await client.RequestAsync("setExceptionBreakpoints", new JsonObject { ["filters"] = new JsonArray() }); }
        await client.RequestAsync("configurationDone");
        await started;
        if (State == DebugState.Starting) SetState(DebugState.Running);
    }

    void OnEvent(DapClient client, string name, JsonObject body)
    {
        switch (name)
        {
            case "stopped":
                var threadId = body["threadId"]?.GetValue<int>();
                var reason = body["reason"]?.GetValue<string>() ?? "pause";
                var text = body["text"]?.GetValue<string>() ?? body["description"]?.GetValue<string>();
                _ = Task.Run(() => OnStoppedAsync(client, threadId, reason, text));
                break;
            case "continued":
                SetState(DebugState.Running);
                break;
            case "output":
                var category = body["category"]?.GetValue<string>() ?? "console";
                if (category == "telemetry") break;
                _publish("debug.output", new { category, output = body["output"]?.GetValue<string>() ?? "" });
                break;
            case "breakpoint":
                _publish("debug.breakpoint", new { breakpoint = body["breakpoint"]?.ToJsonString() });
                break;
            case "exited":
                _publish("debug.output", new { category = "console", output = $"Program exited with code {body["exitCode"]}.\n" });
                break;
            case "terminated":
                SetState(DebugState.Exited);
                _nextStop?.TrySetResult(false);
                _publish("debug.exited", new { reason = "terminated" });
                break;
        }
    }

    async Task OnStoppedAsync(DapClient client, int? threadId, string reason, string? text)
    {
        try
        {
            threadId ??= await FirstThreadAsync(client);
            StoppedThread = threadId;
            var body = await client.RequestAsync("stackTrace", new JsonObject { ["threadId"] = threadId, ["startFrame"] = 0, ["levels"] = 60 });
            var mapper = _mapper();
            var frames = new List<StackFrameInfo>();
            foreach (var frame in body["stackFrames"]?.AsArray() ?? [])
            {
                if (frame is null) continue;
                var file = frame["source"]?["path"]?.GetValue<string>();
                var line = frame["line"]?.GetValue<int>() ?? 0;
                var location = mapper.FromSource(file, line);
                frames.Add(new StackFrameInfo(frame["id"]?.GetValue<int>() ?? 0, frame["name"]?.GetValue<string>() ?? "?", file, line, location?.TypeId, location?.MemberId));
            }
            Frames = frames;
            var locals = frames.Count > 0 ? await LocalsAsync(client, frames[0].Id) : [];
            SetState(DebugState.Stopped);
            _publish("debug.stopped", new { reason, text, threadId, frames, locals });
            _nextStop?.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _publish("debug.error", new { message = ex.Message });
            _nextStop?.TrySetResult(false);
        }
    }

    static async Task<int> FirstThreadAsync(DapClient client)
    {
        var threads = await client.RequestAsync("threads");
        return threads["threads"]?.AsArray().FirstOrDefault()?["id"]?.GetValue<int>() ?? 1;
    }

    async Task<IReadOnlyList<VariableInfo>> LocalsAsync(DapClient client, int frameId)
    {
        var scopes = await client.RequestAsync("scopes", new JsonObject { ["frameId"] = frameId });
        var result = new List<VariableInfo>();
        foreach (var scope in scopes["scopes"]?.AsArray() ?? [])
        {
            var reference = scope?["variablesReference"]?.GetValue<int>() ?? 0;
            if (reference == 0) continue;
            result.AddRange(await VariablesAsync(client, reference));
            break; // the first scope is the locals; the rest can be expanded on demand
        }
        return result;
    }

    public Task<IReadOnlyList<VariableInfo>> VariablesAsync(int reference) =>
        _client is { } client ? VariablesAsync(client, reference) : Task.FromResult<IReadOnlyList<VariableInfo>>([]);

    static async Task<IReadOnlyList<VariableInfo>> VariablesAsync(DapClient client, int reference)
    {
        var body = await client.RequestAsync("variables", new JsonObject { ["variablesReference"] = reference });
        return (body["variables"]?.AsArray() ?? [])
            .Where(v => v is not null)
            .Select(v => new VariableInfo(
                v!["name"]?.GetValue<string>() ?? "",
                v["value"]?.GetValue<string>() ?? "",
                v["type"]?.GetValue<string>(),
                v["variablesReference"]?.GetValue<int>() ?? 0))
            .ToList();
    }

    public async Task<IReadOnlyList<VariableInfo>> FrameLocalsAsync(int frameId) =>
        _client is { } client ? await LocalsAsync(client, frameId) : [];

    public async Task<string> EvaluateAsync(string expression, int? frameId)
    {
        var client = _client ?? throw new InvalidOperationException("Nothing is being debugged.");
        var args = new JsonObject { ["expression"] = expression, ["context"] = "repl" };
        if ((frameId ?? Frames.FirstOrDefault()?.Id) is { } id) args["frameId"] = id;
        var body = await client.RequestAsync("evaluate", args);
        return body["result"]?.GetValue<string>() ?? "";
    }

    public Task ContinueAsync() => ThreadCommand("continue");
    public Task StepOverAsync() => ThreadCommand("next");
    public Task StepInAsync() => ThreadCommand("stepIn");
    public Task StepOutAsync() => ThreadCommand("stepOut");

    public async Task PauseAsync()
    {
        var client = _client ?? throw new InvalidOperationException("Nothing is being debugged.");
        await client.RequestAsync("pause", new JsonObject { ["threadId"] = StoppedThread ?? await FirstThreadAsync(client) });
    }

    async Task ThreadCommand(string command)
    {
        var client = _client ?? throw new InvalidOperationException("Nothing is being debugged.");
        if (State != DebugState.Stopped) return;
        SetState(DebugState.Running);
        await client.RequestAsync(command, new JsonObject { ["threadId"] = StoppedThread ?? await FirstThreadAsync(client) });
    }

    /// <summary>
    /// Watch the code run: step into every line, pausing <paramref name="delay"/> between steps,
    /// so the UI can draw the path through the graph. Stops at <paramref name="maxSteps"/>, when
    /// the program ends, or when <see cref="StopAnimation"/> is called.
    /// </summary>
    public async Task AnimateAsync(int maxSteps, TimeSpan delay, bool stepOver)
    {
        StopAnimation();
        var cts = _animation = new CancellationTokenSource();
        _publish("debug.animating", new { on = true });
        try
        {
            for (var i = 0; i < maxSteps && !cts.IsCancellationRequested && State == DebugState.Stopped; i++)
            {
                _nextStop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await (stepOver ? StepOverAsync() : StepInAsync());
                var stopped = await Task.WhenAny(_nextStop.Task, Task.Delay(TimeSpan.FromSeconds(10), cts.Token).ContinueWith(_ => false));
                if (!await stopped) break;
                await Task.Delay(delay, cts.Token).ContinueWith(_ => { });
            }
        }
        finally
        {
            _nextStop = null;
            _publish("debug.animating", new { on = false });
        }
    }

    public void StopAnimation() => _animation?.Cancel();

    public async Task SetBreakpointsAsync(string file, IEnumerable<int> lines)
    {
        int[] snapshot;
        lock (_gate)
        {
            var set = new SortedSet<int>(lines);
            if (set.Count == 0) _breakpoints.Remove(file);
            else _breakpoints[file] = set;
            snapshot = set.ToArray();
        }
        if (_client is { IsRunning: true } client && State is DebugState.Running or DebugState.Stopped)
            await SendBreakpointsAsync(client, file, snapshot);
        _publish("debug.breakpoints", Breakpoints);
    }

    static async Task SendBreakpointsAsync(DapClient client, string file, int[] lines)
    {
        await client.RequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = file, ["name"] = Path.GetFileName(file) },
            ["breakpoints"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject { ["line"] = l }).ToArray()),
            ["lines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray()),
        });
    }

    public async Task StopAsync()
    {
        StopAnimation();
        var client = _client;
        _client = null;
        if (client is null) return;
        try { await client.RequestAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true }, TimeSpan.FromSeconds(3)); } catch { }
        await client.DisposeAsync();
        Frames = [];
        SetState(DebugState.Idle);
        _publish("debug.exited", new { reason = "stopped" });
    }

    void SetState(DebugState state)
    {
        if (State == state) return;
        State = state;
        if (state != DebugState.Stopped) Frames = state == DebugState.Running ? Frames : [];
        _publish("debug.state", new { state = state.ToString().ToLowerInvariant(), program = Program });
    }

    static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)) return host;
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, exe))) return Path.Combine(root, exe);
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        for (var dir = runtimeDir is null ? null : new DirectoryInfo(runtimeDir); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, exe))) return Path.Combine(dir.FullName, exe);
        return "dotnet";
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
