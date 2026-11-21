using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CodeToLife.Analysis;
using CodeToLife.Insights;

namespace CodeToLife.Tests;

/// <summary>Routes, page flows, the Start here tour, explanations, the glossary and the AI client.</summary>
public class LearningTests : IDisposable
{
    readonly TempCode _code = new TempCode()
        .File("Web/Web.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.0" /><PackageReference Include="Serilog.Sinks.Console" Version="6.0.0" /></ItemGroup>
            </Project>
            """)
        .File("Web/Program.cs", """
            using Web.Services;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddScoped<IOrders, Orders>();
            var app = builder.Build();
            var api = app.MapGroup("/api");
            api.MapGet("/orders", (IOrders orders) => orders.All());
            api.MapGroup("v2").MapPost("orders/{id}", OrderEndpoints.Create);
            app.MapMethods("/ping", new[] { "GET", "HEAD" }, () => "pong");
            app.MapOrderAdmin();
            var cache = new Dictionary<string, string>();
            app.Run();
            """)
        .File("Web/Services/Orders.cs", """
            namespace Web.Services;

            public interface IOrders { string[] All(); }

            public class Orders(Store store) : IOrders
            {
                public string[] All() => store.Read();
            }

            public class Store
            {
                public string[] Read() => Format.Lines("a");
            }

            public static class Format
            {
                public static string[] Lines(string text) => [text];
            }

            public static class OrderEndpoints
            {
                public const string AdminPath = "/admin/orders";
                public static string Create(int id, IOrders orders) => orders.All()[0] + id;

                public static void MapOrderAdmin(this IEndpointRouteBuilder routes) => routes.MapDelete(AdminPath, () => "gone");
            }
            """)
        .File("Web/Controllers/ProductsController.cs", """
            using Microsoft.AspNetCore.Mvc;
            using Web.Services;

            namespace Web.Controllers;

            [ApiController]
            [Route("api/[controller]")]
            public class ProductsController(IOrders orders) : ControllerBase
            {
                [HttpGet]
                public string[] List() => orders.All();

                [HttpGet("{id:int}")]
                public string Get(int id) => orders.All()[id];

                [HttpPost, Route("import")]
                public void Import() { }

                [NonAction]
                public void Helper() { }
            }
            """)
        .File("Web/Controllers/HomeController.cs", """
            using Microsoft.AspNetCore.Mvc;
            namespace Web.Controllers;

            public class HomeController : Controller
            {
                public IActionResult Index() => View();
                public async Task<IActionResult> AboutAsync() { await Task.Yield(); return View("Story"); }
                [HttpGet("/privacy")]
                public IActionResult Privacy(object model) => View(model);
            }
            """)
        .File("Web/Views/Home/Index.cshtml", "<h1>Home</h1>\r\n")
        .File("Web/Views/Shared/Story.cshtml", "<p>Story</p>\n")
        .File("Web/Views/Home/Privacy.cshtml", "<p>Privacy</p>\n")
        .File("Web/Pages/Products/Edit.cshtml", "@page \"{id:int}\"\n@model Web.Pages.Products.EditModel\n<form></form>\n")
        .File("Web/Pages/Products/Edit.cshtml.cs", """
            using Microsoft.AspNetCore.Mvc.RazorPages;
            namespace Web.Pages.Products;

            public class EditModel : PageModel
            {
                public void OnGet(int id) { }
                public Task OnPostDeleteAsync(int id) => Task.CompletedTask;
                public void Helper() { }
            }
            """)
        .File("Web.Tests/Web.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup><PackageReference Include="xunit" Version="2.9.3" /><ProjectReference Include="..\Web\Web.csproj" /></ItemGroup>
            </Project>
            """)
        .File("Web.Tests/AppTests.cs", """
            namespace Web.Tests;
            public class AppTests
            {
                public void Routes(Microsoft.AspNetCore.Builder.WebApplication app) => app.MapGet("/test-only", () => "x");
            }
            """);

    public void Dispose() => _code.Dispose();

