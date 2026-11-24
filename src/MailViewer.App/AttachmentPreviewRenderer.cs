using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MailViewer.App;

internal enum PreviewState { Empty, Loading, Ready, Unsupported, Unreadable, Limit, Unavailable, TimedOut }

internal sealed record PreviewFrame(int Width, int Height, bool IsDocument, byte[] Pixels);
internal sealed record PreviewResult(PreviewState State, PreviewFrame? Frame = null);

internal sealed class AttachmentPreviewRenderer : IDisposable
{
    public const int MaxInputBytes = 24 * 1024 * 1024;
    private const int MaxEdge = 1600;
    private readonly SemaphoreSlim _renderGate = new(1);
    private DirectoryInfo? _directory;
    private string? _worker;
    private string? _sid;
    private string? _profile;
    private volatile bool _disposed;

    public async Task<PreviewResult> RenderAsync(byte[] attachment, CancellationToken cancellationToken)
    {
        if (attachment.Length > MaxInputBytes) return new(PreviewState.Limit);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        bool entered = false;
        try
        {
            await _renderGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            if (_disposed) return new(PreviewState.Unavailable);
            PrepareWorker();
            using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            using SafeFileHandle job = PreviewSandbox.Start(_worker!, _sid!,
                input.ClientSafePipeHandle, output.ClientSafePipeHandle);
            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            using var stop = timeout.Token.Register(() => job.Dispose());

            await input.WriteAsync(attachment, timeout.Token).ConfigureAwait(false);
            input.Close();
            byte[] header = new byte[10];
            await output.ReadExactlyAsync(header.AsMemory(0, 1), timeout.Token).ConfigureAwait(false);
            if (header[0] != 0)
            {
                return new(header[0] switch
                {
                    1 => PreviewState.Unsupported,
                    2 => PreviewState.Unreadable,
                    3 => PreviewState.Limit,
                    _ => PreviewState.Unreadable
                });
            }

            await output.ReadExactlyAsync(header.AsMemory(1), timeout.Token).ConfigureAwait(false);
            int width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2, 4));
            int height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(6, 4));
            if (header[1] > 1 || width is < 1 or > MaxEdge || height is < 1 or > MaxEdge)
                return new(PreviewState.Unreadable);
            var buffer = new byte[width * height * 4];
            await output.ReadExactlyAsync(buffer, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return new(PreviewState.Ready, new(width, height, header[1] == 1, buffer));
        }
        catch (Exception error) when (error is OperationCanceledException or IOException && timeout.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(PreviewState.TimedOut);
        }
        catch (EndOfStreamException)
        {
            return new(PreviewState.Unreadable);
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException
                                      or ArgumentException or ExternalException or InvalidDataException)
        {
            return new(PreviewState.Unavailable);
        }
        finally
        {
            if (entered)
            {
                if (_disposed) RemoveWorker();
                _renderGate.Release();
            }
        }
    }

    private void PrepareWorker()
    {
        if (_worker is not null) return;
        using Stream source = OpenEmbeddedWorker();
        var directory = Directory.CreateTempSubdirectory("MailEvidencePreview-");
        string destination = Path.Combine(directory.FullName, "mail-evidence-preview.exe");
        string profile = "MailPreview-" + Guid.NewGuid().ToString("N");
        bool created = false;
        try
        {
            string sid = PreviewSandbox.CreateProfile(profile);
            created = true;
            var identity = new SecurityIdentifier(sid);
            var permissions = new DirectorySecurity();
            permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            using WindowsIdentity user = WindowsIdentity.GetCurrent();
            permissions.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            permissions.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(permissions);
            using (FileStream copy = File.Create(destination))
                source.CopyTo(copy);
            _worker = destination;
            _directory = directory;
            _sid = sid;
            _profile = profile;
        }
        catch
        {
            if (created) PreviewSandbox.RemoveProfile(profile);
            DeleteWorkerFiles(directory, destination);
            throw;
        }
    }

    private static Stream OpenEmbeddedWorker()
    {
        const string resource = "MailViewer.App.AttachmentPreviewWorker";
        var assembly = typeof(AttachmentPreviewRenderer).Assembly;
        if (assembly.GetManifestResourceStream(resource + ".br") is { } compressed)
            return new BrotliStream(compressed, CompressionMode.Decompress);
        return assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException("Attachment preview worker is missing.");
    }

    public void Dispose()
    {
        _disposed = true;
        if (!_renderGate.Wait(0)) return;
        try { RemoveWorker(); }
        finally { _renderGate.Release(); }
    }

    private void RemoveWorker()
    {
        if (_profile is not null) PreviewSandbox.RemoveProfile(_profile);
        DeleteWorkerFiles(_directory, _worker);
        _profile = null;
        _directory = null;
        _worker = null;
        _sid = null;
    }

    private static void DeleteWorkerFiles(DirectoryInfo? directory, string? worker)
    {
        try
        {
            if (worker is not null) File.Delete(worker);
            directory?.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class PreviewSandbox
{
    private const uint CreateFlags = 0x08080404; // No window, extended startup, Unicode environment, suspended.
    private const int StartfUseStdHandles = 0x100;
    private const int JobExtendedLimits = 9;
    private const uint JobLimits = 0x2108; // Terminate on close, process memory, one process.
    private static readonly nuint SecurityCapabilitiesAttribute = 0x00020009;
    private static readonly nuint HandleListAttribute = 0x00020002;

    public static string CreateProfile(string name)
    {
        Marshal.ThrowExceptionForHR(CreateAppContainerProfile(name, "Mail Evidence Preview",
            "Isolated renderer for Mail Evidence attachments", IntPtr.Zero, 0, out IntPtr sid));
        try { return new SecurityIdentifier(sid).Value; }
        finally { FreeSid(sid); }
    }

    public static void RemoveProfile(string name) => DeleteAppContainerProfile(name);

    public static SafeFileHandle Start(string path, string sid, SafePipeHandle input, SafePipeHandle output)
    {
        if (!ConvertStringSidToSid(sid, out IntPtr sidBuffer)) throw LastError();
        IntPtr attributes = IntPtr.Zero;
        IntPtr capabilities = IntPtr.Zero;
        IntPtr inherited = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        bool initialized = false;
        SafeFileHandle? job = null;
        try
        {
            nuint required = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref required);
            attributes = Marshal.AllocHGlobal(checked((int)required));
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref required)) throw LastError();
            initialized = true;
            capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(new SecurityCapabilities { AppContainerSid = sidBuffer }, capabilities, false);
            if (!UpdateProcThreadAttribute(attributes, 0, SecurityCapabilitiesAttribute, capabilities,
                    (nuint)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero))
                throw LastError();

            inherited = Marshal.AllocHGlobal(IntPtr.Size * 2);
            Marshal.WriteIntPtr(inherited, input.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, IntPtr.Size, output.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, HandleListAttribute, inherited,
                    (nuint)(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero))
                throw LastError();

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            environment = Marshal.StringToHGlobalUni(string.Join("\0",
                $"APPDATA={Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}",
                $"LOCALAPPDATA={Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}",
                "RHWP_WEBFONT_OFFLINE=1", $"SystemRoot={windows}",
                $"TEMP={Path.GetTempPath()}", $"TMP={Path.GetTempPath()}",
                $"USERPROFILE={Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}", $"windir={windows}", ""));
            var startup = new StartupInfoEx
            {
                AttributeList = attributes,
                Info = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    Input = input.DangerousGetHandle(),
                    Output = output.DangerousGetHandle(),
                    Error = output.DangerousGetHandle()
                }
            };
            job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid) throw LastError();
            var limits = new ExtendedLimitInformation
            {
                Basic = new BasicLimitInformation { Flags = JobLimits, ActiveProcessLimit = 1 },
                ProcessMemory = 448 * 1024 * 1024
            };
            if (!SetInformationJobObject(job, JobExtendedLimits, ref limits,
                    (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                throw LastError();

            if (!CreateProcess(path, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, true, CreateFlags,
                    environment, windows, ref startup, out ProcessInformation process))
                throw LastError();
            using var child = new SafeFileHandle(process.Process, ownsHandle: true);
            using var thread = new SafeFileHandle(process.Thread, ownsHandle: true);
            if (!AssignProcessToJobObject(job, child) || ResumeThread(thread) == uint.MaxValue)
            {
                Win32Exception error = LastError();
                TerminateProcess(child, 1);
                throw error;
            }
            SafeFileHandle result = job;
            job = null;
            return result;
        }
        finally
        {
            job?.Dispose();
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(capabilities);
            Marshal.FreeHGlobal(inherited);
            Marshal.FreeHGlobal(environment);
            LocalFree(sidBuffer);
        }
    }

    private static Win32Exception LastError() => new(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities
    {
        public IntPtr AppContainerSid, Capabilities;
        public uint CapabilityCount, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute;
        public int Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Info;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessTime, PerJobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr capabilities,
        uint capabilityCount, out IntPtr sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int DeleteAppContainerProfile(string name);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ConvertStringSidToSidW")]
    private static extern bool ConvertStringSidToSid(string sid, out IntPtr buffer);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern IntPtr FreeSid(IntPtr sid);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value,
        nuint size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(string applicationName, IntPtr commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory,
        ref StartupInfoEx startup, out ProcessInformation process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref ExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint code);
}
