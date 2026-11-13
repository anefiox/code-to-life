using System.ComponentModel;
using CodeToLife.Runtime.Dap;
using ModelContextProtocol.Server;

namespace CodeToLife.Mcp;

/// <summary>
/// Run the program under netcoredbg and look inside it. Every command waits for the program to
/// stop (or exit) and reports where it is, mapped back to the code, with the locals in scope.
/// </summary>
[McpServerToolType]
public sealed class DebugTools(CodebaseHost host, DebugHost debug)
{
    const string BreakpointHelp = "Where to stop: 'OrderService.CheckoutAsync' (stops at its first line) or 'Services/OrderService.cs:28'.";

    [McpServerTool(Name = "debug_launch"), Description("Start the program under the debugger and run until a breakpoint, an unhandled exception or exit (whichever comes first, up to the timeout). Builds nothing: build first with dotnet build. Reports the mapped call stack, locals and the code around the current line.")]
    public async Task<string> Launch(
        [Description("Breakpoints to set before starting, e.g. ['OrderService.CheckoutAsync', 'Program.cs:40'].")] string[]? breakpoints = null,
        [Description("Command-line arguments for the program.")] string[]? args = null,
        [Description("Which executable project to run, when there's more than one.")] string? project = null,
        [Description("Seconds to wait for it to stop.")] int timeoutSeconds = 30)
    {
        if (DebuggerLocator.Find() is not { } adapter) return NoDebugger;
        var code = await host.ReadyAsync();
        var exes = code.Graph.Projects.Where(p => p.OutputKind == "exe" && (project is null || p.Name.Equals(project, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exes.Count == 0) return project is null ? "There's no executable project in this code." : $"No executable project called '{project}'.";
        if (exes.Count > 1) return $"Several programs: {string.Join(", ", exes.Select(p => p.Name))}. Pass project.";
        var exe = exes[0];
        if (exe.BuiltAssembly is null || !File.Exists(exe.BuiltAssembly)) return $"{exe.Name} isn't built yet: run `dotnet build \"{exe.Path}\"` first.";

        if (debug.Session.State is not (DebugState.Idle or DebugState.Exited)) await debug.Session.StopAsync();
        debug.TakeOutput();
        var problems = await SetBreakpointsAsync(breakpoints ?? []);
        var outcome = await debug.RunAndWaitAsync(
            () => debug.Session.LaunchAsync(adapter, new LaunchRequest(exe.BuiltAssembly, args, Path.GetDirectoryName(exe.Path))),
            TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 600)));
        return problems + await debug.DescribeAsync(outcome);
    }

    [McpServerTool(Name = "debug_breakpoints"), Description("Set breakpoints while debugging (adds to the existing ones), or clear them all with clear=true.")]
    public async Task<string> Breakpoints([Description(BreakpointHelp)] string[]? add = null, bool clear = false)
    {
        if (clear)
            foreach (var file in debug.Session.Breakpoints.Keys.ToList())
                await debug.Session.SetBreakpointsAsync(file, []);
        var problems = await SetBreakpointsAsync(add ?? []);
        var all = debug.Session.Breakpoints.SelectMany(p => p.Value.Select(l => $"{Path.GetFileName(p.Key)}:{l}")).ToList();
        return problems + (all.Count == 0 ? "No breakpoints." : "Breakpoints: " + string.Join(", ", all));
    }

    [McpServerTool(Name = "debug_continue"), Description("Resume until the next breakpoint, exception or exit.")]
    public Task<string> Continue(int timeoutSeconds = 30) => Step(debug.Session.ContinueAsync, timeoutSeconds);

    [McpServerTool(Name = "debug_step"), Description("Step one line: 'over' (default) runs calls on the current line, 'into' goes inside them, 'out' finishes the current method.")]
    public Task<string> StepCommand([Description("over, into or out")] string kind = "over") => Step(kind switch
    {
        "into" or "in" => debug.Session.StepInAsync,
        "out" => debug.Session.StepOutAsync,
        _ => debug.Session.StepOverAsync,
    }, 15);

