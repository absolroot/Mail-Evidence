using System.IO;

namespace MailViewer.App;

internal sealed class MailboxMarks
{
    private readonly string _folder;
    private readonly HashSet<string> _bookmarks;
    private readonly HashSet<string> _pins;
    private readonly HashSet<string> _viewed;

    public MailboxMarks(string? folder)
    {
        _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Absolroot", "MailEvidence", "Marks");
        _bookmarks = Read("Bookmarks");
        _pins = Read("Pins");
        _viewed = Read("Viewed");
    }

    public IReadOnlySet<string> Bookmarks => _bookmarks;
    public bool IsBookmarked(string id) => _bookmarks.Contains(id);
    public bool IsPinned(string id) => _pins.Contains(id);
    public bool IsViewed(string id) => _viewed.Contains(id);

    public void MarkViewed(string id)
    {
        if (_viewed.Contains(id)) return;
        WriteMark("Viewed", id, enabled: true);
        _viewed.Add(id);
    }

    public void Set(string id, bool pinned, bool enabled)
    {
        WriteMark(id, pinned, enabled);
        SetInMemory(id, pinned, enabled);
    }

    internal void WriteMark(string id, bool pinned, bool enabled) =>
        WriteMark(pinned ? "Pins" : "Bookmarks", id, enabled);

    private void WriteMark(string group, string id, bool enabled)
    {
        string directory = Path.Combine(_folder, group);
        string file = Path.Combine(directory, Uri.EscapeDataString(id) + ".mark");
        if (enabled)
        {
            Directory.CreateDirectory(directory);
            using (File.Open(file, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
        }
        else if (Directory.Exists(directory))
        {
            File.Delete(file);
        }
    }

    internal void SetInMemory(string id, bool pinned, bool enabled)
    {
        var items = pinned ? _pins : _bookmarks;
        if (enabled) items.Add(id);
        else items.Remove(id);
    }

    private HashSet<string> Read(string group)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        string path = Path.Combine(_folder, group);
        try
        {
            if (Directory.Exists(path))
                foreach (string file in Directory.EnumerateFiles(path, "*.mark"))
                    result.Add(Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(file)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return result;
    }
}

