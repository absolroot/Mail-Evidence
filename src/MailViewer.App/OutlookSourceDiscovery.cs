using System.IO;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace MailViewer.App;

internal sealed record OutlookLocationSet(IReadOnlyList<FolderSearchTarget> Targets, bool HasNewOutlook);

internal static class OutlookSourceDiscovery
{
    public static OutlookLocationSet Find()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Find(local, documents, ReadProfileLocations());
    }

    internal static OutlookLocationSet Find(string local, string documents, IEnumerable<FolderSearchTarget> profileLocations)
    {
        var targets = new Dictionary<string, FolderSearchTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (FolderSearchTarget target in profileLocations)
            Add(target);
        if (local.Length > 0)
            Add(new(Path.Combine(local, "Microsoft", "Outlook"), Recurse: true));
        if (documents.Length > 0)
            Add(new(Path.Combine(documents, "Outlook Files"), Recurse: true));

        bool hasNewOutlook = false;
        if (local.Length > 0)
        {
            string olk = Path.Combine(local, "Microsoft", "Olk");
            string package = Path.Combine(local, "Packages", "Microsoft.OutlookForWindows_8wekyb3d8bbwe");
            hasNewOutlook = Directory.Exists(olk) || Directory.Exists(package);

            // The app's cache can include a browser profile. Search only these locations' top levels.
            Add(new(olk, Recurse: false));
            Add(new(Path.Combine(package, "LocalState"), Recurse: false));
            Add(new(Path.Combine(package, "LocalCache"), Recurse: false));
        }
        return new(targets.Values.OrderBy(target => target.Path, StringComparer.OrdinalIgnoreCase).ToArray(), hasNewOutlook);

        void Add(FolderSearchTarget target)
        {
            if (!FolderSourceSearch.IsLocal(target.Path)) return;
            string path;
            try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Path)); }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return; }
            if (target.IsFile ? !FolderSourceSearch.IsOutlookDataFile(path) || !File.Exists(path) : !Directory.Exists(path))
                return;
            if (!targets.TryGetValue(path, out FolderSearchTarget? existing) || target.Recurse && !existing.Recurse)
                targets[path] = target with { Path = path };
        }
    }

    private static List<FolderSearchTarget> ReadProfileLocations()
    {
        var targets = new List<FolderSearchTarget>();
        using RegistryKey? office = Open(Registry.CurrentUser, @"Software\Microsoft\Office");
        if (office is not null)
        {
            foreach (string version in Subkeys(office).Where(name => Version.TryParse(name, out _)))
            {
                foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    foreach (string prefix in new[] { @"Software\Microsoft\Office", @"Software\Policies\Microsoft\Office" })
                    {
                        using RegistryKey? outlook = Open(root, $@"{prefix}\{version}\Outlook");
                        if (outlook is null) continue;
                        AddFolder(Read(outlook, "ForcePSTPath"));
                        AddFolder(Read(outlook, "ForceOSTPath"));
                    }
                }
                using RegistryKey? profiles = Open(office, $@"{version}\Outlook\Profiles");
                if (profiles is not null) ReadProfiles(profiles);
            }
        }
        using RegistryKey? legacy = Open(Registry.CurrentUser, @"Software\Microsoft\Windows NT\CurrentVersion\Windows Messaging Subsystem\Profiles");
        if (legacy is not null) ReadProfiles(legacy);
        return targets;

        void AddFolder(object? value)
        {
            if (value is string path) targets.Add(new(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')), Recurse: false));
        }

        void ReadProfiles(RegistryKey profiles)
        {
            foreach (string name in Subkeys(profiles))
            {
                using RegistryKey? profile = Open(profiles, name);
                if (profile is null) continue;
                ReadStorePaths(profile);
                foreach (string sectionName in Subkeys(profile))
                {
                    using RegistryKey? section = Open(profile, sectionName);
                    if (section is not null) ReadStorePaths(section);
                }
            }
        }

        void ReadStorePaths(RegistryKey section)
        {
            // The registry uses the MAPI property type followed by the property ID.
            foreach (string tag in new[] { "001f6700", "001e6700", "001f6610", "001e6610" })
            {
                string? path = DecodeProfilePath(Read(section, tag), unicode: tag[3] == 'f');
                if (path is not null) targets.Add(new(path, Recurse: false, IsFile: true));
            }
        }
    }

    internal static string? DecodeProfilePath(object? value, bool unicode)
    {
        if (value is byte[] { Length: > 0 and <= 65536 } bytes)
        {
            if (unicode && bytes.Length % 2 != 0) return null;
            if (!unicode) Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding encoding = unicode ? Encoding.Unicode : Encoding.GetEncoding(0);
            value = encoding.GetString(bytes);
        }
        if (value is not string text) return null;
        string path = Environment.ExpandEnvironmentVariables(text.TrimEnd('\0').Trim().Trim('"'));
        return FolderSourceSearch.IsLocal(path) && FolderSourceSearch.IsOutlookDataFile(path) ? path : null;
    }

    private static RegistryKey? Open(RegistryKey key, string path)
    {
        try { return key.OpenSubKey(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { return null; }
    }

    private static string[] Subkeys(RegistryKey key)
    {
        try { return key.GetSubKeyNames(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { return []; }
    }

    private static object? Read(RegistryKey key, string name)
    {
        try { return key.GetValue(name); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { return null; }
    }
}
