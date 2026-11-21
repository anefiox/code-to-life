using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeToLife.Analysis;

namespace CodeToLife.Insights;

/// <summary>A request that can't be served, with the HTTP status a host should answer with.</summary>
public sealed class ExplainException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>The code of one type or member, as shown in the panel and sent to an AI.</summary>
/// <param name="File">Relative to the project root, with forward slashes.</param>
public sealed record SourceSpan(string Id, string Name, string Kind, string File, string FullPath, int Start, int End, int Total, string Code, string Hash);

/// <param name="Stale">The code has changed since the explanation was written.</param>
public sealed record Explanation(string Summary, bool Stale, string? Updated);

public sealed record ExplanationEntry(string Summary, string Hash, string Updated);

public sealed record QueueItem(string Id, string Name, string Kind, bool Stale, int Links);

/// <summary>
/// Plain-English explanations of types and members, written by a person, Claude (through the MCP
/// server) or an AI the app calls, and kept in <c>.codetolife/explanations.json</c> beside the
/// code so they can be committed. Each remembers a hash of the code it describes, so it is
/// flagged as stale once that code changes.
/// </summary>
public static class Explanations
{
    public const int SummaryLimit = 2000;
    const long SourceLimit = 1024 * 1024;
    const long StoreLimit = 2 * 1024 * 1024;
    static readonly Lock Writing = new();

    public const string Guidance = "Write for someone new to coding and new to this project. In 2 to 4 short sentences, " +
        "say what this class or method is for, when it runs, and how it fits with the rest of the app. Name the main " +
        "things it uses or is used by. Explain any framework term in passing. Avoid restating the code line by line.";

    // A file people read and diff, not HTML: keep quotes, accents and + as they are.
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    sealed record Store(int Version, SortedDictionary<string, ExplanationEntry> Entries);

    public static string PathFor(string root) => Path.Combine(root, ".codetolife", "explanations.json");

