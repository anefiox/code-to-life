using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;

namespace CodeToLife.Runtime.Live;

public sealed record DotnetProcess(int Id, string Name, string? MainModule, string? CommandLine);

/// <summary>What one sample saw: per thread, the graph members on its stack (innermost first).</summary>
public sealed record LiveSample(
    DateTimeOffset At,
    int Number,
    IReadOnlyList<LiveThread> Threads,
    IReadOnlyDictionary<string, int> MemberHits,
    IReadOnlyDictionary<string, int> TypeHits,
    IReadOnlyList<ObservedCall> ObservedCalls,
    long SampleMilliseconds);

public sealed record LiveThread(int ManagedId, IReadOnlyList<GraphLocation> Stack, string? TopFrame);

/// <summary>A caller and callee seen next to each other on a real stack.</summary>
public sealed record ObservedCall(string SourceMember, string TargetMember, int Count);

public sealed record HeapCount(string TypeId, long Count, long Bytes);

/// <summary>
/// Watches a running .NET process without a debugger: takes a snapshot of it a few times a second
/// with ClrMD, reads every managed thread's stack, and maps the frames onto the graph. The process
/// only pauses for the instant the snapshot takes, so it's safe on anything you're allowed to read.
/// </summary>
public sealed class LiveSampler : IAsyncDisposable
{
    readonly int _processId;
    readonly Func<FrameMapper> _mapper;
    readonly Action<LiveSample> _onSample;
    readonly Action<string> _onStopped;
    readonly CancellationTokenSource _stop = new();
    readonly Dictionary<string, int> _memberHits = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _typeHits = new(StringComparer.Ordinal);
    readonly Dictionary<(string, string), int> _calls = [];
    Task? _loop;
    int _number;

    public LiveSampler(int processId, Func<FrameMapper> mapper, Action<LiveSample> onSample, Action<string> onStopped)
    {
        _processId = processId;
        _mapper = mapper;
        _onSample = onSample;
        _onStopped = onStopped;
    }

    public int ProcessId => _processId;
    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(250);