    [Fact]
    public void Finds_minimal_api_controller_and_razor_page_routes()
    {
        var graph = _code.Analyze();
        var index = new GraphIndex(graph);
        var routes = graph.Routes!.Select(r => $"{r.Method} {r.Path} {r.Kind} {(r.FixedPath ? "" : "dynamic ")}{r.Handler[(r.Handler.IndexOf('|') + 1)..]}").ToList();
        var minimal = graph.Routes!.Where(r => r.Kind == "minimal").Select(r => $"{r.Method} {r.Path} {(r.FixedPath ? "" : "dynamic ")}{index.Display(r.Handler)}").ToList();
        Assert.Contains("GET /api/orders Program (top-level code)", minimal);
        Assert.Contains("POST /api/v2/orders/{id} OrderEndpoints.Create", minimal);
        Assert.Contains("GET, HEAD /ping Program (top-level code)", minimal);
        Assert.Contains("GET /api/Products controller M:Web.Controllers.ProductsController.List", routes);
        Assert.Contains("GET /api/Products/{id:int} controller M:Web.Controllers.ProductsController.Get(System.Int32)", routes);
        Assert.Contains("POST /api/Products/import controller M:Web.Controllers.ProductsController.Import", routes);
        Assert.Contains("* /Home/Index controller M:Web.Controllers.HomeController.Index", routes);
        Assert.Contains("* /Home/About controller M:Web.Controllers.HomeController.AboutAsync", routes);
        Assert.Contains("GET /privacy controller M:Web.Controllers.HomeController.Privacy(System.Object)", routes);
        Assert.Contains("GET /Products/Edit/{id:int} page M:Web.Pages.Products.EditModel.OnGet(System.Int32)", routes);
        Assert.Contains("POST /Products/Edit/{id:int} page M:Web.Pages.Products.EditModel.OnPostDeleteAsync(System.Int32)", routes);
        // A constant resolves to its value; a parameter's group prefix can't be known.
        var admin = Assert.Single(graph.Routes!, r => r.Method == "DELETE");
        Assert.Equal("/admin/orders", admin.Path);
        Assert.True(admin.FixedPath);
        Assert.Contains("parameter", admin.Note);
        Assert.DoesNotContain(graph.Routes!, r => r.Path.Contains("test-only") || r.Handler.Contains("Helper"));
        Assert.Equal(12, graph.Routes!.Count);

        var inline = graph.Routes!.Single(r => r.Path == "/api/orders");
        Assert.Equal(["Web.Services.IOrders|M:Web.Services.IOrders.All"], inline.InlineCalls);
        Assert.Contains("{controller}/{action}", graph.Routes!.Single(r => r.Path == "/Home/Index").Note);
        Assert.True(graph.Routes!.Single(r => r.Path == "/ping").Inline);
        Assert.Empty(graph.Routes!.Single(r => r.Path == "/ping").InlineCalls);
        Assert.False(graph.Routes!.Single(r => r.Path == "/api/v2/orders/{id}").Inline);

        var views = graph.Views!.ToDictionary(v => v.Key[(v.Key.IndexOf('|') + 1)..], v => v.Value);
        Assert.Equal(["Views/Home/Index", "Views/Shared/Index", "Pages/Shared/Index"], views["M:Web.Controllers.HomeController.Index"]);
        Assert.Equal("Views/Home/Story", views["M:Web.Controllers.HomeController.AboutAsync"][0]);
        Assert.Equal("Views/Home/Privacy", views["M:Web.Controllers.HomeController.Privacy(System.Object)"][0]);
        Assert.Equal(["Web/Pages/Products/Edit"], views["M:Web.Pages.Products.EditModel.OnGet(System.Int32)"]);
    }

