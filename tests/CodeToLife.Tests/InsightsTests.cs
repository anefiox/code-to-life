using CodeToLife.Analysis;
using CodeToLife.Insights;
using CodeToLife.Runtime;

namespace CodeToLife.Tests;

/// <summary>The graph index, test map, impact analysis and architecture rules, on one small shop.</summary>
public class InsightsTests : IDisposable
{
    readonly TempCode _code = new TempCode()
        .Project("Shop")
        .File("Shop/Domain.cs", """
            namespace Shop.Domain;

            public interface IPricingRule { decimal Apply(decimal total); }

            public sealed class Discount : IPricingRule
            {
                public decimal Apply(decimal total) => total > 100 ? total * 0.9m : total;
            }

            public class Order
            {
                public decimal Total { get; private set; }
                // A domain type reaching into infrastructure: the rule below should catch it.
                public void Save() => new Shop.Data.Store().Write(this);
            }
            """)
        .File("Shop/Services.cs", """
            using Shop.Domain;
            namespace Shop.Services;

            public class Pricing(IEnumerable<IPricingRule> rules)
            {
                public decimal Total(decimal subtotal)
                {
                    var total = subtotal;
                    foreach (var rule in rules) total = rule.Apply(total);
                    return total;
                }
            }

            public class Checkout
            {
                readonly Pricing _pricing = new([new Discount()]);
                public decimal Run(decimal subtotal) => _pricing.Total(subtotal);
                public void Untested() { if (DateTime.Now.Year > 2000) Console.WriteLine("never tested"); }
                public object ByName() => typeof(Checkout).GetMethod("Run")!;
            }
            """)
        .File("Shop/Data.cs", """
            namespace Shop.Data;
            public class Store { public void Write(object o) { } }
            """)
        .Project("Shop.Tests", references: "Shop")
        .File("Shop.Tests/CheckoutTests.cs", """
            namespace Shop.Tests;

            public sealed class FactAttribute : Attribute { }

            public class CheckoutTests
            {
                [Fact]
                public void Total_applies_discount()
                {
                    var total = new Shop.Services.Checkout().Run(200);
                    Helper(total);
                }

                static void Helper(decimal d) { }
            }
            """);

    readonly CodeGraph _graph;
    readonly GraphIndex _index;

    public InsightsTests()
    {
        _graph = _code.Analyze();
        _index = new GraphIndex(_graph);
    }

    public void Dispose() => _code.Dispose();

    string Id(string query) => _index.Resolve(query).Single();

    [Fact]
    public void Attributes_are_recorded_without_the_suffix()
    {
        var test = _graph.Type("Shop.Tests.CheckoutTests").Member("Total_applies_discount");
        Assert.Contains("Fact", test.Attributes);
    }

    [Fact]
    public void Captured_primary_constructor_parameters_count_as_held()
    {
        // Pricing(IEnumerable<IPricingRule> rules) keeps `rules` because Total() reads it.
        Assert.True(_graph.HasEdge("Shop.Services.Pricing", "Shop.Domain.IPricingRule", EdgeKind.Has));
    }

    [Fact]
    public void Resolve_understands_short_names()
    {
        Assert.Equal("Shop.Services.Checkout", Id("Checkout"));
        Assert.EndsWith("Run(System.Decimal)", Id("Checkout.Run"));
        Assert.EndsWith("Run(System.Decimal)", Id("Shop.Services.Checkout.Run"));
        Assert.Equal(2, _index.Resolve("Apply").Count); // the interface member and its implementation
    }

    [Fact]
    public void Callees_follow_interface_calls_into_implementations()
    {
        var callees = _index.Callees(Id("Checkout.Run"), maxDepth: 3);
        var discount = callees.Single(r => r.MemberId == Id("Discount.Apply"));
        Assert.Equal("dispatch", discount.Kind);
        Assert.Equal(2, discount.Depth);
    }

    [Fact]
    public void Callers_come_through_the_interface_a_member_implements()
    {
        var callers = _index.Callers(Id("Discount.Apply"), maxDepth: 3);
        Assert.Contains(callers, r => r.MemberId == Id("Pricing.Total") && r.Kind == "dispatch" && r.Depth == 1);
        Assert.Contains(callers, r => r.MemberId == Id("Checkout.Run") && r.Depth == 2);
    }

