using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LiteMonitor.GameReady;

public static class GameShortcutImport
{
    private static readonly Regex SteamApp = new(@"(?:steam://(?:rungameid|run)/|-applaunch\s+)(\d{1,10})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public static bool IsSupported(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    public static GameProfile? Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) return null;
        string ext = Path.GetExtension(full);
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return Profile(FriendlyExecutableName(full), full, "", Path.GetDirectoryName(full) ?? "", "");
        if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) return FromUrl(full);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return FromLink(full);
        return null;
    }

    public static string? ExecutableFrom(string path)
    {
        GameProfile? game = Read(path);
        if (game == null) return null;
        string exe = game.ProcessPath.Length > 0 ? game.ProcessPath : game.ExecutablePath;
        return exe.Length > 0 && File.Exists(exe) ? exe : null;
    }

    public static void CompleteFromLibrary(GameProfile incoming, IEnumerable<GameProfile> library)
    {
        if (incoming.SteamAppId.Length == 0 || incoming.ProcessPath.Length > 0) return;
        var match = library.FirstOrDefault(g => g.SteamAppId == incoming.SteamAppId && g.ProcessPath.Length > 0);
        if (match == null) return;
        incoming.ProcessPath = match.ProcessPath;
        incoming.InstallDirectory = match.InstallDirectory;
        incoming.WorkingDirectory = match.WorkingDirectory;
        incoming.ProcessCandidates = match.ProcessCandidates.ToList();
    }

    public static bool TryReadSteamAppId(string text, out string appId)
    {
        appId = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        Match match;
        try { match = SteamApp.Match(text); }
        catch (RegexMatchTimeoutException) { return false; }
        if (!match.Success || !uint.TryParse(match.Groups[1].Value, out uint id) || id == 0) return false;
        appId = id.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    public static bool SameGame(GameProfile existing, GameProfile incoming)
    {
        if (incoming.SteamAppId.Length > 0 && existing.SteamAppId == incoming.SteamAppId) return true;
        if (incoming.ProcessPath.Length == 0) return false;
        return GameReadyPolicy.SamePath(existing.ProcessPath, incoming.ProcessPath) ||
            GameReadyPolicy.SamePath(existing.ExecutablePath, incoming.ProcessPath);
    }

    public static GameProfile Apply(GameReadyOptions options, GameProfile? selected, GameProfile incoming)
    {
        var existing = options.Games.FirstOrDefault(g => SameGame(g, incoming));
        if (existing == null && selected != null && selected.ProcessPath.Length == 0 && incoming.ProcessPath.Length > 0 &&
            (incoming.SteamAppId.Length == 0 || incoming.SteamAppId == selected.SteamAppId))
            existing = selected;
        if (existing == null)
        {
            options.Games.Add(incoming);
            return incoming;
        }
        if ((existing.Name.Length == 0 || existing.Name == "\u65b0\u6e38\u620f" || (ContainsCjk(incoming.Name) && !ContainsCjk(existing.Name))) &&
            incoming.Name.Length > 0)
            existing.Name = incoming.Name;
        if (existing.SteamAppId.Length == 0) existing.SteamAppId = incoming.SteamAppId;
        if (existing.ProcessPath.Length == 0 && incoming.ProcessPath.Length > 0)
        {
            existing.ProcessPath = incoming.ProcessPath;
            existing.ExecutablePath = incoming.ExecutablePath.Length > 0 ? incoming.ExecutablePath : incoming.ProcessPath;
            if (existing.Arguments.Length == 0) existing.Arguments = incoming.Arguments;
            if (existing.WorkingDirectory.Length == 0) existing.WorkingDirectory = incoming.WorkingDirectory;
            if (existing.InstallDirectory.Length == 0) existing.InstallDirectory = incoming.InstallDirectory;
            if (existing.ProcessCandidates.Count == 0) existing.ProcessCandidates = incoming.ProcessCandidates;
            existing.ProcessPathOverride = incoming.ProcessPathOverride;
        }
        return existing;
    }

    private static GameProfile? FromUrl(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > 65536) throw new InvalidDataException("\u5feb\u6377\u65b9\u5f0f\u8fc7\u5927\u3002");
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        if (!TryReadSteamAppId(reader.ReadToEnd(), out string appId)) return null;
        return Profile(Path.GetFileNameWithoutExtension(path), "", "", "", appId);
    }

    private static GameProfile? FromLink(string path)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("\u65e0\u6cd5\u8bfb\u53d6\u5feb\u6377\u65b9\u5f0f\u3002");
        object? shell = Activator.CreateInstance(shellType);
        if (shell == null) throw new InvalidOperationException("\u65e0\u6cd5\u8bfb\u53d6\u5feb\u6377\u65b9\u5f0f\u3002");
        object? shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            if (shortcut == null) return null;
            string target = Property(shortcut, "TargetPath");
            string arguments = Property(shortcut, "Arguments");
            string working = Property(shortcut, "WorkingDirectory");
            TryReadSteamAppId(target + "\n" + arguments, out string appId);
            var profile = Profile(Path.GetFileNameWithoutExtension(path), target, arguments, working, appId);
            return profile.ProcessPath.Length > 0 || profile.SteamAppId.Length > 0 ? profile : null;
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string Property(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null) as string ?? "";

    private static GameProfile Profile(string name, string exe, string arguments, string workingDirectory, string steamAppId)
    {
        string process = GameReadyPolicy.NormalizePath(exe);
        bool exeOk = process.Length > 0 && process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(process) &&
            !Path.GetFileName(process).Equals("steam.exe", StringComparison.OrdinalIgnoreCase);
        string directory = workingDirectory.Length > 0 ? GameReadyPolicy.NormalizePath(workingDirectory) : "";
        if (directory.Length == 0 && exeOk) directory = Path.GetDirectoryName(process) ?? "";
        return new GameProfile
        {
            Name = name.Length > 0 ? name : "\u65b0\u6e38\u620f",
            SteamAppId = steamAppId,
            ExecutablePath = exeOk ? process : "",
            ProcessPath = exeOk ? process : "",
            ProcessPathOverride = exeOk,
            Arguments = exeOk ? arguments : "",
            WorkingDirectory = directory,
            InstallDirectory = directory,
            ProcessCandidates = exeOk ? new List<string> { process } : new()
        };
    }

    private static string FriendlyExecutableName(string path)
    {
        try
        {
            string description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? "";
            if (description.Length is > 0 and <= 80 && description.Any(char.IsLetterOrDigit) &&
                !description.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) &&
                !description.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
                return description;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static bool ContainsCjk(string text) => text.Any(c => c is >= '\u4E00' and <= '\u9FFF');
}