    [Fact]
    public void Page_flows_follow_calls_through_interfaces_to_the_view()
    {
        var index = new GraphIndex(_code.Analyze());
        var flows = Learning.RouteFlows(index);
        Assert.Equal("ASP.NET Core", flows.Framework);
        Assert.Contains("middleware", flows.Before);

        var list = flows.Routes.Single(r => r.Path == "/api/orders");
        Assert.True(list.Inline);
        // An inline lambda that calls nothing doesn't drag in everything else Program.cs runs.
        Assert.Empty(flows.Routes.Single(r => r.Path == "/ping").Steps);
        Assert.Equal(["IOrders.All", "Orders.All", "Store.Read", "Format.Lines"], list.Steps.Select(s => index.Display(s.Id)));
        Assert.Equal([1, 1, 2, 3], list.Steps.Select(s => s.Depth));

        var home = flows.Routes.Single(r => r.Path == "/Home/Index");
        Assert.Equal([new TemplateRef("Web/Views/Home/Index.cshtml", "Web/Views/Home/Index.cshtml")], home.Templates);
        Assert.Equal("Web/Views/Shared/Story.cshtml", flows.Routes.Single(r => r.Path == "/Home/About").Templates.Single().File);
        Assert.Equal("Web/Pages/Products/Edit.cshtml", flows.Routes.First(r => r.Kind == "page").Templates.Single().File);

        var template = Learning.ReadTemplate(_code.Root, "Web/Views/Home/Index.cshtml");
        Assert.Equal("<h1>Home</h1>", template.Code);
        foreach (var file in new[] { "Web/Web.csproj", "../outside.cshtml", "Web/Program.cs", null })
            Assert.Throws<ExplainException>(() => Learning.ReadTemplate(_code.Root, file));
    }

    [Fact]
    public void Tour_starts_at_program_and_follows_the_backbone()
    {
        var index = new GraphIndex(_code.Analyze());
        var steps = Learning.StartTour(index);
        Assert.Equal("entry", steps[0].Role);
        Assert.Equal("Program (top-level code)", steps[0].Name);
        Assert.Equal("Web/Program.cs", steps[0].File);
        Assert.Contains("starts running it", steps[0].Reason);
        Assert.Contains("handles 2 addresses: GET /api/orders, GET, HEAD /ping", steps[0].Reason);
        // OrderEndpoints leads to the most code, then the interface it calls and the class behind it.
        Assert.Equal(["OrderEndpoints", "IOrders", "Orders", "Store", "Format"], steps.Skip(1).TakeWhile(s => s.Role == "backbone").Select(s => s.Name));
        Assert.StartsWith("Program maps a web address to it.", steps[1].Reason);
        Assert.Contains("handles 2 addresses", steps[1].Reason);
        Assert.Equal(["HomeController", "ProductsController", "EditModel"], steps.Where(s => s.Role == "pages").Select(s => s.Name));
        Assert.StartsWith("A controller: ASP.NET Core finds it by itself", steps.First(s => s.Role == "pages").Reason);
        Assert.Equal("OrderEndpoints calls it. It leads on to 3 other types.", steps[2].Reason);
        Assert.StartsWith("It implements IOrders, so calls to IOrders end up here.", steps[3].Reason);
        Assert.DoesNotContain(steps, s => s.Name.Contains("Tests"));
    }

    [Fact]
    public void Explanations_are_saved_beside_the_code_and_go_stale_when_it_changes()
    {
        var index = new GraphIndex(_code.Analyze());
        var store = index.Graph.Types.Single(t => t.Name == "Store");
        var queue = Explanations.Queue(index);
        Assert.True(queue.TakeWhile(q => index.Types.ContainsKey(q.Id)).Count() >= index.Graph.Types.Count(t => t.Project == "Web") - 1);
        Assert.DoesNotContain(queue, q => q.Name.Contains("AppTests"));

        Assert.Throws<ExplainException>(() => Explanations.Save(index, store.Id, "  "));
        Assert.Throws<ExplainException>(() => Explanations.Save(index, "Nope", "Text"));
        Explanations.Save(index, store.Id, "  Reads the orders.  ");
        var saved = Explanations.For(index)[store.Id];
        Assert.Equal("Reads the orders.", saved.Summary);
        Assert.False(saved.Stale);
        Assert.DoesNotContain(Explanations.Queue(index), q => q.Id == store.Id);

        var file = _code.PathOf("Web/Services/Orders.cs");
        File.WriteAllText(file, File.ReadAllText(file).Replace("Format.Lines(\"a\")", "Format.Lines(\"b\")"));
        var changed = new GraphIndex(_code.Analyze());
        Assert.True(Explanations.For(changed)[store.Id].Stale);
        Assert.Contains(Explanations.Queue(changed), q => q.Id == store.Id && q.Stale);

        File.WriteAllText(Explanations.PathFor(_code.Root), "{ not json");
        Assert.Throws<ExplainException>(() => Explanations.For(changed));
    }

