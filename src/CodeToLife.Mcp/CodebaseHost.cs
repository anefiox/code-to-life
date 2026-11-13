using CodeToLife.Insights;

namespace CodeToLife.Mcp;

/// <summary>The codebase the server answers questions about: opened once, re-analyzed when files change.</summary>
public sealed class CodebaseHost(string path) : IDisposable
{
    readonly SemaphoreSlim _gate = new(1, 1);
    Task? _opening;

    public Codebase Codebase { get; } = new();
    public string RequestedPath { get; private set; } = Path.GetFullPath(path);

    /// <summary>Waits for the first analysis (and any in progress), then returns the codebase.</summary>
    public async Task<Codebase> ReadyAsync()
    {
        await _gate.WaitAsync();
        try { _opening ??= Codebase.OpenAsync(RequestedPath); }
        finally { _gate.Release(); }
        await _opening;
        if (!Codebase.IsLoaded) throw new InvalidOperationException($"Couldn't analyze {RequestedPath}: {Codebase.LastError}");
        return Codebase;
    }

    public async Task<Codebase> OpenAsync(string newPath)
    {
        await _gate.WaitAsync();
        try
        {
            RequestedPath = Path.GetFullPath(newPath);
            _opening = Codebase.OpenAsync(RequestedPath);
        }
        finally { _gate.Release(); }
        return await ReadyAsync();
    }

    public void Dispose() => Codebase.Dispose();
}
