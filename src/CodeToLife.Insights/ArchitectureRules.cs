using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodeToLife.Analysis;

namespace CodeToLife.Insights;

/// <summary>
/// The design you mean to keep, as rules over the type graph. Saved in <c>.codetolife/rules.json</c>.
/// </summary>
/// <param name="Layers">Named groups of types, e.g. <c>"Domain": "MyApp.Domain.*"</c>. Several patterns are separated by commas.</param>
public sealed record ArchitectureConfig(Dictionary<string, string>? Layers, List<ArchitectureRule> Rules)
{
    public static ArchitectureConfig Empty => new(new(), []);
}

/// <summary>
/// One rule. Either <c>from</c> + <c>notTo</c> ("Domain must not depend on Infrastructure"), or
/// <c>to</c> + <c>onlyFrom</c> ("only Infrastructure may touch the database types").
/// Patterns are layer names, <c>project:Name</c>, or globs over namespaces and type ids
/// (<c>*</c> = any characters; <c>Shop.Data.*</c> also matches the <c>Shop.Data</c> namespace itself).
/// </summary>
/// <param name="Kinds">Edge kinds the rule is about (inherits, implements, has, calls, creates, uses). Default: all of them.</param>
/// <param name="Except">Source types the rule doesn't apply to.</param>
/// <param name="Because">Why the rule exists, shown with each violation.</param>
public sealed record ArchitectureRule(
    string? From = null,
    string? NotTo = null,
    string? To = null,
    string? OnlyFrom = null,
    List<string>? Kinds = null,
    string? Except = null,
    string? Because = null);

public sealed record Violation(int Rule, string RuleText, string Source, string Target, string Kind, int Count, string? File, int Line, string Message);

public sealed record ArchitectureResult(bool HasRules, IReadOnlyList<Violation> Violations, IReadOnlyList<string> Problems);

