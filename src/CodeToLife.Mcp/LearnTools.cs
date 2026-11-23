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

    static string Name(GraphIndex index, string name, out string? id)
    {
        var hits = index.Resolve(name);
        id = hits.Count == 1 ? hits[0] : null;
        return hits.Count == 1 ? "" : hits.Count == 0 ? $"Nothing is called '{name}'." : $"'{name}' could mean {hits.Count} things; pass one of these ids:\n" + string.Join('\n', hits.Take(15));
    }

    [McpServerTool(Name = "how_to", ReadOnly = true), Description(
        "Step-by-step recipes for common changes in this solution (run it, add a page or endpoint, a setting, a service, a database table, " +
        "a partial view or component, a test), worked out from the patterns it already follows. Each step points at an example file and line.")]
    public async Task<string> HowTo()
    {
        var code = await host.ReadyAsync();
        var sb = new StringBuilder();
        foreach (var recipe in Recipes.For(code.Index))
        {
            sb.AppendLine($"## {recipe.Title}").AppendLine(recipe.Summary);
            for (var i = 0; i < recipe.Steps.Count; i++)
                sb.AppendLine($"{i + 1}. {recipe.Steps[i].Text}{(recipe.Steps[i].File is { } f ? $" ({f}:{recipe.Steps[i].Line})" : "")}");
            sb.AppendLine();
        }
        return sb.Length == 0 ? "No common patterns found yet." : sb.ToString();
    }

    [McpServerTool(Name = "settings", ReadOnly = true), Description(
        "Every setting the app reads: appsettings.json keys (and per-environment overrides) and environment variables, with the default, " +
        "the environment variable that overrides it, where it is set for local runs, and every place the code reads it. Secrets are not shown.")]
    public async Task<string> SettingsMap([Description("Only settings whose key, variable or description contains this text.")] string? query = null)
    {
        var code = await host.ReadyAsync();
        var map = Settings.For(code.Index);
        var list = map.Settings.Where(s => query is null || (s.Key + " " + s.Env + " " + s.Doc).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var sb = new StringBuilder($"{list.Count} settings{(map.Schema is null ? "" : ", main file " + map.Schema)}.\n");
        foreach (var s in list)
        {
            sb.AppendLine().AppendLine($"## {s.Key}{(s.Env is not null && s.Env != s.Key ? $" (env {s.Env})" : "")}{(s.Varies ? ", differs by environment" : "")}");
            if (s.Doc.Length > 0) sb.AppendLine(s.Doc);
            sb.AppendLine($"Default: {(s.Sensitive ? "(secret)" : s.Default.Length == 0 ? "(none)" : s.Default)}{(s.File is null ? "" : $" ({s.File}:{s.Line})")}");
            foreach (var (environment, value) in s.Environments) sb.AppendLine($"In {environment}: {value ?? "(secret)"}");
            foreach (var set in s.SetIn) sb.AppendLine($"Set in {set.File}:{set.Line}{(set.Value is null ? "" : " = " + set.Value)}");
            foreach (var use in s.Uses) sb.AppendLine($"Read at {use.File}:{use.Line}{(use.Id is null ? "" : " in " + code.Index.Display(use.Id))}");
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "templates", ReadOnly = true), Description(
        "The Razor views and components: the layout each uses (directly or through _ViewStart), the partials and components it renders, " +
        "the sections it fills, what uses it, and the actions, handlers and pages that render it.")]
    public async Task<string> Templates()
    {
        var code = await host.ReadyAsync();
        var map = RazorMap.For(code.Index);
        if (map.Templates.Count == 0) return "No Razor views or components found.";
        var sb = new StringBuilder();
        foreach (var t in map.Templates)
        {
            sb.AppendLine($"## {t.File} ({t.Kind})");
            if (t.Model is not null) sb.AppendLine($"Model: {t.Model}");
            if (t.Extends is { } layout) sb.AppendLine($"Layout: {layout.File ?? layout.Name + " (" + layout.External + ")"}");
            foreach (var p in t.Includes) sb.AppendLine($"Renders partial {p.File ?? p.Name}");
            foreach (var c in t.Imports) sb.AppendLine($"Uses component {c.File ?? c.Name}");
            if (t.Blocks.Count > 0) sb.AppendLine("Sections: " + string.Join(", ", t.Blocks));
            foreach (var u in t.UsedBy) sb.AppendLine($"{u.File} {u.How}");
            foreach (var r in t.RenderedBy) sb.AppendLine($"Rendered by {r.Name}{(r.Routes.Count > 0 ? " for " + string.Join(", ", r.Routes) : "")}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "test_examples", ReadOnly = true), Description(
        "The test methods that call a type or member, with their source, as worked examples of how it is used and what it should return.")]
    public async Task<string> Examples([Description("A type or member: 'OrderService', 'OrderService.CheckoutAsync' or an id.")] string name)
    {
        var code = await host.ReadyAsync();
        var error = Name(code.Index, name, out var id);
        if (id is null) return error;
        var result = TestExamples.For(code.Index, id);
        if (result.Examples.Count == 0) return $"No tests call {code.Index.Display(id)}.";
        var sb = new StringBuilder();
        foreach (var e in result.Examples) sb.AppendLine($"## {e.Title} ({e.File}:{e.Start})").AppendLine("```csharp").AppendLine(e.Code).AppendLine("```");
        return sb.ToString();
    }

    [McpServerTool(Name = "history", ReadOnly = true), Description(
        "Git history. With a name: the commits that last changed that type or member and who made them. Without: the most changed files (hotspots).")]
    public async Task<string> History([Description("A type or member, or leave empty for the hotspots.")] string? name = null)
    {
        var code = await host.ReadyAsync();
        var sb = new StringBuilder();
        if (string.IsNullOrWhiteSpace(name))
        {
            var spots = GitHistory.For(code.Graph);
            if (!spots.Available) return spots.Reason ?? "No history.";
            sb.AppendLine($"{spots.Commits} commits by {spots.Contributors} people{(spots.Limited ? " (latest only)" : "")}, {spots.First} to {spots.Last}.");
            foreach (var f in spots.Files) sb.AppendLine($"- {f.File}: {f.Commits} commit{(f.Commits == 1 ? "" : "s")}, {f.Churn} lines changed, last {f.Last}, mostly {string.Join(", ", f.Authors.Select(a => a.Name))}");
            return sb.ToString();
        }
        var error = Name(code.Index, name, out var id);
        if (id is null) return error;
        var history = GitHistory.For(code.Index, id);
        if (!history.Available) return history.Reason ?? "No history.";
        if (history.Commits.Count == 0) return history.Note ?? "No commits found.";
        foreach (var c in history.Commits) sb.AppendLine($"- {c.Date} {c.Hash} {c.Author}: {c.Subject}");
        return sb.ToString();
    }

    [McpServerTool(Name = "onboarding_guide", ReadOnly = true), Description(
        "A Markdown onboarding guide for the solution: projects, tools, how to run it, folders, the Start here tour, pages, settings, " +
        "how-to recipes, class explanations and hotspots. Suitable for committing as ONBOARDING.md.")]
    public async Task<string> Guide()
    {
        var code = await host.ReadyAsync();
        return OnboardingGuide.Build(code.Index);
    }
}
