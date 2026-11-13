using CodeToLife.Analysis;
using CodeToLife.Insights;

namespace CodeToLife;

/// <summary>
/// <c>codetolife check [&lt;path&gt;] [--format text|github]</c>: checks the architecture rules in
/// <c>.codetolife/rules.json</c> and exits with 1 when any are broken, for CI.
/// </summary>
static class CheckCommand
{
    public static int Run(string[] args)
    {
        string? path = null;
        var format = "text";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--format" && i + 1 < args.Length) format = args[++i];
            else if (args[i] is "-h" or "--help")
            {
                Console.WriteLine("""
                    Usage: codetolife check [<path>] [--format text|github]

                    Checks the architecture rules in <path>/.codetolife/rules.json against the code.
                    Exits with 1 if any rule is broken, 2 if the rules can't be read.
                    --format github prints GitHub Actions annotations.
                    """);
                return 0;
            }
            else path ??= args[i];
        }
        path = Path.GetFullPath(path ?? Environment.CurrentDirectory);
        var root = File.Exists(path) ? Path.GetDirectoryName(path)! : path;

        ArchitectureConfig? config;
        try { config = ArchitectureRules.Load(root); }
        catch (System.Text.Json.JsonException ex)
        {
            Console.Error.WriteLine($"{ArchitectureRules.PathFor(root)} isn't valid JSON: {ex.Message}");
            return 2;
        }
        if (config is null)
        {
            Console.Error.WriteLine($"No rules yet: create {ArchitectureRules.PathFor(root)} (the Rules tab in Code to Life suggests a starting point).");
            return 2;
        }

        var graph = GraphBuilder.Analyze(path);
        var result = ArchitectureRules.Check(graph, config);
        foreach (var problem in result.Problems) Console.Error.WriteLine($"warning: {problem}");

        foreach (var v in result.Violations)
        {
            var file = v.File is null ? null : Path.GetRelativePath(root, v.File).Replace('\\', '/');
            if (format == "github")
                Console.WriteLine($"::error file={file},line={v.Line},title=Architecture rule {v.Rule + 1}::{v.Message}");
            else
                Console.WriteLine($"{file}:{v.Line}: {v.Message} [{v.Kind}]");
        }
        Console.WriteLine(result.Violations.Count == 0
            ? $"All {config.Rules.Count} architecture rule(s) hold across {graph.Types.Count} types."
            : $"{result.Violations.Count} violation(s) of {result.Violations.Select(v => v.Rule).Distinct().Count()} rule(s).");
        return result.Violations.Count == 0 ? 0 : 1;
    }
}
