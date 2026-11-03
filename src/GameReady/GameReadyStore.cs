using System.Text.Json;

namespace LiteMonitor.GameReady;

public sealed class GameReadyStore
{
    private readonly string directory;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public GameReadyStore(string directory) => this.directory = directory;
    private T? Read<T>(string name)
    {
        string path = Path.Combine(directory, name);
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("记录为空：" + path); }
        catch (JsonException ex) { throw new InvalidDataException("配置或恢复记录损坏，请保留文件并检查：" + path, ex); }
    }
    private void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        string temporary = path + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    public GameReadyOptions LoadOptions()
    {
        var options = Read<GameReadyOptions>("game-ready.json") ?? new();
        if (options.Games == null || options.KeepPaths == null || options.RestartPaths == null ||
            options.Games.Any(g => g == null || g.Name == null || g.SteamAppId == null || g.ProcessPath == null || g.ExecutablePath == null || g.Arguments == null || g.WorkingDirectory == null ||
                g.InstallDirectory == null || g.LaunchUri == null || g.ProcessCandidates == null || g.ProcessCandidates.Any(p => p == null)) ||
            options.KeepPaths.Any(p => p == null) || options.RestartPaths.Any(p => p == null))
            throw new InvalidDataException("Game Ready 配置内容无效。");
        return options;
    }
    public void SaveOptions(GameReadyOptions options) => Write("game-ready.json", options);
    public GameReadySession? LoadSession()
    {
        var session = Read<GameReadySession>("game-ready-session.json");
        if (session != null && (session.Apps == null || session.Apps.Any(a => a == null || a.Path == null || a.Processes == null || a.Processes.Any(p => p == null || p.Path == null))))
            throw new InvalidDataException("Game Ready 恢复记录内容无效。");
        return session;
    }
    public void SaveSession(GameReadySession session) => Write("game-ready-session.json", session);
    public void DiscardSession()
    {
        string path = Path.Combine(directory, "game-ready-session.json");
        if (File.Exists(path)) File.Move(path, path + ".discarded-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), true);
    }
}
