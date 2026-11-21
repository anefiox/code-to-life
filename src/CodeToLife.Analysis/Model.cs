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
    IReadOnlyList<string> Warnings,
    IReadOnlyList<RouteInfo>? Routes = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Views = null);

/// <summary>A web address the app answers: an ASP.NET Core minimal API, controller action or Razor Page.</summary>
/// <param name="Method">GET, POST, "GET, POST", or * for any.</param>
/// <param name="Path">The address, e.g. <c>/api/orders/{id}</c>; as written in the code when <paramref name="FixedPath"/> is false.</param>
/// <param name="Handler">The member that handles it. For an inline lambda, the member the lambda is written in.</param>
/// <param name="Kind">minimal, controller or page.</param>
/// <param name="InlineCalls">For an inline lambda: the members it calls, so the flow starts there rather than at its container.</param>
/// <param name="Note">Something worth knowing about how the address was found, or null.</param>
/// <param name="Inline">The handler is a lambda written where the address is mapped.</param>
public sealed record RouteInfo(
    string Method,
    string Path,
    bool FixedPath,
    string Handler,
    string? File,
    int Line,
    string Kind,
    IReadOnlyList<string> InlineCalls,
    string? Note,
    bool Inline = false);

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
    TypeMetrics Metrics,
    IReadOnlyList<string> Attributes);

public sealed record TypeMetrics(int Lines, int MemberCount, int TotalComplexity, int MaxComplexity, int IncomingCount, int OutgoingCount);

/// <param name="Attributes">Attribute names without the <c>Attribute</c> suffix, e.g. <c>Fact</c>, <c>HttpGet</c>.</param>
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
    IReadOnlyList<string> Implements,
    IReadOnlyList<string> Attributes);

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
