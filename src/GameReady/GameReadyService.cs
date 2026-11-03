namespace LiteMonitor.GameReady;

public sealed class GameReadyService
{
    private readonly IGameReadyPlatform platform;
    private readonly GameReadyStore store;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public GameReadyService(IGameReadyPlatform platform, GameReadyStore store) { this.platform = platform; this.store = store; }

    public static void Validate(GameReadyOptions options, GameProfile game)
    {
        if (string.IsNullOrWhiteSpace(game.Name) || !Path.IsPathFullyQualified(game.ProcessPath) || !File.Exists(game.ProcessPath) || !game.ProcessPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请填写游戏名称，并选择实际游戏进程的完整 EXE 路径。");
        if (game.SteamAppId.Length > 0)
        {
            if (!uint.TryParse(game.SteamAppId, out uint id) || id == 0) throw new InvalidOperationException("Steam App ID 必须为正整数。");
        }
        else if (game.LaunchUri.Length > 0)
        {
            if (!IsEpicLaunchUri(game.LaunchUri)) throw new InvalidOperationException("Epic 启动地址无效，请重新扫描游戏库。");
        }
        else if (!Path.IsPathFullyQualified(game.ExecutablePath) || !File.Exists(game.ExecutablePath) || !game.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择有效的本地游戏 EXE，或填写 Steam App ID。");
        if (game.WorkingDirectory.Length > 0 && !Directory.Exists(game.WorkingDirectory)) throw new InvalidOperationException("游戏工作目录不存在。");
        if (options.CloseWaitSeconds is < 0 or > 120 || options.StartupTimeoutSeconds is < 1 or > 600 || game.SteamDelaySeconds is < 0 or > 600)
            throw new InvalidOperationException("等待时间超出允许范围。");
    }

    public static bool IsEpicLaunchUri(string uri) => uri.StartsWith("com.epicgames.launcher://apps/", StringComparison.Ordinal) &&
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Host == "apps" && parsed.AbsolutePath.Length > 1 &&
        parsed.Query == "?action=launch&silent=true" && parsed.Fragment.Length == 0;

    public async Task<GameReadySession> RunAsync(GameReadyOptions options, GameProfile game, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!await Gate.WaitAsync(0, token)) throw new InvalidOperationException("已有 Game Ready 操作正在运行。");
        GameReadySession? session = null;
        try
        {
            var previous = store.LoadSession();
            if (previous != null && previous.Apps.Any(a => !a.Restored && (a.Closed || a.Processes.Any(p => p.ExitRequested && !platform.IsSame(p)))))
                throw new InvalidOperationException("请先恢复或放弃上一次会话，避免覆盖恢复记录。");
            platform.ValidateLaunch(options, game);
            var snapshot = platform.Snapshot();
            var plan = GameReadyPolicy.Plan(snapshot, options, game);
            session = new GameReadySession { GameName = game.Name, GamePath = game.ProcessPath, MemoryBefore = platform.AvailableMemory() };
            foreach (var group in plan.Where(p => p.ShouldClose).GroupBy(p => p.Process.Path, StringComparer.OrdinalIgnoreCase))
            {
                var members = group.Select(p => p.Process with { ExitRequested = false }).ToList();
                session.Apps.Add(new RestoreApp { Path = group.Key, Label = Path.GetFileNameWithoutExtension(group.Key),
                    CanRestart = (members.Any(p => p.HasWindow) && !IsHelper(group.Key)) || options.RestartPaths.Any(path => GameReadyPolicy.SamePath(path, group.Key)), Processes = members });
            }
            store.SaveSession(session); // 必须先成功落盘，才能发送退出请求。
            progress?.Report($"正在正常退出 {session.Apps.Count} 组应用…");
            session.Stage = "退出应用";
            foreach (var app in session.Apps)
            {
                token.ThrowIfCancellationRequested();
                foreach (var p in app.Processes)
                {
                    token.ThrowIfCancellationRequested();
                    if (!platform.IsSame(p)) continue;
                    app.Attempted = true;
                    p.ExitRequested = true;
                    store.SaveSession(session);
                    p.ExitRequested = platform.TryClose(p);
                    store.SaveSession(session);
                }
            }
            if (session.Apps.Count > 0) await Task.Delay(TimeSpan.FromSeconds(options.CloseWaitSeconds), token);
            // 重新检查保护条件，但不会将新进程加入清理对象。
            var currentPlan = GameReadyPolicy.Plan(platform.Snapshot(), options, game);
            var eligible = currentPlan.Where(p => p.ShouldClose).Select(p => (p.Process.Id, p.Process.StartTicks)).ToHashSet();
            foreach (var app in session.Apps)
            {
                token.ThrowIfCancellationRequested();
                foreach (var p in app.Processes)
                {
                    token.ThrowIfCancellationRequested();
                    if (!eligible.Contains((p.Id, p.StartTicks)) || !platform.IsSame(p)) continue;
                    bool previousRequest = p.ExitRequested;
                    p.ExitRequested = true;
                    app.Attempted = true;
                    store.SaveSession(session);
                    if (!platform.TryKill(p)) p.ExitRequested = previousRequest;
                    store.SaveSession(session);
                }
                UpdateClosed(app);
                store.SaveSession(session);
                progress?.Report(app.Label + "：" + app.Result);
            }
            token.ThrowIfCancellationRequested();
            session.Stage = "启动游戏";
            store.SaveSession(session);
            progress?.Report("正在启动 " + game.Name + "…");
            var running = FindGame(game);
            if (running == null)
            {
                platform.Start(game.SteamAppId.Length > 0 ? "steam://rungameid/" + game.SteamAppId :
                    game.LaunchUri.Length > 0 ? game.LaunchUri : game.ExecutablePath,
                    game.SteamAppId.Length > 0 || game.LaunchUri.Length > 0 ? "" : game.Arguments, game.WorkingDirectory);
                var deadline = DateTime.UtcNow.AddSeconds(options.StartupTimeoutSeconds);
                while ((running = FindGame(game)) == null)
                {
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("等待游戏进程超时。应用恢复记录已保留。");
                    await Task.Delay(1000, token);
                }
            }
            progress?.Report("已检测到游戏进程。" );
            if (game.ExitSteam)
            {
                session.Stage = "等待退出 Steam";
                progress?.Report($"等待 {game.SteamDelaySeconds} 秒后尝试退出 Steam…");
                await Task.Delay(TimeSpan.FromSeconds(game.SteamDelaySeconds), token);
                if (platform.IsSame(running)) await CloseSteamAsync(session, options, token, progress);
                else progress?.Report("游戏已经退出，跳过 Steam 处理。");
            }
            else
            {
                foreach (var p in platform.Snapshot().Where(p => Path.GetFileName(p.Path).Equals("steam.exe", StringComparison.OrdinalIgnoreCase) && p.ProtectionReason.Length == 0))
                    platform.TryClose(p); // Steam 主窗口正常关闭通常只会收起窗口；不强制结束客户端。
            }
            session.Stage = platform.IsSame(running) ? "游戏已启动，可在结束后恢复应用" : "游戏进程已退出，可恢复应用";
            return session;
        }
        catch (OperationCanceledException) { if (session != null) session.Stage = "已取消，已退出的应用可恢复"; throw; }
        catch (Exception ex) { if (session != null) session.Stage = "未完成：" + ex.Message; throw; }
        finally
        {
            try
            {
                if (session != null)
                {
                    foreach (var app in session.Apps) UpdateClosed(app);
                    session.MemoryAfter = platform.AvailableMemory();
                    store.SaveSession(session);
                }
            }
            finally { Gate.Release(); }
        }
    }

    private ProcessEntry? FindGame(GameProfile game)
    {
        // Prefer the primary binary, then other discovered real game binaries.
        foreach (string path in new[] { game.ProcessPath }.Concat(game.ProcessCandidates).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = platform.FindGame(path);
            if (found != null) return found;
        }
        return null;
    }
    private static bool IsHelper(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return new[] { "helper", "renderer", "crash", "update", "unins", "setup" }.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));
    }
    private void UpdateClosed(RestoreApp app)
    {
        if (!app.Attempted) { app.Result = "尚未处理"; return; }
        app.Closed = app.Processes.Any(p => p.ExitRequested && !platform.IsSame(p));
        bool alive = app.Processes.Any(platform.IsSame);
        app.Result = alive ? "部分仍在运行 / 无法退出" : app.CanRestart ? "已退出，可恢复" : "已退出，需手动打开";
        if (!app.Closed) app.Result = "未退出";
    }

    private async Task CloseSteamAsync(GameReadySession session, GameReadyOptions options, CancellationToken token, IProgress<string>? progress)
    {
        var snapshot = platform.Snapshot();
        if (snapshot.Any(p => GameReadyPolicy.IsSteam(p) && options.KeepPaths.Any(path => GameReadyPolicy.SamePath(path, p.Path))))
        { progress?.Report("Steam 组件在保留名单中，跳过退出客户端。"); return; }
        var steam = snapshot.Where(p => Path.GetFileName(p.Path).Equals("steam.exe", StringComparison.OrdinalIgnoreCase) && p.ProtectionReason.Length == 0).ToList();
        foreach (var p in steam)
        {
            token.ThrowIfCancellationRequested();
            if (!platform.IsSame(p)) continue;
            if (options.KeepPaths.Any(path => GameReadyPolicy.SamePath(path, p.Path)))
            { progress?.Report("Steam 在保留名单中，跳过退出。"); continue; }
            var app = new RestoreApp { Path = p.Path, Label = "Steam", CanRestart = true, Attempted = true, Processes = new() { p } };
            p.ExitRequested = true;
            session.Apps.Add(app);
            store.SaveSession(session);
            // Steam 的正式退出命令；CloseMainWindow 仅收起窗口，不能代表退出。
            platform.Start(p.Path, "-shutdown", Path.GetDirectoryName(p.Path) ?? "");
            await Task.Delay(TimeSpan.FromSeconds(options.CloseWaitSeconds), token);
            UpdateClosed(app);
            progress?.Report(app.Closed ? "Steam 已退出。" : "Steam 仍在运行，已保留，不强制结束。");
        }
    }

    public async Task RestoreAsync(IProgress<string>? progress, CancellationToken token)
    {
        if (!await Gate.WaitAsync(0, token)) throw new InvalidOperationException("已有 Game Ready 操作正在运行。");
        try
        {
            var session = store.LoadSession();
            if (session == null) return;
            foreach (var app in session.Apps)
            {
                token.ThrowIfCancellationRequested();
                // 包含意外中断后未完成结果更新的会话。
                if (!app.Closed && app.Processes.Any(p => p.ExitRequested && !platform.IsSame(p))) app.Closed = true;
                if (app.Restored || !app.Closed) continue;
                try
                {
                    if (platform.FindGame(app.Path) != null)
                    { app.Restored = true; app.Result = "已在运行，跳过"; }
                    else if (!app.CanRestart) app.Result = "需手动打开";
                    else
                    {
                        platform.Start(app.Path, "", Path.GetDirectoryName(app.Path) ?? "");
                        app.Restored = true;
                        app.Result = "已请求重新打开";
                    }
                }
                catch (Exception ex) { app.Result = "恢复失败：" + ex.Message; }
                store.SaveSession(session);
                progress?.Report(app.Label + "：" + app.Result);
            }
            session.Stage = "恢复操作完成";
            store.SaveSession(session);
        }
        finally { Gate.Release(); }
    }
}
