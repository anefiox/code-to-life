using CodeToLife.Insights;

namespace CodeToLife;

/// <summary>The AI that writes explanations, when Code to Life was started with --ai.</summary>
public sealed record AiSettings(AiConfig? Config);

public sealed record AskRequest(string? Question);

/// <summary>The API for learning a codebase: explanations, the package glossary, the tour and page flows.</summary>
static class LearnApi
{
    public static void MapLearn(this RouteGroupBuilder api, Workspace workspace, AiSettings ai)
    {
        IResult NotReady() => Results.Problem(workspace.LastError ?? "Still analyzing.", statusCode: 503);
        // Explanations are written one at a time: a local model can only do one at once anyway.
        var asking = new SemaphoreSlim(1, 1);

        api.MapGet("/explanations", () => Guard(() => !workspace.IsLoaded ? NotReady() : Results.Ok(new
        {
            explanations = Explanations.For(workspace.Index),
            glossary = Glossary.ForProject(workspace.Graph),
            concepts = Concepts.For(workspace.Graph),
            ai = ai.Config is { } c ? new { label = c.Label, model = c.Model } : null,
        })));

        api.MapPost("/explain", async (string id, CancellationToken cancellationToken) =>
        {
            if (ai.Config is not { } config) return Results.NotFound(new { error = "Start Code to Life with --ai to write explanations here." });
            if (!workspace.IsLoaded) return NotReady();
            await asking.WaitAsync(cancellationToken);
            try
            {
                var (_, entry) = await AiClient.ExplainAsync(config, workspace.Index, id, cancellationToken: cancellationToken);
                return Results.Ok(new { key = id, entry.Summary, entry.Hash, entry.Updated });
            }
            catch (ExplainException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway); }
            finally { asking.Release(); }
        });

        api.MapGet("/learn", () => Guard(() => !workspace.IsLoaded ? NotReady() : Results.Ok(new
        {
            tour = new { steps = Learning.StartTour(workspace.Index) },
            pages = Learning.RouteFlows(workspace.Index),
            folderNotes = Folders.NotesFor(workspace.Graph),
        })));

        // ---- Onboarding helpers ----
        api.MapGet("/recipes", () => Guard(() => !workspace.IsLoaded ? NotReady() : Results.Ok(new { recipes = Recipes.For(workspace.Index) })));
        api.MapGet("/settings", () => Guard(() => !workspace.IsLoaded ? NotReady() : Results.Ok(Settings.For(workspace.Index))));
        api.MapGet("/templates", () => Guard(() => !workspace.IsLoaded ? NotReady() : Results.Ok(RazorMap.For(workspace.Index))));
        api.MapGet("/file", (string? file) => Guard(() => Results.Ok(ProjectFiles.ReadProjectFile(workspace.RootDirectory, file))));
        api.MapGet("/examples", (string id) => Guard(() => !workspace.IsLoaded ? NotReady() : Results.Ok(TestExamples.For(workspace.Index, id))));
        api.MapGet("/history", (string? id) => Guard(() => !workspace.IsLoaded ? NotReady()
            : id is null ? Results.Ok(GitHistory.For(workspace.Graph)) : Results.Ok(GitHistory.For(workspace.Index, id))));
        api.MapGet("/onboarding", () => Guard(() => !workspace.IsLoaded ? NotReady()
            : Results.Text(OnboardingGuide.Build(workspace.Index), "text/markdown; charset=utf-8")));
        api.MapPost("/ask", async (AskRequest request, CancellationToken cancellationToken) =>
        {
            if (!workspace.IsLoaded) return NotReady();
            try
            {
                if (ai.Config is not { } config)
                    return Results.Ok(new AskAnswer(null, Ask.Sources(workspace.Index, request.Question).Select(id => Ask.Describe(workspace.Index, id)).ToList()));
                var (messages, sources) = Ask.Prompt(workspace.Index, request.Question ?? "");
                await asking.WaitAsync(cancellationToken);
                try { return Results.Ok(new AskAnswer(await AiClient.AskAsync(config, messages, cancellationToken: cancellationToken, maxTokens: 1200), sources)); }
                finally { asking.Release(); }
            }
            catch (ExplainException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway); }
        });

        api.MapGet("/template", (string? file) => Guard(() => Results.Ok(Learning.ReadTemplate(workspace.RootDirectory, file))));
    }

    static IResult Guard(Func<IResult> action)
    {
        try { return action(); }
        catch (ExplainException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
    }
}

