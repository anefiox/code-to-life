using CodeToLife.Analysis;

namespace CodeToLife.Insights;

/// <param name="Pattern">A JavaScript regular expression for the term's spellings, matched as whole words.</param>
public sealed record Concept(string Term, string Pattern, string Meaning);

/// <summary>
/// Framework terms a newcomer will meet, shown as tooltips wherever they appear in explanations,
/// the tour and page flows. Only the terms that fit the project's packages are sent.
/// </summary>
public static class Concepts
{
    // Empty "when" means every project; otherwise any package (or package prefix ending in '.') the project uses.
    static readonly (string Term, string Pattern, string[] When, string Meaning)[] Terms =
    [
        // ASP.NET Core
        ("middleware", "middlewares?", ["Microsoft.AspNetCore.App"], "Code that runs on every request before it reaches a page, such as HTTPS redirection, static files or sign-in. Program.cs adds it with app.Use…() calls, in order."),
        ("endpoint", "endpoints?", ["Microsoft.AspNetCore.App"], "A web address the app answers, with the code that handles it."),
        ("routing", "routing|routes?", [], "Matching a request's address to the code that handles it."),
        ("minimal API", "minimal APIs?|MapGet|MapPost|MapPut|MapDelete|MapGroup", ["Microsoft.AspNetCore.App"], "Addresses mapped straight to a function in Program.cs (or an extension method), with app.MapGet(\"/path\", handler)."),
        ("controller", "controllers?", ["Microsoft.AspNetCore.App"], "A class whose public methods (actions) answer web requests. ASP.NET Core finds controllers by itself."),
        ("action", "actions?", ["Microsoft.AspNetCore.App"], "A public method on a controller that handles one web address."),
        ("Razor Page", "Razor Pages?|PageModel|OnGet|OnPost", ["Microsoft.AspNetCore.App"], "A page made of a .cshtml file and its code in a PageModel class. Its address comes from where the file sits under Pages/."),
        ("Razor view", "Razor views?|\\.cshtml|views?", ["Microsoft.AspNetCore.App"], "An HTML template with C# mixed in, filled in on the server and sent to the browser."),
        ("layout", "layouts?|_Layout", ["Microsoft.AspNetCore.App"], "The template every page shares, with the header and footer. Pages are rendered inside it at @RenderBody()."),
        ("partial view", "partials?|partial views?", ["Microsoft.AspNetCore.App"], "A small template included in other views, such as a menu or a form."),
        ("tag helper", "tag helpers?|asp-\\w+", ["Microsoft.AspNetCore.App"], "Attributes like asp-controller or asp-for that Razor turns into the right HTML, such as links and form fields."),
        ("view component", "view components?|ViewComponent", ["Microsoft.AspNetCore.App"], "A reusable piece of page with its own C# logic, rendered with Component.InvokeAsync."),
        ("model binding", "model binding|\\[FromBody\\]|\\[FromQuery\\]|\\[FromRoute\\]", ["Microsoft.AspNetCore.App"], "ASP.NET Core filling in a handler's parameters from the address, query string, form or JSON body."),
        ("antiforgery token", "antiforgery|CSRF|ValidateAntiForgeryToken", ["Microsoft.AspNetCore.App"], "A hidden token in each form that stops other websites from submitting forms to this one (cross-site request forgery)."),
        ("authentication", "authentication|UseAuthentication|sign-in", [], "Working out who the user is, for example from a cookie or a token."),
        ("authorization", "authorization|UseAuthorization|\\[Authorize\\]", [], "Deciding what a signed-in user is allowed to do or see."),
        ("CORS", "CORS", [], "Cross-origin resource sharing: which other websites may call this app's API from the browser."),
        ("Kestrel", "Kestrel", ["Microsoft.AspNetCore.App"], "The web server built into ASP.NET Core. It receives the requests."),
        ("health check", "health ?checks?|/health", [], "An address the hosting platform calls regularly to check the app is running."),
        ("SignalR", "SignalR|hubs?", ["Microsoft.AspNetCore.SignalR", "Microsoft.AspNetCore.App"], "Real-time messages between the server and the browser over a long-lived connection."),
        // Hosting, DI, configuration, logging
        ("dependency injection", "dependency injection|DI container|inject(?:ed|s)?", [], "Classes ask for what they need in their constructor, and .NET creates and passes it in, so they don't build their own."),
        ("service lifetime", "AddScoped|AddSingleton|AddTransient|scoped|singleton|transient", ["Microsoft.Extensions.DependencyInjection"], "How long a registered service lives: Singleton is one for the app, Scoped one per request, Transient a new one each time."),
        ("service", "services?", [], "A class registered in Program.cs so other classes can ask for it in their constructors."),
        ("IOptions", "IOptions(?:Snapshot|Monitor)?|options class(?:es)?", ["Microsoft.Extensions.Configuration", "Microsoft.Extensions.Options"], "A typed settings class filled in from appsettings.json; classes ask for IOptions<T> to read it."),
        ("appsettings.json", "appsettings(?:\\.\\w+)?\\.json", [], "The app's settings file. appsettings.{Environment}.json overrides it per environment, and environment variables override both."),
        ("environment variable", "environment variables?|env vars?", [], "A setting given to the app by whatever starts it, such as ASPNETCORE_ENVIRONMENT=Development. Section__Key overrides Section:Key in appsettings.json."),
        ("environment", "ASPNETCORE_ENVIRONMENT|Development|Production|Staging", [], "Which kind of place the app runs in. It picks the appsettings file and turns on things like the developer error page."),
        ("ILogger", "ILogger|logging|logs?", [], "Writes messages about what the app is doing, at levels from Trace to Critical, so problems can be found."),
        ("hosted service", "hosted services?|BackgroundService|IHostedService", ["Microsoft.Extensions.Hosting"], "Code that runs in the background for as long as the app runs, such as a queue reader."),
        ("user secrets", "user secrets|user-secrets", [], "Settings such as passwords kept outside the project folder on your machine, so they are never committed."),
        // Data
        ("Entity Framework Core", "Entity Framework(?: Core)?|EF Core", ["Microsoft.EntityFrameworkCore."], "The database library: classes map to tables, and LINQ queries become SQL."),
        ("DbContext", "DbContext", ["Microsoft.EntityFrameworkCore."], "The class that represents a database session in Entity Framework Core. Each DbSet<T> on it is a table."),
        ("DbSet", "DbSets?", ["Microsoft.EntityFrameworkCore."], "A table in Entity Framework Core: query it with LINQ, add and remove rows, then SaveChanges."),
        ("migration", "migrations?", ["Microsoft.EntityFrameworkCore."], "A generated step that changes the database to match the code. Create one with dotnet ef migrations add."),
        ("Dapper", "Dapper", ["Dapper"], "A small library that runs your SQL and fills objects from the results."),
        ("repository", "repositor(?:y|ies)", [], "A class that hides how data is stored, with methods like GetById and Add."),
        ("entity", "entit(?:y|ies)", [], "A class that represents something stored in the database, usually with an Id."),
        ("DTO", "DTOs?", [], "Data transfer object: a simple class for data sent to or from an API, separate from the database classes."),
        // C# language
        ("async/await", "async|await|Task<\\w+>|Task", [], "Code that waits for something slow, such as a database or network call, without blocking a thread. Async methods return a Task."),
        ("LINQ", "LINQ|\\.Where\\(|\\.Select\\(", [], "Query methods such as Where, Select and OrderBy for filtering and shaping lists, and database queries in EF Core."),
        ("interface", "interfaces?", [], "A contract listing methods a class promises to have. Code depends on the interface, so the class behind it can be swapped, for example in tests."),
        ("attribute", "attributes?|\\[\\w+\\]", [], "A label in square brackets, such as [HttpGet] or [Fact], that frameworks read to decide what to do with the code."),
        ("record", "records?", [], "A class for holding data, compared by its values and easy to copy with changes using with."),
        ("extension method", "extension methods?", [], "A static method written as if it belonged to another type, such as app.MapOrderAdmin()."),
        ("nullable reference types", "nullable|\\?\\?|null", [], "C# warns when something that could be null is used without a check; a ? after a type means it may be null."),
        ("generic", "generics?|<T>", [], "Code written once for many types, such as List<T>, where T is filled in when it is used."),
        ("top-level statements", "top-level (?:code|statements)", [], "Code written straight into Program.cs without a class or Main method. The program starts there."),
        // Tooling and tests
        ("NuGet", "NuGet|packages?", [], "The .NET package manager. Packages are listed as PackageReference items in each .csproj file."),
        (".csproj", "\\.csproj|project file", [], "The project file: its target framework, the packages it uses and the projects it references."),
        ("xUnit", "xUnit|\\[Fact\\]|\\[Theory\\]", ["xunit", "xunit.v3"], "The test framework: [Fact] marks a test, [Theory] a test run once per [InlineData] row."),
        ("NUnit", "NUnit|\\[Test\\]|\\[TestCase\\]", ["NUnit"], "The test framework: [Test] marks a test, [TestCase] runs it with different data."),
        ("MSTest", "MSTest|\\[TestMethod\\]", ["MSTest.TestFramework", "MSTest"], "The test framework: [TestMethod] marks a test inside a [TestClass]."),
        ("mock", "mocks?|mocked|fakes?|stubs?|Moq|NSubstitute", [], "A stand-in used in a test instead of a real dependency, so the test only checks one class."),
        ("integration test", "integration tests?|WebApplicationFactory", [], "A test that runs real parts together, such as the whole web app in memory with a test database."),
    ];

    public static IReadOnlyList<Concept> For(CodeGraph graph)
    {
        var packages = graph.Projects.SelectMany(p => Glossary.PackagesOf(p.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Uses(string name) => name.EndsWith('.') ? packages.Any(p => p.StartsWith(name, StringComparison.OrdinalIgnoreCase)) : packages.Contains(name);
        return Terms.Where(t => t.When.Length == 0 || t.When.Any(Uses)).Select(t => new Concept(t.Term, t.Pattern, t.Meaning)).ToList();
    }
}
