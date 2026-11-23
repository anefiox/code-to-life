using System.Text.RegularExpressions;
using CodeToLife.Analysis;

namespace CodeToLife.Insights;

/// <param name="File">An example to copy or the place to change, relative to the root; null for a plain instruction.</param>
public sealed record RecipeStep(string Text, string? File = null, int Line = 1);

public sealed record Recipe(string Id, string Title, string Summary, IReadOnlyList<RecipeStep> Steps);

/// <summary>
/// "How do I…?" recipes: short checklists for common changes, worked out from the patterns the
/// solution already follows, each step pointing at a real example to copy.
/// </summary>
public static partial class Recipes
{
    sealed record Context(GraphIndex Index, CodeGraph Graph, string Root, HashSet<string> TestNames, List<string> TestDirs, SettingsMap Settings,
        IReadOnlyList<string> Templates, ProjectInfo? App)
    {
        public string Rel(string? file) => file is null ? "" : ProjectFiles.Relative(Root, file);
        public string Text(string relative) => ProjectFiles.ReadText(Root, relative);
        public IEnumerable<TypeNode> AppTypes => Graph.Types.Where(t => t.File is not null && !TestNames.Contains(t.Project));
    }

    public static IReadOnlyList<Recipe> For(GraphIndex index)
    {
        var graph = index.Graph;
        var (testNames, testDirs) = ProjectFiles.TestProjects(graph);
        var app = graph.Projects.Where(p => p.OutputKind == "exe")
            .OrderByDescending(p => (graph.Routes ?? []).Count(r => index.TypeOf(r.Handler)?.Project == p.Name))
            .ThenByDescending(p => graph.Types.Count(t => t.Project == p.Name)).FirstOrDefault();
        var context = new Context(index, graph, graph.Root, testNames, testDirs, Settings.For(index), Learning.TemplateFiles(graph.Root), app);
        var makers = new Func<Context, Recipe?>[] { Run, Page, Setting, Service, Data, Partial, Test };
        var result = new List<Recipe>();
        foreach (var make in makers)
        {
            try { if (make(context) is { } recipe) result.Add(recipe); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
        }
        return result;
    }

    static string ProjectDir(Context c, ProjectInfo project) => c.Rel(Path.GetDirectoryName(project.Path));

    static Recipe? Run(Context c)
    {
        var steps = new List<RecipeStep>();
        if (c.App is { } app)
        {
            var path = c.Rel(app.Path);
            steps.Add(new RecipeStep($"dotnet run --project {path}: builds and starts {app.Name}.", path));
            steps.Add(new RecipeStep($"dotnet watch --project {path}: the same, but it rebuilds and reloads when you save."));
            var launch = ProjectDir(c, app) is var dir && dir != "." ? dir + "/Properties/launchSettings.json" : "Properties/launchSettings.json";
            var text = c.Text(launch);
            if (UrlSetting().Match(text) is { Success: true } url)
                steps.Add(new RecipeStep($"It listens on {url.Groups["urls"].Value.Replace(";", " and ")} (from launchSettings.json, which only applies to local runs).",
                    launch, ProjectFiles.LineAt(text, url.Index)));
        }
        else if (c.Graph.Projects.Count > 0) steps.Add(new RecipeStep("dotnet build: builds every project. There is no program project to run; this is a library."));
        if (c.TestNames.Count > 0)
            steps.Add(new RecipeStep($"dotnet test: runs the tests in {string.Join(", ", c.TestNames.Order(StringComparer.Ordinal))}."));
        var compose = ProjectFiles.Find(c.Root, f => ComposeFile().IsMatch(f), 2).FirstOrDefault();
        if (compose is not null)
        {
            var services = ComposeService().Matches(Regex.Match(c.Text(compose), @"^services:\s*$([\s\S]*?)(?=^\S|\z)", RegexOptions.Multiline).Groups[1].Value)
                .Select(m => m.Groups[1].Value).ToList();
            steps.Add(new RecipeStep($"docker compose up starts {(services.Count > 0 ? "the services in " + compose + ": " + string.Join(", ", services) : "everything in " + compose)}.", compose));
        }
        if (c.Graph.Projects.Any(p => File.Exists(p.Path) && File.ReadAllText(p.Path).Contains("UserSecretsId", StringComparison.Ordinal)))
            steps.Add(new RecipeStep("Passwords and keys for local runs go in user secrets (dotnet user-secrets set \"Section:Key\" \"value\"), kept outside the project folder so they are never committed."));
        return steps.Count == 0 ? null : new Recipe("run", "Run, test and check it", "The commands developers use every day.", steps);
    }

    static Recipe? Page(Context c)
    {
        var routes = (c.Graph.Routes ?? []).Where(r => c.Index.TypeOf(r.Handler) is { } t && !c.TestNames.Contains(t.Project)).ToList();
        if (routes.Count == 0) return null;
        var kind = routes.GroupBy(r => r.Kind).OrderByDescending(g => g.Count()).First().Key;
        var steps = new List<RecipeStep>();
        var views = c.Graph.Views ?? new Dictionary<string, IReadOnlyList<string>>();
        if (kind == "controller")
        {
            // Prefer a controller whose actions render views, and not the error or home page.
            var example = routes.Where(r => r.Kind == "controller").GroupBy(r => c.Index.TypeOf(r.Handler)!)
                .OrderByDescending(g => g.Any(r => views.ContainsKey(r.Handler)) ? 1 : 0)
                .ThenBy(g => Regex.IsMatch(g.Key.Name, "Error|Home|Health") ? 1 : 0).ThenByDescending(g => g.Count()).First();
            var type = example.Key;
            var name = type.Name.EndsWith("Controller", StringComparison.Ordinal) ? type.Name[..^"Controller".Length] : type.Name;
            steps.Add(new RecipeStep($"Add a class named YourController next to {type.Name}, deriving from {(type.Attributes.Contains("ApiController") ? "ControllerBase" : "Controller")}. ASP.NET Core finds it by itself.",
                c.Rel(type.File), type.Line));
            var first = example.First();
            steps.Add(new RecipeStep(first.FixedPath && first.Path.Contains(name, StringComparison.OrdinalIgnoreCase) && !type.Attributes.Contains("Route")
                ? $"Each public method is an action with its own address: /Your/Action, like {first.Path}."
                : $"Give each public method an address with [HttpGet(\"…\")], [HttpPost] or [Route(\"…\")], like {first.Method} {first.Path}.",
                c.Rel(c.Index.Members.GetValueOrDefault(first.Handler)?.File ?? type.File), c.Index.Members.GetValueOrDefault(first.Handler)?.Line ?? type.Line));
            var view = c.Templates.FirstOrDefault(f => f.Contains($"Views/{name}/", StringComparison.OrdinalIgnoreCase));
            if (view is not null)
                steps.Add(new RecipeStep($"Add its views in Views/Your/: one .cshtml per action that calls View(), like {view}.", view));
        }
        else if (kind == "page")
        {
            var example = routes.Where(r => r.Kind == "page" && r.File is not null).OrderBy(r => Regex.IsMatch(r.Path, "Error|Privacy|^/$") ? 1 : 0).First();
            var code = c.Rel(example.File);
            var page = code.EndsWith(".cshtml.cs", StringComparison.OrdinalIgnoreCase) ? code[..^3] : null;
            if (page is not null) steps.Add(new RecipeStep($"Add YourPage.cshtml next to {Path.GetFileName(page)}, starting with @page. Its address comes from where it sits under Pages/, like {example.Path}.", page));
            steps.Add(new RecipeStep("Add its code in YourPage.cshtml.cs: a class deriving from PageModel, with OnGet (and OnPost for forms).", code, example.Line));
        }
        else
        {
            var file = routes.Where(r => r.Kind == "minimal" && r.File is not null).GroupBy(r => r.File!).OrderByDescending(g => g.Count()).First();
            var sample = file.OrderBy(r => r.Line).First();
            steps.Add(new RecipeStep($"Map the address in {Path.GetFileName(file.Key)} next to the others, like {(sample.Method == "*" ? "ANY" : sample.Method)} {sample.Path}: app.MapGet(\"/your-path\", handler).",
                c.Rel(file.Key), sample.Line));
            var method = file.Select(r => c.Index.Members.GetValueOrDefault(r.Handler)).FirstOrDefault(m => m is not null && m.Kind == "method" && !file.Any(r => r.Inline && r.Handler == m.Id));
            if (method is not null)
                steps.Add(new RecipeStep($"Keep the handler small: put the work in a method or service, like {c.Index.Display(method.Id)}. Its parameters are filled in from the address, the body or registered services.",
                    c.Rel(method.File), method.Line));
        }
        // The menu: a layout link to an existing page.
        var layout = c.Templates.FirstOrDefault(f => Path.GetFileName(f).Equals("_Layout.cshtml", StringComparison.OrdinalIgnoreCase));
        if (layout is not null && NavLink().Match(c.Text(layout)) is { Success: true } link)
            steps.Add(new RecipeStep("If it belongs in the menu, add a link in _Layout.cshtml like this one.", layout, ProjectFiles.LineAt(c.Text(layout), link.Index)));
        var test = c.Graph.Types.FirstOrDefault(t => c.TestNames.Contains(t.Project) && routes.Any(r => c.Index.TypeOf(r.Handler) is { } h && t.Name.StartsWith(h.Name, StringComparison.Ordinal)));
        if (test is not null) steps.Add(new RecipeStep($"Add a test like {test.Name}.", c.Rel(test.File), test.Line));
        steps.Add(new RecipeStep($"Run it ({(c.App is null ? "dotnet run" : "dotnet watch --project " + c.Rel(c.App.Path))}) and visit the new address. It also appears under Pages here."));
        var title = kind == "minimal" ? "Add an API endpoint" : "Add a page";
        return new Recipe("page", title, kind switch
        {
            "controller" => "Pages here are controller actions: a class whose public methods answer addresses, usually with a Razor view each.",
            "page" => "Pages here are Razor Pages: a .cshtml file and its PageModel class.",
            _ => "Addresses here are minimal APIs: app.MapGet and friends, pointing at a small handler.",
        }, steps);
    }

    static Recipe? Setting(Context c)
    {
        var (schema, _, settings) = c.Settings;
        if (schema is null && settings.Count == 0) return null;
        var steps = new List<RecipeStep>();
        var example = settings.FirstOrDefault(s => s.File == schema && s.Uses.Count > 0 && !s.Sensitive && !s.Key.StartsWith("Logging", StringComparison.Ordinal))
            ?? settings.FirstOrDefault(s => s.File == schema && !s.Sensitive) ?? settings.FirstOrDefault(s => s.File == schema);
        if (schema is not null)
            steps.Add(new RecipeStep($"Add it to {schema}, grouped under a section for your feature{(example is null ? "" : ", like " + example.Key)}.", schema, example?.Line ?? 1));
        var bound = settings.SelectMany(s => s.Uses).FirstOrDefault(u => u.Key.Contains(" → ", StringComparison.Ordinal));
        if (bound is not null)
            steps.Add(new RecipeStep($"Settings here are read through options classes ({bound.Key}): add a property to the class, or a new class bound with builder.Services.Configure<YourOptions>(builder.Configuration.GetSection(\"Your\")), then ask for IOptions<YourOptions> in a constructor.",
                bound.File, bound.Line));
        var use = settings.Where(s => !s.Sensitive).SelectMany(s => s.Uses).FirstOrDefault(u => !u.Key.Contains(" → ", StringComparison.Ordinal))
            ?? settings.SelectMany(s => s.Uses).FirstOrDefault(u => !u.Key.Contains(" → ", StringComparison.Ordinal));
        if (use is not null)
            steps.Add(new RecipeStep($"Or read it directly, as {Path.GetFileName(use.File)} reads {use.Key}.", use.File, use.Line));
        var local = settings.SelectMany(s => s.SetIn).FirstOrDefault(s => s.File.EndsWith("launchSettings.json", StringComparison.OrdinalIgnoreCase));
        steps.Add(new RecipeStep("Override it per environment in appsettings.{Environment}.json, or with an environment variable where : becomes __ (Section__Key)" +
            (local is null ? "." : ", for example in launchSettings.json for local runs."), local?.File, local?.Line ?? 1));
        return new Recipe("setting", "Add a setting", "Settings live in appsettings.json, with per-environment files and environment variables on top.", steps);
    }

    static Recipe? Service(Context c)
    {
        foreach (var type in c.AppTypes.Where(t => t.Members.Any(m => m.Kind == "top-level statements") || t.Name is "Program" or "Startup" || t.Name.EndsWith("Extensions", StringComparison.Ordinal)))
        {
            var file = c.Rel(type.File);
            var text = c.Text(file);
            // Prefer a registration with an interface: it shows the whole pattern.
            var matches = Registration().Matches(text);
            if (matches.Count == 0) continue;
            var m = matches.FirstOrDefault(x => x.Groups["impl"].Success) ?? matches[0];
            var steps = new List<RecipeStep>();
            // AddScoped<IOrders, Orders>() or just AddScoped<Orders>().
            var paired = m.Groups["impl"].Success;
            var contract = paired ? c.Graph.Types.FirstOrDefault(t => t.Name == m.Groups["contract"].Value && !c.TestNames.Contains(t.Project)) : null;
            var implName = paired ? m.Groups["impl"].Value : m.Groups["contract"].Value;
            var impl = c.Graph.Types.FirstOrDefault(t => t.Name == implName && !c.TestNames.Contains(t.Project));
            if (impl is not null)
                steps.Add(new RecipeStep($"Write the class{(contract is null ? "" : " and an interface for it")}, like {(contract is null ? "" : contract.Name + " and ")}{impl.Name}.", c.Rel(impl.File), impl.Line));
            steps.Add(new RecipeStep($"Register it in {Path.GetFileName(file)} next to the others, like this {m.Groups["life"].Value} line: builder.Services.AddScoped<IYourService, YourService>().", file, ProjectFiles.LineAt(text, m.Index)));
            var target = contract ?? impl;
            var consumer = target is null ? null : c.Index.EdgesTo(target.Id).Where(e => e.Kind is EdgeKind.Has or EdgeKind.Uses && e.Source != type.Id)
                .Select(e => c.Index.Types.GetValueOrDefault(e.Source)).FirstOrDefault(t => t is not null && !c.TestNames.Contains(t.Project) && t.File is not null);
            steps.Add(new RecipeStep(consumer is null
                ? "Ask for it in a constructor parameter: .NET creates it and passes it in (dependency injection)."
                : $"Ask for it in a constructor parameter, as {consumer.Name} does: .NET creates it and passes it in (dependency injection).",
                consumer is null ? null : c.Rel(consumer.File), consumer?.Line ?? 1));
            steps.Add(new RecipeStep("Pick the lifetime: AddScoped (one per request) suits most services; AddSingleton is one for the whole app, so it must be safe to share; AddTransient makes a new one every time."));
            return new Recipe("service", "Add a service", "Classes get what they need through their constructors, registered once in Program.cs.", steps);
        }
        return null;
    }

    static Recipe? Data(Context c)
    {
        foreach (var type in c.AppTypes)
        {
            var file = c.Rel(type.File);
            var text = c.Text(file);
            if (!DbContextClass().IsMatch(text) || !text.Contains("class " + type.Name, StringComparison.Ordinal)) continue;
            var steps = new List<RecipeStep>();
            var set = DbSet().Match(text);
            var entity = set.Success ? c.Graph.Types.FirstOrDefault(t => t.Name == set.Groups["entity"].Value) : null;
            if (entity is not null) steps.Add(new RecipeStep($"Write the entity class, like {entity.Name}: properties become columns, and Id is the key.", c.Rel(entity.File), entity.Line));
            steps.Add(new RecipeStep($"Add a DbSet<YourEntity> property to {type.Name}, next to the others. Each DbSet is a table.", file, set.Success ? ProjectFiles.LineAt(text, set.Index) : type.Line));
            var project = c.Graph.Projects.FirstOrDefault(p => p.Name == type.Project);
            var flag = project is null ? "" : " --project " + c.Rel(project.Path);
            steps.Add(new RecipeStep($"Create a migration: dotnet ef migrations add AddYourEntity{flag}. It writes the steps to change the database into a Migrations folder."));
            var migration = c.AppTypes.FirstOrDefault(t => c.Rel(t.File).Contains("/Migrations/", StringComparison.OrdinalIgnoreCase) && !t.Name.EndsWith("ModelSnapshot", StringComparison.Ordinal));
            if (migration is not null) steps.Add(new RecipeStep($"Migrations already here look like {migration.Name}. Don't edit old ones; add a new one instead.", c.Rel(migration.File), migration.Line));
            steps.Add(new RecipeStep($"Apply it to your database: dotnet ef database update{flag}. (Install the tool once with dotnet tool install --global dotnet-ef.)"));
            return new Recipe("data", "Add a database table", "The app uses Entity Framework Core: classes map to tables, and migrations change the database to match.", steps);
        }
        return null;
    }

    static Recipe? Partial(Context c)
    {
        var partials = c.Templates.Where(f => Path.GetFileName(f).StartsWith('_') && f.Contains("/Shared/", StringComparison.OrdinalIgnoreCase)
            && !Regex.IsMatch(Path.GetFileName(f), "^_(Layout|ViewStart|ViewImports|ValidationScriptsPartial)", RegexOptions.IgnoreCase)).ToList();
        var components = c.Templates.Where(f => f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith('_')
            && !c.Text(f).TrimStart().StartsWith("@page", StringComparison.Ordinal)).ToList();
        if (components.Count > 0)
        {
            var example = components.OrderBy(f => c.Text(f).Length).First();
            return new Recipe("component", "Add a Blazor component", "Components are reusable pieces of page, each in its own .razor file.",
            [
                new($"Add YourThing.razor next to {Path.GetFileName(example)}: markup, with any C# in an @code block.", example),
                new("Use it in a page as a tag: <YourThing Title=\"…\" />. Properties marked [Parameter] become its attributes."),
            ]);
        }
        if (partials.Count == 0) return null;
        var partial = partials.OrderBy(f => c.Text(f).Length).First();
        var user = c.Templates.Select(f => (File: f, Match: Regex.Match(c.Text(f), "<partial\\s+name\\s*=\\s*\"" + Regex.Escape(Path.GetFileNameWithoutExtension(partial)) + "\"")))
            .FirstOrDefault(x => x.Match.Success);
        var steps = new List<RecipeStep> { new($"Add _YourPart.cshtml in Views/Shared, like {Path.GetFileName(partial)}. The _ marks it as a piece, not a page.", partial) };
        steps.Add(user.File is null
            ? new RecipeStep("Render it from a view with <partial name=\"_YourPart\" />, passing a model with model=\"…\" if it needs data.")
            : new RecipeStep($"Render it from a view with <partial name=\"_YourPart\" />, as {Path.GetFileName(user.File)} does.", user.File, ProjectFiles.LineAt(c.Text(user.File), user.Match.Index)));
        return new Recipe("component", "Add a partial view", "Partial views are reusable pieces of page shared between views.", steps);
    }

    static Recipe? Test(Context c)
    {
        var projects = c.Graph.Projects.Where(p => p.OutputKind == "test").ToList();
        if (projects.Count == 0) return null;
        var packages = projects.SelectMany(p => Glossary.PackagesOf(p.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var framework = packages.Any(p => p.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)) ? "xUnit ([Fact] and [Theory])"
            : packages.Contains("NUnit") ? "NUnit ([Test] and [TestCase])"
            : packages.Any(p => p.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase)) ? "MSTest ([TestMethod])" : null;
        var mocks = new[] { "Moq", "NSubstitute", "FakeItEasy" }.FirstOrDefault(packages.Contains);
        var example = c.Graph.Types.Where(t => projects.Any(p => p.Name == t.Project) && t.File is not null && t.Members.Any(TestMap.IsTest))
            .OrderBy(t => t.Members.Count(TestMap.IsTest) is var n && n >= 2 ? 0 : 1).ThenBy(t => t.Metrics.Lines).FirstOrDefault();
        var steps = new List<RecipeStep>
        {
            new($"Tests live in {string.Join(", ", projects.Select(p => p.Name))}{(framework is null ? "" : ", written with " + framework)}. Add a class there named after what it tests, like ThingTests."),
        };
        if (example is not null) steps.Add(new RecipeStep($"Start from a small existing test class such as {example.Name}.", c.Rel(example.File), example.Line));
        if (mocks is not null) steps.Add(new RecipeStep($"Use {mocks} to stand in for dependencies, so a test checks one class at a time."));
        steps.Add(new RecipeStep("Run them with dotnet test, or one class with dotnet test --filter FullyQualifiedName~ThingTests. The Tests colour on the map shows what they reach."));
        return new Recipe("test", "Add a test", "Tests check the code still works after a change. Copy the style of the ones already here.", steps);
    }

    [GeneratedRegex(""""applicationUrl"\s*:\s*"(?<urls>[^"]+)"""")]
    private static partial Regex UrlSetting();

    [GeneratedRegex(@"(?:^|/)(?:docker-)?compose(?:\.[\w-]+)?\.ya?ml$", RegexOptions.IgnoreCase)]
    private static partial Regex ComposeFile();

    [GeneratedRegex(@"^ {2}([\w.-]+):\s*$", RegexOptions.Multiline)]
    private static partial Regex ComposeService();

    [GeneratedRegex(@"<a\b[^>]*(?:asp-controller|asp-page|asp-action|href=""/)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex NavLink();

    [GeneratedRegex(@"\.(?<life>AddScoped|AddTransient|AddSingleton)<(?<contract>\w+)\s*(?:,\s*(?<impl>\w+))?>")]
    private static partial Regex Registration();

    [GeneratedRegex(@"class\s+\w+\s*(?:\([^)]*\))?\s*:\s*(?:Identity\w*)?DbContext\b")]
    private static partial Regex DbContextClass();

    [GeneratedRegex(@"DbSet<(?<entity>\w+)>")]
    private static partial Regex DbSet();
}
