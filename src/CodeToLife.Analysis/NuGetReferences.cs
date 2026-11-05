using System.Text.Json;
using System.Xml.Linq;

namespace CodeToLife.Analysis;

/// <summary>
/// Finds the package assemblies a project compiles against, without building it: from
/// obj/project.assets.json when the project has been restored (every package, transitive ones
/// too), otherwise from its direct PackageReferences in the global NuGet cache.
/// </summary>
public static class NuGetReferences
{
    static readonly string[] PreferredFrameworks =
        ["net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netstandard2.1", "netstandard2.0", "netstandard1.6", "netstandard1.3"];

    public static string PackagesFolder =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    public static IReadOnlyList<string> For(ProjectFile project)
    {
        var assets = Path.Combine(project.Directory, "obj", "project.assets.json");
        if (File.Exists(assets))
        {
            try { return FromAssets(assets); }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { }
        }
        return FromPackageReferences(project);
    }

    static List<string> FromAssets(string assetsPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var root = doc.RootElement;
        var folders = root.TryGetProperty("packageFolders", out var pf)
            ? pf.EnumerateObject().Select(p => p.Name).ToList()
            : [PackagesFolder];

        var libraries = root.GetProperty("libraries");
        var targets = root.GetProperty("targets").EnumerateObject().FirstOrDefault();
        var result = new List<string>();
        if (targets.Value.ValueKind != JsonValueKind.Object) return result;

        foreach (var package in targets.Value.EnumerateObject())
        {
            if (!package.Value.TryGetProperty("type", out var type) || type.GetString() != "package") continue;
            if (!package.Value.TryGetProperty("compile", out var compile)) continue;
            if (!libraries.TryGetProperty(package.Name, out var library) || !library.TryGetProperty("path", out var libPath)) continue;

            foreach (var asset in compile.EnumerateObject())
            {
                if (!asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var folder in folders)
                {
                    var full = Path.Combine(folder, libPath.GetString()!, asset.Name.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(full)) { result.Add(full); break; }
                }
            }
        }
        return result;
    }

    static List<string> FromPackageReferences(ProjectFile project)
    {
        var result = new List<string>();
        XDocument doc;
        try { doc = XDocument.Load(project.Path); }
        catch { return result; }

        var central = CentralVersions(project.Directory);
        var pending = new Queue<(string Id, string? Version, int Depth)>();
        foreach (var reference in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var id = reference.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var version = reference.Attribute("Version")?.Value
                ?? reference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value
                ?? central.GetValueOrDefault(id);
            pending.Enqueue((id, version, 0));
        }

        // Follow each package's own dependencies too: Microsoft.CodeAnalysis.CSharp is useless without Microsoft.CodeAnalysis.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var (id, version, depth) = pending.Dequeue();
            if (!seen.Add(id) || depth > 6) continue;
            var packageDir = Path.Combine(PackagesFolder, id.ToLowerInvariant());
            if (!Directory.Exists(packageDir)) continue;

            var wanted = version?.Trim('[', ']', '(', ')', ' ').Split(',')[0].Trim().ToLowerInvariant();
            var versionDir = !string.IsNullOrEmpty(wanted) && Directory.Exists(Path.Combine(packageDir, wanted))
                ? Path.Combine(packageDir, wanted)
                : Directory.GetDirectories(packageDir).OrderByDescending(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : new Version()).FirstOrDefault();
            if (versionDir is null) continue;

            var assemblies = BestLib(Path.Combine(versionDir, "ref")) ?? BestLib(Path.Combine(versionDir, "lib"));
            if (assemblies is not null) result.AddRange(Directory.GetFiles(assemblies, "*.dll"));

            foreach (var (depId, depVersion) in Dependencies(versionDir))
                pending.Enqueue((depId, depVersion, depth + 1));
        }
        return result;
    }

    static IEnumerable<(string Id, string? Version)> Dependencies(string versionDir)
    {
        var nuspec = Directory.GetFiles(versionDir, "*.nuspec").FirstOrDefault();
        if (nuspec is null) return [];
        try
        {
            var groups = XDocument.Load(nuspec).Descendants().Where(e => e.Name.LocalName == "group").ToList();
            var best = PreferredFrameworks
                .Select(tfm => groups.FirstOrDefault(g => NormalizeFramework(g.Attribute("targetFramework")?.Value) == tfm))
                .FirstOrDefault(g => g is not null);
            var dependencies = best?.Elements() ?? XDocument.Load(nuspec).Descendants().Where(e => e.Name.LocalName == "dependency" && e.Parent?.Name.LocalName == "dependencies");
            return dependencies
                .Where(e => e.Name.LocalName == "dependency" && e.Attribute("id") is not null)
                .Select(e => (e.Attribute("id")!.Value, e.Attribute("version")?.Value))
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>".NETStandard2.0" → "netstandard2.0", "net8.0" → "net8.0".</summary>
    static string? NormalizeFramework(string? framework) => framework?.ToLowerInvariant() switch
    {
        null => null,
        var f when f.StartsWith(".netstandard", StringComparison.Ordinal) => "netstandard" + f[".netstandard".Length..],
        var f when f.StartsWith(".netcoreapp", StringComparison.Ordinal) => "netcoreapp" + f[".netcoreapp".Length..],
        var f => f,
    };

    static string? BestLib(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        var available = Directory.GetDirectories(folder).ToDictionary(d => Path.GetFileName(d).ToLowerInvariant(), d => d);
        foreach (var tfm in PreferredFrameworks)
            if (available.TryGetValue(tfm, out var dir) && Directory.GetFiles(dir, "*.dll").Length > 0) return dir;
        return null;
    }

    /// <summary>Central package management: versions live in a Directory.Packages.props above the project.</summary>
    static Dictionary<string, string> CentralVersions(string projectDir)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var dir = new DirectoryInfo(projectDir); dir is not null; dir = dir.Parent)
        {
            var props = Path.Combine(dir.FullName, "Directory.Packages.props");
            if (!File.Exists(props)) continue;
            try
            {
                foreach (var e in XDocument.Load(props).Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
                    if (e.Attribute("Include")?.Value is { } id && e.Attribute("Version")?.Value is { } v) versions.TryAdd(id, v);
            }
            catch { }
            break;
        }
        return versions;
    }
}
