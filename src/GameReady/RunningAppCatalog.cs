using System.Diagnostics;

namespace LiteMonitor.GameReady;

public sealed record RunningAppChoice(string Path, string Name, string Detail, bool HasWindow, bool HasTrayMatch, int ProcessCount)
{
    public int Priority => HasWindow ? 0 : HasTrayMatch ? 1 : 2;
}

public static class RunningAppCatalog
{
    public static IReadOnlyList<RunningAppChoice> Build(IEnumerable<ProcessEntry> processes,
        IReadOnlyDictionary<string, string> trayPaths, IReadOnlyList<string> trayLabels, Func<string, string>? readName = null)
    {
        readName ??= ReadName;
        var tray = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in trayPaths) tray[GameReadyPolicy.NormalizePath(pair.Key)] = pair.Value;
        var result = new List<RunningAppChoice>();
        foreach (var group in processes.Where(p => p.ProtectionReason.Length == 0 && GameReadyPolicy.NormalizePath(p.Path).Length > 0)
            .GroupBy(p => GameReadyPolicy.NormalizePath(p.Path), StringComparer.OrdinalIgnoreCase))
        {
            string name = readName(group.Key);
            if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileNameWithoutExtension(group.Key);
            bool hasWindow = group.Any(p => p.HasWindow);
            string title = group.Where(p => p.HasWindow && p.Title.Length > 0).Select(p => p.Title).FirstOrDefault() ?? "";
            bool trayMatch = tray.TryGetValue(group.Key, out string? trayText);
            if (!trayMatch)
            {
                trayText = trayLabels.FirstOrDefault(label => Matches(label, name) || Matches(label, System.IO.Path.GetFileNameWithoutExtension(group.Key)));
                trayMatch = trayText != null;
            }
            result.Add(new RunningAppChoice(group.Key, name, title.Length > 0 ? title : trayText ?? "",
                hasWindow, trayMatch, group.Count()));
        }
        return result.OrderBy(p => p.Priority).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool Matches(string label, string name)
    {
        if (name.Length < 2) return false;
        if (label.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            label.StartsWith(name + "\n", StringComparison.OrdinalIgnoreCase) ||
            label.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)) return true;
        return name.Length >= 4 && label.Contains(name, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadName(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            string name = version.FileDescription?.Trim() ?? "";
            if (name.Length > 0 && !name.Equals("Electron", StringComparison.OrdinalIgnoreCase)) return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }
}
