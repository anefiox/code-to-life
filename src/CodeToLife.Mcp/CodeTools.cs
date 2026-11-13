using System.ComponentModel;
using System.Text;
using CodeToLife.Analysis;
using CodeToLife.Insights;
using ModelContextProtocol.Server;

namespace CodeToLife.Mcp;

/// <summary>
/// Questions about the code, answered from the Roslyn graph. Names can be given the way you'd
/// write them: <c>OrderService</c>, <c>OrderService.CheckoutAsync</c> or a full id.
/// </summary>
[McpServerToolType]
public sealed class CodeTools(CodebaseHost host)
{
    const string NameHelp = "A type or member: 'OrderService', 'OrderService.CheckoutAsync', 'Shop.Services.OrderService' or an id from another tool's output.";

    [McpServerTool(Name = "overview", ReadOnly = true), Description("Start here: the projects, namespaces, most-used and most complex types of the C# codebase, and any analysis warnings.")]
    public async Task<string> Overview()
    {
        var code = await host.ReadyAsync();
        var graph = code.Graph;
        var sb = new StringBuilder();
        sb.AppendLine($"# {graph.Name}  ({graph.Root})");
        sb.AppendLine($"{graph.Types.Count} types, {graph.Types.Sum(t => t.Members.Count)} members, {graph.Calls.Count} call paths, analyzed {graph.AnalyzedAt:HH:mm:ss}.");
        sb.AppendLine().AppendLine("## Projects");
        foreach (var p in graph.Projects)
            sb.AppendLine($"- {p.Name} ({p.OutputKind}, {p.FileCount} files{(p.References.Count > 0 ? ", references " + string.Join(", ", p.References) : "")}{(p.ErrorCount > 0 ? $", {p.ErrorCount} compile errors without a build" : "")})");
        sb.AppendLine().AppendLine("## Namespaces");
        foreach (var ns in graph.Types.GroupBy(t => t.Namespace).OrderBy(g => g.Key))
            sb.AppendLine($"- {(ns.Key.Length == 0 ? "(global)" : ns.Key)}: {string.Join(", ", ns.Take(12).Select(t => t.Name))}{(ns.Count() > 12 ? $" and {ns.Count() - 12} more" : "")}");
        sb.AppendLine().AppendLine("## Most depended on");
        foreach (var t in graph.Types.OrderByDescending(t => t.Metrics.IncomingCount).Take(8))
            sb.AppendLine($"- {t.Name}: used by {t.Metrics.IncomingCount} types");
        sb.AppendLine().AppendLine("## Most complex methods");
        foreach (var (t, m) in graph.Types.SelectMany(t => t.Members.Select(m => (t, m))).OrderByDescending(x => x.m.Complexity).Take(8))
            sb.AppendLine($"- {code.Index.Display(m.Id)}: complexity {m.Complexity} ({code.Index.Location(m.Id)})");
        var tests = code.Tests;
        sb.AppendLine().AppendLine($"## Tests\n{tests.Tests.Count} test methods in {string.Join(", ", tests.TestProjects.DefaultIfEmpty("no test projects"))}.");
        foreach (var w in graph.Warnings) sb.AppendLine($"\nWarning: {w}");
        return sb.ToString();
    }

    [McpServerTool(Name = "find", ReadOnly = true), Description("Find types and members by name (case-insensitive, partial names work). Returns ids, kinds and file:line.")]
    public async Task<string> Find([Description("A name or part of one, e.g. 'Checkout' or 'Repository.Save'.")] string query, [Description("Most results to return.")] int limit = 25)
    {
        var code = await host.ReadyAsync();
        var index = code.Index;
        var hits = index.Resolve(query).ToList();
        if (hits.Count == 0)
        {
            // Fall back to substring matching.
            var q = query.Trim();
            hits = code.Graph.Types.Where(t => t.Id.Contains(q, StringComparison.OrdinalIgnoreCase)).Select(t => t.Id)
                .Concat(code.Graph.Types.SelectMany(t => t.Members).Where(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).Select(m => m.Id))
                .ToList();
        }
        if (hits.Count == 0) return $"Nothing in the code is called '{query}'.";
        return string.Join('\n', hits.Take(limit).Select(id => Line(index, id))) + (hits.Count > limit ? $"\n…and {hits.Count - limit} more." : "");
    }

