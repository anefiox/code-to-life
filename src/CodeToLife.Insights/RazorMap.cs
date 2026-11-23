using System.Text.RegularExpressions;
using CodeToLife.Analysis;

namespace CodeToLife.Insights;

/// <param name="File">The template it points at, or null when it isn't in the project.</param>
/// <param name="External">Where it comes from when it isn't a file here, e.g. "view component Cart".</param>
/// <param name="Names">For a component: the names used.</param>
public sealed record TemplateLink(string Name, string? File, string? External, int Line, IReadOnlyList<string> Names);

public sealed record TemplateUse(string File, string How, int Line);

public sealed record TemplateRender(string Id, string Name, string? File, int? Line, IReadOnlyList<string> Routes);

/// <param name="Kind">page, layout, partial, component, viewstart or imports.</param>
/// <param name="Extends">Its layout, from <c>Layout = "…"</c> or the nearest _ViewStart.cshtml.</param>
/// <param name="Includes">Partial views it renders.</param>
/// <param name="Imports">View components and Blazor components it uses.</param>
/// <param name="Blocks">Sections: the ones a layout defines with RenderSection, or a page fills with @section.</param>
/// <param name="Model">The @model type, or a Blazor @inherits.</param>
public sealed record TemplateInfo(string File, string Kind, TemplateLink? Extends, IReadOnlyList<TemplateLink> Includes, IReadOnlyList<TemplateLink> Imports,
    IReadOnlyList<string> Blocks, IReadOnlyList<string> Macros, List<TemplateUse> UsedBy, List<TemplateRender> RenderedBy, string? Model, string? ModelId);

public sealed record TemplateMap(IReadOnlyList<TemplateInfo> Templates);

/// <summary>
/// The Razor map: which views use which layout (directly or through _ViewStart.cshtml), the partials
/// and components they pull in, the sections they fill, and the actions, handlers and pages that render them.
/// </summary>
public static partial class RazorMap
{
    static string DirOf(string file) => file.Contains('/') ? file[..file.LastIndexOf('/')] : "";
    static string Bare(string file) => Path.GetFileNameWithoutExtension(file);

