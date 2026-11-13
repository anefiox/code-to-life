using CodeToLife.Analysis;

namespace CodeToLife.Insights;

/// <summary>One member reached while walking the call graph.</summary>
/// <param name="Via">The member it was reached from (the previous step on the path), or null for the start.</param>
/// <param name="Kind">How it was reached: the call kind (direct, virtual, property, delegate, creates) or "dispatch" for an implementation of a called interface or base member.</param>
public sealed record Reach(string MemberId, int Depth, string? Via, string Kind);

/// <summary>
/// Fast lookups over a <see cref="CodeGraph"/>: who calls what, who implements what, and walks of
/// the call graph that see through interfaces and virtual calls. A call to
/// <c>IPricingRule.Apply</c> reaches every class that implements it, which is what really runs.
/// </summary>
public sealed class GraphIndex
{
    readonly Dictionary<string, List<CallEdge>> _out = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<CallEdge>> _in = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<string>> _implementedBy = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<TypeEdge>> _typeOut = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<TypeEdge>> _typeIn = new(StringComparer.Ordinal);

    public GraphIndex(CodeGraph graph)
    {
        Graph = graph;
        Types = graph.Types.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var members = new Dictionary<string, MemberNode>(StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in graph.Types)
            foreach (var member in type.Members)
            {
                members.TryAdd(member.Id, member);
                owners.TryAdd(member.Id, type.Id);
                foreach (var contract in member.Implements) Add(_implementedBy, contract, member.Id);
            }
        Members = members;
        MemberOwner = owners;
        foreach (var call in graph.Calls)
        {
            Add(_out, call.Source, call);
            Add(_in, call.Target, call);
        }
        foreach (var edge in graph.Edges)
        {
            Add(_typeOut, edge.Source, edge);
            Add(_typeIn, edge.Target, edge);
        }
    }

    public CodeGraph Graph { get; }
    public IReadOnlyDictionary<string, TypeNode> Types { get; }
    public IReadOnlyDictionary<string, MemberNode> Members { get; }
    /// <summary>Member id → the id of the type that declares it.</summary>
    public IReadOnlyDictionary<string, string> MemberOwner { get; }

    public TypeNode? TypeOf(string memberId) =>
        MemberOwner.TryGetValue(memberId, out var typeId) ? Types.GetValueOrDefault(typeId) : null;

    public IReadOnlyList<CallEdge> CallsFrom(string memberId) => _out.GetValueOrDefault(memberId) ?? [];
    public IReadOnlyList<CallEdge> CallsTo(string memberId) => _in.GetValueOrDefault(memberId) ?? [];
    public IReadOnlyList<TypeEdge> EdgesFrom(string typeId) => _typeOut.GetValueOrDefault(typeId) ?? [];
    public IReadOnlyList<TypeEdge> EdgesTo(string typeId) => _typeIn.GetValueOrDefault(typeId) ?? [];