    [McpServerTool(Name = "describe", ReadOnly = true), Description("Everything about a type or member: kind, signature, docs, complexity, members (for a type), relationships, callers and callees (for a member), tests that reach it, and its source code.")]
    public async Task<string> Describe([Description(NameHelp)] string name, [Description("Include the source code.")] bool source = true)
    {
        var code = await host.ReadyAsync();
        var index = code.Index;
        if (Resolve(index, name) is not { } id) return Ambiguous(index, name);
        var sb = new StringBuilder();
        if (index.Types.TryGetValue(id, out var type))
        {
            sb.AppendLine($"# {type.Accessibility} {string.Join(' ', type.Modifiers)} {type.Kind} {type.Name}".Replace("  ", " "));
            sb.AppendLine($"{type.Namespace} · project {type.Project} · {index.Location(id)} · {type.Metrics.Lines} lines");
            if (type.Summary is not null) sb.AppendLine().AppendLine(type.Summary);
            if (type.Attributes.Count > 0) sb.AppendLine($"Attributes: {string.Join(", ", type.Attributes.Select(a => $"[{a}]"))}");
            sb.AppendLine().AppendLine("## Members");
            foreach (var m in type.Members)
                sb.AppendLine($"- {m.Signature}  ({m.Kind}{(m.Complexity > 1 ? $", complexity {m.Complexity}" : "")}, line {m.Line})");
            sb.AppendLine().AppendLine("## Depends on");
            foreach (var g in index.EdgesFrom(id).Where(e => e.Kind != EdgeKind.Nested).GroupBy(e => e.Target))
                sb.AppendLine($"- {index.Display(g.Key)}: {string.Join(", ", g.Select(e => e.Kind))}");
            sb.AppendLine().AppendLine("## Used by");
            foreach (var g in index.EdgesTo(id).Where(e => e.Kind != EdgeKind.Nested).GroupBy(e => e.Source))
                sb.AppendLine($"- {index.Display(g.Key)}: {string.Join(", ", g.Select(e => e.Kind))}");
            var coverage = code.Tests.CoverageOf(type);
            sb.AppendLine().AppendLine($"## Tests\n{coverage.Covered} of {coverage.Members} members with code are reached by tests.");
            if (source && type.File is not null) AppendSource(sb, type.File, type.Line, type.EndLine);
        }
        else
        {
            var member = index.Members[id];
            sb.AppendLine($"# {member.Kind} {index.Display(id)}");
            sb.AppendLine($"`{member.Signature}` · {index.Location(id)}{(member.Complexity > 1 ? $" · complexity {member.Complexity}" : "")}{(member.IsAsync ? " · async" : "")}");
            if (member.Summary is not null) sb.AppendLine().AppendLine(member.Summary);
            if (member.Attributes.Count > 0) sb.AppendLine($"Attributes: {string.Join(", ", member.Attributes.Select(a => $"[{a}]"))}");
            sb.AppendLine().AppendLine("## Calls");
            foreach (var c in index.CallsFrom(id)) sb.AppendLine($"- {index.Display(c.Target)} ({c.Kind}{(c.Count > 1 ? $" ×{c.Count}" : "")})");
            foreach (var impl in index.CallsFrom(id).Where(c => c.Kind is "virtual" or "property").SelectMany(c => index.ImplementationsOf(c.Target)).Distinct())
                sb.AppendLine($"- {index.Display(impl)} (an implementation that may run)");
            sb.AppendLine().AppendLine("## Called by");
            foreach (var r in index.Callers(id, 1)) sb.AppendLine($"- {index.Display(r.MemberId)}{(r.Kind == "dispatch" ? " (through an interface or base member)" : "")}");
            foreach (var contract in index.ContractsOf(id)) sb.AppendLine($"Implements {index.Display(contract)}.");
            foreach (var impl in index.ImplementationsOf(id)) sb.AppendLine($"Implemented by {index.Display(impl)}.");
            var hits = code.Tests.For(id);
            sb.AppendLine().AppendLine(hits.Count == 0 ? "## Tests\nNo test reaches it." : $"## Tests ({hits.Count})");
            foreach (var h in hits.Take(10)) sb.AppendLine($"- {index.Display(h.TestId)} ({h.Depth} call{(h.Depth == 1 ? "" : "s")} away)");
            if (source && member.File is not null) AppendSource(sb, member.File, member.Line, member.EndLine);
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "callers", ReadOnly = true), Description("Who calls a member, directly and indirectly, nearest first. Follows calls made through interfaces and base classes, which grep can't.")]
    public async Task<string> Callers([Description(NameHelp)] string name, [Description("How many calls away to look (1-8).")] int depth = 3)
    {
        var code = await host.ReadyAsync();
        if (ResolveMember(code.Index, name) is not { } id) return Ambiguous(code.Index, name, membersOnly: true);
        var reached = code.Index.Callers(id, Math.Clamp(depth, 1, 8));
        return reached.Count == 0
            ? $"Nothing in this code calls {code.Index.Display(id)}: it's an entry point, called from outside, or only via reflection."
            : $"Callers of {code.Index.Display(id)}:\n" + Tree(code.Index, reached);
    }

