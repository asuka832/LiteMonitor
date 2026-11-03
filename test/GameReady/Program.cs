using LiteMonitor.GameReady;
using System.Diagnostics;

if (args.Contains("--helper")) { await Task.Delay(30000); return; }
if (args.Contains("--discover"))
{
    var result = GameLibraryDiscovery.ScanInstalled(CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

var game = new GameProfile { Name = "Test", ExecutablePath = @"C:\Games\Game.exe", ProcessPath = @"C:\Games\Game.exe" };
DiscoveryTests.Run(Check);
RunningAppTests.Run(Check);
var options = new GameReadyOptions { CloseWaitSeconds = 0, StartupTimeoutSeconds = 1 };
ProcessEntry P(int id, string path, int parent = 0, string reason = "", bool window = true) =>
    new() { Id = id, Path = path, ParentId = parent, StartTicks = id, ProtectionReason = reason, HasWindow = window };
var entries = new[] { P(1, @"C:\Apps\Keep.exe"), P(2, @"C:\Apps\Helper.exe", 1),
    P(3, @"C:\Apps\Close.exe"), P(4, game.ProcessPath), P(5, @"C:\Windows\System.exe", reason: "系统"),
    P(6, @"C:\Steam\steam.exe"), P(7, @"C:\Apps\Keeper.exe") };
options.KeepPaths.Add(@"c:\apps\KEEP.exe");
var plan = GameReadyPolicy.Plan(entries, options, game);
Check(plan.Single(p => p.Process.Id == 1).Reason.Length > 0, "白名单路径不区分大小写");
Check(!plan.Single(p => p.Process.Id == 2).ShouldClose, "保留应用的现有辅助进程受到保护");
Check(plan.Single(p => p.Process.Id == 3).ShouldClose, "普通应用包含前台窗口");
Check(!plan.Single(p => p.Process.Id == 4).ShouldClose, "目标游戏受到保护");
Check(!plan.Single(p => p.Process.Id == 5).ShouldClose, "系统保护优先");
Check(!plan.Single(p => p.Process.Id == 6).ShouldClose, "Steam 在清理阶段临时保留");
Check(plan.Single(p => p.Process.Id == 7).ShouldClose, "保留名单不做前缀匹配");
Check(GameReadyPolicy.SamePath(@"C:/Apps/../Apps/Keep.exe", @"C:\Apps\Keep.exe"), "路径斜杠和点段规范化");
var alternate = new GameReadyOptions { KeepPaths = new() { @"C:/Apps/../Apps/Keep.exe" } };
Check(!GameReadyPolicy.Plan(entries, alternate, game).Single(p => p.Process.Id == 1).ShouldClose, "非标准路径写法仍受白名单保护");

string testDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LiteMonitor-GameReady-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDir);
try
{
    var store = new GameReadyStore(testDir);
    store.SaveOptions(options);
    Check(store.LoadOptions().KeepPaths.Count == 1, "配置往返保存");
    var fake = new FakePlatform(entries.ToList());
    var service = new GameReadyService(fake, store);
    await service.RunAsync(options, game, null, CancellationToken.None);
    Check(fake.Closed.Contains(3) && fake.Killed.Contains(3), "正常退出超时才强制结束");
    Check(!fake.Closed.Contains(1) && !fake.Killed.Contains(4), "执行中不结束白名单和游戏");
    Check(!fake.Killed.Contains(6) && !fake.Started.Any(p => p.EndsWith("steam.exe")), "默认不退出 Steam");
    Check(store.LoadSession()!.Apps.Any(a => a.Path.EndsWith("Close.exe") && a.Closed), "退出结果持久化");
    int before = fake.Started.Count;
    await service.RestoreAsync(null, CancellationToken.None);
    Check(fake.Started.Count > before, "恢复实际关闭的应用");
    before = fake.Started.Count;
    await service.RestoreAsync(null, CancellationToken.None);
    Check(fake.Started.Count == before, "重复恢复不重复启动");

    store.DiscardSession();
    fake = new FakePlatform(entries.ToList()) { ReusedId = 3 };
    service = new GameReadyService(fake, store);
    await service.RunAsync(options, game, null, CancellationToken.None);
    Check(!fake.Killed.Contains(3), "PID 身份变化不强制结束");

    store.DiscardSession();
    fake = new FakePlatform(entries.ToList());
    service = new GameReadyService(fake, store);
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    try { await service.RunAsync(options, game, null, cts.Token); } catch (OperationCanceledException) { }
    Check(fake.Closed.Count == 0 && fake.Killed.Count == 0, "预先取消不退出任何应用");

    store.DiscardSession();
    fake = new FakePlatform(entries.ToList());
    service = new GameReadyService(fake, store);
    game.ExitSteam = true;
    game.SteamDelaySeconds = 0;
    await service.RunAsync(options, game, null, CancellationToken.None);
    Check(fake.Closed.Contains(6) && !fake.Killed.Contains(6), "Steam 拒绝退出不升级强杀");
    bool blocked = false;
    try { await service.RunAsync(options, game, null, CancellationToken.None); } catch (InvalidOperationException) { blocked = true; }
    Check(blocked, "未恢复会话不能覆盖");

    store.DiscardSession();
    fake = new FakePlatform(entries.Where(p => p.Id != 4).ToList()) { FailLaunch = true };
    service = new GameReadyService(fake, store);
    try { await service.RunAsync(options, game, null, CancellationToken.None); } catch (InvalidOperationException) { }
    Check(store.LoadSession()!.Apps.Any(a => a.Closed), "游戏启动失败保留恢复记录");

    store.DiscardSession();
    var lateProcesses = entries.ToList();
    fake = new FakePlatform(lateProcesses) { OnClose = () => lateProcesses.Add(P(900, @"C:\Apps\Later.exe")) };
    service = new GameReadyService(fake, store);
    game.ExitSteam = false;
    await service.RunAsync(options, game, null, CancellationToken.None);
    Check(!fake.Killed.Contains(900) && !fake.Closed.Contains(900), "快照之后新增的应用不清理");

    store.DiscardSession();
    fake = new FakePlatform(entries.ToList());
    service = new GameReadyService(fake, store);
    using (var cancel = new CancellationTokenSource())
    {
        options.CloseWaitSeconds = 1;
        var running = service.RunAsync(options, game, null, cancel.Token);
        bool concurrent = false;
        try { await service.RunAsync(options, game, null, CancellationToken.None); } catch (InvalidOperationException) { concurrent = true; }
        Check(concurrent, "并行操作被拒绝");
        cancel.Cancel();
        try { await running; } catch (OperationCanceledException) { }
        Check(fake.Killed.Count == 0, "等待期间取消不会进入强制阶段");
        options.CloseWaitSeconds = 0;
    }

    store.DiscardSession();
    var background = P(50, @"C:\Apps\TrayApp.exe", window: false);
    fake = new FakePlatform(entries.Append(background).ToList());
    service = new GameReadyService(fake, store);
    options.RestartPaths.Add(background.Path);
    await service.RunAsync(options, game, null, CancellationToken.None);
    await service.RestoreAsync(null, CancellationToken.None);
    Check(fake.Started.Contains(background.Path), "显式配置的无窗口后台应用可以恢复");

    store.DiscardSession();
    fake = new FakePlatform(new());
    service = new GameReadyService(fake, store);
    store.SaveSession(new GameReadySession { Apps = new()
    {
        new RestoreApp { Path = @"C:\Apps\Attempted.exe", Attempted = true, CanRestart = true, Processes = new() { P(70, @"C:\Apps\Attempted.exe") with { ExitRequested = true } } },
        new RestoreApp { Path = @"C:\Apps\Untouched.exe", Attempted = false, CanRestart = true, Processes = new() { P(71, @"C:\Apps\Untouched.exe") } }
    } });
    await service.RestoreAsync(null, CancellationToken.None);
    Check(fake.Started.Count == 1 && fake.Started[0].EndsWith("Attempted.exe"), "意外中断后只恢复已尝试退出的应用");

    store.DiscardSession();
    fake = new FakePlatform(entries.ToList()) { ReusedId = 3 };
    service = new GameReadyService(fake, store);
    await service.RunAsync(options, game, null, CancellationToken.None);
    Check(!store.LoadSession()!.Apps.Single(a => a.Path.EndsWith("Close.exe")).Closed, "行动之前身份已变化的进程不记为已关闭");

    store.DiscardSession();
    fake = new FakePlatform(entries.Where(p => p.Id != 3 && p.Id != 7).ToList());
    service = new GameReadyService(fake, store);
    await service.RunAsync(options, game, null, CancellationToken.None);
    await service.RunAsync(options, game, null, CancellationToken.None);
    Check(true, "没有实际关闭的应用时可以直接开始新会话");

    store.DiscardSession();
    var otherUserApp = P(80, @"C:\Apps\OtherUser.exe", reason: "其他登录会话");
    fake = new FakePlatform(new() { otherUserApp });
    service = new GameReadyService(fake, store);
    store.SaveSession(new GameReadySession { Apps = new()
    {
        new RestoreApp { Path = otherUserApp.Path, Closed = true, CanRestart = true }
    } });
    await service.RestoreAsync(null, CancellationToken.None);
    Check(fake.Started.Contains(otherUserApp.Path), "其他登录会话的同路径应用不会阻止本用户恢复");

    File.WriteAllText(System.IO.Path.Combine(testDir, "game-ready-session.json"), "broken");
    bool corrupt = false;
    try { store.LoadSession(); } catch (InvalidDataException) { corrupt = true; }
    Check(corrupt, "损坏会话报错不静默覆盖");
}
finally { Directory.Delete(testDir, true); }

Console.WriteLine("All Game Ready tests passed. No user processes were terminated.");

if (args.Contains("--native"))
{
    var native = new WindowsGameReadyPlatform();
    var snapshot = native.Snapshot();
    Check(snapshot.Count > 0, "Windows 原生快照可读取");
    Check(snapshot.Single(p => p.Id == Environment.ProcessId).ProtectionReason.Length > 0, "原生策略保护测试宿主");
    Check(snapshot.Where(p => p.Path.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)).All(p => p.ProtectionReason.Length > 0), "Windows Shell 不作为清理对象");
    Console.WriteLine($"Native snapshot: {snapshot.Count} processes, {snapshot.Count(p => p.ProtectionReason.Length > 0)} protected.");
    using var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--helper") { UseShellExecute = false, CreateNoWindow = true })!;
    try
    {
        var entry = native.Snapshot().Single(p => p.Id == helper.Id);
        Check(entry.ProtectionReason.Length == 0 && native.IsSame(entry), "专用测试子进程可识别");
        var changed = new ProcessEntry { Id = entry.Id, Path = entry.Path, StartTicks = entry.StartTicks + 1 };
        Check(!native.TryKill(changed) && !helper.HasExited, "真实进程身份不符时拒绝结束");
        Check(native.TryKill(entry), "仅结束本次创建的可丢弃测试子进程");
        await helper.WaitForExitAsync();
        Check(!native.IsSame(entry), "退出后身份检查返回 false");
    }
    finally { if (!helper.HasExited) helper.Kill(); }
}

