using System.Text.RegularExpressions;
using CodeToLife.Analysis;

namespace CodeToLife.Runtime;

/// <summary>Where a running frame lives in the graph.</summary>
public sealed record GraphLocation(string TypeId, string? MemberId);

/// <summary>
/// Maps what a debugger or a stack sample reports (a source line, or a CLR type and method name)
/// back to the type and member nodes in the graph, seeing through the classes the compiler
/// generates for async methods, iterators, lambdas and local functions.
/// </summary>
public sealed partial class FrameMapper
{
    readonly Dictionary<string, TypeNode> _byRuntimeName = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<TypeNode>> _byFile = new(StringComparer.OrdinalIgnoreCase);

    public FrameMapper(CodeGraph graph)
    {
        foreach (var type in graph.Types)
        {
            _byRuntimeName.TryAdd(StripArity(type.RuntimeName), type);
            if (type.File is null) continue;
            var file = Normalize(type.File);
            if (!_byFile.TryGetValue(file, out var list)) _byFile[file] = list = [];
            list.Add(type);
            // Partial types and members declared in other files of the same type.
            foreach (var other in type.Members.Select(m => m.File).Where(f => f is not null && Normalize(f) != file).Distinct())
            {
                var key = Normalize(other!);
                if (!_byFile.TryGetValue(key, out var l)) _byFile[key] = l = [];
                if (!l.Contains(type)) l.Add(type);
            }
        }
    }

    /// <summary>A source location from the debugger: the innermost member whose lines contain it.</summary>
    public GraphLocation? FromSource(string? path, int line)
    {
        if (path is null || !_byFile.TryGetValue(Normalize(path), out var types)) return null;
        var normalized = Normalize(path);

        var member = types
            .SelectMany(t => t.Members.Select(m => (Type: t, Member: m)))
            .Where(x => x.Member.File is not null && Normalize(x.Member.File) == normalized && x.Member.Line <= line && line <= x.Member.EndLine)
            .OrderBy(x => x.Member.EndLine - x.Member.Line)
            .FirstOrDefault();
        if (member.Member is not null) return new GraphLocation(member.Type.Id, member.Member.Id);

        var type = types.Where(t => t.Line <= line && line <= t.EndLine).OrderBy(t => t.EndLine - t.Line).FirstOrDefault();
        if (type is not null) return new GraphLocation(type.Id, null);

        // Top-level statements live outside any type declaration.
        var topLevel = types.SelectMany(t => t.Members.Select(m => (Type: t, Member: m))).FirstOrDefault(x => x.Member.Kind == "top-level statements");
        return topLevel.Member is null ? null : new GraphLocation(topLevel.Type.Id, topLevel.Member.Id);
    }

    /// <summary>
    /// A CLR frame: <c>Shop.OrderService+&lt;CheckoutAsync&gt;d__5</c> / <c>MoveNext</c> maps to
    /// <c>OrderService.CheckoutAsync</c>; <c>Program+&lt;&gt;c</c> / <c>&lt;&lt;Main&gt;$&gt;b__0_0</c> maps to the top-level statements.
    /// </summary>
    public GraphLocation? FromRuntime(string? typeName, string? methodName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;
        var segments = SplitNested(StripGenericArguments(typeName));
        string? impliedMethod = null;

        // Walk outwards past compiler-generated nested classes, remembering the method they belong to.
        while (segments.Count > 1 && segments[^1].StartsWith('<'))
        {
            var generated = segments[^1];
            impliedMethod ??= GeneratedOwner().Match(generated) is { Success: true } m && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;
            segments.RemoveAt(segments.Count - 1);
        }

        var key = string.Join('+', segments.Select(StripArity));
        if (!_byRuntimeName.TryGetValue(key, out var type)) return null;

        var method = methodName ?? "";
        if (method.StartsWith('<'))
        {
            // <Main>b__0_0 (lambda), <Main>g__Local|0_1 (local function), <<Main>$>b__0_0 (lambda in top-level code).
            var owner = LambdaOwner().Match(method);
            method = owner.Success ? owner.Groups[1].Value : method;
        }
        else if (method == "MoveNext" && impliedMethod is not null)
            method = impliedMethod;
        else if (impliedMethod is not null && method is "Invoke" or "")
            method = impliedMethod;

        return new GraphLocation(type.Id, FindMember(type, method)?.Id);
    }

    static MemberNode? FindMember(TypeNode type, string clrName)
    {
        if (clrName is "<Main>$" or "Main" && type.Members.FirstOrDefault(m => m.Kind == "top-level statements") is { } top) return top;
        var name = clrName switch
        {
            ".ctor" => type.Name.Split('<')[0],
            ".cctor" => type.Name.Split('<')[0],
            _ when clrName.StartsWith("get_", StringComparison.Ordinal) || clrName.StartsWith("set_", StringComparison.Ordinal) => clrName[4..],
            _ when clrName.StartsWith("add_", StringComparison.Ordinal) => clrName[4..],
            _ when clrName.StartsWith("remove_", StringComparison.Ordinal) => clrName[7..],
            _ => clrName,
        };
        if (clrName is ".ctor" or ".cctor")
            return type.Members.FirstOrDefault(m => m.Kind is "constructor" or "primary constructor" && (clrName == ".cctor") == m.IsStatic)
                ?? type.Members.FirstOrDefault(m => m.Kind == "static constructor" && clrName == ".cctor");
        // Explicit interface implementations are named "Namespace.IFoo.Bar" in metadata.
        var simple = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        return type.Members.FirstOrDefault(m => m.Name == name) ?? type.Members.FirstOrDefault(m => m.Name == simple);
    }

    static List<string> SplitNested(string name)
    {
        // Split on '+', but not inside compiler-generated names such as "<>c__DisplayClass0_0".
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '<') depth++;
            else if (name[i] == '>') depth--;
            else if (name[i] == '+' && depth == 0)
            {
                parts.Add(name[start..i]);
                start = i + 1;
            }
        }
        parts.Add(name[start..]);
        return parts;
    }

    /// <summary>"Shop.Repo&lt;System.Int32&gt;" → "Shop.Repo", leaving compiler names like "&lt;Main&gt;d__0" alone.</summary>
    static string StripGenericArguments(string name)
    {
        var result = new System.Text.StringBuilder(name.Length);
        var depth = 0;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var startsGenerated = c == '<' && (i == 0 || name[i - 1] is '+' or '.' or '<');
            if (c == '<' && !startsGenerated && depth == 0) { depth = 1; continue; }
            if (depth > 0)
            {
                if (c == '<') depth++;
                else if (c == '>') depth--;
                continue;
            }
            if (c == '[' && i + 1 < name.Length && name[i + 1] is '[' or ']' or ',') break; // reflection-style generic args or arrays
            result.Append(c);
        }
        return result.ToString();
    }

    static string StripArity(string name) => ArityPattern().Replace(name, "");

    static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();

    [GeneratedRegex(@"`\d+")]
    private static partial Regex ArityPattern();

    /// <summary>"&lt;CheckoutAsync&gt;d__5" → "CheckoutAsync"; "&lt;&gt;c" → "".</summary>
    [GeneratedRegex(@"^<(<[^>]+>\$|[^>]*)>")]
    private static partial Regex GeneratedOwner();

    [GeneratedRegex(@"^<(<[^>]+>\$|[^>]+)>")]
    private static partial Regex LambdaOwner();
}