    [Fact]
    public void Path_between_two_members()
    {
        var path = _index.PathBetween(Id("Checkout.Run"), Id("Discount.Apply"));
        Assert.NotNull(path);
        Assert.Equal(["Checkout.Run", "Pricing.Total", "Discount.Apply"], path!.Select(r => _index.Display(r.MemberId)));
    }

    [Fact]
    public void Test_map_finds_what_tests_reach()
    {
        var map = TestMap.Build(_index);
        var test = Id("CheckoutTests.Total_applies_discount");
        Assert.Equal([test], map.Tests);
        Assert.Equal(3, map.For(Id("Discount.Apply")).Single().Depth);
        Assert.Empty(map.For(Id("Checkout.Untested")));
        // Helpers inside the test project aren't "code under test".
        Assert.Empty(map.For(Id("CheckoutTests.Helper")));

        var affected = map.Affected([Id("Discount.Apply"), Id("Checkout.Untested")]);
        Assert.Equal([test], affected.Tests);
        Assert.Equal([Id("Checkout.Untested")], affected.Untested);
        var command = Assert.Single(affected.Commands);
        Assert.Equal("FullyQualifiedName~Shop.Tests.CheckoutTests.Total_applies_discount", command.Filter);

        var coverage = map.CoverageOf(_graph.Type("Shop.Services.Checkout"));
        Assert.Equal(1, coverage.Nearest);
        Assert.True(coverage.Covered < coverage.Members);
    }

    [Fact]
    public void Impact_lists_callers_contracts_tests_and_reflection()
    {
        var mapper = new FrameMapper(_graph);
        var tests = TestMap.Build(_index);
        var files = Directory.GetFiles(_code.Root, "*.cs", SearchOption.AllDirectories);

        var run = ImpactAnalyzer.Analyze(_index, tests, mapper, files, Id("Checkout.Run"));
        Assert.Contains(run.Items, i => i.Group == "tests" && i.MemberId == Id("CheckoutTests.Total_applies_discount"));
        Assert.Contains(run.Items, i => i.Group == "risks" && i.Detail.Contains("reflection"));

        var apply = ImpactAnalyzer.Analyze(_index, tests, mapper, files, Id("Discount.Apply"));
        Assert.Contains(apply.Items, i => i.Group == "contracts" && i.MemberId == Id("IPricingRule.Apply"));
        Assert.Contains(apply.Items, i => i.Group == "callers" && i.MemberId == Id("Pricing.Total") && i.Depth == 1);

        var rule = ImpactAnalyzer.Analyze(_index, tests, mapper, files, Id("IPricingRule"));
        Assert.Contains(rule.Items, i => i.Group == "implementations" && i.TypeId == "Shop.Domain.Discount");
        Assert.Contains(rule.Items, i => i.Group == "dependents" && i.TypeId == "Shop.Services.Pricing");
    }

    [Fact]
    public void Architecture_rules_report_violations()
    {
        var config = ArchitectureRules.Parse("""
            {
              // comments are allowed
              "layers": { "Domain": "Shop.Domain.*", "Data": "Shop.Data.*" },
              "rules": [
                { "from": "Domain", "notTo": "Data", "because": "the domain shouldn't know about storage" },
                { "to": "Discount", "onlyFrom": "Shop.Services.*", "kinds": ["creates"] },
                { "from": "Shop.Services.*", "notTo": "project:Shop.Tests" }
              ]
            }
            """);
        var result = ArchitectureRules.Check(_graph, config);
        Assert.Empty(result.Problems);
        var violation = Assert.Single(result.Violations);
        Assert.Equal(("Shop.Domain.Order", "Shop.Data.Store", 0), (violation.Source, violation.Target, violation.Rule));
        Assert.Equal("creates, calls, uses", violation.Kind);
        Assert.Contains("storage", violation.Message);
        Assert.NotNull(violation.File);

        // Tighten rule 2 so Checkout's `new Discount()` breaks it.
        config.Rules[1] = config.Rules[1] with { OnlyFrom = "Shop.Domain.*" };
        Assert.Contains(ArchitectureRules.Check(_graph, config).Violations, v => v.Source == "Shop.Services.Checkout" && v.Kind == "creates");
    }

    [Fact]
    public void Suggested_rules_have_a_layer_per_namespace()
    {
        var suggestion = ArchitectureRules.Suggest(_graph);
        Assert.Equal("Shop.Domain.*", suggestion.Layers!["Domain"]);
        Assert.Contains("Data", suggestion.Layers.Keys);
    }
}
