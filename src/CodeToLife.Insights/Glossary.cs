using System.Xml.Linq;
using CodeToLife.Analysis;

namespace CodeToLife.Insights;

/// <param name="Namespaces">The namespaces a file <c>using</c>s when it uses the package, to show it beside that file's code.</param>
public sealed record GlossaryEntry(string Package, string Term, string Meaning, IReadOnlyList<string> Namespaces);

/// <summary>
/// Plain-English descriptions of common NuGet packages and frameworks, written for someone new to
/// .NET. Keys are package ids; a key ending in "." matches every package that starts with it.
/// </summary>
public static class Glossary
{
    static readonly Dictionary<string, (string Term, string Meaning, string[] Namespaces)> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        // Frameworks the project's SDK brings in
        ["Microsoft.AspNetCore.App"] = ("ASP.NET Core", "The web framework. It receives each browser request, runs the middleware set up in Program.cs, then calls the controller, page or endpoint that matches the address.", ["Microsoft.AspNetCore"]),
        ["Microsoft.Extensions.Hosting"] = (".NET Generic Host", "Starts the app, wires up dependency injection, configuration and logging, and keeps background services running.", ["Microsoft.Extensions.Hosting"]),
        ["Microsoft.Extensions.DependencyInjection"] = ("Dependency injection", "Creates objects for you and hands them to the classes that ask for them in their constructors, so classes don't build their own dependencies.", ["Microsoft.Extensions.DependencyInjection"]),
        ["Microsoft.Extensions.Configuration"] = ("Configuration", "Reads settings from appsettings.json, environment variables and other sources, so they can change between your machine, test and live.", ["Microsoft.Extensions.Configuration"]),
        ["Microsoft.Extensions.Logging"] = ("Logging", "The standard way to write log messages; where they end up (console, files, a monitoring service) is set up separately.", ["Microsoft.Extensions.Logging"]),
        ["Microsoft.Extensions.Options"] = ("Options", "Binds a section of configuration to a settings class, which is then injected where it's needed.", ["Microsoft.Extensions.Options"]),
        ["Microsoft.Extensions.Http"] = ("HttpClientFactory", "Creates and reuses HttpClient instances for calling other web services.", ["Microsoft.Extensions.Http"]),
        ["Microsoft.Extensions."] = ("Microsoft.Extensions add-on", "Part of the standard .NET building blocks for hosting, configuration, logging and dependency injection.", []),

        // Web
        ["Microsoft.AspNetCore.Authentication.JwtBearer"] = ("JWT bearer sign-in", "Checks the access token sent with each API request to work out who is calling.", ["Microsoft.AspNetCore.Authentication.JwtBearer"]),
        ["Microsoft.AspNetCore.Authentication.OpenIdConnect"] = ("OpenID Connect sign-in", "Signs users in through an external identity provider such as Entra ID, Auth0 or GOV.UK One Login.", ["Microsoft.AspNetCore.Authentication.OpenIdConnect"]),
        ["Microsoft.Identity.Web"] = ("Microsoft Identity Web", "Signs users in with Microsoft Entra ID (Azure AD) and gets tokens for calling other APIs.", ["Microsoft.Identity.Web"]),
        ["Microsoft.AspNetCore.OpenApi"] = ("OpenAPI", "Describes the app's API endpoints in a standard document that tools can read.", ["Microsoft.AspNetCore.OpenApi"]),
        ["Microsoft.AspNetCore."] = ("ASP.NET Core add-on", "An extra piece of the ASP.NET Core web framework.", []),
        ["Swashbuckle.AspNetCore"] = ("Swagger", "Generates an interactive page that documents and lets you try the app's API endpoints.", ["Swashbuckle", "Microsoft.OpenApi"]),
        ["Scalar.AspNetCore"] = ("Scalar", "An interactive page that documents and lets you try the app's API endpoints.", ["Scalar.AspNetCore"]),
        ["GovUk.Frontend.AspNetCore"] = ("GOV.UK Frontend", "GOV.UK Design System components and tag helpers for Razor pages.", ["GovUk.Frontend.AspNetCore"]),
        ["Yarp.ReverseProxy"] = ("YARP", "A reverse proxy: forwards incoming requests on to other services.", ["Yarp"]),
        ["Grpc.AspNetCore"] = ("gRPC", "Fast service-to-service calls defined by .proto contract files.", ["Grpc"]),
        ["Microsoft.AspNetCore.SignalR"] = ("SignalR", "Real-time messages between the server and the browser, for live updates.", ["Microsoft.AspNetCore.SignalR"]),

