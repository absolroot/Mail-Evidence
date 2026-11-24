using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MailViewer.Core;

internal sealed class ExportDirectoryLease : IDisposable
{
    private const uint ReadAttributes = 0x80;
    private const uint OpenExisting = 3;
    private const uint OpenDirectory = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private readonly Stack<SafeFileHandle> handles = new();

    private ExportDirectoryLease(string path) => Path = path;

    public string Path { get; }

    public static ExportDirectoryLease Open(string path)
    {
        var directory = new DirectoryInfo(path);
        var lease = new ExportDirectoryLease(directory.FullName);
        try
        {
            var drive = new DriveInfo(directory.Root.FullName);
            if (!OperatingSystem.IsWindows() || drive.DriveType != DriveType.Fixed ||
                !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                throw new IOException("저장 및 검증에는 로컬 NTFS 폴더를 지정해 주세요.");

            var ancestors = new Stack<DirectoryInfo>();
            for (DirectoryInfo? parent = directory; parent is not null; parent = parent.Parent)
                ancestors.Push(parent);

            foreach (var ancestor in ancestors)
            {
                var handle = CreateFile(ancestor.FullName, ReadAttributes,
                    FileShare.Read | FileShare.Write, IntPtr.Zero, OpenExisting,
                    OpenDirectory | OpenReparsePoint, IntPtr.Zero);
                lease.handles.Push(handle);
                if (handle.IsInvalid)
                    throw new IOException($"폴더를 열 수 없습니다. {ancestor.FullName}",
                        new Win32Exception(Marshal.GetLastWin32Error()));

                var attributes = File.GetAttributes(ancestor.FullName);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) == 0)
                    throw new IOException($"일반 폴더를 지정해 주세요. {ancestor.FullName}");
            }
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        while (handles.TryPop(out var handle))
            handle.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess,
        FileShare shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);
}
