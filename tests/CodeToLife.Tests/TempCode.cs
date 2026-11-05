using CodeToLife.Analysis;

namespace CodeToLife.Tests;

/// <summary>A throwaway folder of projects and C# files to analyze.</summary>
sealed class TempCode : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ctl-tests", Guid.NewGuid().ToString("N"));

    public TempCode() => Directory.CreateDirectory(Root);

    public TempCode Project(string name, string? references = null, string outputType = "Library")
    {
        var refs = references is null ? "" : $"<ItemGroup><ProjectReference Include=\"..\\{references}\\{references}.csproj\" /></ItemGroup>";
        return File($"{name}/{name}.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>{outputType}</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
              {refs}
            </Project>
            """);
    }

    public TempCode File(string path, string content)
    {
        var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return this;
    }

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public CodeGraph Analyze() => GraphBuilder.Analyze(Root);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}

static class GraphExtensions
{
    public static TypeNode Type(this CodeGraph graph, string id) => graph.Types.Single(t => t.Id == id);

    public static bool HasEdge(this CodeGraph graph, string source, string target, string kind) =>
        graph.Edges.Any(e => e.Source == source && e.Target == target && e.Kind == kind);

    public static MemberNode Member(this TypeNode type, string name) => type.Members.First(m => m.Name == name);

    public static bool Calls(this CodeGraph graph, string sourceMemberName, string targetMemberName) =>
        graph.Calls.Any(c => c.Source.Contains(sourceMemberName, StringComparison.Ordinal) && c.Target.Contains(targetMemberName, StringComparison.Ordinal));
}
