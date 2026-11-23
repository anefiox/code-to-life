using System.Diagnostics;
using CodeToLife.Analysis;

namespace CodeToLife.Insights;

public sealed record Author(string Name, int Commits);

public sealed record Hotspot(string File, int Commits, int Churn, IReadOnlyList<Author> Authors, string Last);

/// <param name="Limited">The history is longer than was read; the figures cover the latest commits.</param>
public sealed record Hotspots(bool Available, string? Reason, int Commits, int Contributors, string? First, string? Last, bool Limited,
    IReadOnlyList<Hotspot> Files);

public sealed record CommitInfo(string Hash, string Author, string Date, string Subject);

public sealed record NodeHistory(bool Available, string? Reason, IReadOnlyList<CommitInfo> Commits, IReadOnlyList<Author> Authors, string? Note);

/// <summary>History and hotspots from Git: which files change most, who works on them, and the commits behind a class or method.</summary>
public static class GitHistory
{
    const int LogLimit = 3000;
    const int HotspotLimit = 15;
    static readonly Lock Gate = new();
    static (string Key, Hotspots Value)? _cached;

    static string? Git(string root, params string[] args)
    {
        // Git must not inherit stdin: under the MCP server that is the protocol's pipe.
        var start = new ProcessStartInfo("git") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-C", root, "-c", "core.quotepath=off" }.Concat(args)) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000)) { try { process.Kill(true); } catch (InvalidOperationException) { } return null; }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    static (string Head, string Prefix)? Repo(string root) =>
        Git(root, "rev-parse", "HEAD") is { } head && Git(root, "rev-parse", "--show-prefix") is { } prefix ? (head.Trim(), prefix.Trim()) : null;

    static IReadOnlyList<Author> Top(Dictionary<string, int> counts) =>
        counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Take(3).Select(c => new Author(c.Key, c.Value)).ToList();

    public static Hotspots For(CodeGraph graph)
    {
        var root = graph.Root;
        if (Repo(root) is not { } repo) return new Hotspots(false, "This folder isn't a Git repository, or Git isn't installed.", 0, 0, null, null, false, []);
        var tests = graph.Projects.Where(p => p.OutputKind == "test").Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var known = graph.Types.Where(t => t.File is not null && !tests.Contains(t.Project))
            .Select(t => ProjectFiles.Relative(root, t.File!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var key = root + "\n" + repo.Head + "\n" + string.Join('\n', known.Order(StringComparer.Ordinal));
        lock (Gate) if (_cached is { } c && c.Key == key) return c.Value;

        var output = Git(root, "log", "--no-merges", "-n", LogLimit.ToString(), "--date=short", "--numstat", "--format=%x1e%h%x1f%an%x1f%ad%x1f%s", "--", ".");
        if (output is null) return new Hotspots(false, "Git couldn't read the history.", 0, 0, null, null, false, []);
        var stats = new Dictionary<string, (int Commits, int Churn, Dictionary<string, int> Authors, string Last)>(StringComparer.OrdinalIgnoreCase);
        var commits = 0;
        string? first = null, last = null;
        var people = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in output.Split('\x1e').Skip(1))
        {
            var rows = record.Split('\n');
            var header = rows[0].Split('\x1f');
            if (header.Length < 3) continue;
            var (author, date) = (header[1], header[2]);
            commits++;
            people.Add(author);
            last ??= date;
            first = date;
            foreach (var row in rows.Skip(1))
            {
                var parts = row.Split('\t');
                if (parts.Length < 3) continue;
                var file = System.Text.RegularExpressions.Regex.Replace(parts[2], @"\{[^}]*? => ([^}]*)\}", "$1");
                if (file.Contains(" => ")) file = file[(file.IndexOf(" => ", StringComparison.Ordinal) + 4)..];
                file = file.Replace("//", "/");
                if (repo.Prefix.Length > 0 && file.StartsWith(repo.Prefix, StringComparison.OrdinalIgnoreCase)) file = file[repo.Prefix.Length..];
                if (!known.Contains(file)) continue;
                var entry = stats.TryGetValue(file, out var e) ? e : (Commits: 0, Churn: 0, Authors: new Dictionary<string, int>(StringComparer.Ordinal), Last: date);
                entry.Authors[author] = entry.Authors.GetValueOrDefault(author) + 1;
                stats[file] = (entry.Commits + 1, entry.Churn + (int.TryParse(parts[0], out var a) ? a : 0) + (int.TryParse(parts[1], out var d) ? d : 0), entry.Authors, entry.Last);
            }
        }
        var files = stats.OrderByDescending(s => s.Value.Commits).ThenByDescending(s => s.Value.Churn).ThenBy(s => s.Key, StringComparer.Ordinal)
            .Take(HotspotLimit).Select(s => new Hotspot(s.Key, s.Value.Commits, s.Value.Churn, Top(s.Value.Authors), s.Value.Last)).ToList();
        var value = new Hotspots(true, null, commits, people.Count, first, last, commits >= LogLimit, files);
        lock (Gate) _cached = (key, value);
        return value;
    }

    public static NodeHistory For(GraphIndex index, string id)
    {
        string? file;
        int start, end;
        var wholeFile = false;
        if (index.Types.TryGetValue(id, out var type)) (file, start, end) = (type.File, type.Line, type.EndLine);
        else if (index.Members.TryGetValue(id, out var member)) (file, start, end, wholeFile) = (member.File, member.Line, member.EndLine, member.Kind == "top-level statements");
        else throw new ExplainException("Unknown type or member.", 404);
        var root = index.Graph.Root;
        if (Repo(root) is null) return new NodeHistory(false, "This folder isn't a Git repository, or Git isn't installed.", [], [], null);
        if (file is null) return new NodeHistory(true, null, [], [], "It has no source file.");
        var relative = ProjectFiles.Relative(root, file);
        string[] args = wholeFile
            ? ["log", "--no-merges", "-n", "8", "--date=short", "--format=%h%x1f%an%x1f%ad%x1f%s", "--follow", "--", relative]
            : ["log", "--no-merges", "-n", "8", "--date=short", "--format=%h%x1f%an%x1f%ad%x1f%s", "-s", "-L", $"{start},{Math.Max(start, end)}:{relative}"];
        var output = Git(root, args);
        if (output is null) return new NodeHistory(true, null, [], [], "No commits yet: this code may be new or not committed.");
        var commits = output.Split('\n').Where(l => l.Contains('\x1f')).Select(l => l.Split('\x1f'))
            .Where(p => p.Length >= 4).Select(p => new CommitInfo(p[0], p[1], p[2], p[3])).ToList();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var commit in commits) counts[commit.Author] = counts.GetValueOrDefault(commit.Author) + 1;
        return new NodeHistory(true, null, commits, Top(counts), commits.Count == 0 ? "No commits yet: this code may be new or not committed." : null);
    }
}