    [McpServerTool(Name = "debug_pause"), Description("Pause a running program wherever it is.")]
    public Task<string> Pause() => Step(debug.Session.PauseAsync, 10);

    [McpServerTool(Name = "debug_evaluate"), Description("Evaluate a C# expression in the paused program, e.g. 'order.Lines.Count' or 'customer.Name'.")]
    public async Task<string> Evaluate(string expression, [Description("A frame id from the stack (#…), default the current one.")] int? frameId = null)
    {
        if (debug.Session.State != DebugState.Stopped) return "The program isn't paused.";
        try { return $"{expression} = {await debug.Session.EvaluateAsync(expression, frameId)}"; }
        catch (Exception ex) { return $"Couldn't evaluate {expression}: {ex.Message}"; }
    }

    [McpServerTool(Name = "debug_where", ReadOnly = true), Description("Where the program is now: state, mapped stack, locals and recent output, without moving it.")]
    public async Task<string> Where() => debug.Session.State switch
    {
        DebugState.Stopped => await debug.DescribeAsync("stopped"),
        DebugState.Idle => "Not debugging. Use debug_launch.",
        DebugState.Exited => "The program exited.\n" + debug.TakeOutput(),
        var s => $"{s}.\n{debug.TakeOutput()}",
    };

    [McpServerTool(Name = "debug_stop"), Description("Stop the program and the debugger.")]
    public async Task<string> Stop()
    {
        await debug.Session.StopAsync();
        return "Stopped." + (debug.TakeOutput() is { Length: > 0 } output ? "\nProgram output:\n" + output : "");
    }

    async Task<string> Step(Func<Task> command, int timeoutSeconds)
    {
        if (debug.Session.State is DebugState.Idle or DebugState.Exited) return "Not debugging. Use debug_launch.";
        try
        {
            var outcome = await debug.RunAndWaitAsync(command, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 600)));
            return await debug.DescribeAsync(outcome);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DapException or TimeoutException) { return ex.Message; }
    }

    /// <summary>Resolves names and file:line specs to breakpoints and sets them, grouped by file.</summary>
    async Task<string> SetBreakpointsAsync(IEnumerable<string> specs)
    {
        var code = await host.ReadyAsync();
        var problems = new List<string>();
        var byFile = debug.Session.Breakpoints.ToDictionary(p => p.Key, p => p.Value.ToHashSet(), StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
        {
            var colon = spec.LastIndexOf(':');
            if (colon > 1 && int.TryParse(spec[(colon + 1)..], out var line))
            {
                var fileSpec = spec[..colon].Replace('\\', '/');
                var file = code.SourceFiles.FirstOrDefault(f => f.Replace('\\', '/').EndsWith("/" + fileSpec.TrimStart('/'), StringComparison.OrdinalIgnoreCase) || f.Equals(spec[..colon], StringComparison.OrdinalIgnoreCase));
                if (file is null) { problems.Add($"No source file matches '{spec[..colon]}'."); continue; }
                (byFile.TryGetValue(file, out var set) ? set : byFile[file] = []).Add(line);
                continue;
            }
            if (CodeTools.ResolveMember(code.Index, spec) is not { } id) { problems.Add(CodeTools.Ambiguous(code.Index, spec, membersOnly: true)); continue; }
            var member = code.Index.Members[id];
            if (member.File is null) { problems.Add($"{spec} has no source location."); continue; }
            var full = Path.GetFullPath(member.File);
            (byFile.TryGetValue(full, out var lines) ? lines : byFile[full] = []).Add(member.BodyLine > 0 ? member.BodyLine : member.Line);
        }
        foreach (var (file, lines) in byFile) await debug.Session.SetBreakpointsAsync(file, lines.Order());
        return problems.Count == 0 ? "" : string.Join('\n', problems) + "\n\n";
    }

    const string NoDebugger = "netcoredbg wasn't found. In the Code to Life repo run tools/Get-Netcoredbg.ps1, put netcoredbg on PATH, or set CODETOLIFE_NETCOREDBG.";
}
