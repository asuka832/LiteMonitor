using System.Runtime.InteropServices;
using System.Text;
using Accessibility;
using Microsoft.Win32;
using LiteMonitor.GameReady;

namespace LiteMonitor.src.UI;

// Best-effort hints only: public accessibility names and persisted Windows tray records.
// Does not read Explorer memory, open the overflow UI, or change tray settings.
internal static class TrayAppHints
{
    public static (IReadOnlyDictionary<string, string> Paths, IReadOnlyList<string> Labels) Read()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root != null)
                foreach (string name in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(name);
                    if (key?.GetValue("ExecutablePath") is not string path) continue;
                    path = ExpandPath(path);
                    if (path.Length == 0) continue;
                    string label = key.GetValue("InitialTooltip") as string ?? "";
                    paths[WindowsGameReadyPlatform.ResolveExecutablePath(path)] = label;
                }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        foreach (string windowClass in new[] { "Shell_TrayWnd", "NotifyIconOverflowWindow" })
        {
            IntPtr window = FindWindow(windowClass, null);
            if (window == IntPtr.Zero) continue;
            if (windowClass == "Shell_TrayWnd")
            {
                window = FindWindowEx(window, IntPtr.Zero, "TrayNotifyWnd", null);
                if (window == IntPtr.Zero) continue;
            }
            EnumWindowsCallback callback = (child, _) =>
            {
                var className = new StringBuilder(128);
                GetClassName(child, className, className.Capacity);
                if (className.ToString() == "ToolbarWindow32") ReadAccessibleNames(child, labels);
                return true;
            };
            EnumChildWindows(window, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
        return (paths, labels.ToArray());
    }

    private static string ExpandPath(string value)
    {
        value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (value.StartsWith('{') && value.IndexOf('}') is int end && end > 0 &&
            Guid.TryParse(value[..(end + 1)], out var folder))
        {
            IntPtr memory = IntPtr.Zero;
            try
            {
                if (SHGetKnownFolderPath(ref folder, 0, IntPtr.Zero, out memory) == 0)
                    value = (Marshal.PtrToStringUni(memory) ?? "") + value[(end + 1)..];
            }
            finally { if (memory != IntPtr.Zero) Marshal.FreeCoTaskMem(memory); }
        }
        return GameReadyPolicy.NormalizePath(value);
    }

    private static void ReadAccessibleNames(IntPtr window, HashSet<string> labels)
    {
        var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        IAccessible? accessible = null;
        try
        {
            if (AccessibleObjectFromWindow(window, 0xFFFFFFFC, ref iid, out accessible) != 0 || accessible == null) return;
            // Toolbar child IDs are indexed from one; do not recursively walk unrelated UI.
            int count = Math.Min(accessible.accChildCount, 256);
            for (int child = 1; child <= count; child++)
            {
                try
                {
                    string? name = accessible.get_accName(child);
                    if (!string.IsNullOrWhiteSpace(name)) labels.Add(name);
                }
                catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException) { }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or InvalidOperationException) { }
        finally { if (accessible != null && Marshal.IsComObject(accessible)) Marshal.ReleaseComObject(accessible); }
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int size);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
