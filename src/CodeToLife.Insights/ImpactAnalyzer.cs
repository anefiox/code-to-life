using System.Text.RegularExpressions;
using CodeToLife.Analysis;
using CodeToLife.Runtime;

namespace CodeToLife.Insights;

/// <summary>One thing that may need to change, or be checked, when the target changes.</summary>
/// <param name="Group">callers, implementations, contracts, dependents, tests or risks.</param>
/// <param name="Depth">For callers: how many calls away. Otherwise 1.</param>
public sealed record ImpactItem(string Group, string TypeId, string? MemberId, string Label, string Detail, string? File, int Line, int Depth);

public sealed record ImpactReport(
    string TargetId,
    string Title,
    bool IsType,
    bool PublicApi,
    IReadOnlyList<ImpactItem> Items,
    IReadOnlyList<string> Summary);

/// <summary>
/// "What breaks if I change this?" for a type or member: who calls it (through interfaces too),
/// what implements or overrides it, the contracts it fulfills, the types that hold or create it,
/// the tests that reach it, and places the compiler won't warn about: reflection by name,
/// serialization and dependency-injection registrations.
/// </summary>
public static partial class ImpactAnalyzer
{
    public static readonly IReadOnlyList<string> Groups = ["callers", "implementations", "contracts", "dependents", "tests", "risks"];

    static readonly HashSet<string> SerializationAttributes = new(StringComparer.Ordinal)
    {
        "Serializable", "DataContract", "DataMember", "JsonPropertyName", "JsonProperty", "JsonInclude", "JsonIgnore",
        "JsonSerializable", "JsonConverter", "XmlElement", "XmlAttribute", "XmlRoot", "ProtoContract", "ProtoMember", "Column", "Table", "Key",
    };

