using System.Text.Json;
using System.Text.RegularExpressions;
using CodeToLife.Analysis;

namespace CodeToLife.Insights;

public sealed record SettingUse(string File, int Line, string? Id, string Key);

/// <param name="Value">Null when it shouldn't be shown: a secret, or a file that may hold secrets.</param>
public sealed record SettingSource(string File, int Line, string? Value);

/// <param name="Key">The configuration key, e.g. <c>Logging:LogLevel:Default</c>, or an environment variable's name.</param>
/// <param name="Env">The environment variable that overrides it, e.g. <c>Logging__LogLevel__Default</c>.</param>
/// <param name="Varies">An appsettings.{Environment}.json file gives it a different value.</param>
/// <param name="Environments">Its value in each appsettings.{Environment}.json that sets it.</param>
public sealed record SettingInfo(string Key, string? Env, string Doc, string Default, bool Sensitive, bool Varies, string? File, int Line,
    List<SettingUse> Uses, List<SettingSource> SetIn, SortedDictionary<string, string?> Environments);

/// <param name="Schema">The main appsettings.json.</param>
public sealed record SettingsMap(string? Schema, IReadOnlyList<string> EnvFiles, IReadOnlyList<SettingInfo> Settings);

/// <summary>
/// The settings map: every key in appsettings.json (and per-environment overrides), and any other
/// environment variable the code reads, with where each is set for local runs and every place the code reads it.
/// </summary>
public static partial class Settings
{
    const int ValueLimit = 80;

    static readonly (Regex Key, string Doc)[] KnownDocs =
    [
        (new(@"^Logging:LogLevel:Default$"), "How much the app logs: Trace, Debug, Information, Warning, Error or Critical."),
        (new(@"^Logging:LogLevel:"), "How much this part of the code logs, overriding the default level."),
        (new(@"^AllowedHosts$"), "The host names the app answers to; * means any."),
        (new(@"^ConnectionStrings:(.+)$"), "How to connect to the $1 database or service."),
        (new(@"^Kestrel:"), "Addresses and certificates for Kestrel, the web server built into ASP.NET Core."),
        (new(@"^ASPNETCORE_ENVIRONMENT$"), "Which environment the app runs as (Development, Staging or Production). It picks appsettings.{Environment}.json."),
        (new(@"^ASPNETCORE_URLS$"), "The addresses the web server listens on."),
        (new(@"^DOTNET_ENVIRONMENT$"), "Which environment a non-web .NET app runs as. It picks appsettings.{Environment}.json."),
    ];

    static string Short(string text) => text.Length > ValueLimit ? text[..(ValueLimit - 1)] + "…" : text;

    static bool IsSensitive(string key) => SensitiveKey().IsMatch(key);

    static string DocFor(string key)
    {
        foreach (var (pattern, doc) in KnownDocs)
            if (pattern.Match(key) is { Success: true } m) return m.Groups.Count > 1 ? doc.Replace("$1", m.Groups[1].Value) : doc;
        return "";
    }

