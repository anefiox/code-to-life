namespace CodeToLife.Runtime.Dap;

/// <summary>
/// Finds netcoredbg, the open-source (MIT) .NET debug adapter from Samsung. Microsoft's own
/// vsdbg is licensed for Microsoft products only, so it isn't used even when it's installed.
/// </summary>
public static class DebuggerLocator
{
    public const string EnvironmentVariable = "CODETOLIFE_NETCOREDBG";

    public static string ExecutableName => OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";

    /// <summary>Where <c>tools/Get-Netcoredbg.ps1</c> puts it, next to the app.</summary>
    public static string DefaultInstallFolder => Path.Combine(AppContext.BaseDirectory, "netcoredbg");

    public static string? Find()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } configured)
        {
            if (File.Exists(configured)) return configured;
            var inFolder = Path.Combine(configured, ExecutableName);
            if (File.Exists(inFolder)) return inFolder;
        }

        foreach (var candidate in Candidates())
            if (File.Exists(candidate)) return candidate;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var path = Path.Combine(dir.Trim('"'), ExecutableName);
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(DefaultInstallFolder, ExecutableName);
        yield return Path.Combine(DefaultInstallFolder, "netcoredbg", ExecutableName);
        // Running from source: <repo>/tools/netcoredbg.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "tools", "netcoredbg", ExecutableName);
            yield return Path.Combine(dir.FullName, "tools", "netcoredbg", "netcoredbg", ExecutableName);
        }
    }
}
