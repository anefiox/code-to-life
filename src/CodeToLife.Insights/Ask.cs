using System.Text;
using System.Text.RegularExpressions;

namespace CodeToLife.Insights;

public sealed record AskSource(string Id, string Name, string? File, string Kind);

public sealed record AskAnswer(string? Answer, IReadOnlyList<AskSource> Sources);

/// <summary>
/// "Ask the codebase": finds the classes and methods that best match a question, using names, files,
/// addresses and saved explanations, and builds a prompt that answers from them with references the page can link.
/// </summary>
public static partial class Ask
{
    const int SourceLimit = 10;
    const int ContextLimit = 24000;
    const int CodeLines = 60;

    static readonly HashSet<string> StopWords = new(("a an and are as at be by can do does for from how i in is it its of on or so that the their there this to " +
        "was what when where which who why will with you your me my we our get gets use used uses work works happen happens code app file files " +
        "class classes method methods function functions").Split(' '), StringComparer.Ordinal);

    public const string Guidance = "You answer questions about one codebase for a developer who is new to it. Use only the context you are " +
        "given. Whenever you mention a class, method or file, write its name or path in backticks exactly as it appears in the context, for " +
        "example `OrderService`, `OrderService.CheckoutAsync` or `Services/OrderService.cs`, so it can be linked. If the context does not " +
        "answer the question, say so and suggest where to look. Keep the answer under 200 words, in plain English.";

    static HashSet<string> Words(string? text) =>
        Split().Split(Camel().Replace(text ?? "", "$1 $2").ToLowerInvariant())
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .Select(w => w.Length > 4 && w.EndsWith("es", StringComparison.Ordinal) ? w[..^2] : w.Length > 4 && w.EndsWith('s') ? w[..^1] : w)
            .ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<string> Sources(GraphIndex index, string? question)
    {
        if (question is null || question.Trim().Length < 3 || question.Length > 500) throw new ExplainException("Ask a question of 3 to 500 characters.");
        var wanted = Words(question);
        if (wanted.Count == 0) throw new ExplainException("Ask about something in the code, such as a page, a setting or a feature.");
        var explanations = Explanations.For(index);
        var graph = index.Graph;
        var tests = graph.Projects.Where(p => p.OutputKind == "test").Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var routes = (graph.Routes ?? []).GroupBy(r => r.Handler, StringComparer.Ordinal).ToDictionary(g => g.Key, g => string.Join(' ', g.Select(r => r.Path)), StringComparer.Ordinal);
        var scored = new List<(string Id, double Score)>();
        void Score(string id, string name, string? file, bool isType)
        {
            var nameWords = Words(name);
            var fileWords = Words(file is null ? "" : ProjectFiles.Relative(graph.Root, file));
            var about = Words(explanations.GetValueOrDefault(id)?.Summary);
            var routeWords = Words(routes.GetValueOrDefault(id));
            double score = 0;
            foreach (var word in wanted)
            {
                if (nameWords.Contains(word)) score += 4;
                else if (fileWords.Contains(word)) score += 2;
                if (routeWords.Contains(word)) score += 3;
                if (about.Contains(word)) score += 1.5;
            }
            if (score > 0) scored.Add((id, score + (explanations.ContainsKey(id) ? 0.5 : 0) + (isType ? 0.25 : 0)));
        }
        foreach (var type in graph.Types)
        {
            if (tests.Contains(type.Project) || type.File is null) continue;
            Score(type.Id, type.Name, type.File, true);
            foreach (var member in type.Members)
                if (member.Kind is "method" or "constructor" or "top-level statements") Score(member.Id, type.Name + " " + member.Name, member.File, false);
        }
        return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Id, StringComparer.Ordinal).Take(SourceLimit).Select(s => s.Id).ToList();
    }

    public static AskSource Describe(GraphIndex index, string id)
    {
        var isType = index.Types.TryGetValue(id, out var type);
        var file = isType ? type!.File : index.Members[id].File;
        return new AskSource(id, isType ? type!.Name : index.Display(id), ProjectFiles.RelativeOrNull(index.Graph.Root, file), isType ? type!.Kind : index.Members[id].Kind);
    }

    public static (IReadOnlyList<(string Role, string Content)> Messages, IReadOnlyList<AskSource> Sources) Prompt(GraphIndex index, string question)
    {
        var ids = Sources(index, question);
        var explanations = Explanations.For(index);
        var context = new StringBuilder();
        foreach (var id in ids)
        {
            var source = Describe(index, id);
            string code;
            try { code = string.Join('\n', Explanations.ReadSource(index, id).Code.Split('\n').Take(CodeLines)); }
            catch (ExplainException) { code = ""; }
            var block = new StringBuilder($"### {source.Name} in {source.File}\n");
            if (explanations.GetValueOrDefault(id) is { } e) block.Append("Explanation: ").Append(e.Summary).Append('\n');
            if (code.Length > 0) block.Append("```csharp\n").Append(code).Append("\n```\n");
            if (context.Length + block.Length > ContextLimit) break;
            context.Append(block).Append('\n');
        }
        var routes = (index.Graph.Routes ?? []).Take(40).Select(r => $"{(r.Method == "*" ? "ANY" : r.Method)} {r.Path} ({index.Display(r.Handler)})");
        var user = $"Question: {question.Trim()}\n\nAddresses the app answers: {string.Join("; ", routes).DefaultIfEmpty("none found")}\n\nThe most relevant code:\n\n{context}";
        return ([("system", Guidance), ("user", user)], ids.Select(id => Describe(index, id)).ToList());
    }

    static string DefaultIfEmpty(this string text, string fallback) => text.Length == 0 ? fallback : text;

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex Camel();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Split();
}