    [Fact]
    public void Glossary_describes_the_packages_and_sdk_a_project_uses()
    {
        var glossary = Glossary.ForProject(_code.Analyze());
        var packages = glossary.Select(g => g.Package).ToList();
        Assert.Contains("Microsoft.AspNetCore.App", packages);
        Assert.Contains("Microsoft.EntityFrameworkCore.SqlServer", packages);
        Assert.Equal("Serilog add-on", glossary.Single(g => g.Package == "Serilog.Sinks.Console").Term);
        Assert.Contains("Microsoft.AspNetCore", glossary.Single(g => g.Package == "Microsoft.AspNetCore.App").Namespaces);
    }

    [Fact]
    public void Ai_config_accepts_openrouter_or_a_server_url()
    {
        string? Env(string name) => name switch { "OPENROUTER_API_KEY" => "or-key", "CODETOLIFE_AI_KEY" => "local-key", _ => null };
        Assert.Null(AiConfig.From(null, null, Env));
        Assert.Throws<ArgumentException>(() => AiConfig.From(null, "m", Env));
        var router = AiConfig.From("OpenRouter", null, Env)!;
        Assert.Equal(("https://openrouter.ai/api/v1/chat/completions", "or-key", "openrouter/auto"), (router.Endpoint.ToString(), router.Key, router.Model));
        Assert.Throws<ArgumentException>(() => AiConfig.From("openrouter", null, _ => null));

        var local = AiConfig.From("http://100.99.57.40:8888/chat", "qwen", Env)!;
        Assert.Equal(("http://100.99.57.40:8888/v1/chat/completions", "local-key", "qwen", "100.99.57.40:8888"), (local.Endpoint.ToString(), local.Key, local.Model, local.Label));
        Assert.Equal("http://box/api/v1/chat/completions", AiConfig.From("http://box/api/v1/", null, _ => null)!.Endpoint.ToString());
        Assert.Throws<ArgumentException>(() => AiConfig.From("http://user:pw@box", null, Env));
        Assert.Throws<ArgumentException>(() => AiConfig.From("ftp://box", null, Env));

        Assert.Equal("It saves orders.", AiClient.CleanAnswer("<think>hmm</think>\n**Explanation:** \"It saves orders.\""));
        Assert.Equal("Done.", AiClient.CleanAnswer("half a thought</think>Done."));
    }

    [Fact]
    public async Task Ai_explanations_are_asked_for_and_saved()
    {
        var index = new GraphIndex(_code.Analyze());
        var orders = index.Graph.Types.Single(t => t.Name == "Orders");
        JsonNode? sent = null;
        string? auth = null;
        var http = new HttpClient(new Fake(async request =>
        {
            sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
            auth = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"Summary: Looks up orders."}}]}""", Encoding.UTF8, "application/json") };
        }));
        var config = new AiConfig("box", new Uri("http://box/v1/chat/completions"), "secret", "small");
        var (id, entry) = await AiClient.ExplainAsync(config, index, orders.Id, http);
        Assert.Equal(orders.Id, id);
        Assert.Equal("Looks up orders.", entry.Summary);
        Assert.Equal("Bearer secret", auth);
        Assert.Equal("small", sent!["model"]!.GetValue<string>());
        var prompt = sent["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("Explain this class from a C# project.", prompt);
        Assert.Contains("File: Web/Services/Orders.cs", prompt);
        Assert.Contains("Uses: IOrders", prompt);
        Assert.Contains("```csharp", prompt);
        Assert.Equal("Looks up orders.", Explanations.For(index)[orders.Id].Summary);

        var refused = new HttpClient(new Fake(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("no") })));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AiClient.AskAsync(config, [("user", "hi")], refused));
        Assert.Contains("CODETOLIFE_AI_KEY", error.Message);
    }

    sealed class Fake(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
