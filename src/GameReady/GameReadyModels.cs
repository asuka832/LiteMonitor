namespace LiteMonitor.GameReady;

using System.Text.Json.Serialization;

public sealed class GameReadyOptions
{
    public bool AcceptedClosePolicy { get; set; }
    public List<GameProfile> Games { get; set; } = new();
    public List<string> KeepPaths { get; set; } = new();
    public List<string> RestartPaths { get; set; } = new();
    public bool KeepNvidiaOverlay { get; set; } = true;
    public int CloseWaitSeconds { get; set; } = 10;
    public int StartupTimeoutSeconds { get; set; } = 120;
}

public sealed class GameProfile
{
    public string Name { get; set; } = "新游戏";
    public string SteamAppId { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string ProcessPath { get; set; } = "";
    public string InstallDirectory { get; set; } = "";
    public string LaunchUri { get; set; } = "";
    public List<string> ProcessCandidates { get; set; } = new();
    public bool ProcessPathOverride { get; set; }
    public bool ExitSteam { get; set; }
    public int SteamDelaySeconds { get; set; } = 30;
    [JsonIgnore]
    public string CatalogName { get; set; } = "";
    public override string ToString() => Name;
}

public sealed record ProcessEntry
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public long StartTicks { get; set; }
    public string Path { get; set; } = "";
    public bool HasWindow { get; set; }
    public string Title { get; set; } = "";
    public string ProtectionReason { get; set; } = "";
    public bool ExitRequested { get; set; }
}

public sealed record PlannedProcess(ProcessEntry Process, string Reason)
{
    public bool ShouldClose => Reason.Length == 0;
}

public sealed class GameReadySession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GameName { get; set; } = "";
    public string GamePath { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Stage { get; set; } = "准备";
    public ulong MemoryBefore { get; set; }
    public ulong MemoryAfter { get; set; }
    public List<RestoreApp> Apps { get; set; } = new();
    public bool HasPending => Apps.Any(a => a.Closed && !a.Restored);
}

public sealed class RestoreApp
{
    public string Path { get; set; } = "";
    public string Label { get; set; } = "";
    public bool CanRestart { get; set; }
    public bool Attempted { get; set; }
    public bool Closed { get; set; }
    public bool Restored { get; set; }
    public string Result { get; set; } = "待退出";
    public List<ProcessEntry> Processes { get; set; } = new();
}

public interface IGameReadyPlatform
{
    void ValidateLaunch(GameReadyOptions options, GameProfile game);
    IReadOnlyList<ProcessEntry> Snapshot();
    ProcessEntry? FindGame(string path) => Snapshot().FirstOrDefault(p => p.ProtectionReason.Length == 0 && GameReadyPolicy.SamePath(p.Path, path));
    bool IsSame(ProcessEntry process);
    bool TryClose(ProcessEntry process);
    bool TryKill(ProcessEntry process);
    void Start(string path, string arguments, string workingDirectory);
    ulong AvailableMemory();
}