    public static IReadOnlyList<DotnetProcess> ListProcesses()
    {
        var self = Environment.ProcessId;
        var result = new List<DotnetProcess>();
        foreach (var pid in DiagnosticsClient.GetPublishedProcesses())
        {
            if (pid == self) continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                string? module = null;
                try { module = process.MainModule?.FileName; } catch { }
                result.Add(new DotnetProcess(pid, process.ProcessName, module, null));
            }
            catch (ArgumentException) { } // exited in the meantime
        }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToList();
    }

    public void Start() => _loop = Task.Run(Loop);

    async Task Loop()
    {
        var reason = "stopped";
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var sample = TakeSample();
                if (sample is null) { reason = "the process exited"; break; }
                _onSample(sample with { SampleMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                var wait = Interval - Stopwatch.GetElapsedTime(started);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { reason = ex.Message; }
        _onStopped(reason);
    }

    LiveSample? TakeSample()
    {
        if (!IsAlive(_processId)) return null;
        using var target = Attach(_processId);
        var clr = target.ClrVersions.FirstOrDefault() ?? throw new InvalidOperationException("That process isn't running .NET.");
        using var runtime = clr.CreateRuntime();
        var mapper = _mapper();
        var threads = new List<LiveThread>();
        var memberHits = new Dictionary<string, int>(StringComparer.Ordinal);
        var typeHits = new Dictionary<string, int>(StringComparer.Ordinal);
        var calls = new List<ObservedCall>();

        foreach (var thread in runtime.Threads)
        {
            if (!thread.IsAlive) continue;
            var stack = new List<GraphLocation>();
            string? top = null;
            foreach (var frame in thread.EnumerateStackTrace().Take(200))
            {
                if (frame.Method is not { } method) continue;
                top ??= $"{method.Type?.Name}.{method.Name}";
                var location = mapper.FromRuntime(method.Type?.Name, method.Name);
                if (location is null) continue;
                // Async state machines and lambdas can put the same member on the stack twice in a row.
                if (stack.Count > 0 && stack[^1] == location) continue;
                stack.Add(location);
            }
            if (stack.Count == 0) continue;
            threads.Add(new LiveThread(thread.ManagedThreadId, stack, top));

            foreach (var member in stack.Select(s => s.MemberId).Where(m => m is not null).Distinct())
                memberHits[member!] = memberHits.GetValueOrDefault(member!) + 1;
            foreach (var type in stack.Select(s => s.TypeId).Distinct())
                typeHits[type] = typeHits.GetValueOrDefault(type) + 1;
            // The stack is innermost first, so frame i+1 called frame i.
            for (var i = 0; i + 1 < stack.Count; i++)
                if (stack[i + 1].MemberId is { } caller && stack[i].MemberId is { } callee && caller != callee)
                    calls.Add(new ObservedCall(caller, callee, 1));
        }

        lock (_memberHits)
        {
            foreach (var (k, v) in memberHits) _memberHits[k] = _memberHits.GetValueOrDefault(k) + v;
            foreach (var (k, v) in typeHits) _typeHits[k] = _typeHits.GetValueOrDefault(k) + v;
            foreach (var c in calls) _calls[(c.SourceMember, c.TargetMember)] = _calls.GetValueOrDefault((c.SourceMember, c.TargetMember)) + 1;
        }

        return new LiveSample(DateTimeOffset.Now, Interlocked.Increment(ref _number), threads, memberHits, typeHits,
            calls.GroupBy(c => (c.SourceMember, c.TargetMember)).Select(g => new ObservedCall(g.Key.SourceMember, g.Key.TargetMember, g.Count())).ToList(), 0);
    }

    /// <summary>Totals since sampling started: how often each member and call was seen.</summary>
    public (IReadOnlyDictionary<string, int> Members, IReadOnlyDictionary<string, int> Types, IReadOnlyList<ObservedCall> Calls, int Samples) Totals()
    {
        lock (_memberHits)
            return (new Dictionary<string, int>(_memberHits), new Dictionary<string, int>(_typeHits),
                _calls.Select(c => new ObservedCall(c.Key.Item1, c.Key.Item2, c.Value)).ToList(), _number);
    }

    /// <summary>
    /// How many instances of each graph type are alive on the heap right now. Walks the whole heap,
    /// so it's on demand, with a time limit for big processes.
    /// </summary>
    public static (IReadOnlyList<HeapCount> Counts, bool Complete, long ObjectsSeen) CountInstances(int processId, Func<FrameMapper> mapper, TimeSpan limit)
    {
        using var target = Attach(processId);
        var clr = target.ClrVersions.FirstOrDefault() ?? throw new InvalidOperationException("That process isn't running .NET.");
        using var runtime = clr.CreateRuntime();
        var map = mapper();
        var byType = new Dictionary<string, (long Count, long Bytes)>(StringComparer.Ordinal);
        var resolved = new Dictionary<ulong, string?>();
        var watch = Stopwatch.StartNew();
        long seen = 0;
        var complete = true;

        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            if ((++seen & 0xFFF) == 0 && watch.Elapsed > limit) { complete = false; break; }
            if (obj.Type is not { } type) continue;
            if (!resolved.TryGetValue(type.MethodTable, out var typeId))
            {
                var location = map.FromRuntime(type.Name, null);
                // Only the type itself, not compiler-generated closures that map back to it.
                typeId = location is not null && type.Name is { } n && !n.Contains("+<", StringComparison.Ordinal) && !n.EndsWith(']') && !type.IsArray
                    ? location.TypeId
                    : null;
                resolved[type.MethodTable] = typeId;
            }
            if (typeId is null) continue;
            var current = byType.GetValueOrDefault(typeId);
            byType[typeId] = (current.Count + 1, current.Bytes + (long)obj.Size);
        }

        return (byType.Select(kv => new HeapCount(kv.Key, kv.Value.Count, kv.Value.Bytes)).OrderByDescending(h => h.Count).ToList(), complete, seen);
    }

    static DataTarget Attach(int processId) =>
        OperatingSystem.IsWindows()
            ? DataTarget.CreateSnapshotAndAttach(processId) // a copy of the process: it barely notices
            : DataTarget.AttachToProcess(processId, suspend: true);

    static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) try { await _loop; } catch { }
    }
}