    public static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code.ReplaceLineEndings("\n"))))[..16];

    /// <summary>The source of a type or member in the graph, and nothing else: ids are the only way in.</summary>
    public static SourceSpan ReadSource(GraphIndex index, string id)
    {
        string? file;
        int start, end;
        string name, kind;
        if (index.Types.TryGetValue(id, out var type))
            (file, start, end, name, kind) = (type.File, type.Line, type.EndLine, type.Name, type.Kind);
        else if (index.Members.TryGetValue(id, out var member))
            (file, start, end, name, kind) = (member.File, member.Line, member.EndLine, index.Display(id), member.Kind);
        else throw new ExplainException("Unknown type or member.", 404);
        if (file is null || !File.Exists(file)) throw new ExplainException("Its source file can't be found.", 404);
        if (new FileInfo(file).Length > SourceLimit) throw new ExplainException("The file is too large to show.", 413);

        var lines = File.ReadAllLines(file);
        if (kind == "top-level statements") (start, end) = (1, lines.Length);
        start = Math.Clamp(start, 1, Math.Max(1, lines.Length));
        end = Math.Clamp(end, start, Math.Max(start, lines.Length));
        var code = string.Join('\n', lines.Skip(start - 1).Take(end - start + 1));
        var relative = Path.GetRelativePath(index.Graph.Root, file).Replace('\\', '/');
        return new SourceSpan(id, name, kind, relative, file, start, end, lines.Length, code, Hash(code));
    }

    static string StorePath(string root)
    {
        var dir = Path.Combine(root, ".codetolife");
        if (File.Exists(dir)) throw new ExplainException(".codetolife must be a folder.");
        if (Directory.Exists(dir) && new DirectoryInfo(dir).LinkTarget is not null) throw new ExplainException(".codetolife must be a local folder, not a link.");
        var file = PathFor(root);
        if (Directory.Exists(file)) throw new ExplainException("explanations.json must be a file.");
        if (File.Exists(file))
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is not null) throw new ExplainException("explanations.json must be a regular file, not a link.");
            if (info.Length > StoreLimit) throw new ExplainException("explanations.json is too large.");
        }
        return file;
    }

    public static IReadOnlyDictionary<string, ExplanationEntry> Load(string root)
    {
        var file = StorePath(root);
        if (!File.Exists(file)) return new Dictionary<string, ExplanationEntry>();
        Store? store;
        try { store = JsonSerializer.Deserialize<Store>(File.ReadAllText(file), Json); }
        catch (JsonException) { throw new ExplainException("explanations.json contains invalid JSON."); }
        if (store is not { Version: 1, Entries: not null }) throw new ExplainException("Expected a version 1 explanations file.");
        return store.Entries;
    }

    public static ExplanationEntry Save(GraphIndex index, string id, string summary)
    {
        summary = summary?.Trim() ?? "";
        if (summary.Length is 0 or > SummaryLimit) throw new ExplainException($"Write a summary of 1 to {SummaryLimit} characters.");
        var source = ReadSource(index, id);
        var root = index.Graph.Root;
        lock (Writing)
        {
            var entries = new SortedDictionary<string, ExplanationEntry>(Load(root).ToDictionary(), StringComparer.Ordinal);
            var entry = new ExplanationEntry(summary, source.Hash, DateTime.UtcNow.ToString("O"));
            entries[id] = entry;
            var encoded = JsonSerializer.Serialize(new Store(1, entries), Json) + "\n";
            if (Encoding.UTF8.GetByteCount(encoded) > StoreLimit) throw new ExplainException("explanations.json would be too large.", 413);
            var file = StorePath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = Path.Combine(Path.GetDirectoryName(file)!, $".explanations-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temp, encoded, new UTF8Encoding(false));
                File.Move(temp, file, overwrite: true);
            }
            finally { File.Delete(temp); }
            return entry;
        }
    }

    /// <summary>The explanations for what's in the graph now, each flagged as stale when its code has changed.</summary>
    public static IReadOnlyDictionary<string, Explanation> For(GraphIndex index)
    {
        var result = new Dictionary<string, Explanation>(StringComparer.Ordinal);
        foreach (var (id, entry) in Load(index.Graph.Root))
        {
            if (!index.Types.ContainsKey(id) && !index.Members.ContainsKey(id) || string.IsNullOrWhiteSpace(entry.Summary)) continue;
            bool stale;
            try { stale = ReadSource(index, id).Hash != entry.Hash; }
            catch (Exception ex) when (ex is ExplainException or IOException or UnauthorizedAccessException) { stale = true; }
            result[id] = new Explanation(entry.Summary, stale, entry.Updated);
        }
        return result;
    }

    /// <summary>What still needs explaining, most useful first: types before members, hubs before leaves, never tests.</summary>
    public static IReadOnlyList<QueueItem> Queue(GraphIndex index)
    {
        var done = For(index);
        var tests = index.Graph.Projects.Where(p => p.OutputKind == "test").Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var items = new List<(QueueItem Item, int Order)>();
        foreach (var type in index.Graph.Types)
        {
            if (tests.Contains(type.Project) || type.File is null || type.Kind is "delegate") continue;
            if (!done.TryGetValue(type.Id, out var existing) || existing.Stale)
                items.Add((new QueueItem(type.Id, type.Name, type.Kind, existing is not null, type.Metrics.IncomingCount + type.Metrics.OutgoingCount), 0));
            foreach (var member in type.Members)
            {
                if (member.Kind is not ("method" or "constructor" or "primary constructor" or "top-level statements") || member.File is null || member.Complexity == 0) continue;
                if (done.TryGetValue(member.Id, out var memberDone) && !memberDone.Stale) continue;
                var links = index.CallsFrom(member.Id).Count + index.CallsTo(member.Id).Count;
                items.Add((new QueueItem(member.Id, index.Display(member.Id), member.Kind, memberDone is not null, links), 1));
            }
        }
        return items.OrderBy(i => i.Order).ThenByDescending(i => i.Item.Links).ThenBy(i => i.Item.Id, StringComparer.Ordinal)
            .Select(i => i.Item).ToList();
    }
}
