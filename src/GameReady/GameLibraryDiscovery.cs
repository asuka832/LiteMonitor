using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteMonitor.GameReady;

public sealed class GameDiscoveryResult
{
    public List<GameProfile> Games { get; } = new();
    public List<string> Warnings { get; } = new();
}

// Reads launcher metadata and bounded game folders only. Never traverses entire disks.
public static class GameLibraryDiscovery
{
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Engine", "_CommonRedist", "redist", "redistributables", "DirectX", "Support", "EasyAntiCheat",
        "EasyAntiCheat_EOS", "BattlEye", "DotNet", "MonoBleedingEdge", "node_modules", ".git"
    };

    public static GameDiscoveryResult ScanInstalled(CancellationToken token)
    {
        var roots = new List<string>();
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            if (key?.GetValue("SteamPath") is string path) roots.Add(path);
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = machine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (key?.GetValue("InstallPath") is string path) roots.Add(path);
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        return Scan(roots, new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests") }, token);
    }

    public static GameDiscoveryResult Scan(IEnumerable<string> steamRoots, IEnumerable<string> epicManifestDirectories, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = new GameDiscoveryResult();
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots.Select(GameReadyPolicy.NormalizePath).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            libraries.Add(root);
            ReadSafely(result, root, () =>
            {
                string file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(file)) return;
                var document = ParseVdf(ReadSmallFile(file));
                if (document.GetValueOrDefault("libraryfolders") is not Dictionary<string, object> folders) return;
                foreach (var pair in folders)
                {
                    string? path = pair.Value is Dictionary<string, object> folder ? Text(folder, "path") :
                        uint.TryParse(pair.Key, out _) ? pair.Value as string : null;
                    if (!string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path)) libraries.Add(GameReadyPolicy.NormalizePath(path));
                }
            });
        }
        foreach (string library in libraries)
        {
            token.ThrowIfCancellationRequested();
            string apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            ReadSafely(result, apps, () =>
            {
                foreach (string file in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    token.ThrowIfCancellationRequested();
                    ReadSafely(result, file, () =>
                    {
                        if (ParseVdf(ReadSmallFile(file)).GetValueOrDefault("AppState") is not Dictionary<string, object> state) return;
                        string id = Text(state, "appid");
                        string install = Text(state, "installdir");
                        if (!uint.TryParse(id, out uint appId) || appId == 0 || install.Length == 0) return;
                        string common = Path.Combine(apps, "common");
                        string directory = GameReadyPolicy.NormalizePath(Path.Combine(common, install));
                        if (!GameReadyPolicy.IsInside(directory, common) || !Directory.Exists(directory)) return;
                        var candidates = FindExecutables(directory, token);
                        if (candidates.Count == 0) return;
                        string name = Text(state, "name");
                        result.Games.Add(new GameProfile
                        {
                            Name = name.Length > 0 ? name : install, SteamAppId = id, InstallDirectory = directory,
                            ProcessPath = candidates[0], ProcessCandidates = candidates, WorkingDirectory = directory
                        });
                    });
                }
            });
        }
        foreach (string directory in epicManifestDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory)) continue;
            ReadSafely(result, directory, () =>
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*.item"))
                {
                    token.ThrowIfCancellationRequested();
                    ReadSafely(result, file, () =>
                    {
                        using var json = JsonDocument.Parse(ReadSmallFile(file));
                        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Epic 清单必须是对象。");
                        string Value(string key) => json.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                        string install = GameReadyPolicy.NormalizePath(Value("InstallLocation"));
                        string app = Value("AppName");
                        if (!Directory.Exists(install) || app.Length == 0) return;
                        var candidates = FindExecutables(install, token);
                        string declared = GameReadyPolicy.NormalizePath(Path.Combine(install, Value("LaunchExecutable")));
                        if (GameReadyPolicy.IsInside(declared, install) && File.Exists(declared) && IsGameExecutable(declared) &&
                            !candidates.Any(p => p.Contains("-Shipping", StringComparison.OrdinalIgnoreCase)))
                        { candidates.RemoveAll(p => GameReadyPolicy.SamePath(p, declared)); candidates.Insert(0, declared); }
                        if (candidates.Count == 0) return;
                        string ns = Value("CatalogNamespace"), item = Value("CatalogItemId");
                        string launchId = ns.Length > 0 && item.Length > 0 ? ns + ":" + item + ":" + app : app;
                        result.Games.Add(new GameProfile
                        {
                            Name = Value("DisplayName").Length > 0 ? Value("DisplayName") : app,
                            InstallDirectory = install, ProcessPath = candidates[0], ProcessCandidates = candidates,
                            WorkingDirectory = install, LaunchUri = "com.epicgames.launcher://apps/" + Uri.EscapeDataString(launchId) + "?action=launch&silent=true"
                        });
                    });
                }
            });
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        result.Games.RemoveAll(g => !seen.Add(Identity(g)));
        result.Games.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return result;
    }

    public static void Merge(GameReadyOptions options, IEnumerable<GameProfile> discovered)
    {
        foreach (var game in discovered)
        {
            var existing = options.Games.FirstOrDefault(g => Identity(g).Equals(Identity(game), StringComparison.OrdinalIgnoreCase));
            if (existing == null) { options.Games.Add(game); continue; }
            existing.InstallDirectory = game.InstallDirectory;
            existing.ProcessCandidates = game.ProcessCandidates;
            existing.ProcessPath = game.ProcessPath;
            if (existing.WorkingDirectory.Length == 0) existing.WorkingDirectory = game.WorkingDirectory;
            if (existing.LaunchUri.Length == 0) existing.LaunchUri = game.LaunchUri;
        }
    }

    private static string Identity(GameProfile game) => game.SteamAppId.Length > 0 ? "steam:" + game.SteamAppId :
        game.LaunchUri.Length > 0 ? game.LaunchUri : GameReadyPolicy.NormalizePath(game.ProcessPath);

    private static List<string> FindExecutables(string root, CancellationToken token)
    {
        var found = new List<(string Path, int Score)>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        int visited = 0;
        while (queue.Count > 0 && visited++ < 512 && found.Count < 128)
        {
            token.ThrowIfCancellationRequested();
            var directory = queue.Dequeue();
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory.Path, "*.exe"))
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsGameExecutable(file)) continue;
                    int score = file.Contains("-Shipping", StringComparison.OrdinalIgnoreCase) ? 1000 : directory.Depth == 0 ? 50 :
                        file.Contains("Win64", StringComparison.OrdinalIgnoreCase) ? 40 : 10;
                    string Stem(string value) => new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                    if (Stem(Path.GetFileNameWithoutExtension(file)) == Stem(Path.GetFileName(root))) score += 200;
                    long size = new FileInfo(file).Length;
                    if (size > 64L * 1024 * 1024) score += 100;
                    else if (size > 8L * 1024 * 1024) score += 40;
                    found.Add((file, score));
                    if (found.Count >= 128) break;
                }
                if (directory.Depth >= 6) continue;
                foreach (string folder in Directory.EnumerateDirectories(directory.Path))
                    if (!SkipDirectories.Contains(Path.GetFileName(folder)) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0)
                        queue.Enqueue((folder, directory.Depth + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        bool shipping = found.Any(f => f.Path.Contains("-Shipping", StringComparison.OrdinalIgnoreCase));
        int best = found.Count > 0 ? found.Max(f => f.Score) : 0;
        return found.Where(f => shipping ? f.Path.Contains("-Shipping", StringComparison.OrdinalIgnoreCase) : f.Score >= best - 50)
            .OrderByDescending(f => f.Score).ThenBy(f => f.Path.Length)
            .Select(f => GameReadyPolicy.NormalizePath(f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
    }

    private static bool IsGameExecutable(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("7z", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("8z", StringComparison.OrdinalIgnoreCase) &&
            !new[] { "crash", "unins", "setup", "install", "redist", "helper", "launcher", "anticheat", "benchmark", "report", "vc_redist",
                "checkenv", "createdump", "webengine", "launchpad", "sendrpt", "diagnostic", "compiler", "service", "apputil", "winrtutil" }
                .Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));
    }
    private static string ReadSmallFile(string file)
    {
        if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new InvalidDataException("游戏库清单过大。");
        return File.ReadAllText(file);
    }
    private static void ReadSafely(GameDiscoveryResult result, string source, Action read)
    {
        try { read(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or RegexMatchTimeoutException)
        { result.Warnings.Add(Path.GetFileName(source) + "：" + ex.Message); }
    }
    private static string Text(Dictionary<string, object> values, string key) => values.GetValueOrDefault(key) as string ?? "";

    private static Dictionary<string, object> ParseVdf(string text)
    {
        var tokens = Regex.Matches(text, "\"((?:\\\\.|[^\"\\\\])*)\"|[{}]|//[^\\r\\n]*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Cast<Match>().Where(m => !m.Value.StartsWith("//", StringComparison.Ordinal)).ToArray();
        int index = 0;
        Dictionary<string, object> Read(int depth)
        {
            if (depth > 16) throw new InvalidDataException("游戏库清单嵌套过深。");
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (index < tokens.Length)
            {
                string key = tokens[index++].Value;
                if (key == "}") return values;
                if (key == "{" || index >= tokens.Length) throw new InvalidDataException("游戏库清单格式无效。");
                string value = tokens[index++].Value;
                values[Unquote(key)] = value == "{" ? Read(depth + 1) : value == "}" ?
                    throw new InvalidDataException("游戏库清单缺少值。") : Unquote(value);
            }
            if (depth > 0) throw new InvalidDataException("游戏库清单缺少结束括号。");
            return values;
        }
        return Read(0);
    }
    private static string Unquote(string value) => value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
}
