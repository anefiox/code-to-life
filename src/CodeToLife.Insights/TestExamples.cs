namespace CodeToLife.Insights;

/// <param name="Id">The test method.</param>
/// <param name="Title">Its class and name, e.g. <c>CartTests.Total_adds_up_the_lines</c>.</param>
public sealed record TestExample(string Id, string Title, string File, int Start, int End, string Code, bool Truncated);

public sealed record TestExamplesResult(string Node, IReadOnlyList<string> TestFiles, IReadOnlyList<TestExample> Examples);

/// <summary>
/// Worked examples from the tests: the test methods that call a type or member, so a newcomer can see
/// real inputs and the results they should give.
/// </summary>
public static class TestExamples
{
    const int ExampleLimit = 4;
    const int LineLimit = 30;
    const int Depth = 2;

    public static TestExamplesResult For(GraphIndex index, string id)
    {
        IEnumerable<string> targets;
        if (index.Types.TryGetValue(id, out var type)) targets = type.Members.Select(m => m.Id);
        else if (index.Members.ContainsKey(id)) targets = [id];
        else throw new ExplainException("Unknown type or member.", 404);
        var testProjects = index.Graph.Projects.Where(p => p.OutputKind == "test").Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var owner = type ?? index.TypeOf(id);
        if (owner is not null && testProjects.Contains(owner.Project)) return new TestExamplesResult(id, [], []);

        // Tests that call it directly come first, then tests one call further away.
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var target in targets)
            foreach (var reach in index.Callers(target, type is null ? Depth : 1))
                if (index.Members.TryGetValue(reach.MemberId, out var caller) && TestMap.IsTest(caller)
                    && (!found.TryGetValue(reach.MemberId, out var known) || reach.Depth < known))
                    found[reach.MemberId] = reach.Depth;

        var examples = new List<TestExample>();
        foreach (var (testId, _) in found.OrderBy(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal))
        {
            SourceSpan source;
            try { source = Explanations.ReadSource(index, testId); }
            catch (ExplainException) { continue; }
            var lines = source.Code.Split('\n');
            var test = index.TypeOf(testId);
            examples.Add(new TestExample(testId, $"{test?.Name}.{index.Members[testId].Name}", source.File, source.Start,
                source.Start + Math.Min(lines.Length, LineLimit) - 1,
                string.Join('\n', lines.Take(LineLimit)) + (lines.Length > LineLimit ? "\n// …" : ""), lines.Length > LineLimit));
        }
        var files = examples.Select(e => e.File).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new TestExamplesResult(id, files, examples.OrderBy(e => found[e.Id]).ThenBy(e => e.Code.Length).Take(ExampleLimit).ToList());
    }
}
