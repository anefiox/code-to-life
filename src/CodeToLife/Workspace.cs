using CodeToLife.Analysis;
using CodeToLife.Insights;
using CodeToLife.Runtime;

namespace CodeToLife;

/// <summary>
/// The web host's view of the <see cref="Codebase"/>: the same graph and indexes, with every
/// re-analysis announced to the browser so the picture keeps up while you write code.
/// </summary>
public sealed class Workspace : IDisposable
{
    readonly Codebase _codebase = new();

    public Workspace(EventHub events, ILogger<Workspace> log)
    {
        _codebase.Analyzing += path => events.Publish("graph.analyzing", new { path });
        _codebase.Changed += (graph, took) =>
        {
            log.LogInformation("Analyzed {Name}: {Types} types, {Edges} edges, {Calls} calls in {Ms} ms",
                graph.Name, graph.Types.Count, graph.Edges.Count, graph.Calls.Count, (int)took.TotalMilliseconds);
            events.Publish("graph.changed", new { types = graph.Types.Count, at = graph.AnalyzedAt });
        };
        _codebase.Failed += ex =>
        {
            log.LogError(ex, "Analysis failed");
            events.Publish("graph.error", new { message = ex.Message });
        };
    }

    public Codebase Codebase => _codebase;
    public string? Path => _codebase.Path;
    public string RootDirectory => _codebase.RootDirectory;
    public bool IsLoaded => _codebase.IsLoaded;
    public string? LastError => _codebase.LastError;
    public bool IsAnalyzing => _codebase.IsAnalyzing;
    public CodeGraph Graph => _codebase.Graph;
    public FrameMapper Mapper => _codebase.Mapper;
    public GraphIndex Index => _codebase.Index;
    public TestMap Tests => _codebase.Tests;

    /// <summary>True for the source files in the graph: the only files the API will show.</summary>
    public bool Contains(string file) => _codebase.Contains(file);

    public Task OpenAsync(string path) => _codebase.OpenAsync(path);
    public Task AnalyzeAsync() => _codebase.AnalyzeAsync();

    public void Dispose() => _codebase.Dispose();
}
