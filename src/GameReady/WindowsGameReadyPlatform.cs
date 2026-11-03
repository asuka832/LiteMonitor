using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace LiteMonitor.GameReady;

// All native enumeration is on demand. No WMI, background timer or external process tool.
public sealed class WindowsGameReadyPlatform : IGameReadyPlatform
{
    public void ValidateLaunch(GameReadyOptions options, GameProfile game)
    {
        GameReadyService.Validate(options, game);
        options.KeepPaths = options.KeepPaths.Select(ResolveExecutablePath).ToList();
        options.RestartPaths = options.RestartPaths.Select(ResolveExecutablePath).ToList();
        game.ProcessPath = ResolveExecutablePath(game.ProcessPath);
        game.ExecutablePath = ResolveExecutablePath(game.ExecutablePath);
        game.InstallDirectory = ResolveExecutablePath(game.InstallDirectory);
        game.ProcessCandidates = game.ProcessCandidates.Select(ResolveExecutablePath).ToList();
    }

    public static string ResolveExecutablePath(string path)
    {
        string normalized = GameReadyPolicy.NormalizePath(path);
        if (normalized.Length == 0) return "";
        // Resolve junctions and symbolic links without reading executable contents.
        using var handle = CreateFile(normalized, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) return normalized;
        var text = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, text, (uint)text.Capacity, 0);
        return length > 0 && length < text.Capacity ? GameReadyPolicy.NormalizePath(text.ToString()) : normalized;
    }
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
        "dwm", "fontdrvhost", "sihost", "taskhostw", "explorer", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "SearchIndexer", "RuntimeBroker", "ApplicationFrameHost", "SecurityHealthSystray",
        "SecurityHealthService", "MsMpEng", "NisSrv", "audiodg", "ctfmon", "TextInputHost", "conhost", "OpenConsole"
    };

    public IReadOnlyList<ProcessEntry> Snapshot()
    {
        var parents = ReadParents();
        var services = ReadServiceIds(); // Failure aborts enumeration; do not guess which processes host services.
        var ancestors = new HashSet<int> { Environment.ProcessId };
        int id = Environment.ProcessId;
        while (parents.TryGetValue(id, out int parent) && parent > 0 && ancestors.Add(parent)) id = parent;
        var result = new List<ProcessEntry>();
        using var self = Process.GetCurrentProcess();
        using var currentIdentity = WindowsIdentity.GetCurrent();
        int session = self.SessionId;
        string user = currentIdentity.User?.Value ?? throw new InvalidOperationException("无法确认当前用户。");
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var entry = new ProcessEntry { Id = process.Id, ParentId = parents.GetValueOrDefault(process.Id) };
                try
                {
                    entry.Path = ReadPath(process);
                    entry.StartTicks = process.StartTime.ToUniversalTime().Ticks;
                    entry.ProtectionReason = Protection(process, entry.Path, session, user, services, ancestors);
                    entry.HasWindow = process.MainWindowHandle != IntPtr.Zero;
                    entry.Title = entry.HasWindow ? process.MainWindowTitle : "";
                }
                catch { entry.ProtectionReason = "无法核实身份或权限不足"; }
                result.Add(entry);
            }
        }
        return result;
    }

    public bool IsSame(ProcessEntry entry)
    {
        try
        {
            using var process = Process.GetProcessById(entry.Id);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == entry.StartTicks &&
                GameReadyPolicy.SamePath(ReadPath(process), entry.Path);
        }
        catch { return false; }
    }

    public ProcessEntry? FindGame(string path)
    {
        // Only inspect processes with the configured game name during the bounded startup wait.
        using var self = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        string user = identity.User?.Value ?? "";
        if (user.Length == 0) return null;
        var services = ReadServiceIds();
        ProcessEntry? found = null;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            using (process)
            {
                try
                {
                    string actual = ReadPath(process);
                    if (!GameReadyPolicy.SamePath(actual, path) ||
                        Protection(process, actual, self.SessionId, user, services, new HashSet<int> { Environment.ProcessId }).Length > 0) continue;
                    found ??= new ProcessEntry { Id = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks, Path = actual };
                }
                catch { }
            }
        }
        return found;
    }

    private static string ReadPath(Process process)
    {
        var text = new StringBuilder(32768);
        int length = text.Capacity;
        if (!QueryFullProcessImageName(process.Handle, 0, text, ref length)) throw new Win32Exception();
        return ResolveExecutablePath(text.ToString());
    }

    private static string Protection(Process process, string path, int session, string user, HashSet<int> services, HashSet<int> ancestors)
    {
        if (ancestors.Contains(process.Id)) return "LiteMonitor 与启动宿主";
        if (process.SessionId != session) return "其他登录会话 / 系统会话";
        if (services.Contains(process.Id)) return "系统或驱动服务";
        if (SystemNames.Contains(Path.GetFileNameWithoutExtension(path))) return "Windows 桌面 / 系统组件";
        if (!IsProcessCritical(process.Handle, out bool critical)) return "无法确认关键进程状态";
        if (critical) return "系统关键进程";
        if (!OpenProcessToken(process.Handle, 8, out IntPtr token)) return "无法确认进程所有者";
        try
        {
            using var identity = new WindowsIdentity(token);
            if (identity.User?.Value != user) return "其他用户 / 系统所有者";
        }
        finally { CloseHandle(token); }
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // These directories contain system binaries whose role cannot safely be inferred as ordinary apps.
        foreach (string folder in new[] { "System32", "SysWOW64", "WinSxS", "SystemApps" })
            if (path.StartsWith(Path.Combine(windows, folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "系统组件目录（保守跳过）";
        return "";
    }

    public bool TryClose(ProcessEntry entry) => Act(entry, false);
    public bool TryKill(ProcessEntry entry) => Act(entry, true);
    private bool Act(ProcessEntry entry, bool force)
    {
        try
        {
            using var process = Process.GetProcessById(entry.Id);
            // Opening and retaining this Process handle prevents a reused PID from changing the target.
            if (process.StartTime.ToUniversalTime().Ticks != entry.StartTicks || !GameReadyPolicy.SamePath(ReadPath(process), entry.Path)) return false;
            using var identity = WindowsIdentity.GetCurrent();
            using var self = Process.GetCurrentProcess();
            string user = identity.User?.Value ?? "";
            if (user.Length == 0 || Protection(process, entry.Path, self.SessionId, user,
                ReadServiceIds(), new HashSet<int> { Environment.ProcessId }).Length > 0) return false;
            if (force) { process.Kill(entireProcessTree: false); return true; }
            return process.CloseMainWindow();
        }
        catch { return false; }
    }

    public void Start(string path, string arguments, string workingDirectory)
    {
        if (path.StartsWith("steam://rungameid/", StringComparison.Ordinal))
        {
            if (!uint.TryParse(path["steam://rungameid/".Length..], out uint id) || id == 0) throw new InvalidOperationException("Steam 启动地址无效。");
        }
        else if (GameReadyService.IsEpicLaunchUri(path)) { }
        else if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("应用 EXE 不存在：" + path);
        using var process = Process.Start(new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory.Length > 0 ? workingDirectory : Path.GetDirectoryName(path) ?? ""
        });
        if (process == null && !path.StartsWith("steam://", StringComparison.Ordinal) && !GameReadyService.IsEpicLaunchUri(path))
            throw new InvalidOperationException("未能启动应用：" + path);
    }

    public ulong AvailableMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.AvailablePhysical : 0;
    }

    private static Dictionary<int, int> ReadParents()
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            var result = new Dictionary<int, int>();
            var entry = new Process32 { Size = (uint)Marshal.SizeOf<Process32>(), Exe = "" };
            if (!Process32First(snapshot, ref entry)) throw new Win32Exception();
            do { result[(int)entry.Id] = (int)entry.Parent; } while (Process32Next(snapshot, ref entry));
            return result;
        }
        finally { CloseHandle(snapshot); }
    }

    private static HashSet<int> ReadServiceIds()
    {
        IntPtr manager = OpenSCManager(null, null, 4);
        if (manager == IntPtr.Zero) throw new Win32Exception();
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int resume = 0;
            EnumServicesStatusEx(manager, 0, 0x30, 3, IntPtr.Zero, 0, out int needed, out _, ref resume, null);
            if (needed == 0) throw new Win32Exception();
            buffer = Marshal.AllocHGlobal(needed);
            resume = 0;
            if (!EnumServicesStatusEx(manager, 0, 0x30, 3, buffer, needed, out _, out int count, ref resume, null)) throw new Win32Exception();
            var ids = new HashSet<int>();
            int size = Marshal.SizeOf<ServiceEntry>();
            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<ServiceEntry>(IntPtr.Add(buffer, i * size));
                if (entry.ProcessId != 0) ids.Add((int)entry.ProcessId);
            }
            return ids;
        }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Process32
    {
        public uint Size, Usage, Id;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceEntry
    {
        public IntPtr ServiceName, DisplayName;
        public uint Type, State, Controls, ExitCode, SpecificExit, CheckPoint, WaitHint, ProcessId, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder text, ref int size);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessCritical(IntPtr process, out bool critical);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref Process32 entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref Process32 entry);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool EnumServicesStatusEx(IntPtr manager, int level, uint type, uint state, IntPtr buffer, int size, out int needed, out int count, ref int resume, string? group);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}