    /// <summary>appsettings.json flattened to Section:Key → (value, line), in file order.</summary>
    static List<(string Key, string Value, int Line)> Flatten(string text)
    {
        var result = new List<(string, string, int)>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException) { return result; }
        using (doc)
        {
            var cursor = 0;
            void Walk(JsonElement element, string prefix)
            {
                foreach (var property in element.EnumerateObject())
                {
                    var key = prefix.Length == 0 ? property.Name : prefix + ":" + property.Name;
                    var at = text.IndexOf('"' + property.Name + '"', cursor, StringComparison.Ordinal);
                    if (at >= 0) cursor = at + property.Name.Length + 2;
                    var line = at >= 0 ? ProjectFiles.LineAt(text, at) : 1;
                    if (property.Value.ValueKind == JsonValueKind.Object && property.Value.EnumerateObject().Any()) Walk(property.Value, key);
                    else result.Add((key, property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString() ?? "",
                        JsonValueKind.Object => "{}",
                        _ => property.Value.GetRawText().ReplaceLineEndings(" "),
                    }, line));
                }
            }
            if (doc.RootElement.ValueKind == JsonValueKind.Object) Walk(doc.RootElement, "");
        }
        return result;
    }

    public static SettingsMap For(GraphIndex index)
    {
        var graph = index.Graph;
        var root = graph.Root;
        var (testNames, testDirs) = ProjectFiles.TestProjects(graph);
        var appsettings = ProjectFiles.Find(root, f => AppSettings().IsMatch(f) && !ProjectFiles.InTests(f, testDirs));
        // The app project's settings first: the one beside the program with the most addresses.
        var exeDirs = graph.Projects.Where(p => p.OutputKind == "exe")
            .OrderByDescending(p => (graph.Routes ?? []).Count(r => index.TypeOf(r.Handler)?.Project == p.Name))
            .Select(p => ProjectFiles.Relative(root, Path.GetDirectoryName(p.Path) ?? root).TrimEnd('/') + "/").ToList();
        int Rank(string file) => exeDirs.FindIndex(d => d == "./" ? !file.Contains('/') : file.StartsWith(d, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : 99;
        var bases = appsettings.Where(f => Path.GetFileName(f).Equals("appsettings.json", StringComparison.OrdinalIgnoreCase)).OrderBy(Rank).ThenBy(f => f.Length).ToList();
        var schema = bases.FirstOrDefault();

        var settings = new Dictionary<string, SettingInfo>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        SettingInfo Add(string key, string? env, string value, string? file, int line)
        {
            if (settings.TryGetValue(key, out var existing)) return existing;
            var sensitive = IsSensitive(key);
            var info = new SettingInfo(key, env, DocFor(key), sensitive ? "" : Short(value), sensitive, false, file, line, [], [], new(StringComparer.OrdinalIgnoreCase));
            settings[key] = info;
            order.Add(key);
            return info;
        }
        foreach (var file in bases)
            foreach (var (key, value, line) in Flatten(ProjectFiles.ReadText(root, file)))
                Add(key, key.Replace(":", "__"), value, file, line);
        foreach (var file in appsettings.Except(bases))
        {
            var environment = AppSettings().Match(file).Groups[1].Value.TrimStart('.');
            foreach (var (key, value, line) in Flatten(ProjectFiles.ReadText(root, file)))
            {
                var info = Add(key, key.Replace(":", "__"), "", file, line);
                info.Environments[environment] = info.Sensitive ? null : Short(value);
                info.SetIn.Add(new SettingSource(file, line, info.Sensitive ? null : Short(value)));
            }
        }
        // Mark settings whose value changes between environments.
        foreach (var key in order.ToList())
        {
            var info = settings[key];
            if (info.Environments.Count > 0 && info.Environments.Values.Any(v => v != info.Default))
                settings[key] = info with { Varies = true };
        }

        // Where the code reads them.
        var sources = graph.Types.Where(t => t.File is not null && !testNames.Contains(t.Project)).Select(t => t.File!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(f => ProjectFiles.Relative(root, f)).Order(StringComparer.Ordinal).ToList();
        var envOnly = new List<string>();
        foreach (var file in sources)
        {
            var text = ProjectFiles.ReadText(root, file);
            if (text.Length == 0) continue;
            foreach (Match m in ConfigRead().Matches(text))
            {
                var key = m.Groups["key"].Success ? m.Groups["key"].Value
                    : m.Groups["conn"].Success ? "ConnectionStrings:" + m.Groups["conn"].Value
                    : null;
                var line = ProjectFiles.LineAt(text, m.Index);
                var use = new SettingUse(file, line, ProjectFiles.NodeAt(index, file, line), key ?? m.Groups["env"].Value);
                if (key is not null)
                {
                    var matched = false;
                    foreach (var name in order)
                        if (name.Equals(key, StringComparison.OrdinalIgnoreCase) || name.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                        {
                            settings[name].Uses.Add(use);
                            matched = true;
                        }
                    if (!matched) Add(key, key.Replace(":", "__"), "", null, 0).Uses.Add(use);
                    continue;
                }
                var env = m.Groups["env"].Value;
                var byEnv = order.FirstOrDefault(k => string.Equals(settings[k].Env, env, StringComparison.OrdinalIgnoreCase));
                if (byEnv is not null) { settings[byEnv].Uses.Add(use); continue; }
                Add(env, env, "", null, 0).Uses.Add(use);
                envOnly.Add(env);
            }
            // services.Configure<ShopOptions>(config.GetSection("Shop")): ShopOptions holds every Shop:* setting.
            foreach (Match m in OptionsBinding().Matches(text))
            {
                var options = graph.Types.FirstOrDefault(t => t.Name == m.Groups["type"].Value && !testNames.Contains(t.Project));
                if (options?.File is null) continue;
                var use = new SettingUse(ProjectFiles.Relative(root, options.File), options.Line, options.Id, m.Groups["section"].Value + " → " + options.Name);
                foreach (var name in order)
                    if (name.StartsWith(m.Groups["section"].Value + ":", StringComparison.OrdinalIgnoreCase)) settings[name].Uses.Add(use);
            }
        }

        // Where they are set for local runs and deployments.
        var envFiles = ProjectFiles.Find(root, f => EnvFile().IsMatch(f) && !ProjectFiles.InTests(f, testDirs), 4);
        foreach (var file in envFiles)
        {
            var text = ProjectFiles.ReadText(root, file);
            var secretFile = SecretFile().IsMatch(file);
            var lines = text.Split('\n');
            // launchSettings.json can hold several "NAME": "value" pairs on one line.
            var json = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            var assignments = lines.SelectMany((row, i) => (json ? JsonPair().Matches(row).Cast<Match>() : [EnvAssignment().Match(row)])
                .Where(m => m.Success).Select(m => (Match: m, Line: i)));
            foreach (var (m, i) in assignments)
            {
                var name = m.Groups["name"].Value;
                var key = order.FirstOrDefault(k => string.Equals(settings[k].Env, name, StringComparison.OrdinalIgnoreCase));
                if (key is null)
                {
                    if (!name.StartsWith("ASPNETCORE_", StringComparison.Ordinal) && !name.StartsWith("DOTNET_", StringComparison.Ordinal)) continue;
                    key = Add(name, name, "", null, 0).Key;
                }
                var info = settings[key];
                var value = secretFile || info.Sensitive ? null : Short(m.Groups["value"].Value.Trim().Trim('"', '\'').TrimEnd(','));
                info.SetIn.Add(new SettingSource(file, i + 1, value?.TrimEnd('"')));
            }
        }
        return new SettingsMap(schema, envFiles, order.Select(k => settings[k]).ToList());
    }

    [GeneratedRegex(@"(?:^|/)appsettings(\.[\w-]+)?\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex AppSettings();

    [GeneratedRegex(@"(?:password|secret|token|apikey|api_key|clientsecret|connectionstring|(?:^|:)key$|signingkey|privatekey)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveKey();

    // Configuration["A:B"], GetValue<T>("A:B"), GetSection("A"), GetConnectionString("Db"), BindConfiguration("A"),
    // Environment.GetEnvironmentVariable("X").
    [GeneratedRegex("""(?:(?:[Cc]onfig\w*|Configuration)\s*\[\s*"(?<key>[^"]+)"\s*\]|\.GetValue<[^>]+>\(\s*"(?<key>[^"]+)"|\.GetSection\(\s*"(?<key>[^"]+)"|\.GetRequiredSection\(\s*"(?<key>[^"]+)"|\.BindConfiguration\(\s*"(?<key>[^"]+)"|\.GetConnectionString\(\s*"(?<conn>[^"]+)"|GetEnvironmentVariable\(\s*"(?<env>[^"]+)")""")]
    private static partial Regex ConfigRead();

    [GeneratedRegex("""Configure<(?<type>\w+)>\(\s*[\w.]*GetSection\(\s*"(?<section>[^"]+)"|AddOptions<(?<type>\w+)>\(\)\s*\.BindConfiguration\(\s*"(?<section>[^"]+)"|Bind\(\s*"(?<section>[^"]+)"\s*,\s*\w+\s*\)\s*;\s*//\s*(?<type>\w+)""")]
    private static partial Regex OptionsBinding();

    [GeneratedRegex(@"(?:^|/)(?:launchSettings\.json|\.env(?:\..+)?|[^/]*\.env|[^/]*compose[^/]*\.ya?ml|Dockerfile|\.github/workflows/[^/]+\.ya?ml)$", RegexOptions.IgnoreCase)]
    private static partial Regex EnvFile();

    [GeneratedRegex(@"(?:^|/)\.env(?:\.local|\.development|\.production)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretFile();

    [GeneratedRegex(@"""(?<name>[A-Za-z_][A-Za-z0-9_]*)""\s*:\s*(?<value>""[^""]*""|true|false|-?\d[\d.]*)")]
    private static partial Regex JsonPair();

    // NAME=value, NAME: value, - NAME=value, ENV NAME=value.
    [GeneratedRegex("""^\s*(?:-\s*)?(?:export\s+|ENV\s+|ARG\s+)?"?(?<name>[A-Za-z_][A-Za-z0-9_]*)"?\s*[:=]\s*(?<value>.*)$""")]
    private static partial Regex EnvAssignment();
}
