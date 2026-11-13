using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeToLife.Runtime;

namespace CodeToLife.Insights;

/// <summary>One line of a stack trace, mapped back to the graph when it's code from this codebase.</summary>
public sealed record TraceFrame(string Raw, string Method, string? File, int? Line, string? TypeId, string? MemberId)
{
    public bool IsUserCode => TypeId is not null;
}

public sealed record TraceException(string Type, string? Message, IReadOnlyList<TraceFrame> Frames);

/// <summary>
/// A parsed stack trace.
/// </summary>
/// <param name="Exceptions">Outermost first; inner exceptions (the causes) follow.</param>
/// <param name="Stack">Every frame in run order, innermost (where it was thrown) first: the path to draw.</param>
public sealed record ParsedTrace(IReadOnlyList<TraceException> Exceptions, IReadOnlyList<TraceFrame> Stack)
{
    public string Title => Exceptions.Count == 0 ? "Stack trace"
        : Exceptions.Count == 1 ? Exceptions[0].Type
        : $"{Exceptions[0].Type} ← {Exceptions[^1].Type}";
}

/// <summary>Failures from a log that share a cause: same exception types, same frames.</summary>
public sealed record TraceGroup(string Key, string Title, string? Message, int Count, int FirstLine, int LastLine, ParsedTrace Sample);

public sealed record LogScan(int Lines, int Failures, IReadOnlyList<TraceGroup> Groups, IReadOnlyDictionary<string, int> TypeHits);

/// <summary>
/// Reads .NET stack traces the way they appear in exceptions, console output and logs, and maps
/// every frame back to the code: <c>at Shop.OrderService.CheckoutAsync(Order order) in …:line 31</c>
/// becomes <c>OrderService.CheckoutAsync</c>, lambdas and nested classes included.
/// </summary>
public static partial class TraceParser
{
    public static ParsedTrace Parse(string text, FrameMapper mapper)
    {
        var exceptions = new List<(string Type, string? Message, List<TraceFrame> Frames)>();
        // .NET prints the outer exception, then "---> Inner: …" for each cause, then the innermost
        // frames, then "--- End of inner exception stack trace ---" and the frames of the one outside it.
        var open = new Stack<int>();
        int? current = null;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (FrameLine().Match(line) is { Success: true } frame)
            {
                if (current is null) { exceptions.Add(("", null, [])); current = 0; open.Push(0); }
                exceptions[current.Value].Frames.Add(MapFrame(line, frame, mapper));
                continue;
            }
            if (line.StartsWith("--- End of inner exception stack trace", StringComparison.Ordinal))
            {
                if (open.Count > 1) { open.Pop(); current = open.Peek(); }
                continue;
            }
            if (line.StartsWith("---", StringComparison.Ordinal) && !line.StartsWith("--->", StringComparison.Ordinal)) continue; // "--- End of stack trace from previous location ---"

            var inner = line.StartsWith("--->", StringComparison.Ordinal);
            var header = Header(inner ? line[4..].Trim() : line);
            if (header is null) continue;
            if (inner && current is not null)
            {
                exceptions.Add((header.Value.Type, header.Value.Message, []));
                current = exceptions.Count - 1;
                open.Push(current.Value);
            }
            else if (current is null || exceptions[current.Value].Frames.Count == 0 && exceptions[current.Value].Type == "")
            {
                if (current is null) { exceptions.Add((header.Value.Type, header.Value.Message, [])); current = 0; open.Push(0); }
                else exceptions[current.Value] = (header.Value.Type, header.Value.Message, exceptions[current.Value].Frames);
            }
            else if (exceptions[current.Value].Frames.Count == 0)
            {
                // A message that ran on to a second line, or an aggregate's listing: keep the first header.
                continue;
            }
            else break; // a second, unrelated trace: Parse handles one at a time
        }

