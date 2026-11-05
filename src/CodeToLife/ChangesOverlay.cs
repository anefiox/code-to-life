#if PRBUDDY
using PrBuddy.Core;
using PrBuddy.Core.Git;
using PrBuddy.Core.Semantics;
#endif

namespace CodeToLife;

public sealed record ChangedItem(string TypeId, string? MemberId, string Change, string DisplayName, string MemberKind, bool PublicApi, string? Detail);

public sealed record ChangeFinding(string RuleId, string Severity, string Message, string? Path, int? Line, string? TypeId, string? MemberId);

public sealed record ChangedFileInfo(string Path, string Status, int Added, int Deleted, int Risk, int RealChanges);

public sealed record ChangesReport(
    bool Available,
    string? Message,
    string? Base,
    string? Head,
    string? MergeBase,
    IReadOnlyList<ChangedFileInfo> Files,
    IReadOnlyList<ChangedItem> Items,
    IReadOnlyList<ChangeFinding> Findings);

/// <summary>
/// "What did this branch change?" drawn onto the graph, using PR Buddy's semantic diff: members
/// that really changed behavior or signature light up, reformatting doesn't, and PR Buddy's
/// findings are pinned to the members they're about.
/// </summary>
public static class ChangesOverlay
{
    public static bool IsAvailable =>
#if PRBUDDY
        true;
#else
        false;
#endif

    public static ChangesReport Build(Workspace workspace, string? baseRef, bool uncommitted)
    {
#if PRBUDDY
        string repoRoot;
        try { repoRoot = GitDiffReader.WorkingDirectory(workspace.RootDirectory); }
        catch (Exception ex) { return Unavailable($"Not a git repository: {ex.Message}"); }

        DiffRange range;
        IReadOnlyList<ChangedFile> files;
        try
        {
            (range, files) = uncommitted
                ? GitDiffReader.ReadUncommitted(repoRoot, string.IsNullOrWhiteSpace(baseRef) ? null : baseRef)
                : GitDiffReader.Read(repoRoot, string.IsNullOrWhiteSpace(baseRef) ? null : baseRef);
        }
        catch (Exception ex) { return Unavailable(ex.Message); }

        var report = PrAnalyzer.Analyze(files);
        var mapper = workspace.Mapper;
        var items = new List<ChangedItem>();
        var findings = new List<ChangeFinding>();

        foreach (var file in report.Files)
        {
            var full = Path.GetFullPath(Path.Combine(repoRoot, file.Path));
            foreach (var change in file.SemanticDiff?.Changes ?? [])
            {
                if (change.Change == MemberChangeKind.CosmeticOnly) continue;
                var location = change.Line is { } line ? mapper.FromSource(full, line) : null;
                if (location is null && change.Change == MemberChangeKind.Removed)
                {
                    // Removed members have no line any more; pin them to the type they were in, if it survived.
                    var typeName = change.DisplayName.Contains('.') ? change.DisplayName[..change.DisplayName.LastIndexOf('.')] : null;
                    var type = workspace.Graph.Types.FirstOrDefault(t => t.File is not null && Path.GetFullPath(t.File) == full && typeName is not null && (t.Id.EndsWith(typeName, StringComparison.Ordinal)));
                    if (type is not null) location = new Runtime.GraphLocation(type.Id, null);
                }
                if (location is null) continue;
                var isType = SemanticDiffer.TypeKinds.Contains(change.MemberKind);
                items.Add(new ChangedItem(
                    location.TypeId,
                    isType || change.Change == MemberChangeKind.Removed ? null : location.MemberId,
                    Name(change.Change),
                    change.DisplayName,
                    change.MemberKind,
                    change.IsPublicApi,
                    change.OldDisplayName is not null ? $"was {change.OldDisplayName}" : change.MovedPath is not null ? $"from {change.MovedPath}" : null));
            }

            foreach (var finding in file.Findings)
            {
                var location = finding.Line is { } line ? mapper.FromSource(full, line) : null;
                findings.Add(new ChangeFinding(finding.RuleId, finding.Severity.ToString().ToLowerInvariant(), finding.Message, file.Path, finding.Line, location?.TypeId, location?.MemberId));
            }
        }
        findings.AddRange(report.PullRequestFindings.Select(f => new ChangeFinding(f.RuleId, f.Severity.ToString().ToLowerInvariant(), f.Message, null, null, null, null)));

        return new ChangesReport(true, files.Count == 0 ? "No changes against the base." : null,
            range.BaseRef, range.HeadRef, range.MergeBaseSha[..Math.Min(10, range.MergeBaseSha.Length)],
            report.Files.Select(f => new ChangedFileInfo(f.Path, f.Status.ToString().ToLowerInvariant(), f.AddedLines, f.DeletedLines, f.RiskScore, f.SemanticDiff?.RealChangeCount ?? 0)).ToList(),
            items, findings);
#else
        return Unavailable("Built without PR Buddy. Clone https://github.com/anefiox/pr-buddy next to this repo (or pass -p:PrBuddyPath=...) and rebuild.");
#endif
    }

    static ChangesReport Unavailable(string message) => new(false, message, null, null, null, [], [], []);

#if PRBUDDY
    static string Name(MemberChangeKind kind) => kind switch
    {
        MemberChangeKind.BehaviorChanged => "behavior",
        MemberChangeKind.SignatureChanged => "signature",
        MemberChangeKind.Added => "added",
        MemberChangeKind.Removed => "removed",
        MemberChangeKind.Renamed => "renamed",
        MemberChangeKind.Moved => "moved",
        _ => "cosmetic",
    };
#endif
}
