using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeToLife;

/// <summary>
/// A sketch of code that doesn't exist yet, drawn on top of the real graph: new types, the members
/// you expect them to have, how they'll connect to what's there, and notes on existing types.
/// Saved next to the code in <c>.codetolife/plan.json</c> so it can be committed and discussed.
/// </summary>
public sealed record Plan(List<PlannedType> Types, List<PlannedEdge> Edges, List<PlanNote> Notes)
{
    public static Plan Empty => new([], [], []);
}

public sealed record PlannedType(string Id, string Name, string Namespace, string Kind, List<PlannedMember> Members, string? Note);

public sealed record PlannedMember(string Name, string Kind, string Signature);

public sealed record PlannedEdge(string Source, string Target, string Kind, string? Note);

public sealed record PlanNote(string TargetId, string Text, string Color);

public sealed partial class PlanStore(Workspace workspace, JsonSerializerOptions json)
{
    readonly object _gate = new();

    string FilePath => Path.Combine(workspace.RootDirectory, ".codetolife", "plan.json");

    public Plan Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath)) return Plan.Empty;
            try { return JsonSerializer.Deserialize<Plan>(File.ReadAllText(FilePath), json) ?? Plan.Empty; }
            catch (JsonException) { return Plan.Empty; }
        }
    }

    public void Save(Plan plan)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var indented = new JsonSerializerOptions(json) { WriteIndented = true };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(plan, indented));
        }
    }

    /// <summary>C# skeletons for the planned types, ready to paste: bases, fields for "has" edges, members that throw.</summary>
    public string Stubs(Plan plan)
    {
        var graph = workspace.IsLoaded ? workspace.Graph : null;
        string NameOf(string id) =>
            plan.Types.FirstOrDefault(t => t.Id == id)?.Name
            ?? graph?.Types.FirstOrDefault(t => t.Id == id)?.Name
            ?? id.Split('.').Last();
        bool IsInterface(string id) =>
            plan.Types.FirstOrDefault(t => t.Id == id)?.Kind == "interface"
            || graph?.Types.FirstOrDefault(t => t.Id == id)?.Kind == "interface";

        var sb = new StringBuilder();
        foreach (var group in plan.Types.GroupBy(t => t.Namespace).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (group.Key.Length > 0) sb.Append("namespace ").Append(group.Key).Append(";\n\n");
            foreach (var type in group)
            {
                if (!string.IsNullOrWhiteSpace(type.Note))
                    sb.Append("/// <summary>").Append(type.Note.Trim()).Append("</summary>\n");

                var outgoing = plan.Edges.Where(e => e.Source == type.Id).ToList();
                var bases = outgoing.Where(e => e.Kind == "inherits").Select(e => NameOf(e.Target))
                    .Concat(outgoing.Where(e => e.Kind == "implements").Select(e => NameOf(e.Target)))
                    .ToList();
                var keyword = type.Kind switch
                {
                    "interface" => "public interface",
                    "record" => "public sealed record",
                    "struct" => "public struct",
                    "enum" => "public enum",
                    "static class" => "public static class",
                    "abstract class" => "public abstract class",
                    _ => "public class",
                };
                sb.Append(keyword).Append(' ').Append(type.Name);
                if (bases.Count > 0) sb.Append(" : ").Append(string.Join(", ", bases));
                sb.Append("\n{\n");

                if (type.Kind == "enum")
                {
                    foreach (var m in type.Members) sb.Append("    ").Append(m.Name).Append(",\n");
                    sb.Append("}\n\n");
                    continue;
                }

                var isInterface = type.Kind == "interface";
                foreach (var has in outgoing.Where(e => e.Kind == "has" && !isInterface))
                {
                    var target = NameOf(has.Target);
                    sb.Append("    readonly ").Append(target).Append(" _").Append(Camel(target.TrimStart('I'))).Append(';')
                        .Append(has.Note is { Length: > 0 } n ? " // " + n : "").Append('\n');
                }
                if (outgoing.Any(e => e.Kind == "has") && !isInterface) sb.Append('\n');

                foreach (var member in type.Members)
                    sb.Append("    ").Append(MemberStub(member, isInterface)).Append('\n');

                // Interfaces it implements: list their members as a reminder.
                foreach (var iface in outgoing.Where(e => e.Kind == "implements" && IsInterface(e.Target)))
                {
                    var planned = plan.Types.FirstOrDefault(t => t.Id == iface.Target);
                    var existing = graph?.Types.FirstOrDefault(t => t.Id == iface.Target);
                    var signatures = planned?.Members.Select(m => m.Signature)
                        ?? existing?.Members.Select(m => m.Signature) ?? [];
                    foreach (var signature in signatures)
                        sb.Append("    // from ").Append(NameOf(iface.Target)).Append(": ").Append(signature).Append('\n');
                }

                // Abstract members of an existing base class: they'll have to be overridden.
                foreach (var inherit in outgoing.Where(e => e.Kind == "inherits"))
                    foreach (var abstractMember in graph?.Types.FirstOrDefault(t => t.Id == inherit.Target)?.Members.Where(m => m.IsAbstract) ?? [])
                        sb.Append("    // override from ").Append(NameOf(inherit.Target)).Append(": ").Append(abstractMember.Signature).Append('\n');

                foreach (var uses in outgoing.Where(e => e.Kind is "calls" or "creates" or "uses"))
                    sb.Append("    // ").Append(uses.Kind).Append(' ').Append(NameOf(uses.Target)).Append(uses.Note is { Length: > 0 } note ? ": " + note : "").Append('\n');
                sb.Append("}\n\n");
            }
        }
        return sb.Length == 0 ? "// Nothing planned yet. Add a type in Plan mode." : sb.ToString().TrimEnd() + "\n";
    }

    static string MemberStub(PlannedMember member, bool isInterface)
    {
        var signature = member.Signature.Trim().TrimEnd(';');
        if (signature.Length == 0) signature = member.Kind == "property" ? $"object {member.Name} {{ get; set; }}" : $"void {member.Name}()";
        var looksLikeMethod = signature.Contains('(') && !signature.Contains("=>") && !signature.Contains('{');
        if (isInterface) return (looksLikeMethod || !signature.Contains('{') ? signature + ";" : signature).Replace(";;", ";");
        if (looksLikeMethod)
        {
            var prefix = AccessModifier().IsMatch(signature) ? "" : "public ";
            return $"{prefix}{signature} => throw new NotImplementedException();";
        }
        return (AccessModifier().IsMatch(signature) ? "" : "public ") + signature + (signature.EndsWith('}') ? "" : ";");
    }

    static string Camel(string name) => name.Length == 0 ? "value" : char.ToLowerInvariant(name[0]) + name[1..];

    [GeneratedRegex(@"^\s*(public|private|protected|internal)\b")]
    private static partial Regex AccessModifier();
}