    static readonly HashSet<string> EndpointAttributes = new(StringComparer.Ordinal)
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "Route", "ApiController", "Function", "FunctionName",
    };

    public static ImpactReport Analyze(GraphIndex index, TestMap tests, FrameMapper mapper, IEnumerable<string> sourceFiles, string id, int callerDepth = 4)
    {
        var items = new List<ImpactItem>();
        var isType = index.Types.TryGetValue(id, out var targetType);
        var member = isType ? null : index.Members.GetValueOrDefault(id);
        if (!isType && member is null) throw new KeyNotFoundException($"'{id}' isn't a type or member in the graph.");
        var owner = targetType ?? index.TypeOf(id)!;
        var members = isType ? owner.Members.Select(m => m.Id).ToList() : [id];
        var inside = members.ToHashSet(StringComparer.Ordinal);

        // Callers: the nearest call to anything in the target, from outside it.
        var callers = new Dictionary<string, Reach>(StringComparer.Ordinal);
        foreach (var m in members)
            foreach (var reach in index.Callers(m, callerDepth))
                if (!inside.Contains(reach.MemberId) && (!callers.TryGetValue(reach.MemberId, out var known) || known.Depth > reach.Depth))
                    callers[reach.MemberId] = reach;
        foreach (var reach in callers.Values.OrderBy(r => r.Depth).ThenBy(r => index.Display(r.MemberId), StringComparer.Ordinal))
        {
            if (tests.Tests.Contains(reach.MemberId)) continue; // listed under tests
            var via = reach.Via is not null && !inside.Contains(reach.Via) ? $" via {index.Display(reach.Via)}" : "";
            var how = reach.Kind == "dispatch" ? " (through an interface or base)" : "";
            items.Add(Item(index, "callers", reach.MemberId, reach.Depth == 1 ? $"calls it directly{how}" : $"{reach.Depth} calls away{via}{how}", reach.Depth));
        }

        // Implementations and overrides, and the contracts this fulfills.
        foreach (var m in members)
        {
            foreach (var impl in index.ImplementationsOf(m))
                if (!inside.Contains(impl)) items.Add(Item(index, "implementations", impl, $"implements or overrides {index.Display(m)}: must keep matching its signature", 1));
            foreach (var contract in index.ContractsOf(m))
                if (!inside.Contains(contract)) items.Add(Item(index, "contracts", contract, $"{index.Display(m)} fulfills this: a signature change breaks the contract", 1));
        }
        if (isType)
        {
            foreach (var edge in index.EdgesTo(owner.Id).Where(e => e.Kind is EdgeKind.Inherits or EdgeKind.Implements))
                if (index.Types.TryGetValue(edge.Source, out var sub))
                    items.Add(new ImpactItem("implementations", sub.Id, null, sub.Name, edge.Kind == EdgeKind.Inherits ? $"inherits from {owner.Name}" : $"implements {owner.Name}", edge.File, edge.Line, 1));
        }

        // Types that depend on the target type's shape.
        if (isType)
        {
            // One line per dependent type, saying the most consequential way it touches the target.
            var dependents = index.EdgesTo(owner.Id)
                .Where(e => e.Kind is EdgeKind.Has or EdgeKind.Creates or EdgeKind.Uses && index.Types.ContainsKey(e.Source))
                .GroupBy(e => e.Source)
                .OrderBy(g => g.Min(e => DependentOrder(e.Kind))).ThenBy(g => g.Key, StringComparer.Ordinal);
            foreach (var group in dependents)
            {
                var dependent = index.Types[group.Key];
                var reasons = group.OrderBy(e => DependentOrder(e.Kind)).Select(e => e.Kind switch
                {
                    EdgeKind.Has => $"holds a {owner.Name} in a field or property",
                    EdgeKind.Creates => $"creates it with new ({e.Count}×): constructor changes land here",
                    _ => "mentions it in signatures or locals",
                }).ToList();
                var first = group.MinBy(e => DependentOrder(e.Kind))!;
                items.Add(new ImpactItem("dependents", dependent.Id, null, dependent.Name, string.Join("; ", reasons), first.File, first.Line, 1));
            }
        }

        // Tests.
        foreach (var hit in tests.For(id))
            items.Add(Item(index, "tests", hit.TestId, hit.Depth == 1 ? "calls it directly" : $"reaches it in {hit.Depth} calls", hit.Depth));

        // Risks the compiler won't catch.
        var attrs = (isType ? owner.Attributes.Concat(owner.Members.SelectMany(m => m.Attributes)) : member!.Attributes).ToList();
        if (attrs.FirstOrDefault(SerializationAttributes.Contains) is { } serialization)
            items.Add(new ImpactItem("risks", owner.Id, member?.Id, isType ? owner.Name : index.Display(id), $"[{serialization}]: renaming or retyping changes the serialized form (stored data, API payloads)", owner.File, member?.Line ?? owner.Line, 1));
        if (attrs.FirstOrDefault(EndpointAttributes.Contains) is { } endpoint)
            items.Add(new ImpactItem("risks", owner.Id, member?.Id, isType ? owner.Name : index.Display(id), $"[{endpoint}]: part of an HTTP or function endpoint; outside callers aren't in this code", owner.File, member?.Line ?? owner.Line, 1));
        var publicApi = owner.Accessibility == "public" && (member is null || member.Accessibility is "public" or "protected" or "protected internal");
        var name = isType ? GraphIndex.BaseName(owner.Name) : member!.Name;
        foreach (var hit in TextMentions(sourceFiles, name, isType))
        {
            var location = mapper.FromSource(hit.File, hit.Line);
            if (location is null || (location.MemberId is { } m && inside.Contains(m))) continue;
            items.Add(new ImpactItem("risks", location.TypeId, location.MemberId,
                location.MemberId is { } at ? index.Display(at) : index.Display(location.TypeId), hit.Why, hit.File, hit.Line, 1));
        }

        var summary = new List<string>();
        int Count(string group) => items.Count(i => i.Group == group);
        var direct = items.Count(i => i.Group == "callers" && i.Depth == 1);
        summary.Add($"{direct} direct caller{S(direct)}, {Count("callers")} within {callerDepth} calls");
        if (Count("implementations") > 0) summary.Add($"{Count("implementations")} implementation{S(Count("implementations"))} or override{S(Count("implementations"))}");
        if (Count("contracts") > 0) summary.Add($"fulfills {Count("contracts")} interface or base member{S(Count("contracts"))}");
        if (Count("dependents") > 0) summary.Add(Count("dependents") == 1 ? "1 type holds, creates or mentions it" : $"{Count("dependents")} types hold, create or mention it");
        summary.Add(Count("tests") switch { 0 => "no tests reach it", 1 => "1 test reaches it", var n => $"{n} tests reach it" });
        if (Count("risks") > 0) summary.Add($"{Count("risks")} thing{S(Count("risks"))} the compiler won't catch");
        if (publicApi) summary.Add("public API: code outside this solution may use it");

        return new ImpactReport(id, isType ? $"{owner.Kind} {owner.Name}" : $"{member!.Kind} {index.Display(id)}", isType, publicApi, items, summary);
    }

    static string S(int n) => n == 1 ? "" : "s";

    static int DependentOrder(string kind) => kind switch { EdgeKind.Creates => 0, EdgeKind.Has => 1, _ => 2 };

    static ImpactItem Item(GraphIndex index, string group, string memberId, string detail, int depth)
    {
        var m = index.Members.GetValueOrDefault(memberId);
        var type = index.TypeOf(memberId);
        return new ImpactItem(group, type?.Id ?? "", memberId, index.Display(memberId), detail, m?.File, m?.Line ?? 0, depth);
    }

    sealed record Mention(string File, int Line, string Why);

    /// <summary>Places that refer to a name as text, which renames and the compiler miss.</summary>
    static IEnumerable<Mention> TextMentions(IEnumerable<string> files, string name, bool isType)
    {
        if (name.Length < 3 || name.StartsWith('<') || name.StartsWith('.')) yield break;
        var quoted = new Regex($"\"{Regex.Escape(name)}\"", RegexOptions.CultureInvariant);
        var reflection = new Regex($@"\b(GetMethod|GetProperty|GetField|GetType|GetEvent|InvokeMember|CreateInstance)\s*\(\s*""[^""]*\b{Regex.Escape(name)}\b", RegexOptions.CultureInvariant);
        var di = isType ? new Regex($@"\b(Add|TryAdd)(Singleton|Scoped|Transient|Keyed\w*|HostedService)\s*<[^>()]*\b{Regex.Escape(name)}\b", RegexOptions.CultureInvariant) : null;
        foreach (var file in files)
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { continue; }
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains(name, StringComparison.Ordinal)) continue;
                if (reflection.IsMatch(line)) yield return new Mention(file, i + 1, $"looks up \"{name}\" by reflection: a rename breaks it at run time");
                else if (di?.IsMatch(line) == true) yield return new Mention(file, i + 1, "registered for dependency injection: lifetime and constructor changes matter here");
                else if (quoted.IsMatch(line) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal)) yield return new Mention(file, i + 1, $"mentions \"{name}\" as a string (config, routes, reflection, logs?)");
            }
        }
    }
}