/// <summary>
/// <c>codetolife explain [&lt;path&gt;] --ai openrouter|URL [--ai-model M] [--limit N]</c>: writes
/// explanations for the types and methods that don't have one yet, most connected first.
/// </summary>
static class ExplainCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? path = null, aiChoice = null, model = null;
        var limit = 10;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ai" when i + 1 < args.Length: aiChoice = args[++i]; break;
                case "--ai-model" when i + 1 < args.Length: model = args[++i]; break;
                case "--limit" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out limit) || limit is < 1 or > 500) { Console.Error.WriteLine("--limit must be a whole number from 1 to 500."); return 2; }
                    break;
                case "-h" or "--help":
                    Console.WriteLine("""
                        Usage: codetolife explain [<path>] --ai openrouter|URL [--ai-model MODEL] [--limit N]

                        Writes plain-English explanations for up to N types and methods that don't have one
                        (default 10), most connected first, into <path>/.codetolife/explanations.json.
                        OpenRouter needs OPENROUTER_API_KEY; a URL is any OpenAI-compatible server, with its key,
                        if it needs one, in CODETOLIFE_AI_KEY.
                        """);
                    return 0;
                default:
                    if (args[i].StartsWith('-')) { Console.Error.WriteLine($"Unknown option {args[i]}. Try codetolife explain --help."); return 2; }
                    path ??= args[i];
                    break;
            }
        }
        AiConfig? config;
        try { config = AiConfig.From(aiChoice, model, Environment.GetEnvironmentVariable); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        if (config is null) { Console.Error.WriteLine("explain needs --ai openrouter or --ai <URL>."); return 2; }

        using var codebase = new Codebase();
        await codebase.OpenAsync(Path.GetFullPath(path ?? Environment.CurrentDirectory), watch: false);
        if (!codebase.IsLoaded) { Console.Error.WriteLine($"Couldn't analyze the code: {codebase.LastError}"); return 2; }
        var index = codebase.Index;
        var queue = Explanations.Queue(index);
        var batch = queue.Take(limit).ToList();
        Console.WriteLine($"Explaining {batch.Count} of {queue.Count} with {config}…");
        var failures = 0;
        for (var i = 0; i < batch.Count; i++)
        {
            var label = $"[{i + 1}/{batch.Count}] {batch[i].Name} ({batch[i].Kind})";
            try
            {
                await AiClient.ExplainAsync(config, index, batch[i].Id);
                Console.WriteLine(label);
            }
            catch (Exception ex) when (ex is ExplainException or InvalidOperationException or IOException)
            {
                failures++;
                Console.Error.WriteLine($"{label} failed: {ex.Message}");
                if (failures >= 3 && failures == i + 1) { Console.Error.WriteLine("Stopping after 3 failures in a row."); return 1; }
            }
        }
        Console.WriteLine($"Saved to {Explanations.PathFor(index.Graph.Root)}. {queue.Count - batch.Count + failures} still to explain.");
        return failures > 0 ? 1 : 0;
    }
}

/// <summary><c>codetolife guide [&lt;path&gt;] [--out FILE]</c>: writes the Markdown onboarding guide.</summary>
static class GuideCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? path = null, output = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: output = args[++i]; break;
                case "-h" or "--help":
                    Console.WriteLine("Usage: codetolife guide [<path>] [--out FILE]");
                    Console.WriteLine();
                    Console.WriteLine("Writes a Markdown onboarding guide for the code at <path> to FILE, or to the console.");
                    return 0;
                default:
                    if (args[i].StartsWith('-')) { Console.Error.WriteLine($"Unknown option {args[i]}. Try codetolife guide --help."); return 2; }
                    path ??= args[i];
                    break;
            }
        }
        using var codebase = new Codebase();
        await codebase.OpenAsync(Path.GetFullPath(path ?? Environment.CurrentDirectory), watch: false);
        if (!codebase.IsLoaded) { Console.Error.WriteLine($"Couldn't analyze the code: {codebase.LastError}"); return 2; }
        var text = OnboardingGuide.Build(codebase.Index);
        if (output is null)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Write(text);
        }
        else
        {
            await File.WriteAllTextAsync(output, text);
            Console.WriteLine($"Wrote {output}.");
        }
        return 0;
    }
}
