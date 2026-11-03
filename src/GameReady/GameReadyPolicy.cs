namespace LiteMonitor.GameReady;

public static class GameReadyPolicy
{
    public static bool IsInside(string path, string directory)
    {
        string root = NormalizePath(directory).TrimEnd('\\');
        return root.Length > 3 && NormalizePath(path).StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            path = path.Replace('/', '\\');
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
            else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : "";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }
    public static bool SamePath(string a, string b)
    {
        string normalized = NormalizePath(a);
        return normalized.Length > 0 && string.Equals(normalized, NormalizePath(b), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSteam(ProcessEntry p) => System.IO.Path.GetFileName(p.Path).Equals("steam.exe", StringComparison.OrdinalIgnoreCase) ||
        System.IO.Path.GetFileName(p.Path).Equals("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase) ||
        System.IO.Path.GetFileName(p.Path).Equals("GameOverlayUI.exe", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<PlannedProcess> Plan(IReadOnlyList<ProcessEntry> processes, GameReadyOptions options, GameProfile game)
    {
        var reasons = new Dictionary<int, string>();
        var family = new HashSet<int>();
        var keep = new HashSet<string>(options.KeepPaths.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
        foreach (var p in processes)
        {
            string reason = p.ProtectionReason;
            if (string.IsNullOrEmpty(p.Path) || p.StartTicks == 0) reason = "无法核实进程身份";
            if (keep.Contains(NormalizePath(p.Path))) { reason = "保留名单"; family.Add(p.Id); }
            if (SamePath(p.Path, game.ProcessPath) || SamePath(p.Path, game.ExecutablePath) ||
                IsInside(p.Path, game.InstallDirectory)) { reason = "目标游戏"; family.Add(p.Id); }
            if (game.LaunchUri.StartsWith("com.epicgames.launcher://", StringComparison.Ordinal) &&
                new[] { "EpicGamesLauncher.exe", "EpicWebHelper.exe" }.Contains(Path.GetFileName(p.Path), StringComparer.OrdinalIgnoreCase))
                reason = "Epic 游戏启动器";
            if (IsSteam(p)) reason = "游戏启动器（单独处理）";
            if (options.KeepNvidiaOverlay && (System.IO.Path.GetFileName(p.Path).Equals("NVIDIA Overlay.exe", StringComparison.OrdinalIgnoreCase) ||
                System.IO.Path.GetFileName(p.Path).Equals("NVIDIA Share.exe", StringComparison.OrdinalIgnoreCase)))
            { reason = "NVIDIA 截图覆盖层"; family.Add(p.Id); }
            reasons[p.Id] = reason;
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var p in processes)
                if (!family.Contains(p.Id) && family.Contains(p.ParentId))
                {
                    var parent = processes.FirstOrDefault(x => x.Id == p.ParentId);
                    if (parent == null || parent.StartTicks > p.StartTicks) continue;
                    family.Add(p.Id);
                    if (reasons[p.Id].Length == 0) reasons[p.Id] = "保留应用 / 游戏的子进程";
                    changed = true;
                }
        } while (changed);
        return processes.Select(p => new PlannedProcess(p, reasons[p.Id])).ToArray();
    }
}