    public static TemplateMap For(GraphIndex index)
    {
        var graph = index.Graph;
        var root = graph.Root;
        var files = Learning.TemplateFiles(root);
        var texts = files.ToDictionary(f => f, f => ProjectFiles.ReadText(root, f), StringComparer.Ordinal);

        // "_Layout" → Views/Shared/_Layout.cshtml, looking beside the view, then up its folders and their Shared folders.
        string? Resolve(string name, string from)
        {
            var clean = name.Trim();
            if (clean.StartsWith("~/", StringComparison.Ordinal) || clean.StartsWith('/') || clean.Contains('/'))
            {
                var tail = clean.TrimStart('~', '/');
                if (!Path.HasExtension(tail)) tail += ".cshtml";
                return files.FirstOrDefault(f => f.Equals(tail, StringComparison.OrdinalIgnoreCase) || f.EndsWith("/" + tail, StringComparison.OrdinalIgnoreCase));
            }
            var fileName = Path.HasExtension(clean) ? clean : clean + ".cshtml";
            for (var dir = DirOf(from); ; dir = DirOf(dir))
            {
                foreach (var candidate in new[] { Join(dir, fileName), Join(dir, "Shared/" + fileName) })
                    if (files.FirstOrDefault(f => f.Equals(candidate, StringComparison.OrdinalIgnoreCase)) is { } found) return found;
                if (dir.Length == 0) break;
            }
            return files.FirstOrDefault(f => f.EndsWith("/Shared/" + fileName, StringComparison.OrdinalIgnoreCase));
        }
        static string Join(string dir, string file) => dir.Length == 0 ? file : dir + "/" + file;

        string? ViewStartLayout(string file)
        {
            for (var dir = DirOf(file); ; dir = DirOf(dir))
            {
                var start = files.FirstOrDefault(f => f.Equals(Join(dir, "_ViewStart.cshtml"), StringComparison.OrdinalIgnoreCase));
                if (start is not null && LayoutAssignment().Match(texts[start]) is { Success: true } m) return m.Groups["name"].Value;
                if (dir.Length == 0) return null;
            }
        }

        var razorNames = files.Where(f => f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .GroupBy(Bare, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var items = new Dictionary<string, TemplateInfo>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var text = texts[file];
            var name = Path.GetFileName(file);
            var isRazor = file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
            TemplateLink? layout = null;
            if (!isRazor)
            {
                var explicitLayout = LayoutAssignment().Match(text);
                var special = name.StartsWith('_') || Regex.IsMatch(file, "/Components/", RegexOptions.IgnoreCase);
                // Layout = null means no layout at all.
                var layoutName = explicitLayout.Success ? (explicitLayout.Groups["name"].Success ? explicitLayout.Groups["name"].Value : null)
                    : special ? null : ViewStartLayout(file);
                if (layoutName is not null && !name.Equals("_ViewStart.cshtml", StringComparison.OrdinalIgnoreCase))
                {
                    var target = Resolve(layoutName, file);
                    layout = new TemplateLink(layoutName, target, target is null ? "a layout outside this project" : null,
                        explicitLayout.Success ? ProjectFiles.LineAt(text, explicitLayout.Index) : 1, []);
                }
            }
            else if (RazorLayout().Match(text) is { Success: true } blazorLayout)
            {
                var target = razorNames.GetValueOrDefault(blazorLayout.Groups["name"].Value);
                layout = new TemplateLink(blazorLayout.Groups["name"].Value, target, target is null ? "a layout outside this project" : null, ProjectFiles.LineAt(text, blazorLayout.Index), []);
            }

            var includes = Partial().Matches(text).Select(m =>
            {
                var target = Resolve(m.Groups["name"].Value, file);
                return new TemplateLink(m.Groups["name"].Value, target, target is null ? "a partial outside this project" : null, ProjectFiles.LineAt(text, m.Index), []);
            }).ToList();

            var imports = new List<TemplateLink>();
            foreach (Match m in ViewComponent().Matches(text))
            {
                var raw = m.Groups["name"].Value;
                var component = raw.Contains('-') ? string.Concat(raw.Split('-').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..])) : raw;
                var target = files.FirstOrDefault(f => f.EndsWith($"/Components/{component}/Default.cshtml", StringComparison.OrdinalIgnoreCase));
                imports.Add(new TemplateLink(component, target, target is null ? "view component " + component : null, ProjectFiles.LineAt(text, m.Index), [component]));
            }
            if (isRazor)
                foreach (Match m in RazorTag().Matches(text))
                    if (razorNames.TryGetValue(m.Groups["name"].Value, out var target) && target != file && imports.All(i => i.File != target))
                        imports.Add(new TemplateLink(m.Groups["name"].Value, target, null, ProjectFiles.LineAt(text, m.Index), [m.Groups["name"].Value]));

            var blocks = RenderSection().Matches(text).Select(m => m.Groups["name"].Value)
                .Concat(SectionFill().Matches(text).Select(m => m.Groups["name"].Value)).Distinct(StringComparer.Ordinal).ToList();
            var model = ModelDirective().Match(text) is { Success: true } mm ? mm.Groups["type"].Value.Trim() : null;
            // IReadOnlyList<WebShop.Data.Product> → Product: the innermost type the project defines.
            var modelId = model is null ? null : Regex.Matches(model, @"\w+").Select(w => w.Value).Reverse()
                .Select(n => graph.Types.FirstOrDefault(t => t.Name == n)).FirstOrDefault(t => t is not null)?.Id;
            items[file] = new TemplateInfo(file, "page", layout, includes, imports, blocks, [], [], [], model, modelId);
        }

        foreach (var item in items.Values)
        {
            if (item.Extends?.File is { } layoutFile && items.TryGetValue(layoutFile, out var l)) l.UsedBy.Add(new TemplateUse(item.File, "uses it as its layout", item.Extends.Line));
            foreach (var include in item.Includes)
                if (include.File is { } f && items.TryGetValue(f, out var p)) p.UsedBy.Add(new TemplateUse(item.File, "renders it as a partial", include.Line));
            foreach (var import in item.Imports)
                if (import.File is { } f && items.TryGetValue(f, out var c)) c.UsedBy.Add(new TemplateUse(item.File, "uses it as a component", import.Line));
        }

