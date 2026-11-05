using CodeToLife.Analysis;

namespace CodeToLife.Tests;

public class GraphBuilderTests
{
    const string Shop = """
        namespace Shop;

        /// <summary>Something to sell.</summary>
        public record Product(string Name, decimal Price);

        public interface IRule { decimal Apply(decimal total); }

        public abstract class RuleBase : IRule
        {
            public abstract decimal Apply(decimal total);
        }

        public sealed class Discount : RuleBase
        {
            public override decimal Apply(decimal total) => total * 0.9m;
        }

        public class Cart
        {
            readonly List<Product> _items = [];
            readonly IRule _rule = new Discount();

            public void Add(Product product)
            {
                if (product.Price < 0) throw new ArgumentException("negative");
                _items.Add(product);
            }

            public decimal Total() => _rule.Apply(_items.Sum(p => p.Price));

            public class Line { }
        }
        """;

    [Fact]
    public void Finds_types_with_their_kinds_and_docs()
    {
        using var code = new TempCode().Project("Shop").File("Shop/Shop.cs", Shop);
        var graph = code.Analyze();

        Assert.Equal("record", graph.Type("Shop.Product").Kind);
        Assert.Equal("interface", graph.Type("Shop.IRule").Kind);
        Assert.Contains("abstract", graph.Type("Shop.RuleBase").Modifiers);
        Assert.Equal("Something to sell.", graph.Type("Shop.Product").Summary);
        Assert.Equal("Shop.Cart", graph.Type("Shop.Cart.Line").ContainingType);
        Assert.Equal("Shop.Cart+Line", graph.Type("Shop.Cart.Line").RuntimeName);
        Assert.Empty(graph.Warnings);
    }

    [Fact]
    public void Connects_types_by_inheritance_composition_creation_and_calls()
    {
        using var code = new TempCode().Project("Shop").File("Shop/Shop.cs", Shop);
        var graph = code.Analyze();

        Assert.True(graph.HasEdge("Shop.Discount", "Shop.RuleBase", EdgeKind.Inherits));
        Assert.True(graph.HasEdge("Shop.RuleBase", "Shop.IRule", EdgeKind.Implements));
        Assert.True(graph.HasEdge("Shop.Cart", "Shop.Product", EdgeKind.Has), "List<Product> field");
        Assert.True(graph.HasEdge("Shop.Cart", "Shop.IRule", EdgeKind.Has));
        Assert.True(graph.HasEdge("Shop.Cart", "Shop.Discount", EdgeKind.Creates));
        Assert.True(graph.HasEdge("Shop.Cart", "Shop.IRule", EdgeKind.Calls));
        Assert.True(graph.HasEdge("Shop.Cart.Line", "Shop.Cart", EdgeKind.Nested));
    }

    [Fact]
    public void Records_member_calls_interface_implementations_and_complexity()
    {
        using var code = new TempCode().Project("Shop").File("Shop/Shop.cs", Shop);
        var graph = code.Analyze();

        Assert.True(graph.Calls("Cart.Total", "IRule.Apply"));
        var discountApply = graph.Type("Shop.Discount").Member("Apply");
        Assert.Contains(discountApply.Implements, id => id.Contains("RuleBase.Apply"));
        var baseApply = graph.Type("Shop.RuleBase").Member("Apply");
        Assert.Contains(baseApply.Implements, id => id.Contains("IRule.Apply"));

        var add = graph.Type("Shop.Cart").Member("Add");
        Assert.Equal(2, add.Complexity);
        Assert.True(add.BodyLine > add.Line, "the breakpoint line is inside the body");
    }

    [Fact]
    public void Top_level_statements_become_a_program_entry_point()
    {
        using var code = new TempCode()
            .Project("App", outputType: "Exe")
            .File("App/Program.cs", """
                var greeter = new Greeter();
                greeter.Hello();

                class Greeter { public void Hello() => Console.WriteLine("hi"); }
                """);
        var graph = code.Analyze();

        var main = graph.Type("Program").Members.Single(m => m.Kind == "top-level statements");
        Assert.True(graph.Calls(main.Id, "Greeter.Hello"));
        Assert.True(graph.HasEdge("Program", "Greeter", EdgeKind.Creates));
        Assert.Equal("exe", graph.Projects.Single().OutputKind);
    }

    [Fact]
    public void Cross_project_references_resolve_and_same_names_stay_apart()
    {
        using var code = new TempCode()
            .Project("Core").File("Core/Widget.cs", "namespace Core; public class Widget { public void Spin() { } }")
            .Project("App", references: "Core", outputType: "Exe")
            .File("App/Program.cs", "var w = new Core.Widget(); w.Spin(); class Helper { }")
            .Project("Other")
            .File("Other/Program.cs", "class Helper { }");
        var graph = code.Analyze();

        Assert.True(graph.HasEdge("Program", "Core.Widget", EdgeKind.Creates));
        Assert.Empty(graph.Warnings);
        Assert.Contains(graph.Types, t => t.Id == "Helper [App]");
        Assert.Contains(graph.Types, t => t.Id == "Helper [Other]");
    }

    [Fact]
    public void Partial_classes_are_one_type()
    {
        using var code = new TempCode().Project("P")
            .File("P/A.cs", "namespace P; public partial class Big { public void One() { } }")
            .File("P/B.cs", "namespace P; public partial class Big { public void Two() => One(); }");
        var graph = code.Analyze();

        var big = graph.Type("P.Big");
        Assert.Contains("partial", big.Modifiers);
        Assert.Equal(["One", "Two"], big.Members.Select(m => m.Name).Order());
        Assert.True(graph.Calls("Big.Two", "Big.One"));
    }

    [Fact]
    public void Generic_types_keep_their_arity_in_the_id()
    {
        using var code = new TempCode().Project("G").File("G/Repo.cs", """
            namespace G;
            public interface IRepo<T> { T Get(int id); }
            public class Thing { }
            public class ThingRepo : IRepo<Thing> { public Thing Get(int id) => new(); }
            """);
        var graph = code.Analyze();

        Assert.Equal("IRepo<T>", graph.Type("G.IRepo`1").Name);
        Assert.True(graph.HasEdge("G.ThingRepo", "G.IRepo`1", EdgeKind.Implements));
        Assert.True(graph.HasEdge("G.ThingRepo", "G.Thing", EdgeKind.Uses));
    }

    [Fact]
    public void A_folder_without_projects_still_gets_a_graph()
    {
        using var code = new TempCode().File("Loose.cs", "class A { B b = new(); } class B { }");
        var graph = code.Analyze();

        Assert.True(graph.HasEdge("A", "B", EdgeKind.Has));
    }
}
