using CodeToLife.Analysis;
using CodeToLife.Runtime;

namespace CodeToLife.Insights;

/// <summary>
/// The codebase being looked at, for any host (the web page or the MCP server): its latest graph
/// and the indexes built from it, re-analyzed whenever a .cs or project file changes.
/// </summary>
public sealed class Codebase : IDisposable
{
    readonly SemaphoreSlim _analyzing = new(1, 1);
    FileSystemWatcher? _watcher;
    Timer? _debounce;
    Snapshot? _current;
    HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    sealed record Snapshot(CodeGraph Graph, FrameMapper Mapper, GraphIndex Index, Lazy<TestMap> Tests);

    /// <summary>Raised before each analysis, with the path.</summary>
    public event Action<string>? Analyzing;
    /// <summary>Raised after a successful analysis, with how long it took.</summary>
    public event Action<CodeGraph, TimeSpan>? Changed;
    public event Action<Exception>? Failed;

    public string? Path { get; private set; }
    public string RootDirectory => Path is null ? Environment.CurrentDirectory : File.Exists(Path) ? System.IO.Path.GetDirectoryName(Path)! : Path;
    public bool IsLoaded => _current is not null;
    public string? LastError { get; private set; }
    public bool IsAnalyzing => _analyzing.CurrentCount == 0;

    Snapshot Current => _current ?? throw new InvalidOperationException(LastError ?? "No codebase is open yet.");
    public CodeGraph Graph => Current.Graph;
    public FrameMapper Mapper => Current.Mapper;
    public GraphIndex Index => Current.Index;
    /// <summary>Which tests reach which code; built the first time it's asked for.</summary>
    public TestMap Tests => Current.Tests.Value;

    /// <summary>True for the source files in the graph: the only files a host should show.</summary>
    public bool Contains(string file) => _files.Contains(System.IO.Path.GetFullPath(file));

    public IEnumerable<string> SourceFiles => _files;

    public async Task OpenAsync(string path, bool watch = true)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException($"'{full}' doesn't exist.");
        Path = full;
        await AnalyzeAsync();
        if (watch) Watch();
    }

    public async Task AnalyzeAsync()
    {
        if (Path is null) return;
        await _analyzing.WaitAsync();
        try
        {
            Analyzing?.Invoke(Path);
            var started = DateTime.UtcNow;
            var graph = await Task.Run(() => GraphBuilder.Analyze(Path));
            var index = new GraphIndex(graph);
            _current = new Snapshot(graph, new FrameMapper(graph), index, new Lazy<TestMap>(() => TestMap.Build(index)));
            _files = graph.Types.SelectMany(t => t.Members.Select(m => m.File).Append(t.File))
                .Where(f => f is not null).Select(f => System.IO.Path.GetFullPath(f!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            LastError = null;
            Changed?.Invoke(graph, DateTime.UtcNow - started);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Failed?.Invoke(ex);
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
