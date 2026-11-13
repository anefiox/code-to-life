using System.Diagnostics;
using CodeToLife.Insights;

namespace CodeToLife;

/// <summary>The API for the tools built on the graph: tests, impact, architecture rules and stack traces.</summary>
static class InsightsApi
{
    public static void MapInsights(this RouteGroupBuilder api, Workspace workspace, EventHub events)
    {
        IResult NotReady() => Results.Problem(workspace.LastError ?? "Still analyzing.", statusCode: 503);

        // ---- Which tests reach which code ----
        api.MapGet("/tests/map", () =>
        {
            if (!workspace.IsLoaded) return NotReady();
            var tests = workspace.Tests;
            return Results.Ok(new
            {
                tests = tests.Tests,
                testProjects = tests.TestProjects,
                maxDepth = tests.MaxDepth,
                members = tests.NearestDepths(),
                types = tests.Coverage(),
            });
        });

        api.MapGet("/tests/for", (string id) =>
        {
            if (!workspace.IsLoaded) return NotReady();
            var index = workspace.Index;
            return Results.Ok(workspace.Tests.For(id).Select(h => new { testId = h.TestId, name = index.Display(h.TestId), depth = h.Depth, location = index.Location(h.TestId) }));
        });

        api.MapPost("/tests/affected", (IdsRequest request) =>
        {
            if (!workspace.IsLoaded) return NotReady();
            var affected = workspace.Tests.Affected(request.Ids);
            var index = workspace.Index;
            return Results.Ok(new
            {
                tests = affected.Tests.Select(t => new { testId = t, name = index.Display(t) }),
                untested = affected.Untested.Select(u => new { id = u, name = index.Display(u), location = index.Location(u) }),
                commands = affected.Commands,
            });
        });

        api.MapPost("/tests/run", async (IdsRequest request) =>
        {
            if (!workspace.IsLoaded) return NotReady();
            var tests = workspace.Tests;
            // Ids may be tests themselves or code to find the tests for.
            var commands = tests.Affected(request.Ids).Commands;
            if (commands.Count == 0) return Results.Ok(new { success = true, ran = 0, output = "No tests reach that code." });
            var success = true;
            foreach (var command in commands)
            {
                events.Publish("debug.output", new { category = "build", output = $"> {command.CommandLine}\n" });
                var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in new[] { "test", command.ProjectPath, "--filter", command.Filter, "-nologo", "-v", "q" }) info.ArgumentList.Add(a);
                using var process = Process.Start(info)!;
                process.OutputDataReceived += (_, e) => { if (e.Data is not null) events.Publish("debug.output", new { category = "stdout", output = e.Data + "\n" }); };
                process.ErrorDataReceived += (_, e) => { if (e.Data is not null) events.Publish("debug.output", new { category = "stderr", output = e.Data + "\n" }); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync();
                success &= process.ExitCode == 0;
            }
            return Results.Ok(new { success, ran = commands.Count });
        });

        // ---- What breaks if this changes ----
        api.MapGet("/impact", (string id, int? depth) =>
        {
            if (!workspace.IsLoaded) return NotReady();
            try
            {
                return Results.Ok(ImpactAnalyzer.Analyze(workspace.Index, workspace.Tests, workspace.Mapper, workspace.Codebase.SourceFiles, id, Math.Clamp(depth ?? 4, 1, 10)));
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        // ---- Architecture rules ----
        api.MapGet("/rules", () =>
        {
            var path = ArchitectureRules.PathFor(workspace.RootDirectory);
            var exists = File.Exists(path);
            return Results.Ok(new
            {
                exists,
                path,
                json = exists ? File.ReadAllText(path) : null,
                suggestion = workspace.IsLoaded ? ArchitectureRules.Serialize(ArchitectureRules.Suggest(workspace.Graph)) : null,
            });
        });

        api.MapPut("/rules", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            try { ArchitectureRules.Save(workspace.RootDirectory, json); }
            catch (System.Text.Json.JsonException ex) { return Results.BadRequest(new { error = $"That isn't valid rules JSON: {ex.Message}" }); }
            events.Publish("rules.changed", new { });
            return Results.Ok();
        });

        api.MapGet("/rules/check", () =>
        {
            if (!workspace.IsLoaded) return NotReady();
            try { return Results.Ok(ArchitectureRules.Check(workspace.Graph, ArchitectureRules.Load(workspace.RootDirectory))); }
            catch (System.Text.Json.JsonException ex) { return Results.Ok(new ArchitectureResult(true, [], [$"rules.json isn't valid: {ex.Message}"])); }
        });

        // ---- Stack traces and logs ----
        api.MapPost("/trace", (TextRequest request) =>
            workspace.IsLoaded ? Results.Ok(TraceParser.Parse(request.Text ?? "", workspace.Mapper)) : NotReady());

        api.MapPost("/trace/log", (TextRequest request) =>
        {
            if (!workspace.IsLoaded) return NotReady();
            var text = request.Text;
            if (!string.IsNullOrWhiteSpace(request.Path))
            {
                var path = Path.GetFullPath(Path.Combine(workspace.RootDirectory, request.Path));
                if (!File.Exists(path)) return Results.NotFound(new { error = $"'{path}' doesn't exist." });
                if (new FileInfo(path).Length > 200_000_000) return Results.BadRequest(new { error = "That log is over 200 MB; give me a slice of it." });
                text = File.ReadAllText(path);
            }
            return Results.Ok(TraceParser.ScanLog(text ?? "", workspace.Mapper));
        });
    }
}

record IdsRequest(string[] Ids);
record TextRequest(string? Text, string? Path);
