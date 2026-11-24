using System.IO;
using MailViewer.Core;

namespace MailViewer.App;

internal sealed record FolderSearchTarget(string Path, bool Recurse, bool IsFile = false);
internal sealed record FolderSearchHit(string Path, string Root);
internal sealed record FolderSearchResult(IReadOnlyList<FolderSearchHit> Hits, IReadOnlyList<string> Issues, bool Canceled);

internal static class FolderSourceSearch
{
    private static readonly HashSet<string> Extensions = new(EvidenceSession.MailFileExtensions, StringComparer.OrdinalIgnoreCase);

    public static FolderSearchResult Scan(IReadOnlyList<string> roots, bool recurse, CancellationToken cancellationToken) =>
        Scan(roots.Select(root => new FolderSearchTarget(root, recurse)).ToArray(), outlookDataOnly: false, cancellationToken);

    public static FolderSearchResult Scan(IReadOnlyList<FolderSearchTarget> targets, bool outlookDataOnly,
        CancellationToken cancellationToken)
    {
        var hits = new List<FolderSearchHit>();
        var issues = new List<string>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        try
        {
            foreach (FolderSearchTarget target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string root = target.Path;
                if (!IsLocal(root))
                {
                    issues.Add(root);
                    continue;
                }
                if (target.IsFile)
                {
                    if (!File.Exists(root)) issues.Add(root);
                    else AddFile(root, Path.GetDirectoryName(root)!);
                    continue;
                }
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.TryPop(out string? directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(directory, "*", options))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            AddFile(file, root);
                        }
                        if (target.Recurse)
                        {
                            foreach (string folder in Directory.EnumerateDirectories(directory, "*", options))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                pending.Push(folder);
                            }
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        issues.Add($"{directory} · {error.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Finish(canceled: true);
        }
        return Finish(canceled: false);

        void AddFile(string file, string root)
        {
            bool supported = outlookDataOnly ? IsOutlookDataFile(file) : IsMailCandidate(file);
            if (supported && paths.Add(file)) hits.Add(new FolderSearchHit(file, root));
        }

        FolderSearchResult Finish(bool canceled) =>
            new(hits.OrderBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase).ToArray(), issues, canceled);
    }

    private static bool IsMailCandidate(string path)
    {
        if (Extensions.Contains(Path.GetExtension(path))) return true;
        string name = Path.GetFileName(path);
        if (name.Equals("winmail.dat", StringComparison.OrdinalIgnoreCase) || name == ".mh_sequences") return true;
        if (Path.GetDirectoryName(path) is not { } parent) return false;
        string directory = Path.GetFileName(parent);
        if (directory is "cur" or "new" && Path.GetDirectoryName(parent) is { } root
            && Directory.Exists(Path.Combine(root, "cur")) && Directory.Exists(Path.Combine(root, "new")))
            return true;
        return name.All(char.IsAsciiDigit) && (File.Exists(Path.Combine(parent, ".mh_sequences")) ||
            File.Exists(Path.Combine(parent, ".mh_profile"))) || EvidenceSession.IsExtensionlessMailbox(path);
    }

    public static bool IsOutlookDataFile(string path) =>
        Path.GetExtension(path).Equals(".pst", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".ost", StringComparison.OrdinalIgnoreCase);

    public static bool IsLocal(string path)
    {
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(path)) return false;
        string? root = Path.GetPathRoot(path);
        if (root is not { Length: 3 } || root[1] != ':' || !char.IsAsciiLetter(root[0])) return false;
        return new DriveInfo(root).DriveType != DriveType.Network;
    }
}