        // Data
        ["Microsoft.EntityFrameworkCore"] = ("Entity Framework Core", "Maps C# classes to database tables, so the code can query and save data without writing SQL. A DbContext class is the doorway to the database.", ["Microsoft.EntityFrameworkCore"]),
        ["Microsoft.EntityFrameworkCore.SqlServer"] = ("EF Core for SQL Server", "Lets Entity Framework Core talk to Microsoft SQL Server or Azure SQL.", []),
        ["Npgsql.EntityFrameworkCore.PostgreSQL"] = ("EF Core for PostgreSQL", "Lets Entity Framework Core talk to a PostgreSQL database.", []),
        ["Microsoft.EntityFrameworkCore.Sqlite"] = ("EF Core for SQLite", "Lets Entity Framework Core use SQLite, a small database stored in a single file.", []),
        ["Microsoft.EntityFrameworkCore.Design"] = ("EF Core design tools", "Used by the dotnet ef command to create migrations: scripts that change the database to match the code.", []),
        ["Microsoft.EntityFrameworkCore."] = ("EF Core add-on", "An extra piece of Entity Framework Core, such as a database provider or tooling.", []),
        ["Dapper"] = ("Dapper", "Runs SQL you write yourself and turns the results into C# objects.", ["Dapper"]),
        ["Microsoft.Data.SqlClient"] = ("SqlClient", "Connects to Microsoft SQL Server and runs commands against it.", ["Microsoft.Data.SqlClient"]),
        ["Npgsql"] = ("Npgsql", "Connects to a PostgreSQL database.", ["Npgsql"]),
        ["MongoDB.Driver"] = ("MongoDB driver", "Connects to a MongoDB document database.", ["MongoDB"]),
        ["StackExchange.Redis"] = ("Redis client", "Talks to Redis, a fast shared key-value store often used for caching and sessions.", ["StackExchange.Redis"]),
        ["Azure.Storage.Blobs"] = ("Azure Blob Storage", "Stores and reads files in Azure.", ["Azure.Storage.Blobs"]),
        ["Azure.Messaging.ServiceBus"] = ("Azure Service Bus", "Sends and receives messages between services through Azure queues and topics.", ["Azure.Messaging.ServiceBus"]),
        ["Azure.Identity"] = ("Azure Identity", "Signs the app in to Azure services, using a managed identity in the cloud or your own login locally.", ["Azure.Identity"]),
        ["Azure."] = ("Azure SDK", "A client library for an Azure cloud service.", []),
        ["AWSSDK."] = ("AWS SDK", "A client library for an Amazon Web Services cloud service.", ["Amazon"]),

        // Application structure
        ["MediatR"] = ("MediatR", "Sends a request object to the one handler class for it, so controllers stay thin. Look for classes that implement IRequestHandler.", ["MediatR"]),
        ["AutoMapper"] = ("AutoMapper", "Copies data between similar classes (for example an entity and a DTO) using mapping profiles.", ["AutoMapper"]),
        ["Mapster"] = ("Mapster", "Copies data between similar classes, such as an entity and a DTO.", ["Mapster"]),
        ["FluentValidation"] = ("FluentValidation", "Rules that check incoming data is valid, written in validator classes.", ["FluentValidation"]),
        ["FluentValidation.AspNetCore"] = ("FluentValidation for ASP.NET Core", "Runs FluentValidation rules on incoming requests automatically.", []),
        ["Polly"] = ("Polly", "Retries, timeouts and circuit breakers, so calls to other services cope with brief failures.", ["Polly"]),
        ["Microsoft.Extensions.Http.Resilience"] = ("HTTP resilience", "Adds retries and timeouts to HttpClient calls (built on Polly).", []),
        ["Hangfire.Core"] = ("Hangfire", "Runs background jobs later or on a schedule, stored in a database so they survive restarts.", ["Hangfire"]),
        ["Quartz"] = ("Quartz.NET", "Runs jobs on a schedule, like cron.", ["Quartz"]),
        ["MassTransit"] = ("MassTransit", "Sends and receives messages between services over a message broker.", ["MassTransit"]),
        ["Microsoft.FeatureManagement"] = ("Feature flags", "Turns features on and off from configuration without changing code.", ["Microsoft.FeatureManagement"]),

        // Serialization, logging and monitoring
        ["Newtonsoft.Json"] = ("Json.NET", "Converts C# objects to and from JSON.", ["Newtonsoft.Json"]),
        ["System.Text.Json"] = ("System.Text.Json", "The built-in JSON reader and writer.", ["System.Text.Json"]),
        ["Serilog"] = ("Serilog", "Structured logging: log messages with named values that are easy to search.", ["Serilog"]),
        ["Serilog.AspNetCore"] = ("Serilog for ASP.NET Core", "Routes ASP.NET Core's logs through Serilog and logs each request.", []),
        ["Serilog."] = ("Serilog add-on", "Sends Serilog logs somewhere (a sink) or adds extra information to them.", []),
        ["NLog"] = ("NLog", "A logging library that writes to files, the console and other targets.", ["NLog"]),
        ["Microsoft.ApplicationInsights.AspNetCore"] = ("Application Insights", "Sends requests, errors and performance data to Azure Monitor.", ["Microsoft.ApplicationInsights"]),
        ["OpenTelemetry."] = ("OpenTelemetry", "Collects traces, metrics and logs in a standard format for monitoring tools.", ["OpenTelemetry"]),
        ["AspNetCore.HealthChecks."] = ("Health checks", "Endpoints that report whether the app and the things it depends on are working.", ["HealthChecks"]),

