using System.Text.RegularExpressions;
using CodeToLife.Analysis;

namespace CodeToLife.Insights;

/// <param name="Dir">The folder, relative to the root, "" for the root itself.</param>
public sealed record FolderGroup(string Dir, IReadOnlyList<string> Files, IReadOnlyList<string> Types, int Tests);

/// <summary>What common folder names are usually for, and the grouping the Folders map and onboarding guide share.</summary>
public static class Folders
{
    static readonly (Regex Pattern, string Note)[] Notes =
    [
        (new(@"^controllers?$"), "Controllers: classes whose public methods answer web requests. Each action pairs an address with the code that handles it."),
        (new(@"^(pages|areas)$"), "Razor Pages: each .cshtml page is a web address, with its code in the matching .cshtml.cs file."),
        (new(@"^(views|shared|layouts?|partials|components)$"), "Razor views and components: the HTML the app fills in and sends to the browser."),
        (new(@"^(endpoints?|routes?|apis?)$"), "The web addresses the app answers and the code that handles each one."),
        (new(@"^(middlewares?|filters)$"), "Code that runs on every request before (or after) it reaches a page, for example sign-in, logging or error handling."),
        (new(@"^(models?|entities|domain|dtos?|viewmodels?|contracts|records)$"), "The shapes of the data the app works with: classes with properties, and the rules that belong to them."),
        (new(@"^(data|persistence|repositories|repository|db|database|dbcontexts?)$"), "Data access: how the app reads and saves data, usually in a database through Entity Framework Core or Dapper."),
        (new(@"^migrations$"), "Database migrations: generated steps that change the database to match the code. Rarely edited by hand."),
        (new(@"^(services?|application|handlers|commands|queries|features|usecases)$"), "Business logic: the work the app does, kept apart from the web and the database."),
        (new(@"^(infrastructure|clients?|integrations?|gateways?|adapters)$"), "Code that talks to the outside world: other APIs, queues, files, email and the like."),
        (new(@"^(config|configuration|settings|options)$"), "Settings classes, bound from appsettings.json and environment variables."),
        (new(@"^(extensions|helpers?|utils?|utilities|common|shared|core)$"), "Shared helpers used in many places across the app."),
        (new(@"^(validators?|validation)$"), "Rules that check incoming data is valid before it is used."),
        (new(@"^(mappings?|mappers?|profiles)$"), "Code that copies data between similar classes, such as an entity and a DTO."),
        (new(@"^(jobs|workers?|background|hostedservices)$"), "Background work that runs on a schedule or alongside the web app."),
        (new(@"^(hubs)$"), "SignalR hubs: real-time messages between the server and the browser."),
        (new(@"^(properties)$"), "Project settings, such as launchSettings.json for running locally."),
        (new(@"^(wwwroot|static|assets)$"), "Static files (CSS, JavaScript, images) sent to the browser as they are."),
        (new(@"^(tests?|.*\.tests?|.*\.(unit|integration)tests?|specs?|fakes|mocks|fixtures)$"), "Test code and helpers. They check the app works, but do not run in the live service."),
        (new(@"^(tools|scripts|build)$"), "Scripts and tools for developers. They are not part of the running app."),
        (new(@"^src$"), "The app's source code."),
    ];

    public static string? Note(string dir)
    {
        foreach (var part in dir.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
            foreach (var (pattern, note) in Notes)
                if (pattern.IsMatch(part.ToLowerInvariant())) return note;
        return dir.Length == 0 ? "Files at the top of the solution, usually the entry point and setup." : null;
    }

    static string DirOf(string file) => file.Contains('/') ? file[..file.LastIndexOf('/')] : "";

    /// <summary>A note for every folder that holds code, and each of its parents.</summary>
    public static IReadOnlyDictionary<string, string?> NotesFor(CodeGraph graph)
    {
        var notes = new SortedDictionary<string, string?>(StringComparer.Ordinal) { [""] = Note("") };
        foreach (var type in graph.Types)
        {
            if (type.File is null) continue;
            var parts = ProjectFiles.Relative(graph.Root, type.File).Split('/')[..^1];
            for (var i = 1; i <= parts.Length; i++)
            {
                var dir = string.Join('/', parts[..i]);
                if (!notes.ContainsKey(dir)) notes[dir] = Note(dir);
            }
        }
        return notes;
    }

    /// <summary>The code's folders, at most 14, merging the deepest into their parents until it fits on one screen.</summary>
    public static IReadOnlyList<FolderGroup> Groups(CodeGraph graph)
    {
        var tests = graph.Projects.Where(p => p.OutputKind == "test").Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var types = graph.Types.Where(t => t.File is not null && t.ContainingType is null).ToList();
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in types) files[ProjectFiles.Relative(graph.Root, t.File!)] = DirOf(ProjectFiles.Relative(graph.Root, t.File!));
        static int Depth(string dir) => dir.Length == 0 ? 0 : dir.Split('/').Length;
        while (true)
        {
            var dirs = files.Values.ToHashSet(StringComparer.Ordinal);
            var deepest = dirs.Count == 0 ? 0 : dirs.Max(Depth);
            if (dirs.Count <= 14 || deepest <= 1) break;
            foreach (var (file, dir) in files.ToList()) if (Depth(dir) == deepest) files[file] = DirOf(dir);
        }
        return types.GroupBy(t => files[ProjectFiles.Relative(graph.Root, t.File!)], StringComparer.Ordinal)
            .Select(g => new FolderGroup(g.Key, g.Select(t => ProjectFiles.Relative(graph.Root, t.File!)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList(),
                g.Select(t => t.Id).ToList(), g.Count(t => tests.Contains(t.Project))))
            .OrderBy(g => g.Dir, StringComparer.Ordinal).ToList();
    }
}