    [McpServerTool(Name = "callees", ReadOnly = true), Description("What a member ends up calling, nearest first, including the classes that implement the interfaces it calls.")]
    public async Task<string> Callees([Description(NameHelp)] string name, [Description("How many calls deep to follow (1-8).")] int depth = 3)
    {
        var code = await host.ReadyAsync();
        if (ResolveMember(code.Index, name) is not { } id) return Ambiguous(code.Index, name, membersOnly: true);
        var reached = code.Index.Callees(id, Math.Clamp(depth, 1, 8));
        return reached.Count == 0 ? $"{code.Index.Display(id)} doesn't call anything in this code." : $"{code.Index.Display(id)} calls:\n" + Tree(code.Index, reached);
    }

    [McpServerTool(Name = "path_between", ReadOnly = true), Description("The shortest chain of calls from one member to another, if there is one.")]
    public async Task<string> PathBetween([Description("Where the path starts. " + NameHelp)] string from, [Description("Where it should end.")] string to)
    {
        var code = await host.ReadyAsync();
        var index = code.Index;
        if (ResolveMember(index, from) is not { } a) return Ambiguous(index, from, membersOnly: true);
        if (ResolveMember(index, to) is not { } b) return Ambiguous(index, to, membersOnly: true);
        var path = index.PathBetween(a, b);
        return path is null
            ? $"No call path from {index.Display(a)} to {index.Display(b)} within 8 calls."
            : string.Join("\n  → ", path.Select(r => $"{index.Display(r.MemberId)}{(r.Kind == "dispatch" ? " (via interface)" : "")} [{index.Location(r.MemberId)}]"));
    }

    [McpServerTool(Name = "impact", ReadOnly = true), Description("What would need to change, or be re-checked, if this type or member changes: callers, implementations, contracts, dependents, tests, and risks the compiler won't catch (reflection, string mentions, DI, serialization). Use it before a refactor.")]
    public async Task<string> Impact([Description(NameHelp)] string name, [Description("How many calls away to look for callers.")] int depth = 4)
    {
        var code = await host.ReadyAsync();
        if (Resolve(code.Index, name) is not { } id) return Ambiguous(code.Index, name);
        var report = ImpactAnalyzer.Analyze(code.Index, code.Tests, code.Mapper, code.SourceFiles, id, Math.Clamp(depth, 1, 10));
        var sb = new StringBuilder($"# Impact of changing {report.Title}\n{string.Join(" · ", report.Summary)}\n");
        foreach (var group in ImpactAnalyzer.Groups)
        {
            var items = report.Items.Where(i => i.Group == group).ToList();
            if (items.Count == 0) continue;
            sb.AppendLine().AppendLine($"## {group} ({items.Count})");
            foreach (var i in items)
                sb.AppendLine($"- [ ] {i.Label}: {i.Detail}{(i.File is null ? "" : $" ({Path.GetRelativePath(code.Graph.Root, i.File).Replace('\\', '/')}:{i.Line})")}");
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "tests_for", ReadOnly = true), Description("Which tests reach some code (found by following calls from every test), plus the dotnet test command that runs just those. Pass several names separated by commas for a whole change. Also lists code nothing tests.")]
    public async Task<string> TestsFor([Description("One or more types/members, comma-separated.")] string names)
    {
        var code = await host.ReadyAsync();
        var index = code.Index;
        var ids = new List<string>();
        var unknown = new List<string>();
        foreach (var n in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Resolve(index, n) is { } id) ids.Add(id); else unknown.Add(n);
        var affected = code.Tests.Affected(ids);
        var sb = new StringBuilder();
        if (code.Tests.Tests.Count == 0) sb.AppendLine("This codebase has no test methods ([Fact], [Test], [TestMethod]…).");
        sb.AppendLine(affected.Tests.Count == 0 ? "No tests reach it." : $"{affected.Tests.Count} test(s) reach it:");
        foreach (var t in affected.Tests) sb.AppendLine($"- {index.Display(t)} ({index.Location(t)})");
        if (affected.Untested.Count > 0)
        {
            sb.AppendLine().AppendLine("Not reached by any test:");
            foreach (var u in affected.Untested) sb.AppendLine($"- {index.Display(u)} ({index.Location(u)})");
        }
        if (affected.Commands.Count > 0) sb.AppendLine().AppendLine("Run them with:").AppendLine(string.Join('\n', affected.Commands.Select(c => c.CommandLine)));
        if (unknown.Count > 0) sb.AppendLine().AppendLine($"Couldn't find: {string.Join(", ", unknown)} (try find).");
        return sb.ToString();
    }

