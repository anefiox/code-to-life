using System.Diagnostics;
using CodeToLife.Insights;

namespace CodeToLife.Tests;

/// <summary>Recipes, the settings map, the Razor map, test examples, history, the onboarding guide and Ask.</summary>
public class OnboardingTests : IDisposable
{
    readonly TempCode _code = new TempCode()
        .File("src/Shop/Shop.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><UserSecretsId>shop</UserSecretsId></PropertyGroup>
            </Project>
            """)
        .File("src/Shop/Program.cs", """
            using Shop.Services;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddControllersWithViews();
            builder.Services.AddSingleton<Clock>();
            builder.Services.AddScoped<ICatalog, Catalog>();
            builder.Services.Configure<ShopOptions>(builder.Configuration.GetSection("Shop"));
            var db = builder.Configuration.GetConnectionString("Shop");
            var region = Environment.GetEnvironmentVariable("SHOP_REGION");
            var app = builder.Build();
            app.MapDefaultControllerRoute();
            app.Run();
            """)
        .File("src/Shop/Services/Catalog.cs", """
            namespace Shop.Services;

            public class ShopOptions { public string Currency { get; set; } = "GBP"; }
            public class Clock { public DateTime Now() => DateTime.UtcNow; }
            public record Product(int Id, string Name, bool InStock);
            public interface ICatalog { IReadOnlyList<Product> All(); }

            public class Catalog : ICatalog
            {
                readonly List<Product> _products = [new(1, "Tea", true), new(2, "Cake", false)];
                public IReadOnlyList<Product> All() => _products.Where(p => p.InStock).ToList();
            }
            """)
        .File("src/Shop/Controllers/ProductsController.cs", """
            using Microsoft.AspNetCore.Mvc;
            using Shop.Services;

            namespace Shop.Controllers;

            public class ProductsController(ICatalog catalog, IConfiguration config) : Controller
            {
                public IActionResult Index() => View(catalog.All());
                public string Currency() => config["Shop:Currency"] ?? "GBP";
            }
            """)
        .File("src/Shop/Views/_ViewStart.cshtml", "@{\n    Layout = \"_Layout\";\n}\n")
        .File("src/Shop/Views/Shared/_Layout.cshtml", "<nav><a asp-controller=\"Products\" asp-action=\"Index\">Products</a></nav>\n<partial name=\"_Banner\" />\n@RenderBody()\n@await RenderSectionAsync(\"Scripts\", required: false)\n")
        .File("src/Shop/Views/Shared/_Banner.cshtml", "<p>Sale</p>\n")
        .File("src/Shop/Views/Products/Index.cshtml", "@model IReadOnlyList<Shop.Services.Product>\n<h1>Products</h1>\n@section Scripts { <script></script> }\n")
        .File("src/Shop/appsettings.json", """
            {
              "Logging": { "LogLevel": { "Default": "Information" } },
              "ConnectionStrings": { "Shop": "Server=db;Password=hunter2" },
              "Shop": { "Currency": "GBP", "PageSize": 20 }
            }
            """)
        .File("src/Shop/appsettings.Development.json", """{ "Shop": { "PageSize": 5 } }""")
        .File("src/Shop/Properties/launchSettings.json", """
            {
              "profiles": {
                "Shop": {
                  "applicationUrl": "https://localhost:7001;http://localhost:5001",
                  "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development", "Shop__Currency": "EUR" }
                }
              }
            }
            """)
        .File(".env", "SHOP_REGION=uk\n")
        .File("tests/Shop.Tests/Shop.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup><PackageReference Include="xunit" Version="2.9.2" /><PackageReference Include="Moq" Version="4.20.72" /></ItemGroup>
              <ItemGroup><ProjectReference Include="..\..\src\Shop\Shop.csproj" /></ItemGroup>
            </Project>
            """)
        .File("tests/Shop.Tests/CatalogTests.cs", """
            using Shop.Services;
            using Xunit;

            namespace Shop.Tests;

            public class CatalogTests
            {
                [Fact]
                public void All_lists_only_products_in_stock()
                {
                    var catalog = new Catalog();
                    Assert.Single(catalog.All());
                }

                [Fact]
                public void Unrelated() => Assert.True(true);
            }
            """)
        .File("Shop.slnx", """
            <Solution>
              <Project Path="src/Shop/Shop.csproj" />
              <Project Path="tests/Shop.Tests/Shop.Tests.csproj" />
            </Solution>
            """);

    GraphIndex Index() => new(_code.Analyze());

    public void Dispose() => _code.Dispose();

    [Fact]
    public void Settings_map_reads_appsettings_overrides_and_every_use()
    {
        var map = Settings.For(Index());
        Assert.Equal("src/Shop/appsettings.json", map.Schema);
        var byKey = map.Settings.ToDictionary(s => s.Key);

        var currency = byKey["Shop:Currency"];
        Assert.Equal("Shop__Currency", currency.Env);
        Assert.Equal("GBP", currency.Default);
        Assert.Contains(currency.Uses, u => u.Key == "Shop:Currency" && u.File == "src/Shop/Controllers/ProductsController.cs");
        Assert.Contains(currency.Uses, u => u.Key == "Shop → ShopOptions");
        Assert.Contains(currency.SetIn, s => s.File == "src/Shop/Properties/launchSettings.json" && s.Value == "EUR");

        var pageSize = byKey["Shop:PageSize"];
        Assert.True(pageSize.Varies);
        Assert.Equal("5", pageSize.Environments["Development"]);

        var connection = byKey["ConnectionStrings:Shop"];
        Assert.True(connection.Sensitive);
        Assert.Equal("", connection.Default);
        Assert.Contains("Shop database", connection.Doc);
        Assert.Single(connection.Uses);

        // Values in a real .env file are never shown.
        var region = byKey["SHOP_REGION"];
        Assert.Contains(region.SetIn, s => s.File == ".env" && s.Value is null);
        Assert.Single(region.Uses);
        Assert.True(byKey.ContainsKey("ASPNETCORE_ENVIRONMENT"));
        Assert.DoesNotContain(map.Settings, s => s.Default.Contains("hunter2"));
    }

    [Fact]
    public void Recipes_follow_the_solution_s_own_patterns()
    {
        var recipes = Recipes.For(Index()).ToDictionary(r => r.Id);
        Assert.Equal(["run", "page", "setting", "service", "component", "test"], Recipes.For(Index()).Select(r => r.Id));

        var run = recipes["run"].Steps;
        Assert.StartsWith("dotnet run --project src/Shop/Shop.csproj", run[0].Text);
        Assert.Contains(run, s => s.Text.Contains("https://localhost:7001 and http://localhost:5001"));
        Assert.Contains(run, s => s.Text.StartsWith("dotnet test", StringComparison.Ordinal));
        Assert.Contains(run, s => s.Text.Contains("user-secrets"));

        var page = recipes["page"];
        Assert.Equal("Add a page", page.Title);
        Assert.Equal("src/Shop/Controllers/ProductsController.cs", page.Steps[0].File);
        Assert.Contains(page.Steps, s => s.File == "src/Shop/Views/Products/Index.cshtml");
        Assert.Contains(page.Steps, s => s.File == "src/Shop/Views/Shared/_Layout.cshtml" && s.Text.Contains("menu"));
        Assert.Contains(page.Steps, s => s.Text.StartsWith("Add a test like CatalogTests", StringComparison.Ordinal) || s.Text.Contains("visit"));

        Assert.Equal("src/Shop/appsettings.json", recipes["setting"].Steps[0].File);
        Assert.Contains(recipes["setting"].Steps, s => s.Text.Contains("ShopOptions"));

        var service = recipes["service"].Steps;
        Assert.Contains("ICatalog and Catalog", service[0].Text);
        Assert.Equal(("src/Shop/Program.cs", 6), (service[1].File, service[1].Line));
        Assert.Contains("ProductsController", service[2].Text);

        Assert.Contains("_Banner.cshtml", recipes["component"].Steps[0].Text);
        Assert.Contains(recipes["test"].Steps, s => s.Text.Contains("xUnit"));
        Assert.Contains(recipes["test"].Steps, s => s.Text.Contains("Moq"));
    }

    [Fact]
    public void Razor_map_finds_layouts_partials_sections_models_and_who_renders_each_view()
    {
        var index = Index();
        var views = RazorMap.For(index).Templates.ToDictionary(t => t.File);
        var page = views["src/Shop/Views/Products/Index.cshtml"];
        Assert.Equal("page", page.Kind);
        Assert.Equal("src/Shop/Views/Shared/_Layout.cshtml", page.Extends?.File);
        Assert.Equal(["Scripts"], page.Blocks);
        Assert.Equal("Shop.Services.Product", page.ModelId);
        Assert.Equal("ProductsController.Index", Assert.Single(page.RenderedBy).Name);

        var layout = views["src/Shop/Views/Shared/_Layout.cshtml"];
        Assert.Equal("layout", layout.Kind);
        Assert.Equal("src/Shop/Views/Shared/_Banner.cshtml", Assert.Single(layout.Includes).File);
        Assert.Contains(layout.UsedBy, u => u.File == page.File);
        Assert.Equal("partial", views["src/Shop/Views/Shared/_Banner.cshtml"].Kind);
        Assert.Equal("viewstart", views["src/Shop/Views/_ViewStart.cshtml"].Kind);
        Assert.Null(views["src/Shop/Views/Shared/_Banner.cshtml"].Extends);
    }

    [Fact]
    public void Test_examples_are_the_tests_that_call_it()
    {
        var index = Index();
        var all = index.Types["Shop.Services.Catalog"].Members.Single(m => m.Name == "All").Id;
        var result = TestExamples.For(index, all);
        var example = Assert.Single(result.Examples);
        Assert.Equal("CatalogTests.All_lists_only_products_in_stock", example.Title);
        Assert.Equal("tests/Shop.Tests/CatalogTests.cs", example.File);
        Assert.Contains("Assert.Single(catalog.All())", example.Code);
        Assert.Single(TestExamples.For(index, "Shop.Services.Catalog").Examples);
        Assert.Empty(TestExamples.For(index, "Shop.Services.Clock").Examples);
        Assert.Throws<ExplainException>(() => TestExamples.For(index, "nope"));
    }

    [Fact]
    public void History_finds_hotspots_and_the_commits_behind_a_class()
    {
        var index = Index();
        Assert.False(GitHistory.For(index.Graph).Available);
        void Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-C", _code.Root, "-c", "user.name=Ada", "-c", "user.email=ada@example.com", "-c", "core.autocrlf=false" }.Concat(args)) start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        Git("init", "-q");
        Git("add", ".");
        Git("commit", "-qm", "Open the shop");
        File.AppendAllText(_code.PathOf("src/Shop/Controllers/ProductsController.cs"), "\n// more\n");
        Git("commit", "-qam", "Tweak the products page");

        var spots = GitHistory.For(index.Graph);
        Assert.True(spots.Available);
        Assert.Equal(2, spots.Commits);
        var top = spots.Files[0];
        Assert.Equal(("src/Shop/Controllers/ProductsController.cs", 2), (top.File, top.Commits));
        Assert.Equal("Ada", top.Authors[0].Name);
        Assert.DoesNotContain(spots.Files, f => f.File.StartsWith("tests/", StringComparison.Ordinal));

        var catalog = GitHistory.For(index, "Shop.Services.Catalog");
        Assert.Equal(["Open the shop"], catalog.Commits.Select(c => c.Subject));
        var program = GitHistory.For(index, index.Graph.Types.SelectMany(t => t.Members).First(m => m.Kind == "top-level statements").Id);
        Assert.Equal(["Open the shop"], program.Commits.Select(c => c.Subject));
    }

    [Fact]
    public void Onboarding_guide_gathers_everything_without_secrets()
    {
        var index = Index();
        Explanations.Save(index, "Shop.Services.Catalog", "Lists the products for sale.");
        var guide = OnboardingGuide.Build(index, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        Assert.StartsWith($"# Getting started with {index.Graph.Name}\n\n_Generated by Code to Life on 2026-01-02", guide);
        foreach (var heading in new[] { "## Projects", "## Run it", "## How the code is organised", "## Start here", "## Pages", "## Settings", "## How do I…", "### Add a page", "## Classes explained" })
            Assert.Contains("\n" + heading + "\n", guide);
        Assert.Contains("| ConnectionStrings:Shop | ConnectionStrings__Shop | (secret) |", guide);
        Assert.Contains("- **Catalog** (`src/Shop/Services/Catalog.cs`): Lists the products for sale.", guide);
        Assert.DoesNotContain("hunter2", guide);
        Assert.DoesNotContain("SHOP_REGION=uk", guide);
    }

    [Fact]
    public void Ask_finds_matching_code_and_builds_a_grounded_prompt()
    {
        var index = Index();
        Explanations.Save(index, "Shop.Services.Clock", "Tells the current time, so tests can fake it.");
        var ids = Ask.Sources(index, "Which products are in stock?");
        // The pages and the Product record both match "products".
        Assert.Contains("Shop.Services.Product", ids.Take(4));
        Assert.Contains(ids.Take(2), id => id.StartsWith("Shop.Controllers.ProductsController|", StringComparison.Ordinal));
        Assert.DoesNotContain(ids, id => id.Contains("Tests", StringComparison.Ordinal));
        Assert.Equal("Shop.Services.Clock", Ask.Sources(index, "Where does the current time come from?")[0]);

        var (messages, sources) = Ask.Prompt(index, "How does the catalog list products?");
        Assert.Contains("backticks", messages[0].Content);
        Assert.StartsWith("Question: How does the catalog list products?", messages[1].Content);
        Assert.Contains(sources, s => s.Name == "Catalog" && s.File == "src/Shop/Services/Catalog.cs");
        Assert.Throws<ExplainException>(() => Ask.Sources(index, "hi"));
        Assert.Throws<ExplainException>(() => Ask.Sources(index, "how is the it?"));
    }

    [Fact]
    public void Project_files_can_be_read_but_never_secrets()
    {
        Assert.Equal(5, ProjectFiles.ReadProjectFile(_code.Root, "src/Shop/appsettings.json").Total);
        _code.File("src/Shop/secrets.json", "{}").File(".env.example", "SHOP_REGION=\n");
        Assert.Equal("SHOP_REGION=", ProjectFiles.ReadProjectFile(_code.Root, ".env.example").Code);
        foreach (var file in new[] { ".env", "src/Shop/secrets.json", "../x.cs", "src/Shop/bin/a.cs", "logo.png", null })
            Assert.Throws<ExplainException>(() => ProjectFiles.ReadProjectFile(_code.Root, file));
    }

    [Fact]
    public void Concepts_fit_the_packages_and_folders_get_notes()
    {
        var graph = _code.Analyze();
        var terms = Concepts.For(graph).Select(c => c.Term).ToList();
        Assert.Contains("middleware", terms);
        Assert.Contains("dependency injection", terms);
        Assert.Contains("xUnit", terms);
        Assert.DoesNotContain("DbContext", terms);
        Assert.DoesNotContain("NUnit", terms);
        var notes = Folders.NotesFor(graph);
        Assert.Contains("Controllers", notes["src/Shop/Controllers"]);
        Assert.Contains("top of the solution", notes[""]);
        Assert.NotEmpty(Folders.Groups(graph));
    }
}