        // Who renders each view: an action's View() call, or the PageModel behind a Razor Page.
        var routesBy = (graph.Routes ?? []).GroupBy(r => r.Handler, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => $"{(r.Method == "*" ? "ANY" : r.Method)} {r.Path}").Distinct().ToList(), StringComparer.Ordinal);
        void Rendered(string file, string id)
        {
            if (!items.TryGetValue(file, out var item) || item.RenderedBy.Any(r => r.Id == id)) return;
            var member = index.Members.GetValueOrDefault(id);
            var type = member is null ? index.Types.GetValueOrDefault(id) : index.TypeOf(id);
            item.RenderedBy.Add(new TemplateRender(id, member is null ? type?.Name ?? id : index.Display(id),
                ProjectFiles.RelativeOrNull(root, member?.File ?? type?.File), member?.Line ?? type?.Line, routesBy.GetValueOrDefault(id) ?? []));
        }
        foreach (var (owner, candidates) in graph.Views ?? new Dictionary<string, IReadOnlyList<string>>())
            foreach (var candidate in candidates)
            {
                var file = files.FirstOrDefault(f =>
                {
                    var bare = f[..f.LastIndexOf('.')];
                    return bare.Equals(candidate, StringComparison.OrdinalIgnoreCase) || bare.EndsWith("/" + candidate, StringComparison.OrdinalIgnoreCase);
                });
                if (file is not null) { Rendered(file, owner); break; }
            }
        foreach (var route in graph.Routes ?? [])
            if (route.Kind == "page" && route.File is { } code && code.EndsWith(".cshtml.cs", StringComparison.OrdinalIgnoreCase))
                Rendered(ProjectFiles.Relative(root, code[..^3]), route.Handler);

        var result = items.Values.Select(item =>
        {
            var name = Path.GetFileName(item.File);
            var kind = name.Equals("_ViewStart.cshtml", StringComparison.OrdinalIgnoreCase) ? "viewstart"
                : name.Equals("_ViewImports.cshtml", StringComparison.OrdinalIgnoreCase) || name.Equals("_Imports.razor", StringComparison.OrdinalIgnoreCase) ? "imports"
                : item.UsedBy.Any(u => u.How.Contains("layout", StringComparison.Ordinal)) || RenderBody().IsMatch(texts[item.File]) ? "layout"
                : item.RenderedBy.Count > 0 || PageDirective().IsMatch(texts[item.File]) ? "page"
                : Regex.IsMatch(item.File, "/Components/", RegexOptions.IgnoreCase) || item.File.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ? "component"
                : name.StartsWith('_') || item.UsedBy.Count > 0 ? "partial"
                : "page";
            return item with { Kind = kind };
        }).ToList();
        return new TemplateMap(result);
    }

    [GeneratedRegex("""Layout\s*=\s*(?:"(?<name>[^"]+)"|null)""")]
    private static partial Regex LayoutAssignment();

    [GeneratedRegex(@"^@layout\s+(?<name>\w+)", RegexOptions.Multiline)]
    private static partial Regex RazorLayout();

    [GeneratedRegex(""""<partial\s+name\s*=\s*"(?<name>[^"]+)"|Html\.(?:Render)?Partial(?:Async)?\(\s*"(?<name>[^"]+)"""")]
    private static partial Regex Partial();

    [GeneratedRegex("""Component\.InvokeAsync\(\s*(?:"(?<name>\w+)"|nameof\((?<name>\w+?)(?:ViewComponent)?\)|typeof\((?<name>\w+?)(?:ViewComponent)?\))|<vc:(?<name>[\w-]+)""")]
    private static partial Regex ViewComponent();

    [GeneratedRegex(@"<(?<name>[A-Z]\w+)[\s/>]")]
    private static partial Regex RazorTag();

    [GeneratedRegex(""""RenderSection(?:Async)?\(\s*"(?<name>\w+)"""")]
    private static partial Regex RenderSection();

    [GeneratedRegex(@"^\s*@section\s+(?<name>\w+)", RegexOptions.Multiline)]
    private static partial Regex SectionFill();

    [GeneratedRegex(@"^@(?:model|inherits)\s+(?<type>[\w.<>,\s]+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex ModelDirective();

    [GeneratedRegex(@"RenderBody\(\)")]
    private static partial Regex RenderBody();

    [GeneratedRegex(@"^@page\b", RegexOptions.Multiline)]
    private static partial Regex PageDirective();
}
