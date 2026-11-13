using CodeToLife.Analysis;

namespace CodeToLife.Insights;

/// <summary>A test that reaches a member, and in how many calls.</summary>
public sealed record TestHit(string TestId, int Depth);

/// <summary>How well a type's members are reached by tests.</summary>
/// <param name="Members">Members that run code (methods, constructors, properties with bodies).</param>
/// <param name="Covered">Of those, how many some test reaches.</param>
/// <param name="Nearest">The fewest calls from any test to any of its members, or null when none reaches it.</param>
public sealed record TypeCoverage(string TypeId, int Members, int Covered, int? Nearest);

/// <summary>Tests to run for a set of code, and the command that runs them.</summary>
public sealed record AffectedTests(IReadOnlyList<string> Tests, IReadOnlyList<string> Untested, IReadOnlyList<TestCommand> Commands);

/// <summary>One <c>dotnet test</c> run: a test project and the filter that picks the affected tests in it.</summary>
public sealed record TestCommand(string Project, string ProjectPath, string Filter, string CommandLine);

/// <summary>
/// Which tests exercise which code, worked out from the call graph: every member a test reaches
/// by following calls (through interfaces too) counts as covered by it. It's static, so it can't
/// see code only reached by reflection, and it counts code a test calls even if it doesn't assert
/// on it, but it needs no test run and works on half-written code.
/// </summary>
public sealed class TestMap
{
    /// <summary>xUnit, NUnit and MSTest attributes that mark a test method.</summary>
    public static readonly IReadOnlySet<string> TestAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Fact", "Theory", "SkippableFact", "SkippableTheory",
        "Test", "TestCase", "TestCaseSource", "TestOf",
        "TestMethod", "DataTestMethod",
    };

    readonly GraphIndex _index;
    readonly Dictionary<string, List<TestHit>> _byMember = new(StringComparer.Ordinal);
    readonly HashSet<string> _testProjects;

    TestMap(GraphIndex index, IReadOnlyList<string> tests, int maxDepth)
    {
        _index = index;
        Tests = tests;
        MaxDepth = maxDepth;
        _testProjects = tests.Select(t => index.TypeOf(t)?.Project).Where(p => p is not null).ToHashSet(StringComparer.Ordinal)!;
        foreach (var test in tests)
            foreach (var reach in index.Callees(test, maxDepth))
            {
                if (IsInTestProject(reach.MemberId)) continue;
                if (!_byMember.TryGetValue(reach.MemberId, out var hits)) _byMember[reach.MemberId] = hits = [];
                hits.Add(new TestHit(test, reach.Depth));
            }
        foreach (var hits in _byMember.Values) hits.Sort((a, b) => a.Depth.CompareTo(b.Depth));
    }

    public static TestMap Build(GraphIndex index, int maxDepth = 8)
    {
        var tests = index.Graph.Types
            .SelectMany(t => t.Members)
            .Where(IsTest)
            .Select(m => m.Id)
            .ToList();
        return new TestMap(index, tests, maxDepth);
    }

    public static bool IsTest(MemberNode member) =>
        member.Kind == "method" && member.Attributes.Any(TestAttributes.Contains);

    /// <summary>Every test method found.</summary>
    public IReadOnlyList<string> Tests { get; }
    public int MaxDepth { get; }
    public IReadOnlyCollection<string> TestProjects => _testProjects;

    public bool IsInTestProject(string memberOrTypeId) =>
        (_index.Types.GetValueOrDefault(memberOrTypeId) ?? _index.TypeOf(memberOrTypeId)) is { } type && _testProjects.Contains(type.Project);

    /// <summary>Tests that reach a member, nearest first. For a type: tests that reach any of its members.</summary>
    public IReadOnlyList<TestHit> For(string id)
    {
        if (_index.Types.TryGetValue(id, out var type))
            return type.Members
                .SelectMany(m => _byMember.GetValueOrDefault(m.Id) ?? [])
                .GroupBy(h => h.TestId).Select(g => g.MinBy(h => h.Depth)!)
                .OrderBy(h => h.Depth).ToList();
        return _byMember.GetValueOrDefault(id) ?? [];
    }

    /// <summary>Every member some test reaches, with the fewest calls it takes.</summary>
    public IReadOnlyDictionary<string, int> NearestDepths() =>
        _byMember.ToDictionary(p => p.Key, p => p.Value[0].Depth, StringComparer.Ordinal);

    /// <summary>Members that run code: worth asking "is this tested?" about.</summary>
    public static bool HasCode(MemberNode member) =>
        member.Kind is "method" or "constructor" or "operator" or "top-level statements" or "property" or "indexer"
        && !member.IsAbstract && member.Complexity > 0;

    public TypeCoverage CoverageOf(TypeNode type)
    {
        var members = type.Members.Where(HasCode).ToList();
        var hits = members.Select(m => _byMember.GetValueOrDefault(m.Id)).ToList();
        var nearest = hits.Where(h => h is { Count: > 0 }).Select(h => h![0].Depth).DefaultIfEmpty().Min();
        return new TypeCoverage(type.Id, members.Count, hits.Count(h => h is { Count: > 0 }), hits.Any(h => h is { Count: > 0 }) ? nearest : null);
    }

    /// <summary>Coverage for every type outside the test projects.</summary>
    public IReadOnlyList<TypeCoverage> Coverage() =>
        _index.Graph.Types.Where(t => !_testProjects.Contains(t.Project)).Select(CoverageOf).ToList();

    /// <summary>
    /// The tests to run after changing some code, and the code that no test reaches. Pass member
    /// or type ids; a type stands for all its members.
    /// </summary>
    public AffectedTests Affected(IEnumerable<string> ids)
    {
        var tests = new HashSet<string>(StringComparer.Ordinal);
        var untested = new List<string>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            // A changed test runs itself; a changed test class runs all of its tests.
            if (_index.Members.TryGetValue(id, out var m) && IsTest(m)) { tests.Add(id); continue; }
            if (_index.Types.TryGetValue(id, out var testType) && _testProjects.Contains(testType.Project))
            {
                foreach (var test in testType.Members.Where(IsTest)) tests.Add(test.Id);
                continue;
            }
            var hits = For(id);
            if (hits.Count == 0)
            {
                var runsCode = _index.Members.TryGetValue(id, out var member) ? HasCode(member) : _index.Types.ContainsKey(id);
                if (runsCode && !IsInTestProject(id)) untested.Add(id);
            }
            foreach (var hit in hits) tests.Add(hit.TestId);
        }
        var ordered = tests.OrderBy(t => _index.Display(t), StringComparer.Ordinal).ToList();
        return new AffectedTests(ordered, untested, Commands(ordered));
    }

    /// <summary><c>dotnet test</c> command lines that run exactly these tests, one per test project.</summary>
    public IReadOnlyList<TestCommand> Commands(IEnumerable<string> testIds)
    {
        var commands = new List<TestCommand>();
        foreach (var group in testIds.GroupBy(t => _index.TypeOf(t)?.Project ?? ""))
        {
            var project = _index.Graph.Projects.FirstOrDefault(p => p.Name == group.Key);
            if (project is null) continue;
            var filter = string.Join("|", group.Select(FullyQualifiedName).Distinct().Select(n => $"FullyQualifiedName~{n}"));
            commands.Add(new TestCommand(project.Name, project.Path, filter, $"dotnet test \"{project.Path}\" --filter \"{filter}\""));
        }
        return commands;
    }

    /// <summary>The name test runners filter on: <c>Namespace.Class.Method</c>, nested classes joined with <c>+</c>.</summary>
    public string FullyQualifiedName(string testId)
    {
        var type = _index.TypeOf(testId);
        var member = _index.Members.GetValueOrDefault(testId);
        if (type is null || member is null) return testId;
        var typeName = type.RuntimeName.Contains('`') ? type.RuntimeName[..type.RuntimeName.IndexOf('`')] : type.RuntimeName;
        return typeName + "." + member.Name;
    }
}