    [McpServerTool(Name = "check_architecture", ReadOnly = true), Description("Check the architecture rules in .codetolife/rules.json (e.g. 'Domain must not depend on Infrastructure'). With no rules file, suggests one from the namespaces.")]
    public async Task<string> CheckArchitecture()
    {
        var code = await host.ReadyAsync();
        var config = ArchitectureRules.Load(code.RootDirectory);
        if (config is null)
            return $"No rules yet. A starting point for {ArchitectureRules.PathFor(code.RootDirectory)}:\n\n{ArchitectureRules.Serialize(ArchitectureRules.Suggest(code.Graph))}\n\nRules are {{\"from\": A, \"notTo\": B}} or {{\"to\": B, \"onlyFrom\": A}}, with optional \"kinds\", \"except\" and \"because\".";
        var result = ArchitectureRules.Check(code.Graph, config);
        var sb = new StringBuilder();
        foreach (var p in result.Problems) sb.AppendLine($"Problem: {p}");
        if (result.Violations.Count == 0) sb.AppendLine($"All {config.Rules.Count} rules hold.");
        foreach (var v in result.Violations)
            sb.AppendLine($"- {v.Message} [{v.Kind}] at {(v.File is null ? "?" : Path.GetRelativePath(code.Graph.Root, v.File).Replace('\\', '/'))}:{v.Line} (rule {v.Rule + 1}: {v.RuleText})");
        return sb.ToString();
    }

    [McpServerTool(Name = "map_stack_trace", ReadOnly = true), Description("Map a .NET exception or stack trace (from a log, a bug report, CI output) onto this code: which members each frame is, with file:line, innermost first.")]
    public async Task<string> MapStackTrace([Description("The exception text and its 'at …' lines.")] string trace)
    {
        var code = await host.ReadyAsync();
        var parsed = TraceParser.Parse(trace, code.Mapper);
        if (parsed.Stack.Count == 0) return "No stack frames found: paste the lines that start with 'at '.";
        var sb = new StringBuilder();
        foreach (var e in parsed.Exceptions) sb.AppendLine($"{e.Type}: {e.Message}");
        sb.AppendLine().AppendLine("Run order, where it was thrown first:");
        var i = 0;
        foreach (var f in parsed.Stack)
            sb.AppendLine(f.IsUserCode
                ? $"{i++,2}. {code.Index.Display(f.MemberId ?? f.TypeId!)}  [{code.Index.Location(f.MemberId ?? f.TypeId!)}]{(f.Line is { } l ? $" line {l} in the trace" : "")}"
                : $"{i++,2}. ({f.Method}: not this code)");
        return sb.ToString();
    }

