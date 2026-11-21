using System.ComponentModel;
using System.Text;
using CodeToLife.Insights;
using ModelContextProtocol.Server;

namespace CodeToLife.Mcp;

/// <summary>Plain-English explanations for the Code to Life learning panel, written by the assistant.</summary>
[McpServerToolType]
public sealed class LearnTools(CodebaseHost host)
{
    const int CodeLimit = 12000;

    [McpServerTool(Name = "explain_next", ReadOnly = true), Description(
        "List types and methods that still need a plain-English explanation for the Code to Life learning panel, " +
        "most connected first, with their source. Write each one with save_explanation. Stale items have changed since they were explained.")]
    public async Task<string> ExplainNext(
        [Description("How many to return (1 to 20).")] int limit = 5,
        [Description("How many to skip, to page through the list.")] int offset = 0)
    {
        var code = await host.ReadyAsync();
        var index = code.Index;
        limit = Math.Clamp(limit, 1, 20);
        offset = Math.Max(0, offset);
        var queue = Explanations.Queue(index);
        var sb = new StringBuilder();
        sb.AppendLine($"How to write them: {Explanations.Guidance}");
        sb.AppendLine().AppendLine($"{queue.Count} still to explain; showing {offset + 1} to {Math.Min(queue.Count, offset + limit)}.");
        foreach (var item in queue.Skip(offset).Take(limit))
        {
            SourceSpan source;
            try { source = Explanations.ReadSource(index, item.Id); }
            catch (ExplainException ex) { sb.AppendLine().AppendLine($"## {item.Name}: {ex.Message}"); continue; }
            var text = source.Code.Length > CodeLimit ? source.Code[..CodeLimit] + "\n… (truncated)" : source.Code;
            sb.AppendLine().AppendLine($"## {item.Name} ({item.Kind}{(item.Stale ? ", stale" : "")}, {item.Links} links)");
            sb.AppendLine($"id: {item.Id}");
            sb.AppendLine($"{source.File}:{source.Start}-{source.End}");
            sb.AppendLine("```csharp").AppendLine(text).AppendLine("```");
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "save_explanation", Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Save a plain-English explanation of one type or method. It appears in the Code to Life panel when that item is " +
        "selected, and is stored in .codetolife/explanations.json in the project.")]
    public async Task<string> SaveExplanation(
        [Description("A type or member: 'OrderService', 'OrderService.CheckoutAsync' or an id from explain_next.")] string name,
        [Description("The explanation: 2 to 4 short sentences, up to 2000 characters.")] string summary)
    {
        var code = await host.ReadyAsync();
        var index = code.Index;
        var hits = index.Resolve(name);
        if (hits.Count != 1) return hits.Count == 0 ? $"Nothing is called '{name}'. Use an id from explain_next." : $"'{name}' could mean {hits.Count} things; pass one of these ids:\n" + string.Join('\n', hits.Take(15));
        try
        {
            var entry = Explanations.Save(index, hits[0], summary);
            return $"Saved the explanation of {index.Display(hits[0])} ({entry.Updated}).";
        }
        catch (ExplainException ex) { return ex.Message; }
    }
}