public static class ArchitectureRules
{
    public const string FileName = "rules.json";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string root) => Path.Combine(root, ".codetolife", FileName);

    public static ArchitectureConfig? Load(string root)
    {
        var path = PathFor(root);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
    }

    public static ArchitectureConfig Parse(string json) =>
        JsonSerializer.Deserialize<ArchitectureConfig>(json, Json) ?? ArchitectureConfig.Empty;

    public static string Serialize(ArchitectureConfig config) => JsonSerializer.Serialize(config, Json);

    public static void Save(string root, string json)
    {
        Parse(json); // throws on invalid JSON, before anything is written
        Directory.CreateDirectory(Path.GetDirectoryName(PathFor(root))!);
        File.WriteAllText(PathFor(root), json);
    }

    /// <summary>A starting point drawn from the code: one layer per top-level namespace under the root namespace.</summary>
    public static ArchitectureConfig Suggest(CodeGraph graph)
    {
        var namespaces = graph.Types.Select(t => t.Namespace).Where(n => n.Length > 0).Distinct().ToList();
        var root = namespaces.Count == 0 ? "" : namespaces.Aggregate((a, b) => CommonPrefix(a, b));
        var layers = namespaces
            .Select(n => n.Length > root.Length ? n[(root.Length + (root.Length > 0 ? 1 : 0))..].Split('.')[0] : null)
            .Where(n => !string.IsNullOrEmpty(n)).Distinct()
            .ToDictionary(n => n!, n => (root.Length > 0 ? root + "." : "") + n + ".*");
        var rules = new List<ArchitectureRule>();
        if (layers.ContainsKey("Domain"))
            foreach (var other in layers.Keys.Where(k => k is "Infrastructure" or "Data" or "Web" or "Api" or "Services" or "Persistence"))
                rules.Add(new ArchitectureRule(From: "Domain", NotTo: other, Because: "the domain model shouldn't know how it's stored or served"));
        return new ArchitectureConfig(layers, rules);
    }

    static string CommonPrefix(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        var n = 0;
        while (n < pa.Length && n < pb.Length && pa[n] == pb[n]) n++;
        return string.Join('.', pa.Take(n));
    }

    public static ArchitectureResult Check(CodeGraph graph, ArchitectureConfig? config)
    {
        if (config is null || config.Rules.Count == 0) return new ArchitectureResult(false, [], []);
        var types = graph.Types.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var problems = new List<string>();
        var violations = new List<Violation>();
        var layers = config.Layers ?? [];

        for (var i = 0; i < config.Rules.Count; i++)
        {
            var rule = config.Rules[i];
            var text = Describe(rule);
            Func<TypeNode, bool> source, target;
            bool inverted; // to + onlyFrom: a violation is an edge into "to" from outside "onlyFrom"
            if (rule.NotTo is not null)
            {
                source = Matcher(rule.From ?? "*", layers, problems, i);
                target = Matcher(rule.NotTo, layers, problems, i);
                inverted = false;
            }
            else if (rule.To is not null && rule.OnlyFrom is not null)
            {
                source = Matcher(rule.OnlyFrom, layers, problems, i);
                target = Matcher(rule.To, layers, problems, i);
                inverted = true;
            }
            else
            {
                problems.Add($"Rule {i + 1}: needs either \"notTo\" or both \"to\" and \"onlyFrom\".");
                continue;
            }
            var except = rule.Except is null ? (_ => false) : Matcher(rule.Except, layers, problems, i);
            var kinds = rule.Kinds is { Count: > 0 } k ? k.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

            // One violation per pair of types, however many ways they touch (new Store().Write() both creates and calls).
            var broken = graph.Edges
                .Where(edge => edge.Kind != EdgeKind.Nested && (kinds is null || kinds.Contains(edge.Kind)))
                .Where(edge => types.TryGetValue(edge.Source, out var from) && types.TryGetValue(edge.Target, out var to) && !except(from)
                               && (inverted
                                   ? target(to) && !source(from) && !target(from)
                                   : source(from) && target(to) && !target(from))) // edges inside the target layer are its own business
                .GroupBy(edge => (edge.Source, edge.Target));
            foreach (var pair in broken)
            {
                var strongest = pair.OrderBy(e => Strength(e.Kind)).First();
                var from = types[pair.Key.Source];
                var to = types[pair.Key.Target];
                var message = $"{from.Name} {Verb(strongest.Kind)} {to.Name}" + (rule.Because is { } why ? $": {why}" : $" ({text})");
                violations.Add(new Violation(i, text, from.Id, to.Id, string.Join(", ", pair.OrderBy(e => Strength(e.Kind)).Select(e => e.Kind)),
                    pair.Sum(e => e.Count), strongest.File, strongest.Line, message));
            }
        }
        return new ArchitectureResult(true, violations, problems);
    }

    public static string Describe(ArchitectureRule rule) =>
        rule.NotTo is not null
            ? $"{rule.From ?? "*"} must not {KindsText(rule)} {rule.NotTo}"
            : $"only {rule.OnlyFrom} may {KindsText(rule)} {rule.To}";

    static string KindsText(ArchitectureRule rule) =>
        rule.Kinds is { Count: > 0 } kinds ? string.Join("/", kinds) : "depend on";

    static int Strength(string kind) => kind switch
    {
        EdgeKind.Inherits => 0,
        EdgeKind.Implements => 1,
        EdgeKind.Creates => 2,
        EdgeKind.Has => 3,
        EdgeKind.Calls => 4,
        _ => 5,
    };

    static string Verb(string kind) => kind switch
    {
        EdgeKind.Inherits => "inherits from",
        EdgeKind.Implements => "implements",
        EdgeKind.Has => "holds",
        EdgeKind.Calls => "calls",
        EdgeKind.Creates => "creates",
        _ => "uses",
    };

    /// <summary>Turns a rule's pattern (layer names, project:, globs, comma-separated) into a test on a type.</summary>
    internal static Func<TypeNode, bool> Matcher(string pattern, IReadOnlyDictionary<string, string> layers, List<string> problems, int rule, int depth = 0)
    {
        var tests = new List<Func<TypeNode, bool>>();
        foreach (var raw in pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*") { tests.Add(_ => true); continue; }
            if (layers.TryGetValue(raw, out var layer))
            {
                if (depth > 5) { problems.Add($"Rule {rule + 1}: layer '{raw}' refers to itself."); continue; }
                tests.Add(Matcher(layer, layers, problems, rule, depth + 1));
                continue;
            }
            if (raw.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
            {
                var project = Glob(raw["project:".Length..]);
                tests.Add(t => project.IsMatch(t.Project));
                continue;
            }
            var glob = Glob(raw);
            var namespaceOnly = raw.EndsWith(".*", StringComparison.Ordinal) ? raw[..^2] : null;
            tests.Add(t => glob.IsMatch(t.Id) || glob.IsMatch(t.Namespace) || glob.IsMatch(t.Name)
                           || (namespaceOnly is not null && t.Namespace == namespaceOnly));
        }
        if (tests.Count == 0) problems.Add($"Rule {rule + 1}: '{pattern}' doesn't match anything.");
        return t => tests.Any(test => test(t));
    }

    static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.CultureInvariant);
}
