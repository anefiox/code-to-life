using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace CodeToLife.Runtime.Live;

/// <summary>Time a method spent waiting on awaits.</summary>
public sealed record WaitStat(string TypeId, string? MemberId, int Count, double TotalMs, double MaxMs);

/// <summary>Exceptions thrown (caught or not), by type and where they were thrown.</summary>
public sealed record ThrowStat(string ExceptionType, string? Message, string? TypeId, string? MemberId, int Count);

/// <summary>Allocation volume by type, from the runtime's sampled allocation ticks (one per ~100 KB).</summary>
public sealed record AllocationStat(string TypeName, string? TypeId, long Bytes, int Samples);

/// <summary>Allocation volume by the method in this code that did the allocating (the innermost one on the stack).</summary>
public sealed record AllocatorStat(string TypeId, string? MemberId, long Bytes, int Samples);

/// <summary>Outgoing HTTP requests by method, host and path.</summary>
public sealed record HttpStat(string Request, int Count, double AverageMs, double MaxMs, int Failures);

public sealed record LiveEvents(
    int ProcessId,
    double Seconds,
    IReadOnlyList<WaitStat> Waits,
    IReadOnlyList<ThrowStat> Exceptions,
    IReadOnlyList<AllocationStat> Allocations,
    IReadOnlyList<AllocatorStat> Allocators,
    IReadOnlyList<HttpStat> Http,
    int Contentions,
    bool StacksResolved,
    string? Note);

/// <summary>
/// Listens to the runtime's own event stream (EventPipe), which sees what stack sampling can't:
/// how long async methods spend awaiting, every exception thrown, what's being allocated, lock
/// contention and outgoing HTTP calls. Each event's call stack is resolved and mapped to the
/// graph, so waits and throws land on the method that did them.
/// </summary>
public sealed class EventTracer : IAsyncDisposable
{
    const string TplProvider = "System.Threading.Tasks.TplEventSource";
    const string HttpProvider = "System.Net.Http";
    const string ClrProvider = "Microsoft-Windows-DotNETRuntime";

    readonly int _processId;
    readonly Func<FrameMapper> _mapper;
    readonly Action<LiveEvents> _onEvents;
    readonly Action<string> _onStopped;
    readonly DateTime _started = DateTime.UtcNow;

    readonly ConcurrentDictionary<long, (DateTime At, GraphLocation? Where)> _openWaits = new();
    readonly ConcurrentDictionary<(string, string?), WaitAccumulator> _waits = new();
    readonly ConcurrentDictionary<(string, string?, string?), ThrowAccumulator> _throws = new();
    readonly ConcurrentDictionary<string, AllocationAccumulator> _allocations = new();
    readonly ConcurrentDictionary<(string, string?), AllocationAccumulator> _allocators = new();
    readonly ConcurrentDictionary<Guid, (DateTime At, string Request)> _openRequests = new();
    readonly ConcurrentDictionary<string, HttpAccumulator> _http = new();
    int _contentions;
    long _eventsSeen;
    string? _pumpError;
    int _stacks, _resolvedStacks;

    EventPipeSession? _session;
    TraceLogEventSource? _source;
    Task? _pump, _publisher;
    readonly CancellationTokenSource _stop = new();

    public EventTracer(int processId, Func<FrameMapper> mapper, Action<LiveEvents> onEvents, Action<string> onStopped)
    {
        _processId = processId;
        _mapper = mapper;
        _onEvents = onEvents;
        _onStopped = onStopped;
    }

    public int ProcessId => _processId;
    public TimeSpan PublishEvery { get; set; } = TimeSpan.FromSeconds(1);