        // Tests
        ["xunit"] = ("xUnit", "The test framework. Methods marked [Fact] or [Theory] are tests.", ["Xunit"]),
        ["xunit.v3"] = ("xUnit", "The test framework. Methods marked [Fact] or [Theory] are tests.", ["Xunit"]),
        ["NUnit"] = ("NUnit", "The test framework. Methods marked [Test] are tests.", ["NUnit"]),
        ["MSTest.TestFramework"] = ("MSTest", "The test framework. Methods marked [TestMethod] are tests.", ["Microsoft.VisualStudio.TestTools"]),
        ["MSTest"] = ("MSTest", "The test framework. Methods marked [TestMethod] are tests.", ["Microsoft.VisualStudio.TestTools"]),
        ["Moq"] = ("Moq", "Makes fake versions of interfaces for tests, so a class can be tested without its real dependencies.", ["Moq"]),
        ["NSubstitute"] = ("NSubstitute", "Makes fake versions of interfaces for tests.", ["NSubstitute"]),
        ["FakeItEasy"] = ("FakeItEasy", "Makes fake versions of interfaces for tests.", ["FakeItEasy"]),
        ["FluentAssertions"] = ("FluentAssertions", "Readable test checks such as result.Should().Be(5).", ["FluentAssertions"]),
        ["Shouldly"] = ("Shouldly", "Readable test checks such as result.ShouldBe(5).", ["Shouldly"]),
        ["AutoFixture"] = ("AutoFixture", "Fills test objects with made-up data so tests only set the values they care about.", ["AutoFixture"]),
        ["Bogus"] = ("Bogus", "Generates realistic fake data (names, addresses) for tests and seeding.", ["Bogus"]),
        ["Microsoft.AspNetCore.Mvc.Testing"] = ("ASP.NET Core test host", "Runs the whole web app in memory so tests can send it real requests.", ["Microsoft.AspNetCore.Mvc.Testing"]),
        ["Testcontainers"] = ("Testcontainers", "Starts real databases and services in Docker for tests.", ["Testcontainers", "DotNet.Testcontainers"]),
        ["coverlet.collector"] = ("Coverlet", "Measures which lines the tests run (code coverage).", []),
        ["Microsoft.NET.Test.Sdk"] = ("Test SDK", "Lets dotnet test find and run the tests.", []),
    };

    /// <summary>The packages (and SDK frameworks) the graph's projects use, described.</summary>
    public static IReadOnlyList<GlossaryEntry> ForProject(CodeGraph graph) =>
        For(graph.Projects.SelectMany(p => PackagesOf(p.Path)));

    public static IReadOnlyList<GlossaryEntry> For(IEnumerable<string> packages)
    {
        var result = new List<GlossaryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in packages.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(name) || Lookup(name) is not { } entry) continue;
            result.Add(new GlossaryEntry(name, entry.Term, entry.Meaning, entry.Namespaces.Length > 0 ? entry.Namespaces : [name]));
        }
        return result;
    }

    static (string Term, string Meaning, string[] Namespaces)? Lookup(string name)
    {
        if (Entries.TryGetValue(name, out var exact)) return exact;
        // The longest matching prefix: Microsoft.EntityFrameworkCore.Tools → Microsoft.EntityFrameworkCore.
        var prefix = Entries.Keys.Where(k => k.EndsWith('.') && name.StartsWith(k, StringComparison.OrdinalIgnoreCase)).MaxBy(k => k.Length);
        return prefix is null ? null : Entries[prefix];
    }

    /// <summary>A project's PackageReferences, plus the frameworks its SDK brings in.</summary>
    public static IEnumerable<string> PackagesOf(string csproj)
    {
        if (!csproj.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(csproj)) yield break;
        XDocument doc;
        try { doc = XDocument.Load(csproj); }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException) { yield break; }
        var sdk = doc.Root?.Attribute("Sdk")?.Value ?? "";
        if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase)) yield return "Microsoft.AspNetCore.App";
        if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) || sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Microsoft.Extensions.Hosting";
            yield return "Microsoft.Extensions.DependencyInjection";
            yield return "Microsoft.Extensions.Configuration";
            yield return "Microsoft.Extensions.Logging";
        }
        foreach (var element in doc.Descendants())
            if (element.Name.LocalName is "PackageReference" or "FrameworkReference" && element.Attribute("Include")?.Value is { Length: > 0 } include)
                yield return include;
    }
}
