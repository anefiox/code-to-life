using System.Text.RegularExpressions;
using CodeToLife.Analysis;

namespace CodeToLife.Insights;

/// <summary>Reading the project's own files for the onboarding helpers: recipes, settings, the Razor map and history.</summary>
public static partial class ProjectFiles
{
    const long TextLimit = 1024 * 1024;
    const long ShowLimit = 512 * 1024;
    static readonly string[] Skipped = ["bin", "obj", ".git", ".vs", ".idea", "node_modules", ".codetolife", "TestResults", "packages", "artifacts"];

    public static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    public static string? RelativeOrNull(string root, string? file) => file is null ? null : Relative(root, file);

    /// <summary>A text file's contents with \n line endings, or "" when it is missing, unreadable or too large.</summary>
    public static string ReadText(string root, string relative)
    {
        try
        {
            var info = new FileInfo(Path.Combine(root, relative));
            return !info.Exists || info.Length > TextLimit ? "" : File.ReadAllText(info.FullName).ReplaceLineEndings("\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>Files under the root (relative, forward slashes) whose relative path matches, skipping build output.</summary>
    public static IReadOnlyList<string> Find(string root, Func<string, bool> match, int maxDepth = 8)
    {
        var found = new List<string>();
        var pending = new Stack<(string Dir, int Depth)>([(root, 0)]);
        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Pop();
            try
            {
                if (depth < maxDepth)
                    foreach (var child in Directory.EnumerateDirectories(dir))
                    {
                        var info = new DirectoryInfo(child);
                        if (info.LinkTarget is not null || Skipped.Contains(info.Name, StringComparer.OrdinalIgnoreCase)) continue;
                        if (info.Name.StartsWith('.') && info.Name != ".github" && !Relative(root, child).StartsWith(".github/", StringComparison.Ordinal)) continue;
                        pending.Push((child, depth + 1));
                    }
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var relative = Relative(root, file);
                    if (match(relative)) found.Add(relative);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found.OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static int LineAt(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

    /// <summary>The smallest member, or else type, around a line of a file: what a link to file:line should open.</summary>
    public static string? NodeAt(GraphIndex index, string file, int line)
    {
        var full = Path.GetFullPath(Path.Combine(index.Graph.Root, file));
        string? best = null;
        var span = int.MaxValue;
        foreach (var type in index.Graph.Types)
        {
            if (!string.Equals(type.File, full, StringComparison.OrdinalIgnoreCase)) continue;
            if (type.Line <= line && type.EndLine >= line && type.EndLine - type.Line < span) (best, span) = (type.Id, type.EndLine - type.Line);
            foreach (var member in type.Members)
                if (member.Kind != "top-level statements" && member.Line <= line && member.EndLine >= line && member.EndLine - member.Line < span)
                    (best, span) = (member.Id, member.EndLine - member.Line);
        }
        if (best is not null) return best;
        // Program.cs with top-level statements: the whole file is the top-level code.
        return index.Graph.Types.SelectMany(t => t.Members).FirstOrDefault(m => m.Kind == "top-level statements"
            && string.Equals(m.File, full, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>The projects that hold tests, and the folders they live in, so helpers can leave them out.</summary>
    public static (HashSet<string> Names, List<string> Dirs) TestProjects(CodeGraph graph)
    {
        var tests = graph.Projects.Where(p => p.OutputKind == "test").ToList();
        return (tests.Select(p => p.Name).ToHashSet(StringComparer.Ordinal),
            tests.Select(p => Relative(graph.Root, Path.GetDirectoryName(p.Path) ?? graph.Root).TrimEnd('/') + "/").ToList());
    }

    public static bool InTests(string relative, List<string> testDirs) =>
        testDirs.Any(d => d != "./" && relative.StartsWith(d, StringComparison.OrdinalIgnoreCase));

    /// <summary>Any readable text file in the project, so recipe and settings steps can show what they point at. Secrets never.</summary>
    public static TemplateSource ReadProjectFile(string root, string? file)
    {
        if (file is null || !Readable().IsMatch(file) || Secret().IsMatch(file) || file.Split('/').Any(p => p == ".." || Skipped.Contains(p, StringComparer.OrdinalIgnoreCase)))
            throw new ExplainException("That file can't be shown.", 404);
        var full = Path.GetFullPath(Path.Combine(root, file));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ExplainException("The file is outside the project.", 403);
        var info = new FileInfo(full);
        if (!info.Exists) throw new ExplainException("File not found.", 404);
        if (info.LinkTarget is not null) throw new ExplainException("That file can't be shown.", 404);
        if (info.Length > ShowLimit) throw new ExplainException("The file is too large to show.", 413);
        var code = File.ReadAllText(full).ReplaceLineEndings("\n").TrimEnd('\n');
        var total = code.Split('\n').Length;
        return new TemplateSource(file, 1, total, total, code);
    }

    [GeneratedRegex(@"\.(?:cs|cshtml|razor|csproj|props|targets|json|ya?ml|md|txt|xml|config|sln|slnx|css|scss|js|ts|html?|sh|ps1|editorconfig)$|(?:^|/)(?:Dockerfile|Makefile|\.env\.(?:example|sample|template))$", RegexOptions.IgnoreCase)]
    private static partial Regex Readable();

    // Real .env files and user secrets hold passwords; launchSettings and appsettings are fine to show.
    [GeneratedRegex(@"(?:^|/)(?:\.env(?:$|\.(?!example|sample|template))|secrets\.json$)", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();
}
