namespace CodeToLife.Analysis;

/// <summary>Everything the UI needs to draw a codebase: projects, types, members and how they connect.</summary>
public sealed record CodeGraph(
    string Root,
    string Name,
    DateTimeOffset AnalyzedAt,
    IReadOnlyList<ProjectInfo> Projects,
    IReadOnlyList<TypeNode> Types,
    IReadOnlyList<TypeEdge> Edges,
    IReadOnlyList<CallEdge> Calls,
    IReadOnlyList<string> Warnings);

public sealed record ProjectInfo(
    string Name,
    string Path,
    string OutputKind,
    IReadOnlyList<string> References,
    int FileCount,
    int ErrorCount,
    string? BuiltAssembly);

/// <param name="Id">Fully qualified name with generic arity, e.g. <c>Shop.Repository`1</c>. Unique within the graph.</param>
/// <param name="RuntimeName">The name the CLR uses, with <c>+</c> for nested types, used to map stack frames back.</param>
public sealed record TypeNode(
    string Id,
    string Name,
    string Namespace,
    string Project,
    string Kind,
    string Accessibility,
    IReadOnlyList<string> Modifiers,
    string? ContainingType,
    string? File,
    int Line,
    int EndLine,
    string? Summary,
    string RuntimeName,
    IReadOnlyList<MemberNode> Members,
    TypeMetrics Metrics);

public sealed record TypeMetrics(int Lines, int MemberCount, int TotalComplexity, int MaxComplexity, int IncomingCount, int OutgoingCount);

/// <param name="Id">Roslyn documentation id, e.g. <c>M:Shop.Cart.Add(Shop.Product,System.Int32)</c>.</param>
public sealed record MemberNode(
    string Id,
    string Name,
    string Kind,
    string Signature,
    string? Type,
    string Accessibility,
    bool IsStatic,
    bool IsAbstract,
    bool IsAsync,
    string? File,
    int Line,
    int EndLine,
    int BodyLine,
    int Complexity,
    string? Summary,
    IReadOnlyList<string> Implements);

public static class EdgeKind
{
    public const string Inherits = "inherits";
    public const string Implements = "implements";
    /// <summary>A field or property holds the other type (including inside a collection or generic).</summary>
    public const string Has = "has";
    /// <summary>Calls a method, constructor or property on the other type.</summary>
    public const string Calls = "calls";
    /// <summary>Creates instances of the other type with <c>new</c>.</summary>
    public const string Creates = "creates";
    /// <summary>Mentions the other type in a signature, local, cast or generic argument.</summary>
    public const string Uses = "uses";
    /// <summary>A nested type and the type it is declared in.</summary>
    public const string Nested = "nested";
}

public sealed record TypeEdge(string Source, string Target, string Kind, int Count, string? File, int Line);

/// <summary>A member-to-member call, for the method-level view and for animating the debugger.</summary>
public sealed record CallEdge(string Source, string Target, int Count, string Kind);