sealed class FakePlatform(List<ProcessEntry> processes) : IGameReadyPlatform
{
    public void ValidateLaunch(GameReadyOptions options, GameProfile game) { }
    public HashSet<int> Closed = new();
    public HashSet<int> Killed = new();
    public List<string> Started = new();
    public int ReusedId;
    public bool FailLaunch;
    public Action? OnClose;
    public IReadOnlyList<ProcessEntry> Snapshot() => processes.ToArray();
    public bool IsSame(ProcessEntry p) => p.Id != ReusedId && processes.Any(x => x.Id == p.Id && x.StartTicks == p.StartTicks);
    public bool TryClose(ProcessEntry p) { Closed.Add(p.Id); var action = OnClose; OnClose = null; action?.Invoke(); return true; }
    public bool TryKill(ProcessEntry p) { Killed.Add(p.Id); processes.RemoveAll(x => x.Id == p.Id); return true; }
    public void Start(string path, string arguments, string workingDirectory)
    {
        if (FailLaunch) throw new InvalidOperationException("模拟启动失败");
        Started.Add(path);
        if (arguments == "-shutdown") { Closed.Add(6); return; }
        processes.Add(new ProcessEntry { Id = 1000 + Started.Count, StartTicks = 1000 + Started.Count, Path = path, HasWindow = true });
    }
    public ulong AvailableMemory() => 4UL * 1024 * 1024 * 1024;
}
