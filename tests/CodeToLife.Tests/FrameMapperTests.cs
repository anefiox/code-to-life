using CodeToLife.Runtime;

namespace CodeToLife.Tests;

public class FrameMapperTests : IDisposable
{
    readonly TempCode _code = new TempCode()
        .Project("App", outputType: "Exe")
        .File("App/Program.cs", """
            await new Shop.Service().RunAsync();

            namespace Shop
            {
                public class Service
                {
                    public int Count { get; set; }

                    public Service() { }

                    public async Task RunAsync()
                    {
                        var items = new[] { 1, 2, 3 };
                        var total = items.Select(i => i * Count).Sum();
                        await Task.Delay(total);
                    }

                    public class Nested { public void Work() { } }
                }

                public class Box<T> { public T? Value { get; set; } public void Fill(T value) => Value = value; }
            }
            """);

    readonly FrameMapper _mapper;

    public FrameMapperTests() => _mapper = new FrameMapper(_code.Analyze());

    public void Dispose() => _code.Dispose();

    [Theory]
    [InlineData("Shop.Service", "RunAsync", "Shop.Service", "RunAsync")]
    [InlineData("Shop.Service+<RunAsync>d__5", "MoveNext", "Shop.Service", "RunAsync")]
    [InlineData("Shop.Service+<>c__DisplayClass5_0", "<RunAsync>b__0", "Shop.Service", "RunAsync")]
    [InlineData("Shop.Service", ".ctor", "Shop.Service", ".ctor")]
    [InlineData("Shop.Service", "get_Count", "Shop.Service", "Count")]
    [InlineData("Shop.Service+Nested", "Work", "Shop.Service.Nested", "Work")]
    [InlineData("Shop.Box<System.Int32>", "Fill", "Shop.Box`1", "Fill")]
    [InlineData("Shop.Box`1", "Fill", "Shop.Box`1", "Fill")]
    [InlineData("Program+<<Main>$>d__0", "MoveNext", "Program", "<top-level statements>")]
    [InlineData("Program", "<Main>$", "Program", "<top-level statements>")]
    public void Maps_runtime_frames_back_to_source_members(string clrType, string clrMethod, string typeId, string memberName)
    {
        var location = _mapper.FromRuntime(clrType, clrMethod);

        Assert.NotNull(location);
        Assert.Equal(typeId, location.TypeId);
        Assert.NotNull(location.MemberId);
        Assert.EndsWith(memberName, MemberName(location.MemberId!), StringComparison.Ordinal);
    }

    string MemberName(string id) =>
        _code.Analyze().Types.SelectMany(t => t.Members).Single(m => m.Id == id).Name;

    [Fact]
    public void Ignores_frames_from_outside_the_codebase()
    {
        Assert.Null(_mapper.FromRuntime("System.Threading.Tasks.Task", "Wait"));
        Assert.Null(_mapper.FromRuntime(null, null));
    }

    [Fact]
    public void Maps_a_source_line_to_the_innermost_member()
    {
        var file = _code.PathOf("App/Program.cs");
        var inRunAsync = _mapper.FromSource(file, 14); // var total = ...
        var inWork = _mapper.FromSource(file, 18);
        var topLevel = _mapper.FromSource(file, 1);

        Assert.Equal("Shop.Service", inRunAsync?.TypeId);
        Assert.Contains("RunAsync", inRunAsync?.MemberId);
        Assert.Equal("Shop.Service.Nested", inWork?.TypeId);
        Assert.Equal("Program", topLevel?.TypeId);
        Assert.Null(_mapper.FromSource(Path.Combine(_code.Root, "Elsewhere.cs"), 1));
    }
}
