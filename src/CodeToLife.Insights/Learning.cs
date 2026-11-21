using CodeToLife.Analysis;

namespace CodeToLife.Insights;

/// <param name="Id">The type (or, for the entry point, the top-level code or Main) to show.</param>
/// <param name="Role">entry, backbone or shared.</param>
public sealed record TourStep(string Id, string TypeId, string Name, string? File, string Role, string Reason);

public sealed record FlowStep(string Id, string From, int Depth);

public sealed record TemplateRef(string Name, string? File);

public sealed record RouteFlow(string Key, string Method, string Path, bool FixedPath, string Handler, string? File, int Line,
    string Kind, string? Note, bool Inline, IReadOnlyList<FlowStep> Steps, IReadOnlyList<TemplateRef> Templates);

public sealed record PageFlows(string? Framework, string? Before, IReadOnlyList<RouteFlow> Routes);

public sealed record TemplateSource(string File, int Start, int End, int Total, string Code);

/// <summary>
/// Guided views for people new to a codebase: a "Start here" reading order, and what happens when
/// a page is visited, from the route to the code it calls and the Razor view it renders.
/// </summary>
public static class Learning
{
    const int TourLength = 12;
    const int FlowDepth = 3;
    const int FlowLimit = 20;
    const long TemplateLimit = 512 * 1024;
    static readonly string[] TemplateExtensions = [".cshtml", ".razor"];
    static readonly string[] SkippedDirectories = ["bin", "obj", ".git", ".vs", ".idea", "node_modules", ".codetolife", "TestResults"];

    public const string AspNetBefore = "For every request, ASP.NET Core first runs the middleware set up in Program.cs, in order: " +
        "things like HTTPS redirection, static files, sign-in (UseAuthentication) and permissions (UseAuthorization). " +
        "Then routing picks the endpoint that matches the address and calls its handler.";

    static HashSet<string> TestProjects(CodeGraph graph) =>
        graph.Projects.Where(p => p.OutputKind == "test").Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    static string? Relative(CodeGraph graph, string? file) =>
        file is null ? null : Path.GetRelativePath(graph.Root, file).Replace('\\', '/');

    // ---------------------------------------------------------------- templates

