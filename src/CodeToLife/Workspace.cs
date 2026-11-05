using CodeToLife.Analysis;
using CodeToLife.Runtime;

namespace CodeToLife;

/// <summary>
/// The codebase being looked at: its latest graph, re-analyzed whenever a .cs or project file
/// changes so the picture keeps up with you while you write code.
/// </summary>
public sealed class Workspace : IDisposable
{
    readonly EventHub _events;
    readonly ILogger<Workspace> _log;
    readonly SemaphoreSlim _analyzing = new(1, 1);
    FileSystemWatcher? _watcher;
    Timer? _debounce;
    CodeGraph? _graph;
    FrameMapper? _mapper;
    HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    public Workspace(EventHub events, ILogger<Workspace> log)
    {
        _events = events;
        _log = log;
    }

    public string? Path { get; private set; }
    public string RootDirectory => Path is null ? Environment.CurrentDirectory : File.Exists(Path) ? System.IO.Path.GetDirectoryName(Path)! : Path;
    public bool IsLoaded => _graph is not null;
    public string? LastError { get; private set; }
    public bool IsAnalyzing => _analyzing.CurrentCount == 0;

    public CodeGraph Graph => _graph ?? throw new InvalidOperationException("No codebase is open yet.");
    public FrameMapper Mapper => _mapper ?? throw new InvalidOperationException("No codebase is open yet.");

    /// <summary>True for the source files in the graph: the only files the API will show.</summary>
    public bool Contains(string file) => _files.Contains(System.IO.Path.GetFullPath(file));

    public async Task OpenAsync(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException($"'{full}' doesn't exist.");
        Path = full;
        await AnalyzeAsync();
        Watch();
    }

    public async Task AnalyzeAsync()
    {
        if (Path is null) return;
        await _analyzing.WaitAsync();
        try
        {
            _events.Publish("graph.analyzing", new { path = Path });
            var started = DateTime.UtcNow;
            var graph = await Task.Run(() => GraphBuilder.Analyze(Path));
            _graph = graph;
            _mapper = new FrameMapper(graph);
            _files = graph.Types.SelectMany(t => t.Members.Select(m => m.File).Append(t.File))
                .Where(f => f is not null).Select(f => System.IO.Path.GetFullPath(f!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            LastError = null;
            _log.LogInformation("Analyzed {Name}: {Types} types, {Edges} edges, {Calls} calls in {Ms} ms",
                graph.Name, graph.Types.Count, graph.Edges.Count, graph.Calls.Count, (int)(DateTime.UtcNow - started).TotalMilliseconds);
            _events.Publish("graph.changed", new { types = graph.Types.Count, at = graph.AnalyzedAt });
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.LogError(ex, "Analysis failed");
            _events.Publish("graph.error", new { message = ex.Message });
        }
        finally { _analyzing.Release(); }
    }

    void Watch()
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(RootDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
        };
        FileSystemEventHandler changed = (_, e) => OnChange(e.FullPath);
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Deleted += changed;
        _watcher.Renamed += (_, e) => OnChange(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    void OnChange(string path)
    {
        var relevant = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
        var separator = System.IO.Path.DirectorySeparatorChar;
        if (!relevant || path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase) || path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase))
            return;
        // Editors save in bursts (temp file, rename, touch); analyze once things settle.
        _debounce?.Dispose();
        _debounce = new Timer(_ => _ = AnalyzeAsync(), null, TimeSpan.FromMilliseconds(700), Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
