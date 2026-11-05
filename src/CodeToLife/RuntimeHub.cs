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

    public RuntimeHub(EventHub events, Workspace workspace)
    {
        _events = events;
        _workspace = workspace;
        Debug = new DebugSession(events.Publish, () => workspace.Mapper);
    }

    public DebugSession Debug { get; }
    public LiveSampler? Sampler => _sampler;

    public string? Debugger => DebuggerLocator.Find();

    public async Task StartLiveAsync(int processId, int intervalMs)
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
        _events.Publish("live.started", new { processId, intervalMs });
    }

    public async Task StopLiveAsync()
    {
        var sampler = _sampler;
        _sampler = null;
        if (sampler is not null) await sampler.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopLiveAsync();
        await Debug.DisposeAsync();
    }
}