    /// <summary>Every Razor view and component under the root, shortest paths first.</summary>
    public static IReadOnlyList<string> TemplateFiles(string root)
    {
        var found = new List<string>();
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    var info = new DirectoryInfo(child);
                    if (!SkippedDirectories.Contains(info.Name, StringComparer.OrdinalIgnoreCase) && info.LinkTarget is null) pending.Push(child);
                }
                foreach (var file in Directory.EnumerateFiles(dir))
                    if (TemplateExtensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                        found.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found.OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>"Views/Home/Index" → "src/Web/Views/Home/Index.cshtml".</summary>
    static string? TemplateFor(string candidate, IReadOnlyList<string> files)
    {
        foreach (var file in files)
        {
            var bare = file[..file.LastIndexOf('.')];
            if (bare.Equals(candidate, StringComparison.OrdinalIgnoreCase) || bare.EndsWith("/" + candidate, StringComparison.OrdinalIgnoreCase)) return file;
        }
        return null;
    }

    public static TemplateSource ReadTemplate(string root, string? file)
    {
        if (file is null || !TemplateFiles(root).Contains(file, StringComparer.Ordinal)) throw new ExplainException("Unknown template.", 404);
        var full = Path.GetFullPath(Path.Combine(root, file));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ExplainException("The file is outside the project.", 403);
        if (new FileInfo(full).Length > TemplateLimit) throw new ExplainException("The template is too large to show.", 413);
        var code = File.ReadAllText(full).ReplaceLineEndings("\n").TrimEnd('\n');
        var total = code.Split('\n').Length;
        return new TemplateSource(file, 1, total, total, code);
    }

    // ---------------------------------------------------------------- pages

    public static PageFlows RouteFlows(GraphIndex index)
    {
        var graph = index.Graph;
        var routes = graph.Routes ?? [];
        var tests = TestProjects(graph);
        var files = routes.Count > 0 ? TemplateFiles(graph.Root) : [];
        // Steps are the code that does the work: methods and constructors, not property reads.
        bool Allowed(string id) => index.TypeOf(id) is { } t && !tests.Contains(t.Project)
            && index.Members.TryGetValue(id, out var m) && m.Kind is "method" or "constructor" or "operator";

        var flows = routes.Select(route =>
        {
            var inline = route.Inline;
            var steps = new List<FlowStep>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { route.Handler };
            var queue = new Queue<(string Id, int Depth)>();
            if (inline)
            {
                foreach (var call in route.InlineCalls)
                    if (seen.Add(call) && Allowed(call) && steps.Count < FlowLimit)
                    {
                        steps.Add(new FlowStep(call, route.Handler, 1));
                        queue.Enqueue((call, 1));
                    }
            }
            else queue.Enqueue((route.Handler, 0));

            while (queue.TryDequeue(out var current) && steps.Count < FlowLimit)
            {
                // A call to an interface or abstract member runs one of its implementations: the same step, one level in.
                var next = index.ImplementationsOf(current.Id).Select(id => (Id: id, Depth: Math.Max(1, current.Depth)))
                    .Concat(current.Depth >= FlowDepth ? [] : index.Callees(current.Id, 1).Select(r => (Id: r.MemberId, Depth: current.Depth + 1)));
                foreach (var (id, depth) in next)
                {
                    if (!seen.Add(id) || !Allowed(id)) continue;
                    steps.Add(new FlowStep(id, current.Id, depth));
                    queue.Enqueue((id, depth));
                    if (steps.Count >= FlowLimit) break;
                }
            }

            var views = graph.Views ?? new Dictionary<string, IReadOnlyList<string>>();
            var templates = new List<TemplateRef>();
            foreach (var id in steps.Select(s => s.Id).Prepend(route.Handler))
            {
                if (!views.TryGetValue(id, out var candidates) || candidates.Count == 0) continue;
                var file = candidates.Select(c => TemplateFor(c, files)).FirstOrDefault(f => f is not null);
                var name = file ?? candidates[0];
                if (templates.All(t => t.Name != name)) templates.Add(new TemplateRef(file is null ? candidates[0] : name, file));
            }
            var relative = Relative(graph, route.File);
            return new RouteFlow($"{route.Method} {route.Path} {relative}:{route.Line}", route.Method, route.Path, route.FixedPath, route.Handler,
                relative, route.Line, route.Kind, route.Note, inline, steps, templates);
        }).ToList();

        return new PageFlows(flows.Count > 0 ? "ASP.NET Core" : null, flows.Count > 0 ? AspNetBefore : null, flows);
    }

    // ---------------------------------------------------------------- start here

    /// <summary>Where the program starts: the top-level code or Main of the app project.</summary>
    public static (string Id, string Reason)? EntryPoint(GraphIndex index)
    {
        var graph = index.Graph;
        var exe = graph.Projects.Where(p => p.OutputKind == "exe").Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var routesByProject = (graph.Routes ?? []).GroupBy(r => index.TypeOf(r.Handler)?.Project ?? "").ToDictionary(g => g.Key, g => g.Count());
        var typesByProject = graph.Types.GroupBy(t => t.Project).ToDictionary(g => g.Key, g => g.Count());
        var candidates = graph.Types
            .Where(t => exe.Contains(t.Project))
            .SelectMany(t => t.Members.Where(m => m.Kind == "top-level statements" || (m.Name == "Main" && m.IsStatic && m.Kind == "method")).Select(m => (Type: t, Member: m)))
            .OrderByDescending(c => routesByProject.GetValueOrDefault(c.Type.Project))
            .ThenByDescending(c => typesByProject.GetValueOrDefault(c.Type.Project))
            .ThenBy(c => c.Type.Project, StringComparer.Ordinal)
            .ToList();
        if (candidates.FirstOrDefault() is { Member: not null } entry)
        {
            var reason = entry.Member.Kind == "top-level statements"
                ? $"{entry.Type.Project} is a program, and .NET starts running it from the top-level code in {Path.GetFileName(entry.Member.File)}."
                : $"{entry.Type.Project} is a program, and .NET starts running it from this static Main method.";
            if (graph.Projects.Count(p => p.OutputKind == "exe") > 1) reason += " (The solution has other programs too; this is the one with the most code.)";
            return (entry.Member.Id, reason);
        }
        var tests = TestProjects(graph);
        var busiest = graph.Types.Where(t => !tests.Contains(t.Project)).MaxBy(t => t.Metrics.OutgoingCount);
        return busiest is null ? null : (busiest.Id, "There is no clear starting point (no program project), so the tour begins with the class that uses the most others.");
    }

    public static IReadOnlyList<TourStep> StartTour(GraphIndex index)
    {
        var graph = index.Graph;
        if (EntryPoint(index) is not { } entry) return [];
        var tests = TestProjects(graph);
        var entryType = index.Types.ContainsKey(entry.Id) ? entry.Id : index.MemberOwner[entry.Id];
        bool Counts(string typeId) => index.Types.TryGetValue(typeId, out var t) && !tests.Contains(t.Project);

        // Who leads to whom: what a type holds, calls or creates, its base types, and from an
        // interface on to the classes that implement it (that's where calls through it end up).
        var children = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var parents = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Link(string from, string to)
        {
            if (from == to || !Counts(from) || !Counts(to)) return;
            (children.TryGetValue(from, out var c) ? c : children[from] = new(StringComparer.Ordinal)).Add(to);
            (parents.TryGetValue(to, out var p) ? p : parents[to] = new(StringComparer.Ordinal)).Add(from);
        }
        foreach (var edge in graph.Edges)
        {
            switch (edge.Kind)
            {
                case EdgeKind.Calls or EdgeKind.Creates or EdgeKind.Has or EdgeKind.Inherits:
                    Link(edge.Source, edge.Target);
                    break;
                case EdgeKind.Implements:
                    Link(edge.Source, edge.Target);
                    Link(edge.Target, edge.Source);
                    break;
                // Program.cs registers services by mentioning them (AddScoped<IOrders, Orders>()).
                case EdgeKind.Uses when edge.Source == entryType:
                    Link(edge.Source, edge.Target);
                    break;
            }
        }

        var reachCache = new Dictionary<string, int>(StringComparer.Ordinal);
        int Reach(string type)
        {
            if (reachCache.TryGetValue(type, out var known)) return known;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>([type]);
            while (stack.TryPop(out var current))
                foreach (var next in children.GetValueOrDefault(current) ?? [])
                    if (next != type && seen.Add(next)) stack.Push(next);
            return reachCache[type] = seen.Count;
        }
        int Users(string type) => parents.GetValueOrDefault(type)?.Count(p => p != entryType) ?? 0;

        var routesIn = (graph.Routes ?? []).GroupBy(r => index.TypeOf(r.Handler)?.Id ?? "").ToDictionary(g => g.Key, g => g.ToList());
        string Pages(string type)
        {
            if (!routesIn.TryGetValue(type, out var list)) return "";
            var names = list.Select(r => $"{(r.Method == "*" ? "ANY" : r.Method)} {r.Path}").Distinct().ToList();
            return $" It handles {(names.Count == 1 ? "the address " : names.Count + " addresses: ")}{string.Join(", ", names.Take(4))}{(names.Count > 4 ? " and more" : "")}.";
        }
        TourStep Step(string id, string role, string reason)
        {
            var typeId = index.Types.ContainsKey(id) ? id : index.MemberOwner[id];
            var file = index.Types.TryGetValue(id, out var t) ? t.File : index.Members[id].File;
            return new TourStep(id, typeId, index.Display(id), Relative(graph, file), role, reason);
        }

        var steps = new List<TourStep> { Step(entry.Id, "entry", entry.Reason + Pages(entryType)) };
        var visited = new HashSet<string>(StringComparer.Ordinal) { entryType };
        // Follow whatever brings in the most code, so the first steps trace the backbone of the app.
        var current = entryType;
        while (steps.Count < (TourLength + 1) / 2)
        {
            var next = (children.GetValueOrDefault(current) ?? []).Where(t => !visited.Contains(t))
                .OrderByDescending(Reach).ThenByDescending(Users).ThenBy(t => t, StringComparer.Ordinal).FirstOrDefault();
            if (next is null) break;
            var size = Reach(next);
            steps.Add(Step(next, "backbone", Relationship(index, index.Display(current), current, next) +
                (size > 0 ? $" It leads on to {size} other type{(size == 1 ? "" : "s")}{(size >= 5 ? ", so a big part of the app hangs off it" : "")}." : "") + Pages(next)));
            visited.Add(next);
            current = next;
            if (size == 0) break;
        }
        // Controllers and pages: ASP.NET Core finds them by itself, so nothing in the code leads to them.
        var handlers = routesIn.Where(r => r.Key.Length > 0 && !visited.Contains(r.Key) && Counts(r.Key))
            .OrderByDescending(r => r.Value.Count).ThenBy(r => r.Key, StringComparer.Ordinal).Take(3).ToList();
        foreach (var (typeId, list) in handlers)
        {
            if (steps.Count >= TourLength) break;
            var why = list[0].Kind switch
            {
                "controller" => "A controller: ASP.NET Core finds it by itself, so nothing in the code calls it directly.",
                "page" => "A Razor Page: ASP.NET Core finds it from its file under Pages/, so nothing in the code calls it directly.",
                _ => "Its methods handle web addresses mapped elsewhere in the app.",
            };
            steps.Add(Step(typeId, "pages", why + Pages(typeId)));
            visited.Add(typeId);
        }
        // Then the shared types that almost everything uses.
        var hubs = children.Keys.Concat(parents.Keys).Distinct(StringComparer.Ordinal)
            .Where(t => !visited.Contains(t) && Users(t) >= 2)
            .OrderByDescending(Users).ThenByDescending(Reach).ThenBy(t => t, StringComparer.Ordinal);
        foreach (var hub in hubs)
        {
            if (steps.Count >= TourLength) break;
            steps.Add(Step(hub, "shared", $"Used by {Users(hub)} other types, so you will see it all over the app." + Pages(hub)));
            visited.Add(hub);
        }
        return steps;
    }

    /// <summary>How the previous step leads to this one, as a sentence.</summary>
    static string Relationship(GraphIndex index, string name, string from, string to)
    {
        var graph = index.Graph;
        var kinds = graph.Edges.Where(e => e.Source == from && e.Target == to).Select(e => e.Kind).ToHashSet();
        if (kinds.Count == 0 && graph.Edges.Any(e => e.Source == to && e.Target == from && e.Kind == EdgeKind.Implements))
            return $"It implements {name}, so calls to {name} end up here.";
        var fromFile = index.Types.GetValueOrDefault(from)?.File;
        if (!kinds.Contains(EdgeKind.Calls) && !kinds.Contains(EdgeKind.Creates)
            && (graph.Routes ?? []).Any(r => r.File == fromFile && index.TypeOf(r.Handler)?.Id == to))
            return $"{name} maps a web address to it.";
        return kinds.Contains(EdgeKind.Creates) ? $"{name} creates it."
            : kinds.Contains(EdgeKind.Calls) ? $"{name} calls it."
            : kinds.Contains(EdgeKind.Has) ? $"{name} keeps one in a field or property."
            : kinds.Contains(EdgeKind.Inherits) ? $"{name} is built on it (it's the base type)."
            : kinds.Contains(EdgeKind.Implements) ? $"{name} implements it."
            : $"{name} refers to it, for example to register it as a service.";
    }
}