    /// <summary>Members that implement or override this one, directly or further down the hierarchy.</summary>
    public IReadOnlyList<string> ImplementationsOf(string memberId)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { memberId };
        var queue = new Queue<string>([memberId]);
        while (queue.TryDequeue(out var current))
            foreach (var impl in _implementedBy.GetValueOrDefault(current) ?? [])
                if (seen.Add(impl)) { result.Add(impl); queue.Enqueue(impl); }
        return result;
    }

    /// <summary>The interface and base members this one implements or overrides, all the way up.</summary>
    public IReadOnlyList<string> ContractsOf(string memberId)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { memberId };
        var queue = new Queue<string>([memberId]);
        while (queue.TryDequeue(out var current))
            foreach (var contract in Members.GetValueOrDefault(current)?.Implements ?? [])
                if (seen.Add(contract)) { result.Add(contract); queue.Enqueue(contract); }
        return result;
    }

    /// <summary>Everything a member ends up calling, nearest first, following calls into interface implementations.</summary>
    public IReadOnlyList<Reach> Callees(string memberId, int maxDepth = 3) => Walk(memberId, maxDepth, forward: true);

    /// <summary>Everything that ends up calling a member, nearest first, including callers that go through an interface or base member it implements.</summary>
    public IReadOnlyList<Reach> Callers(string memberId, int maxDepth = 3) => Walk(memberId, maxDepth, forward: false);

    IReadOnlyList<Reach> Walk(string start, int maxDepth, bool forward)
    {
        var result = new List<Reach>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var frontier = new List<Reach> { new(start, 0, null, "start") };
        for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<Reach>();
            foreach (var from in frontier)
                foreach (var (target, kind) in forward ? Next(from.MemberId) : Previous(from.MemberId))
                    if (seen.Add(target))
                    {
                        var reach = new Reach(target, depth, from.MemberId, kind);
                        next.Add(reach);
                        result.Add(reach);
                    }
            frontier = next;
        }
        return result;
    }

    /// <summary>What runs next from a member: its calls, and for calls to a contract, the implementations.</summary>
    IEnumerable<(string Member, string Kind)> Next(string memberId)
    {
        foreach (var call in CallsFrom(memberId))
        {
            yield return (call.Target, call.Kind);
            if (call.Kind is "virtual" or "property" or "delegate")
                foreach (var impl in ImplementationsOf(call.Target)) yield return (impl, "dispatch");
        }
    }

    /// <summary>Who runs a member: its callers, and the callers of every contract it fulfills.</summary>
    IEnumerable<(string Member, string Kind)> Previous(string memberId)
    {
        foreach (var call in CallsTo(memberId)) yield return (call.Source, call.Kind);
        foreach (var contract in ContractsOf(memberId))
            foreach (var call in CallsTo(contract)) yield return (call.Source, "dispatch");
    }

    /// <summary>The shortest call path from one member to another (both included), or null if there's none within the depth.</summary>
    public IReadOnlyList<Reach>? PathBetween(string from, string to, int maxDepth = 8)
    {
        if (from == to) return [new Reach(from, 0, null, "start")];
        var reached = Walk(from, maxDepth, forward: true).ToDictionary(r => r.MemberId, StringComparer.Ordinal);
        if (!reached.TryGetValue(to, out var end)) return null;
        var path = new List<Reach> { end };
        for (var step = end; step.Via is not null && step.Via != from; step = reached[step.Via]) path.Add(reached[step.Via]);
        path.Add(new Reach(from, 0, null, "start"));
        path.Reverse();
        return path;
    }

    /// <summary><c>OrderService.CheckoutAsync</c>: short and readable.</summary>
    public string Display(string id)
    {
        if (Types.TryGetValue(id, out var type)) return type.Name;
        if (Members.TryGetValue(id, out var member))
            return member.Kind == "top-level statements"
                ? $"{TypeOf(id)?.Name ?? "Program"} (top-level code)"
                : (TypeOf(id)?.Name is { } owner ? owner + "." : "") + member.Name;
        var bar = id.IndexOf('|');
        return bar < 0 ? id : id[(bar + 1)..];
    }

    public string Location(string id) =>
        Types.TryGetValue(id, out var t) ? Where(t.File, t.Line)
        : Members.TryGetValue(id, out var m) ? Where(m.File, m.Line)
        : "";

    string Where(string? file, int line) =>
        file is null ? "" : $"{Path.GetRelativePath(Graph.Root, file).Replace('\\', '/')}:{line}";

    /// <summary>
    /// Finds types and members from what a person (or an AI) would type: an exact id, <c>Type</c>,
    /// <c>Type.Member</c>, <c>Namespace.Type.Member</c> or just <c>Member</c>. Exact matches win;
    /// otherwise every case-insensitive match is returned, types first.
    /// </summary>
    public IReadOnlyList<string> Resolve(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        if (Types.ContainsKey(query) || Members.ContainsKey(query)) return [query];
        var name = query.Contains('(') ? query[..query.IndexOf('(')] : query;

        var exact = new List<string>();
        var loose = new List<string>();
        foreach (var type in Graph.Types)
        {
            if (Matches(type.Id, type.Name, name, out var isExact)) (isExact ? exact : loose).Add(type.Id);
            foreach (var member in type.Members)
            {
                var full = type.Id + "." + member.Name;
                var shortName = BaseName(type.Name) + "." + member.Name;
                if (string.Equals(full, name, StringComparison.Ordinal) || string.Equals(shortName, name, StringComparison.Ordinal))
                    exact.Add(member.Id);
                else if (full.EndsWith("." + name, StringComparison.OrdinalIgnoreCase) || string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(shortName, name, StringComparison.OrdinalIgnoreCase))
                    loose.Add(member.Id);
            }
        }
        return exact.Count > 0 ? exact : loose;
    }

    static bool Matches(string id, string name, string query, out bool exact)
    {
        exact = string.Equals(id, query, StringComparison.Ordinal) || string.Equals(BaseName(name), query, StringComparison.Ordinal) || string.Equals(name, query, StringComparison.Ordinal);
        return exact
            || string.Equals(BaseName(name), query, StringComparison.OrdinalIgnoreCase)
            || id.EndsWith("." + query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>Repository&lt;T&gt;</c> → <c>Repository</c>.</summary>
    public static string BaseName(string typeName) => typeName.IndexOf('<') is var i and > 0 ? typeName[..i] : typeName;

    static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }
}
