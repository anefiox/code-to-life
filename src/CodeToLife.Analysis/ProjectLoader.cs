using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeToLife.Analysis;

/// <summary>A project as found on disk, before compiling.</summary>
public sealed record ProjectFile(
    string Name,
    string Path,
    string Directory,
    string AssemblyName,
    string OutputKind,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> SourceFiles,
    bool ImplicitUsings,
    string Sdk,
    bool UsesWindowsDesktop,
    IReadOnlyList<string> ExtraUsings);

/// <summary>
/// Finds the projects behind a .sln, .slnx, .csproj or folder and compiles each with Roslyn.
///
/// This deliberately doesn't run MSBuild: design-time builds are slow, need a matching SDK and
/// break on half-written code, which is exactly when you want a picture. Instead each project is
/// compiled from its .cs files against the running .NET runtime (plus whatever is already in its
/// bin folder), which resolves every type declared in source. That's all the graph needs.
/// </summary>
public static partial class ProjectLoader
{
    static readonly string[] SkippedDirectories = ["bin", "obj", ".git", ".vs", ".idea", "node_modules", ".codetolife"];

    public static IReadOnlyList<ProjectFile> Discover(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        IEnumerable<string> projectPaths;

        if (File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            projectPaths = [path];
        else if (File.Exists(path) && (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
            projectPaths = ProjectsInSolution(path);
        else if (System.IO.Directory.Exists(path))
            projectPaths = FindFiles(path, "*.csproj");
        else
            throw new FileNotFoundException($"Couldn't find a solution, project or folder at '{path}'.");

        var projects = projectPaths.Distinct(StringComparer.OrdinalIgnoreCase).Select(ReadProject).ToList();

        // A folder of loose .cs files with no project still deserves a picture.
        if (projects.Count == 0 && System.IO.Directory.Exists(path))
        {
            var files = FindFiles(path, "*.cs").ToList();
            if (files.Count > 0)
                projects.Add(new ProjectFile(new DirectoryInfo(path).Name, path, path, new DirectoryInfo(path).Name, "library", [], files, true, "Microsoft.NET.Sdk", false, []));
        }

        return projects;
    }

    static IEnumerable<string> ProjectsInSolution(string solution)
    {
        var dir = System.IO.Path.GetDirectoryName(solution)!;
        var text = File.ReadAllText(solution);
        var matches = solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? SlnxProject().Matches(text).Select(m => m.Groups[1].Value)
            : SlnProject().Matches(text).Select(m => m.Groups[1].Value);
        foreach (var relative in matches)
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, relative.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
            if (File.Exists(full)) yield return full;
        }
    }

    static ProjectFile ReadProject(string csproj)
    {
        var dir = System.IO.Path.GetDirectoryName(csproj)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(csproj);
        XDocument doc;
        try { doc = XDocument.Load(csproj); }
        catch { doc = new XDocument(new XElement("Project")); }

        string? Prop(string prop) => doc.Descendants().LastOrDefault(e => e.Name.LocalName == prop)?.Value.Trim();
        var sdk = doc.Root?.Attribute("Sdk")?.Value ?? "Microsoft.NET.Sdk";
        var outputType = Prop("OutputType")?.ToLowerInvariant();
        var outputKind = outputType is "exe" or "winexe" || sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) || sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase)
            ? "exe"
            : IsTestProject(doc) ? "test" : "library";

        var references = doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, v!.Replace('\\', System.IO.Path.DirectorySeparatorChar))))
            .ToList();

        var removed = doc.Descendants().Where(e => e.Name.LocalName == "Compile" && e.Attribute("Remove") is not null)
            .Select(e => e.Attribute("Remove")!.Value).ToList();

        var files = SourceFilesOf(dir)
            .Where(f => !removed.Any(r => Matches(dir, f, r)))
            .ToList();

        // An SDK project's generated global usings, if it has been built at least once.
        var generated = System.IO.Directory.Exists(System.IO.Path.Combine(dir, "obj"))
            ? System.IO.Directory.EnumerateFiles(System.IO.Path.Combine(dir, "obj"), "*.GlobalUsings.g.cs", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(1)
            : [];
        files.AddRange(generated);

        var implicitUsings = Prop("ImplicitUsings") is "enable" or "true";
        var windowsDesktop = Prop("UseWPF") is "true" || Prop("UseWindowsForms") is "true";
        var extraUsings = doc.Descendants().Where(e => e.Name.LocalName == "Using" && e.Attribute("Include") is not null && e.Attribute("Alias") is null && e.Attribute("Static") is null)
            .Select(e => e.Attribute("Include")!.Value).ToList();
        return new ProjectFile(name, csproj, dir, Prop("AssemblyName") ?? name, outputKind, references, files, implicitUsings, sdk, windowsDesktop, extraUsings);
    }

    static bool IsTestProject(XDocument doc) =>
        doc.Descendants().Any(e => e.Name.LocalName == "PackageReference" &&
            (e.Attribute("Include")?.Value ?? "") is var id &&
            (id.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) || id.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase)
             || id.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase) || id == "Microsoft.NET.Test.Sdk"));

    /// <summary>A project owns every .cs file under its folder, except those under a nested project.</summary>
    static IEnumerable<string> SourceFilesOf(string projectDir)
    {
        var pending = new Stack<string>();
        pending.Push(projectDir);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current != projectDir && System.IO.Directory.EnumerateFiles(current, "*.csproj").Any()) continue;
            foreach (var file in System.IO.Directory.EnumerateFiles(current, "*.cs")) yield return file;
            foreach (var sub in System.IO.Directory.EnumerateDirectories(current))
                if (!SkippedDirectories.Contains(System.IO.Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                    pending.Push(sub);
        }
    }

    static IEnumerable<string> FindFiles(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files, dirs;
            try
            {
                files = System.IO.Directory.EnumerateFiles(current, pattern).ToList();
                dirs = System.IO.Directory.EnumerateDirectories(current).ToList();
            }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
                if (!SkippedDirectories.Contains(System.IO.Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                    pending.Push(d);
        }
    }

    static bool Matches(string dir, string file, string pattern)
    {
        var relative = System.IO.Path.GetRelativePath(dir, file).Replace('\\', '/');
        var regex = "^" + Regex.Escape(pattern.Replace('\\', '/')).Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*") + "$";
        return Regex.IsMatch(relative, regex, RegexOptions.IgnoreCase);
    }

    /// <summary>Compiles every project, referenced projects first, so cross-project types resolve.</summary>
    public static IReadOnlyList<(ProjectFile Project, CSharpCompilation Compilation)> Compile(IReadOnlyList<ProjectFile> projects, CancellationToken cancellationToken = default)
    {
        var byPath = projects.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        var compiled = new Dictionary<string, CSharpCompilation>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(ProjectFile, CSharpCompilation)>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CSharpCompilation Build(ProjectFile project)
        {
            if (compiled.TryGetValue(project.Path, out var done)) return done;
            visiting.Add(project.Path);
            var references = new List<MetadataReference>(FrameworkReferences(project));
            foreach (var reference in project.ProjectReferences)
                if (byPath.TryGetValue(reference, out var referenced) && !visiting.Contains(reference))
                    references.Add(Build(referenced).ToMetadataReference());

            var known = references.OfType<PortableExecutableReference>().Select(r => System.IO.Path.GetFileName(r.FilePath) ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            known.UnionWith(project.ProjectReferences.Select(r => byPath.TryGetValue(r, out var p) ? p.AssemblyName + ".dll" : ""));
            references.AddRange(BinReferences(project, known));
            foreach (var package in NuGetReferences.For(project))
            {
                if (!known.Add(System.IO.Path.GetFileName(package))) continue;
                MetadataReference? reference = null;
                try { reference = MetadataReference.CreateFromFile(package); } catch { }
                if (reference is not null) references.Add(reference);
            }

            var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
            var trees = project.SourceFiles.AsParallel().AsOrdered().WithCancellation(cancellationToken)
                .Select(f => CSharpSyntaxTree.ParseText(SafeRead(f), parseOptions, f))
                .ToList();
            if (!project.SourceFiles.Any(f => f.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase)))
            {
                var usings = (project.ImplicitUsings ? ImplicitUsingsFor(project.Sdk) : "")
                    + string.Concat(project.ExtraUsings.Select(u => $"global using global::{u};\n"));
                if (usings.Length > 0) trees.Add(CSharpSyntaxTree.ParseText(usings, parseOptions, "<implicit usings>"));
            }

            var options = new CSharpCompilationOptions(
                project.OutputKind == "exe" ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                allowUnsafe: true);
            var compilation = CSharpCompilation.Create(project.AssemblyName, trees, references, options);
            compiled[project.Path] = compilation;
            visiting.Remove(project.Path);
            return compilation;
        }

        foreach (var project in projects) result.Add((project, Build(project)));
        return result;
    }

    static string SafeRead(string file)
    {
        try { return File.ReadAllText(file); }
        catch (IOException) { return ""; }
    }

    static IEnumerable<MetadataReference> FrameworkReferences(ProjectFile project)
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => System.IO.Path.GetFileName(p) is var n && (n.StartsWith("System", StringComparison.Ordinal) || n.StartsWith("Microsoft", StringComparison.Ordinal) || n is "netstandard.dll" or "mscorlib.dll"))
            // Our own dependencies (Roslyn, ClrMD...) aren't the target's; the target's real ones come from its bin folder.
            .Where(p => !System.IO.Path.GetFileName(p).StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                     && !System.IO.Path.GetFileName(p).StartsWith("Microsoft.Diagnostics", StringComparison.Ordinal))
            .ToList();

        if (project.UsesWindowsDesktop)
        {
            var core = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var desktop = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(core)!)!, "Microsoft.WindowsDesktop.App");
            if (System.IO.Directory.Exists(desktop))
            {
                var latest = System.IO.Directory.GetDirectories(desktop).OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
                if (latest is not null) tpa.AddRange(System.IO.Directory.GetFiles(latest, "*.dll"));
            }
        }

        foreach (var path in tpa.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            MetadataReference? reference = null;
            try { reference = MetadataReference.CreateFromFile(path); } catch { }
            if (reference is not null) yield return reference;
        }
    }

    /// <summary>NuGet dependencies the project was last built with, so their types resolve too.</summary>
    static IEnumerable<MetadataReference> BinReferences(ProjectFile project, HashSet<string> known)
    {
        var bin = System.IO.Path.Combine(project.Directory, "bin");
        if (!System.IO.Directory.Exists(bin)) yield break;
        var output = BuiltAssemblyIn(project);
        if (output is null) yield break;
        foreach (var dll in System.IO.Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(output)!, "*.dll"))
        {
            var fileName = System.IO.Path.GetFileName(dll);
            if (known.Contains(fileName) || fileName.Equals(project.AssemblyName + ".dll", StringComparison.OrdinalIgnoreCase)) continue;
            MetadataReference? reference = null;
            try
            {
                using var stream = File.OpenRead(dll);
                using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
                if (pe.HasMetadata) reference = MetadataReference.CreateFromFile(dll);
            }
            catch { }
            if (reference is not null) { known.Add(fileName); yield return reference; }
        }
    }

    /// <summary>The newest build output of the project, preferring Debug: the one a debugger can use.</summary>
    public static string? BuiltAssemblyIn(ProjectFile project)
    {
        var bin = System.IO.Path.Combine(project.Directory, "bin");
        if (!System.IO.Directory.Exists(bin)) return null;
        return System.IO.Directory.EnumerateFiles(bin, project.AssemblyName + ".dll", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}ref{System.IO.Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}refint{System.IO.Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Contains($"{System.IO.Path.DirectorySeparatorChar}Debug{System.IO.Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    static string ImplicitUsingsFor(string sdk)
    {
        var usings = new List<string> { "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks" };
        if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase))
            usings.AddRange(["System.Net.Http.Json", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Http",
                "Microsoft.AspNetCore.Routing", "Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection",
                "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging"]);
        if (sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase))
            usings.AddRange(["Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging"]);
        return string.Concat(usings.Select(u => $"global using global::{u};\n"));
    }

    [GeneratedRegex("""Project\(".*?"\)\s*=\s*".*?",\s*"(.*?\.csproj)"\s*,""")]
    private static partial Regex SlnProject();

    [GeneratedRegex("""<Project\s+Path="(.*?\.csproj)"\s*""")]
    private static partial Regex SlnxProject();
}