    public void Start()
    {
        var client = new DiagnosticsClient(_processId);
        var clrKeywords = ClrTraceEventParser.Keywords.Exception | ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Contention
                          | ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.Loader;
        var providers = new[]
        {
            // Verbose for GC allocation ticks; Jit and Loader so methods compiled from now on can be named in stacks.
            new EventPipeProvider(ClrProvider, EventLevel.Verbose, (long)clrKeywords),
            // TaskTransfer | Tasks: TaskWaitBegin (Informational) and TaskWaitEnd (Verbose), which every await emits when someone listens.
            new EventPipeProvider(TplProvider, EventLevel.Verbose, 0x3),
            new EventPipeProvider(HttpProvider, EventLevel.Informational, 0x1),
        };
        _session = client.StartEventPipeSession(providers, requestRundown: false, circularBufferMB: 64);
        // The rundown session tells TraceLog about every method compiled before we started, so stacks resolve.
        _source = TraceLog.CreateFromEventPipeSession(_session, TraceLog.EventPipeRundownConfiguration.Enable(client));
        // Events are only named and parsed for parsers someone subscribes to: the CLR's own, and the
        // self-describing EventSource ones (TPL, HTTP) through the dynamic parser.
        _source.Clr.All += OnEvent;
        new TplEtwProviderTraceEventParser(_source).All += OnEvent; // TraceEvent knows TPL itself, so the dynamic parser skips it
        _source.Dynamic.All += OnEvent;
        _pump = Task.Run(() =>
        {
            try
            {
                _source.Process();
                if (!_stop.IsCancellationRequested) _onStopped("the process exited");
            }
            catch (Exception ex) when (!_stop.IsCancellationRequested) { _pumpError = ex.ToString(); _onStopped(ex.Message); }
            catch { }
        });
        _publisher = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await Task.Delay(PublishEvery, _stop.Token); } catch (OperationCanceledException) { break; }
                _onEvents(Snapshot());
            }
        });
    }

    void OnEvent(TraceEvent e)
    {
        Interlocked.Increment(ref _eventsSeen);
        try
        {
            if (e.ProcessID != _processId && e.ProcessID > 0) return;
            switch (e.ProviderName)
            {
                case TplProvider when e.EventName.StartsWith("TaskWaitBegin", StringComparison.Ordinal) || e.EventName == "TaskWait/Send":
                {
                    if (Payload<int>(e, "TaskID") is not { } task) return;
                    _openWaits[task] = (e.TimeStamp, WhereFrom(e));
                    break;
                }
                case TplProvider when e.EventName.StartsWith("TaskWaitEnd", StringComparison.Ordinal) || e.EventName == "TaskWait/Stop":
                {
                    if (Payload<int>(e, "TaskID") is not { } task || !_openWaits.TryRemove(task, out var open) || open.Where is not { } where) return;
                    var ms = (e.TimeStamp - open.At).TotalMilliseconds;
                    if (ms < 0) return;
                    _waits.GetOrAdd((where.TypeId, where.MemberId), _ => new WaitAccumulator()).Add(ms);
                    break;
                }
                case HttpProvider when e.EventName.StartsWith("RequestStart", StringComparison.Ordinal):
                {
                    var request = $"{e.PayloadByName("scheme")}://{e.PayloadByName("host")}{e.PayloadByName("pathAndQuery")}";
                    var query = request.IndexOf('?');
                    _openRequests[e.ActivityID] = (e.TimeStamp, query > 0 ? request[..query] : request);
                    break;
                }
                case HttpProvider when e.EventName.StartsWith("RequestStop", StringComparison.Ordinal) || e.EventName.StartsWith("RequestFailed", StringComparison.Ordinal):
                {
                    if (!_openRequests.TryRemove(e.ActivityID, out var open)) return;
                    var failed = e.EventName.StartsWith("RequestFailed", StringComparison.Ordinal) || (Payload<int>(e, "statusCode") is >= 500);
                    _http.GetOrAdd(open.Request, _ => new HttpAccumulator()).Add((e.TimeStamp - open.At).TotalMilliseconds, failed);
                    break;
                }
                case ClrProvider:
                    OnClrEvent(e);
                    break;
            }
        }
        catch { /* one odd event mustn't stop the stream */ }
    }

    void OnClrEvent(TraceEvent e)
    {
        if (e.EventName.StartsWith("GC/AllocationTick", StringComparison.Ordinal))
        {
            var typeName = e.PayloadByName("TypeName") as string ?? "?";
            var amount = e.PayloadByName("AllocationAmount64") is { } a ? Convert.ToInt64(a) : Payload<int>(e, "AllocationAmount") ?? 0;
            _allocations.GetOrAdd(typeName, n => new AllocationAccumulator(_mapper().FromRuntime(n, null)?.TypeId)).Add(amount);
            if (WhereFrom(e) is { } by) _allocators.GetOrAdd((by.TypeId, by.MemberId), _ => new AllocationAccumulator(by.TypeId)).Add(amount);
        }
        else if (e.EventName.StartsWith("Exception/Start", StringComparison.Ordinal))
        {
            var type = e.PayloadByName("ExceptionType") as string ?? "Exception";
            var message = e.PayloadByName("ExceptionMessage") as string;
            var where = WhereFrom(e);
            _throws.GetOrAdd((type, where?.TypeId, where?.MemberId), _ => new ThrowAccumulator(type, message)).Add();
        }
        else if (e.EventName.StartsWith("Contention/Start", StringComparison.Ordinal))
            Interlocked.Increment(ref _contentions);
    }

    /// <summary>The innermost frame of an event's stack that's in the analyzed code.</summary>
    GraphLocation? WhereFrom(TraceEvent e)
    {
        var stack = e.CallStack();
        if (stack is null) return null;
        Interlocked.Increment(ref _stacks);
        var mapper = _mapper();
        var named = false;
        for (var frame = stack; frame is not null; frame = frame.Caller)
        {
            var method = frame.CodeAddress.Method;
            if (method is null) continue;
            named = true;
            var full = method.FullMethodName; // "Ns.Type+<Run>d__3.MoveNext()"
            var paren = full.IndexOf('(');
            if (paren > 0) full = full[..paren];
            var dot = LastDotOutsideBrackets(full);
            if (dot <= 0) continue;
            var location = mapper.FromRuntime(full[..dot], full[(dot + 1)..]);
            if (location is not null)
            {
                Interlocked.Increment(ref _resolvedStacks);
                return location;
            }
        }
        if (named) Interlocked.Increment(ref _resolvedStacks);
        return null;
    }

    static int LastDotOutsideBrackets(string name)
    {
        var depth = 0;
        var last = -1;
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] is '<' or '[') depth++;
            else if (name[i] is '>' or ']') depth--;
            else if (name[i] == '.' && depth == 0 && i > 0 && name[i - 1] != '.') last = i;
        }
        // ".ctor": the dot before "ctor" belongs to the method name.
        if (last > 0 && name[last - 1] == '.') last--;
        return last;
    }

    static T? Payload<T>(TraceEvent e, string name) where T : struct
    {
        var value = e.PayloadByName(name);
        if (value is null) return null;
        try { return (T)Convert.ChangeType(value, typeof(T)); } catch { return null; }
    }

    /// <summary>Events received so far, for diagnosing a quiet stream.</summary>
    public long EventsSeen => Interlocked.Read(ref _eventsSeen);

    public LiveEvents Snapshot()
    {
        var mine = _allocations.Where(a => a.Value.TypeId is not null).OrderByDescending(a => a.Value.Bytes).Take(25);
        var others = _allocations.Where(a => a.Value.TypeId is null).OrderByDescending(a => a.Value.Bytes).Take(10);
        string? note = _pumpError is not null ? $"The event stream failed: {_pumpError}"
            : _eventsSeen == 0 && DateTime.UtcNow - _started > TimeSpan.FromSeconds(5) ? "No runtime events have arrived yet." : null;
        if (note is null && _stacks > 20 && _resolvedStacks == 0)
            note = "Event stacks couldn't be resolved to methods, so waits and throws can't be placed on the code. Exceptions, allocations and HTTP still work.";
        return new LiveEvents(
            _processId,
            (DateTime.UtcNow - _started).TotalSeconds,
            _waits.Select(w => new WaitStat(w.Key.Item1, w.Key.Item2, w.Value.Count, Math.Round(w.Value.Total, 1), Math.Round(w.Value.Max, 1))).OrderByDescending(w => w.TotalMs).ToList(),
            _throws.Select(t => new ThrowStat(t.Value.Type, t.Value.Message, t.Key.Item2, t.Key.Item3, t.Value.Count)).OrderByDescending(t => t.Count).ToList(),
            mine.Concat(others).Select(a => new AllocationStat(a.Key, a.Value.TypeId, a.Value.Bytes, a.Value.Samples)).ToList(),
            _allocators.OrderByDescending(a => a.Value.Bytes).Take(25).Select(a => new AllocatorStat(a.Key.Item1, a.Key.Item2, a.Value.Bytes, a.Value.Samples)).ToList(),
            _http.Select(h => new HttpStat(h.Key, h.Value.Count, Math.Round(h.Value.Total / Math.Max(1, h.Value.Count), 1), Math.Round(h.Value.Max, 1), h.Value.Failures)).OrderByDescending(h => h.Count).ToList(),
            _contentions,
            _resolvedStacks > 0,
            note);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _session?.Stop(); } catch { }
        try { _source?.StopProcessing(); } catch { }
        if (_pump is not null) await Task.WhenAny(_pump, Task.Delay(2000));
        if (_publisher is not null) await Task.WhenAny(_publisher, Task.Delay(1000));
        try { _source?.Dispose(); } catch { }
        try { _session?.Dispose(); } catch { }
    }

    sealed class WaitAccumulator
    {
        public int Count;
        public double Total, Max;
        public void Add(double ms) { lock (this) { Count++; Total += ms; if (ms > Max) Max = ms; } }
    }

    sealed class ThrowAccumulator(string type, string? message)
    {
        public string Type { get; } = type;
        public string? Message { get; } = message;
        public int Count;
        public void Add() => Interlocked.Increment(ref Count);
    }

    sealed class AllocationAccumulator(string? typeId)
    {
        public string? TypeId { get; } = typeId;
        public long Bytes;
        public int Samples;
        public void Add(long bytes) { Interlocked.Add(ref Bytes, bytes); Interlocked.Increment(ref Samples); }
    }

    sealed class HttpAccumulator
    {
        public int Count, Failures;
        public double Total, Max;
        public void Add(double ms, bool failed) { lock (this) { Count++; Total += ms; if (ms > Max) Max = ms; if (failed) Failures++; } }
    }
}
