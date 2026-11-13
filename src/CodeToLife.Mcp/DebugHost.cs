using System.Text;
using CodeToLife.Insights;
using CodeToLife.Runtime.Dap;

namespace CodeToLife.Mcp;

/// <summary>
/// One netcoredbg session for the assistant. Commands wait until the program stops (a breakpoint,
/// a step, an exception) or exits, so each tool call answers "where is it now, and what's in scope".
/// </summary>
public sealed class DebugHost : IAsyncDisposable
{
    readonly CodebaseHost _code;
    readonly object _gate = new();
    readonly StringBuilder _output = new();
    TaskCompletionSource<string>? _waiter;
    string? _lastStop;

    public DebugHost(CodebaseHost code)
    {
        _code = code;
        Session = new DebugSession(OnEvent, () => code.Codebase.Mapper);
    }

    public DebugSession Session { get; }

    void OnEvent(string name, object payload)
    {
        switch (name)
        {
            case "debug.output":
                var text = payload.GetType().GetProperty("output")?.GetValue(payload) as string;
                lock (_gate)
                {
                    _output.Append(text);
                    if (_output.Length > 20_000) _output.Remove(0, _output.Length - 20_000);
                }
                break;
            case "debug.stopped":
                _lastStop = payload.GetType().GetProperty("reason")?.GetValue(payload) as string;
                _waiter?.TrySetResult("stopped");
                break;
            case "debug.exited":
                _waiter?.TrySetResult("exited");
                break;
            case "debug.error":
                _waiter?.TrySetResult("error: " + payload.GetType().GetProperty("message")?.GetValue(payload));
                break;
        }
    }

    /// <summary>Program output since the last call.</summary>
    public string TakeOutput()
    {
        lock (_gate)
        {
            var text = _output.ToString();
            _output.Clear();
            return text;
        }
    }

    /// <summary>Runs a command and waits for the program to stop or exit, or for the timeout.</summary>
    public async Task<string> RunAndWaitAsync(Func<Task> command, TimeSpan timeout)
    {
        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiter = waiter;
        await command();
        var finished = await Task.WhenAny(waiter.Task, Task.Delay(timeout));
        return finished == waiter.Task ? waiter.Task.Result : "running";
    }

    /// <summary>Where the program is: mapped stack, locals of the top frame, recent output.</summary>
    public async Task<string> DescribeAsync(string outcome)
    {
        var sb = new StringBuilder();
        var output = TakeOutput();
        if (outcome == "exited") sb.AppendLine("The program exited.");
        else if (outcome == "running") sb.AppendLine("Still running (no breakpoint hit yet). Use debug_pause, or set a breakpoint and debug_continue.");
        else if (outcome.StartsWith("error", StringComparison.Ordinal)) sb.AppendLine(outcome);
        else
        {
            var code = await _code.ReadyAsync();
            var index = code.Index;
            var frames = Session.Frames;
            var mine = frames.Where(f => f.TypeId is not null).ToList();
            var top = frames.FirstOrDefault();
            sb.AppendLine($"Stopped ({_lastStop ?? "paused"}) at {Where(index, top)}.");
            sb.AppendLine().AppendLine("Stack (innermost first):");
            var skipped = 0;
            foreach (var f in frames)
            {
                if (f.TypeId is null) { skipped++; continue; }
                if (skipped > 0) { sb.AppendLine($"     … {skipped} framework frame(s)"); skipped = 0; }
                sb.AppendLine($"  #{f.Id,-5} {Where(index, f)}");
            }
            if (skipped > 0) sb.AppendLine($"     … {skipped} framework frame(s)");
            if (top is not null)
            {
                try
                {
                    var locals = await Session.FrameLocalsAsync(top.Id);
                    sb.AppendLine().AppendLine(locals.Count == 0 ? "No locals." : "Locals:");
                    foreach (var v in locals.Take(40))
                        sb.AppendLine($"  {v.Name} = {Shorten(v.Value)}{(v.Type is null ? "" : $"  ({v.Type})")}");
                }
                catch (Exception ex) { sb.AppendLine($"(couldn't read locals: {ex.Message})"); }
                if (top.File is not null && top.Line > 0) AppendLines(sb, top.File, top.Line);
            }
            if (mine.Count == 0) sb.AppendLine("None of these frames are in the analyzed code.");
        }
        if (output.Length > 0) sb.AppendLine().AppendLine("Program output:").AppendLine(output.Length > 4000 ? "…" + output[^4000..] : output);
        return sb.ToString();
    }

    static string Where(GraphIndex index, StackFrameInfo? f)
    {
        if (f is null) return "?";
        var name = f.MemberId is { } m ? index.Display(m) : f.TypeId is { } t ? index.Display(t) : f.Name;
        var file = f.File is null ? "" : $" ({Path.GetFileName(f.File)}:{f.Line})";
        return name + file;
    }

    static string Shorten(string value) => value.Length > 200 ? value[..200] + "…" : value;

    static void AppendLines(StringBuilder sb, string file, int line)
    {
        try
        {
            var lines = File.ReadAllLines(file);
            sb.AppendLine().AppendLine("Code around the current line:");
            for (var i = Math.Max(1, line - 3); i <= Math.Min(lines.Length, line + 3); i++)
                sb.AppendLine($"{(i == line ? "→" : " ")}{i,4}  {lines[i - 1]}");
        }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync() => await Session.DisposeAsync();
}