    [McpServerTool(Name = "scan_log", ReadOnly = true), Description("Find every exception in a log file (plain text or JSON lines from Serilog, Microsoft.Extensions.Logging, NLog…), group the ones with the same cause, and map them to the code.")]
    public async Task<string> ScanLog([Description("Path to the log file, absolute or relative to the codebase.")] string path)
    {
        var code = await host.ReadyAsync();
        var full = Path.GetFullPath(Path.Combine(code.RootDirectory, path));
        if (!File.Exists(full)) return $"'{full}' doesn't exist.";
        var scan = TraceParser.ScanLog(await File.ReadAllTextAsync(full), code.Mapper);
        var sb = new StringBuilder($"{scan.Failures} failure(s) in {scan.Lines} lines, {scan.Groups.Count} distinct:\n");
        foreach (var g in scan.Groups)
        {
            var mine = g.Sample.Stack.Where(f => f.IsUserCode).Take(4).Select(f => code.Index.Display(f.MemberId ?? f.TypeId!));
            sb.AppendLine($"- {g.Count}× {g.Title}: {g.Message} (lines {g.FirstLine}–{g.LastLine})\n    {string.Join(" ← ", mine.DefaultIfEmpty("no frames from this code"))}");
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "open"), Description("Switch to a different solution, project or folder.")]
    public async Task<string> Open([Description("A .sln, .slnx, .csproj or folder.")] string path)
    {
        var code = await host.OpenAsync(path);
        return $"Opened {code.Graph.Name}: {code.Graph.Types.Count} types in {code.Graph.Projects.Count} projects.";
    }

    // ---------------------------------------------------------------- helpers

    internal static string? Resolve(GraphIndex index, string name) => index.Resolve(name) is [var only] ? only : null;

    internal static string? ResolveMember(GraphIndex index, string name)
    {
        var hits = index.Resolve(name);
        var members = hits.Where(index.Members.ContainsKey).ToList();
        if (members.Count == 1) return members[0];
        // A type with a single interesting method is unambiguous enough.
        if (hits is [var typeId] && index.Types.TryGetValue(typeId, out var type) && type.Members.Count(m => m.Kind == "method") == 1)
            return type.Members.First(m => m.Kind == "method").Id;
        return null;
    }

    internal static string Ambiguous(GraphIndex index, string name, bool membersOnly = false)
    {
        var hits = index.Resolve(name).Where(h => !membersOnly || index.Members.ContainsKey(h)).ToList();
        if (hits.Count == 0)
        {
            var q = name.Trim();
            var near = index.Types.Values.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && !membersOnly).Select(t => t.Id)
                .Concat(index.Members.Values.Where(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).Select(m => m.Id))
                .Take(10).ToList();
            return near.Count == 0
                ? $"Nothing called '{name}'. Try the find tool."
                : $"Nothing is called exactly '{name}'. Did you mean:\n" + string.Join('\n', near.Select(h => Line(index, h)));
        }
        return $"'{name}' could mean any of these; pass one of the ids:\n" + string.Join('\n', hits.Take(15).Select(h => Line(index, h)));
    }

    static string Line(GraphIndex index, string id) =>
        index.Types.TryGetValue(id, out var t)
            ? $"- {t.Kind} {t.Name}  id: {id}  ({index.Location(id)})"
            : $"- {index.Members[id].Kind} {index.Display(id)}  id: {id}  ({index.Location(id)})";

    static string Tree(GraphIndex index, IReadOnlyList<Reach> reached) =>
        string.Join('\n', reached.Select(r =>
            $"{new string(' ', 2 * r.Depth)}{index.Display(r.MemberId)}{(r.Kind == "dispatch" ? " (through an interface or base)" : "")}{(r.Depth > 1 && r.Via is not null ? $" via {index.Display(r.Via)}" : "")}  [{index.Location(r.MemberId)}]"));

    static void AppendSource(StringBuilder sb, string file, int from, int to)
    {
        try
        {
            var lines = File.ReadAllLines(file);
            var end = Math.Min(lines.Length, Math.Min(to, from + 120));
            sb.AppendLine().AppendLine($"## Source ({Path.GetFileName(file)}:{from}-{end})").AppendLine("```csharp");
            for (var i = from - 1; i < end; i++) sb.AppendLine($"{i + 1,4}  {lines[i]}");
            sb.AppendLine("```");
            if (to > end) sb.AppendLine($"…{to - end} more lines.");
        }
        catch (IOException) { }
    }
}
