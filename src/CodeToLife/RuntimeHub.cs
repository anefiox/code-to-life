using CodeToLife.Runtime;
using CodeToLife.Runtime.Dap;
using CodeToLife.Runtime.Live;

namespace CodeToLife;

/// <summary>Owns the one debug session and the one live sampler, and forwards what they see to the browser.</summary>
public sealed class RuntimeHub : IAsyncDisposable
{
    readonly EventHub _events;
    readonly Workspace _workspace;
    LiveSampler? _sampler;
    EventTracer? _tracer;

    public RuntimeHub(EventHub events, Workspace workspace)
    {
        _events = events;
        _workspace = workspace;
        Debug = new DebugSession(events.Publish, () => workspace.Mapper);
    }

    public DebugSession Debug { get; }
    public LiveSampler? Sampler => _sampler;
    public EventTracer? Tracer => _tracer;
    /// <summary>Why runtime events aren't available for the watched process, if they aren't.</summary>
    public string? TracerError { get; private set; }

    public string? Debugger => DebuggerLocator.Find();

    public async Task StartLiveAsync(int processId, int intervalMs, bool traceEvents = true)
    {
        await StopLiveAsync();
        var sampler = new LiveSampler(processId, () => _workspace.Mapper,
            sample => _events.Publish("live.sample", sample),
            reason =>
            {
                _events.Publish("live.stopped", new { processId, reason });
                if (_sampler?.ProcessId == processId) _sampler = null;
            })
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(intervalMs, 50, 5000)),
        };
        _sampler = sampler;
        sampler.Start();

        // Runtime events (awaits, exceptions, allocations, HTTP) alongside the stack samples.
        TracerError = null;
        if (traceEvents)
        {
            try
            {
                var tracer = new EventTracer(processId, () => _workspace.Mapper,
                    snapshot => _events.Publish("live.events", snapshot),
                    reason => { if (_tracer?.ProcessId == processId) TracerError = reason; });
                tracer.Start();
                _tracer = tracer;
            }
            catch (Exception ex) { TracerError = ex.Message; }
        }
        _events.Publish("live.started", new { processId, intervalMs, events = _tracer is not null, eventsError = TracerError });
    }

    public async Task StopLiveAsync()
    {
        var sampler = _sampler;
        var tracer = _tracer;
        _sampler = null;
        _tracer = null;
        if (sampler is not null) await sampler.DisposeAsync();
        if (tracer is not null) await tracer.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopLiveAsync();
        await Debug.DisposeAsync();
    }
}