        var result = exceptions.Select(e => new TraceException(e.Type.Length == 0 ? "Stack trace" : e.Type, e.Message, e.Frames)).ToList();
        // Run order: the innermost exception's frames are deepest; each outer one continues from its catch.
        var stack = new List<TraceFrame>();
        foreach (var e in result.AsEnumerable().Reverse())
            foreach (var f in e.Frames)
                if (stack.Count == 0 || stack[^1].Raw != f.Raw) stack.Add(f);
        return new ParsedTrace(result, stack);
    }

    static (string Type, string? Message)? Header(string line)
    {
        foreach (var prefix in new[] { "Unhandled exception.", "Unhandled Exception:", "Exception:", "Error:" })
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { line = line[prefix.Length..].Trim(); break; }
        var anchored = AnchoredHeader().Match(line);
        if (anchored.Success) return (anchored.Groups["type"].Value, NullIfEmpty(anchored.Groups["msg"].Value));
        var loose = LooseHeader().Match(line);
        return loose.Success ? (loose.Groups["type"].Value, NullIfEmpty(loose.Groups["msg"].Value)) : null;
    }

    static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static TraceFrame MapFrame(string line, Match frame, FrameMapper mapper)
    {
        var method = frame.Groups["method"].Value.Trim();
        var file = frame.Groups["file"].Success ? frame.Groups["file"].Value.Trim() : null;
        int? lineNumber = frame.Groups["line"].Success ? int.Parse(frame.Groups["line"].Value) : null;

        GraphLocation? location = null;
        if (SplitMethod(method) is var (typeName, methodName))
        {
            foreach (var candidate in NestedCandidates(typeName))
            {
                location = mapper.FromRuntime(candidate, methodName);
                if (location is not null) break;
            }
        }
        // The file is the best guide for lambdas and when names don't resolve, but only if it's this checkout.
        if ((location?.MemberId is null) && file is not null && lineNumber is { } l && SafeExists(file) && mapper.FromSource(file, l) is { } fromSource)
            location = fromSource;
        return new TraceFrame(line, method, file, lineNumber, location?.TypeId, location?.MemberId);
    }

    static bool SafeExists(string file)
    {
        try { return File.Exists(file); } catch { return false; }
    }

    /// <summary><c>Shop.Order..ctor</c> → (<c>Shop.Order</c>, <c>.ctor</c>); generic method arguments dropped.</summary>
    internal static (string Type, string Method)? SplitMethod(string method)
    {
        // Drop generic method arguments: Add[T] → Add.
        method = GenericMethodArgs().Replace(method, "");
        var depth = 0;
        var split = -1;
        for (var i = 0; i < method.Length; i++)
        {
            if (method[i] == '<') depth++;
            else if (method[i] == '>') depth--;
            else if (method[i] == '.' && depth == 0) split = i;
        }
        if (split <= 0) return null;
        var type = method[..split];
        var name = method[(split + 1)..];
        if (type.EndsWith('.')) { type = type[..^1]; name = "." + name; } // .ctor / .cctor
        return (type, name);
    }

    /// <summary>
    /// Stack traces print nested types with dots (<c>Program.&lt;&gt;c</c>), the CLR uses <c>+</c>:
    /// try the plain name, then turn the last dots into pluses one at a time.
    /// </summary>
    static IEnumerable<string> NestedCandidates(string typeName)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < typeName.Length; i++)
        {
            if (typeName[i] == '<') depth++;
            else if (typeName[i] == '>') depth--;
            else if (typeName[i] == '.' && depth == 0) { parts.Add(typeName[start..i]); start = i + 1; }
        }
        parts.Add(typeName[start..]);
        for (var nested = 0; nested < parts.Count; nested++)
            yield return string.Join('.', parts.Take(parts.Count - nested)) + string.Concat(parts.Skip(parts.Count - nested).Select(p => "+" + p));
    }

    /// <summary>Finds every failure in a log (plain text or JSON lines) and groups the ones with the same cause.</summary>
    public static LogScan ScanLog(string text, FrameMapper mapper, int maxGroups = 50)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<(int Line, string Text, string? Message)>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('{') && JsonException(trimmed) is { } fromJson)
            {
                blocks.Add((i + 1, fromJson.Trace, fromJson.Message));
                continue;
            }
            if (Header(trimmed) is null || i + 1 >= lines.Length) continue;
            // A header, then frames: collect until the frames stop.
            var block = new StringBuilder(line).Append('\n');
            var j = i + 1;
            var frames = 0;
            for (; j < lines.Length; j++)
            {
                var next = lines[j].Trim();
                if (FrameLine().IsMatch(next)) { frames++; block.Append(next).Append('\n'); }
                else if (next.StartsWith("---", StringComparison.Ordinal)) block.Append(next).Append('\n');
                else if (frames == 0 && next.Length > 0 && j - i < 4) block.Append(next).Append('\n'); // message continued, or a "---> inner" header
                else break;
            }
            if (frames == 0) continue;
            blocks.Add((i + 1, block.ToString(), null));
            i = j - 1;
        }

        var groups = new Dictionary<string, (ParsedTrace Sample, string? Message, int Count, int First, int Last)>(StringComparer.Ordinal);
        foreach (var (line, blockText, message) in blocks)
        {
            var parsed = Parse(blockText, mapper);
            var key = string.Join(" ← ", parsed.Exceptions.Select(e => e.Type)) + " @ "
                      + string.Join(" | ", parsed.Stack.Take(6).Select(f => f.MemberId ?? f.Method));
            groups[key] = groups.TryGetValue(key, out var g)
                ? (g.Sample, g.Message, g.Count + 1, g.First, line)
                : (parsed, message ?? parsed.Exceptions.FirstOrDefault()?.Message, 1, line, line);
        }

        var ordered = groups
            .Select(g => new TraceGroup(Hash(g.Key), g.Value.Sample.Title, g.Value.Message, g.Value.Count, g.Value.First, g.Value.Last, g.Value.Sample))
            .OrderByDescending(g => g.Count).ThenBy(g => g.FirstLine)
            .Take(maxGroups)
            .ToList();
        var typeHits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in ordered)
            foreach (var typeId in group.Sample.Stack.Select(f => f.TypeId).OfType<string>().Distinct())
                typeHits[typeId] = typeHits.GetValueOrDefault(typeId) + group.Count;
        return new LogScan(lines.Length, blocks.Count, ordered, typeHits);
    }

    static string Hash(string key) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();

    static readonly string[] ExceptionFields = ["exception", "@x", "ex", "stack_trace", "stacktrace", "error"];
    static readonly string[] MessageFields = ["message", "@m", "msg", "@mt", "rendered_message"];

    /// <summary>Serilog, Microsoft JSON console, NLog and ECS logs keep the exception in a field.</summary>
    static (string Trace, string? Message)? JsonException(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var trace = Find(doc.RootElement, ExceptionFields, mustLookLikeTrace: true);
            if (trace is null) return null;
            return (trace, Find(doc.RootElement, MessageFields, mustLookLikeTrace: false));
        }
        catch (JsonException) { return null; }
    }

    static string? Find(JsonElement element, string[] names, bool mustLookLikeTrace, int depth = 0)
    {
        if (depth > 4 || element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name.ToLowerInvariant();
            if (names.Contains(name) && property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString() is { } value && (!mustLookLikeTrace || value.Contains(" at ", StringComparison.Ordinal)))
                return value;
            if (property.Value.ValueKind == JsonValueKind.Object && Find(property.Value, names, mustLookLikeTrace, depth + 1) is { } nested)
                return nested;
        }
        return null;
    }

    [GeneratedRegex(@"^at (?<method>[^(]+?)\s*\((?<args>[^)]*)\)(?:\s+in (?<file>.+?):line (?<line>\d+))?\s*$")]
    private static partial Regex FrameLine();

    [GeneratedRegex(@"^(?<type>[A-Za-z_][\w`+]*(?:\.[A-Za-z_][\w`+]*)*(?:Exception|Error|Fault))(?:\s*:\s*(?<msg>.*))?$")]
    private static partial Regex AnchoredHeader();

    [GeneratedRegex(@"(?<![\w.])(?<type>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+Exception)\b(?::\s*(?<msg>.*))?")]
    private static partial Regex LooseHeader();

    [GeneratedRegex(@"\[[^\[\]]*\](?=$|\.)")]
    private static partial Regex GenericMethodArgs();
}
