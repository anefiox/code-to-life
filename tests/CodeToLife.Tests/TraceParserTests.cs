using CodeToLife.Insights;
using CodeToLife.Runtime;

namespace CodeToLife.Tests;

public class TraceParserTests : IDisposable
{
    readonly TempCode _code = new TempCode()
        .Project("App", outputType: "Exe")
        .File("App/Program.cs", """
            await new Shop.OrderService().CheckoutAsync(new Shop.Order());

            namespace Shop
            {
                public class Order { public Order() { } }

                public class OrderService
                {
                    public async Task CheckoutAsync(Order order)
                    {
                        await Task.Yield();
                        new Inventory.Picker().Pick();
                    }

                    public class Inventory
                    {
                        public class Picker { public void Pick() => throw new InvalidOperationException("Out of stock"); }
                    }
                }
            }
            """);

    readonly FrameMapper _mapper;

    public TraceParserTests() => _mapper = new FrameMapper(_code.Analyze());

    public void Dispose() => _code.Dispose();

    const string Trace = """
        Unhandled exception. System.ApplicationException: Checkout failed
         ---> System.InvalidOperationException: Out of stock
           at Shop.OrderService.Inventory.Picker.Pick() in C:\build\App\Program.cs:line 16
           at Shop.OrderService.CheckoutAsync(Order order) in C:\build\App\Program.cs:line 12
           --- End of inner exception stack trace ---
           at Shop.OrderService.CheckoutAsync(Order order) in C:\build\App\Program.cs:line 14
           at Program.<Main>$(String[] args) in C:\build\App\Program.cs:line 1
           at Program.<Main>(String[] args)
        """;

    [Fact]
    public void Parses_exceptions_and_inner_exceptions()
    {
        var parsed = TraceParser.Parse(Trace, _mapper);
        Assert.Equal(["System.ApplicationException", "System.InvalidOperationException"], parsed.Exceptions.Select(e => e.Type));
        Assert.Equal("Out of stock", parsed.Exceptions[1].Message);
        Assert.Equal(2, parsed.Exceptions[1].Frames.Count);
        Assert.Equal(3, parsed.Exceptions[0].Frames.Count);
    }

    [Fact]
    public void Maps_frames_by_name_even_when_the_paths_are_from_another_machine()
    {
        var stack = TraceParser.Parse(Trace, _mapper).Stack;
        Assert.Equal("Shop.OrderService.Inventory.Picker", stack[0].TypeId);
        Assert.EndsWith("Pick", stack[0].MemberId);
        Assert.EndsWith("CheckoutAsync(Shop.Order)", stack[1].MemberId);
        // Both the top-level frame and the compiler's async wrapper around it map to the top-level statements.
        Assert.All(stack.Where(f => f.TypeId == "Program"), f => Assert.Contains("{Main}$", f.MemberId));
        // Run order: the throw site first, the entry point last; repeated frames from the outer exception kept once each.
        Assert.Equal(5, stack.Count);
    }

    [Fact]
    public void Splits_constructors_and_generic_methods()
    {
        Assert.Equal(("Shop.Order", ".ctor"), TraceParser.SplitMethod("Shop.Order..ctor"));
        Assert.Equal(("Shop.Repo`1", "Add"), TraceParser.SplitMethod("Shop.Repo`1.Add[T]"));
        Assert.Equal(("Program.<>c", "<<Main>$>b__0_0"), TraceParser.SplitMethod("Program.<>c.<<Main>$>b__0_0"));
    }

    [Fact]
    public void Groups_failures_in_text_and_json_logs()
    {
        var pick = "   at Shop.OrderService.Inventory.Picker.Pick() in /src/App/Program.cs:line 16";
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            Timestamp = "2026-11-13T10:00:00Z",
            Level = "Error",
            Message = "Checkout failed for order 7",
            Exception = "System.InvalidOperationException: Out of stock\n" + pick + "\n   at Shop.OrderService.CheckoutAsync(Order order)",
        });
        var log = string.Join('\n',
            "info: Shop[0] Starting",
            "fail: Shop[0] Checkout failed",
            "      System.InvalidOperationException: Out of stock",
            "      " + pick.Trim(),
            "         at Shop.OrderService.CheckoutAsync(Order order)",
            "info: Shop[0] Next order",
            json,
            "fail: Shop[0] Something else",
            "System.NullReferenceException: Object reference not set to an instance of an object.",
            "   at Shop.Order..ctor()",
            "done");

        var scan = TraceParser.ScanLog(log, _mapper);
        Assert.Equal(3, scan.Failures);
        Assert.Equal(2, scan.Groups.Count);
        var top = scan.Groups[0];
        Assert.Equal(2, top.Count);
        Assert.Equal("System.InvalidOperationException", top.Title);
        Assert.True(scan.TypeHits["Shop.Order"] >= 1);
    }
}
