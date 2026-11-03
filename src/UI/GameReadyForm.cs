using System.Diagnostics;
using LiteMonitor.GameReady;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI;

public sealed partial class GameReadyForm : Form
{
    private static GameReadyForm? current;
    private static Process? watchedGame;
    private static bool registeredExit;
    private readonly MainForm main;
    private readonly GameReadyStore store = new(AppContext.BaseDirectory);
    private readonly WindowsGameReadyPlatform platform = new();
    private readonly GameReadyOptions options;
    private readonly Panel tabs = new() { Dock = DockStyle.Fill };
    private readonly ListBox games = new() { Dock = DockStyle.Fill };
    private readonly TextBox name = new(), appId = new(), executable = new(), processPath = new(), arguments = new(), workingDirectory = new();
    private readonly NumericUpDown closeWait = new() { Minimum = 0, Maximum = 120, Value = 10 };
    private readonly NumericUpDown startupWait = new() { Minimum = 1, Maximum = 600, Value = 120 };
    private readonly NumericUpDown steamDelay = new() { Minimum = 0, Maximum = 600, Value = 30 };
    private readonly CheckBox exitSteam = new LiteCheck(false, "游戏启动后尝试退出 Steam");
    private readonly CheckBox keepNvidia = new LiteCheck(true, "保留 NVIDIA 截图覆盖层（Alt+F1）");
    private readonly CheckBox acknowledge = new LiteCheck(false, "我已了解：白名单外应用会被关闭；超时强制结束可能丢失未保存内容。");
    private readonly TextBox keepPaths = new() { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
    private readonly TextBox restartPaths = new() { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
    private readonly ListView preview = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true };
    private readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly FlowLayoutPanel actions = new() { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
    private readonly Button cancel;
    private readonly List<Button> idleButtons = new();
    private GameProfile? editing;
    private CancellationTokenSource? operation;
    private bool busy;
    private bool loading;
    private bool accepted;
    private bool scanOnOpen = true;
    private bool pickingApplications;

    public static void Open(MainForm main, bool restore = false)
    {
        try
        {
            if (current == null || current.IsDisposed) current = new GameReadyForm(main);
            current.scanOnOpen = !restore;
            current.Show();
            if (current.WindowState == FormWindowState.Minimized) current.WindowState = FormWindowState.Normal;
            current.Activate();
            if (restore && !current.busy) _ = current.RestoreAsync();
        }
        catch (Exception ex) { MessageBox.Show(main, ex.Message, "Game Ready", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private GameReadyForm(MainForm main)
    {
        this.main = main;
        options = store.LoadOptions();
        accepted = options.AcceptedClosePolicy;
        Text = "Game Ready · 游戏准备";
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = UIColors.MainBg;
        ForeColor = UIColors.TextMain;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(1000, 700);
        MinimumSize = new Size(940, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BuildPresentation();
        BuildGameTab();
        BuildKeepTab();
        BuildRecoveryPages();
        AddAction("清理并启动游戏", async () => await StartAsync(), primary: true, width: 148);
        AddAction("预览关闭范围", async () => await PreviewAsync(), width: 114);
        AddAction("恢复应用", async () => await RestoreAsync(), width: 92);
        AddAction("保存配置", SaveConfig, width: 92);
        cancel = new LiteButton("取消操作") { Width = 92, Enabled = false, Visible = false };
        cancel.Click += (_, _) => operation?.Cancel();
        actions.Controls.Add(cancel);
        FormClosing += (_, e) =>
        {
            if (busy) { e.Cancel = true; operation?.Cancel(); WriteLog("正在停止后续操作，请完成后再关闭窗口。"); }
        };
        FormClosed += (_, _) => { operation?.Dispose(); current = null; };
        if (games.Items.Count > 0) games.SelectedIndex = 0;
        SwitchPage(0);
        Shown += async (_, _) => { if (scanOnOpen) await ScanGamesAsync(); };
        ShowSession();
    }

    private Button AddAction(string text, Action action, Control? parent = null, bool primary = false, int width = 92)
    {
        var button = new LiteButton(text, primary) { Width = width, Margin = new Padding(8, 0, 0, 0), AccessibleName = text };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { ShowError(ex); } };
        (parent ?? actions).Controls.Add(button);
        idleButtons.Add(button);
        return button;
    }

    private void BuildGameTab()
    {
        var page = MakePage("游戏准备", "选择游戏，清理一次后台，然后开始游戏。未识别时把图标或 EXE 拖到左侧列表。");
        var body = new Panel { Dock = DockStyle.Fill };
        var left = new Panel { Dock = DockStyle.Left, Width = 225, Padding = new Padding(0, 0, 20, 0) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(0, 10, 0, 0) };
        var add = AddAction("手动添加", () => { SaveGame(); var game = new GameProfile(); options.Games.Add(game); RebindGames(game); advanced.Visible = true; }, buttons, width: 92);
        var remove = AddAction("移除", () => { if (editing != null) options.Games.Remove(editing); editing = null; RebindGames(options.Games.FirstOrDefault()); }, buttons, width: 66);
        add.Margin = new Padding(0, 0, 8, 0);
        remove.Margin = Padding.Empty;
        var listHeader = new Panel { Dock = DockStyle.Top, Height = 42 };
        gameCount.Dock = DockStyle.Left;
        gameCount.AutoSize = true;
        gameCount.Padding = new Padding(0, 8, 0, 0);
        listHeader.Controls.Add(gameCount);
        var refresh = AddAction("重新扫描", async () => await ScanGamesAsync(), listHeader, width: 82);
        refresh.Dock = DockStyle.Right;
        refresh.Height = 28;
        games.BorderStyle = BorderStyle.None;
        games.BackColor = UIColors.CardBg;
        games.IntegralHeight = false;
        games.DrawMode = DrawMode.OwnerDrawFixed;
        games.ItemHeight = 50;
        games.AccessibleName = "已安装游戏";
        games.DrawItem += DrawGame;
        var librarySurface = new Panel { Dock = DockStyle.Fill, BackColor = UIColors.CardBg };
        librarySurface.Controls.Add(games);
        librarySurface.Controls.Add(emptyGames);
        EnableGameDrop(games);
        EnableGameDrop(librarySurface);
        EnableGameDrop(emptyGames);
        games.BringToFront();
        left.Controls.Add(librarySurface);
        left.Controls.Add(buttons);
        left.Controls.Add(listHeader);
        librarySurface.BringToFront();
        games.Items.AddRange(options.Games.Cast<object>().ToArray());
        games.SelectedIndexChanged += (_, _) =>
        {
            if (loading) return;
            SaveGame();
            editing = games.SelectedItem as GameProfile;
            LoadGame();
        };
        editing = games.SelectedItem as GameProfile;
        var details = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 0, 0, 8) };
        selectedGame.Dock = DockStyle.Top;
        selectedGame.Height = 50;
        selectedGame.Font = headingFont;
        selectedGame.AutoEllipsis = true;
        var timing = new LiteSettingsGroup("清理设置");
        closeWait.Width = startupWait.Width = steamDelay.Width = 80;
        timing.AddItem(new LiteSettingsItem("退出等待（秒）", closeWait));
        timing.AddItem(new LiteSettingsItem("启动超时（秒）", startupWait));
        var steam = new LiteSettingsGroup("Steam");
        steam.AddFullItem(new LiteSettingsItem("启动之后", exitSteam));
        steam.AddFullItem(new LiteSettingsItem("退出延迟（秒）", steamDelay));
        var steamHint = new LiteHintRow("退出 Steam 需逐款实测；拒绝退出时保留后台，不强制结束。");
        foreach (Control child in steamHint.Controls) child.ForeColor = UIColors.TextSub;
        steam.AddFullItem(steamHint);
        var advancedButton = new LiteButton("程序路径与高级配置", dashed: true) { Dock = DockStyle.Top, Height = 34, Margin = new Padding(0, 8, 0, 0) };
        advancedButton.Click += (_, _) =>
        {
            advanced.Visible = !advanced.Visible;
            advancedButton.Text = advanced.Visible ? "收起高级配置" : "程序路径与高级配置";
            if (advanced.Visible) details.ScrollControlIntoView(advanced);
        };
        advanced.Dock = DockStyle.Top;
        advanced.AutoSize = true;
        advanced.Visible = false;
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(12), BackColor = UIColors.CardBg };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(fields, "游戏名称", name);
        AddField(fields, "Steam App ID（可选）", appId);
        AddField(fields, "本地启动 EXE", WithBrowse(executable));
        AddField(fields, "实际游戏进程 EXE", WithBrowse(processPath));
        AddField(fields, "本地启动参数", arguments);
        AddField(fields, "工作目录（可选）", workingDirectory);
        advanced.Controls.Add(fields);
        details.Controls.Add(advanced);
        details.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12 });
        details.Controls.Add(advancedButton);
        details.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 15 });
        details.Controls.Add(steam);
        details.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 15 });
        details.Controls.Add(timing);
        details.Controls.Add(selectedGame);
        var policy = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(0, 12, 0, 0) };
        acknowledge.Checked = accepted;
        acknowledge.AutoSize = false;
        acknowledge.Dock = DockStyle.Fill;
        acknowledge.AccessibleName = "确认应用退出策略";
        policy.Controls.Add(acknowledge);
        body.Controls.Add(details);
        body.Controls.Add(left);
        details.BringToFront();
        page.Controls.Add(body);
        page.Controls.Add(policy);
        body.BringToFront();
        AddPage(page, "游戏准备");
        closeWait.Value = Math.Clamp(options.CloseWaitSeconds, 0, 120);
        startupWait.Value = Math.Clamp(options.StartupTimeoutSeconds, 1, 600);
        LoadGame();
    }

    private static void AddField(TableLayoutPanel table, string title, Control control)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(0, 8, 8, 10) }, 0, row);
        control.Dock = DockStyle.Top;
        control.Margin = new Padding(0, 5, 0, 5);
        table.Controls.Add(control, 1, row);
    }

    private Control WithBrowse(TextBox input)
    {
        var panel = new Panel { Height = 28 };
        var browse = new LiteButton("选择…") { Dock = DockStyle.Right, Width = 65, Height = 28 };
        input.Dock = DockStyle.Fill;
        panel.Controls.Add(input);
        panel.Controls.Add(browse);
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "应用程序|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(panel.FindForm()) == DialogResult.OK) input.Text = dialog.FileName;
        };
        void Drop(string[] dropped)
        {
            if (busy) return;
            foreach (string file in dropped)
            {
                try
                {
                    string? exe = GameShortcutImport.ExecutableFrom(file);
                    if (exe == null) continue;
                    input.Text = exe;
                    break;
                }
                catch (Exception ex) { WriteLog("无法读取拖入路径：" + ex.Message); }
            }
        }
        ShellFileDrop.Attach(panel, Drop);
        ShellFileDrop.Attach(input, Drop);
        ShellFileDrop.Attach(browse, Drop);
        return panel;
    }

    private void EnableGameDrop(Control control)
    {
        ShellFileDrop.Attach(control, async files =>
            await PerformAsync(token => AddDroppedFilesAsync(files, token)));
    }

    private async Task AddDroppedFilesAsync(string[] files, CancellationToken token)
    {
        SaveGame();
        GameProfile? last = null;
        int added = 0;
        GameDiscoveryResult? scanned = null;
        foreach (string file in files.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!GameShortcutImport.IsSupported(file))
            {
                WriteLog("\u8df3过不支持的文件：" + Path.GetFileName(file));
                continue;
            }
            GameProfile? incoming;
            try { incoming = GameShortcutImport.Read(file); }
            catch (Exception ex) { WriteLog(Path.GetFileName(file) + "\uff1a" + ex.Message); continue; }
            if (incoming == null)
            {
                WriteLog("\u6ca1有识别到程序：" + Path.GetFileName(file));
                continue;
            }
            GameShortcutImport.CompleteFromLibrary(incoming, options.Games);
            if (incoming.SteamAppId.Length > 0 && incoming.ProcessPath.Length == 0)
            {
                scanned ??= await Task.Run(() => GameLibraryDiscovery.ScanInstalled(token), token);
                token.ThrowIfCancellationRequested();
                GameShortcutImport.CompleteFromLibrary(incoming, scanned.Games);
            }
            bool known = options.Games.Any(g => GameShortcutImport.SameGame(g, incoming));
            last = GameShortcutImport.Apply(options, editing, incoming);
            if (!known && ReferenceEquals(last, incoming)) added++;
        }
        if (last == null) { WriteLog("请拖入游戏 EXE、.lnk 快捷方式或 Steam 图标（.url）。"); return; }
        RebindGames(last);
        if (last.ProcessPath.Length == 0)
        {
            advanced.Visible = true;
            (advanced.Parent as ScrollableControl)?.ScrollControlIntoView(advanced);
            WriteLog($"\u5df2添加《{last.Name}\u300b。还没有主程序路径，请把游戏 EXE 拖到路径框，或点“选择”。");
        }
        else if (added > 0)
            WriteLog($"\u5df2从拖放添加 {added} 个游戏，记得保存配置。");
        else
            WriteLog($"\u5df2选择《{last.Name}\u300b。");
    }

    private void BuildKeepTab()
    {
        var page = MakePage("保留名单", "这里的应用在游戏期间继续运行。系统和所选游戏会自动受到保护。");
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 90, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        keepNvidia.Checked = options.KeepNvidiaOverlay;
        head.Controls.Add(keepNvidia);
        var buttons = new FlowLayoutPanel { AutoSize = true };
        var file = new LiteButton("选择程序…") { Width = 100 };
        var running = new LiteButton("从运行中应用添加…") { Width = 160 };
        file.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "应用程序|*.exe", Multiselect = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) AppendKeep(dialog.FileNames);
        };
        running.Click += async (_, _) => await PickRunningAsync();
        buttons.Controls.AddRange(new Control[] { file, running });
        head.Controls.Add(buttons);
        keepPaths.Lines = options.KeepPaths.ToArray();
        var content = new Panel { Dock = DockStyle.Fill };
        var editor = MakeEditor(keepPaths, "每行一个需要保留的程序完整路径。可通过上方按钮添加。");
        content.Controls.Add(editor);
        content.Controls.Add(head);
        editor.BringToFront();
        page.Controls.Add(content);
        content.BringToFront();
        AddPage(page, "保留名单");
    }

    private async Task PickRunningAsync()
    {
        if (pickingApplications) return;
        pickingApplications = true;
        try
        {
            var choices = await RunningAppPicker.ReadChoicesAsync();
            if (IsDisposed) return;
            using var picker = new RunningAppPicker(choices, keepPaths.Lines);
            if (picker.ShowDialog(this) == DialogResult.OK) AppendKeep(picker.SelectedPaths);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { pickingApplications = false; }
    }

    private void AppendKeep(IEnumerable<string> paths) => keepPaths.Lines = keepPaths.Lines.Concat(paths).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private void RebindGames(GameProfile? selected)
    {
        loading = true;
        games.Items.Clear();
        games.Items.AddRange(options.Games.Cast<object>().ToArray());
        games.SelectedItem = selected;
        editing = selected;
        loading = false;
        gameCount.Text = $"已安装游戏 · {games.Items.Count}";
        LoadGame();
    }
    private void LoadGame()
    {
        selectedGame.Text = editing?.Name ?? "尚未选择游戏";
        gameCount.Text = $"已安装游戏 · {games.Items.Count}";
        emptyGames.Visible = games.Items.Count == 0;
        games.Visible = games.Items.Count > 0;
        name.Text = editing?.Name ?? "";
        appId.Text = editing?.SteamAppId ?? "";
        executable.Text = editing?.ExecutablePath ?? "";
        processPath.Text = editing?.ProcessPath ?? "";
        arguments.Text = editing?.Arguments ?? "";
        workingDirectory.Text = editing?.WorkingDirectory ?? "";
        exitSteam.Checked = editing?.ExitSteam ?? false;
        steamDelay.Value = Math.Clamp(editing?.SteamDelaySeconds ?? 30, 0, 600);
    }
    private void SaveGame()
    {
        if (editing == null) return;
        editing.Name = name.Text.Trim();
        editing.SteamAppId = appId.Text.Trim();
        editing.ExecutablePath = WindowsGameReadyPlatform.ResolveExecutablePath(executable.Text.Trim());
        string process = WindowsGameReadyPlatform.ResolveExecutablePath(processPath.Text.Trim());
        if (!GameReadyPolicy.SamePath(process, editing.ProcessPath) && process.Length > 0)
        {
            editing.ProcessPathOverride = true;
            editing.ProcessCandidates = new() { process };
        }
        editing.ProcessPath = process;
        editing.Arguments = arguments.Text;
        editing.WorkingDirectory = workingDirectory.Text.Trim();
        editing.ExitSteam = exitSteam.Checked;
        editing.SteamDelaySeconds = (int)steamDelay.Value;
    }
    private void SaveConfig()
    {
        SaveGame();
        selectedGame.Text = editing?.Name ?? "尚未选择游戏";
        games.Invalidate();
        options.KeepPaths = keepPaths.Lines.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (options.KeepPaths.Any(p => !Path.IsPathFullyQualified(p) || !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("保留名单必须使用 EXE 完整路径，每行一个。");
        options.KeepPaths = options.KeepPaths.Select(WindowsGameReadyPlatform.ResolveExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        options.RestartPaths = restartPaths.Lines.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (options.RestartPaths.Any(p => !Path.IsPathFullyQualified(p) || !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("恢复入口必须使用主程序 EXE 完整路径，每行一个。");
        options.RestartPaths = options.RestartPaths.Select(WindowsGameReadyPlatform.ResolveExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        options.KeepNvidiaOverlay = keepNvidia.Checked;
        options.CloseWaitSeconds = (int)closeWait.Value;
        options.StartupTimeoutSeconds = (int)startupWait.Value;
        options.AcceptedClosePolicy = acknowledge.Checked;
        store.SaveOptions(options);
        accepted = options.AcceptedClosePolicy;
        WriteLog("配置已保存。只在点击开始时清理一次。");
    }

    private async Task ScanGamesAsync() => await PerformAsync(async token =>
    {
        SaveGame();
        var selected = editing;
        WriteLog("正在扫描 Steam / Epic 游戏库…");
        var result = await Task.Run(() => GameLibraryDiscovery.ScanInstalled(token), token);
        token.ThrowIfCancellationRequested();
        GameLibraryDiscovery.Merge(options, result.Games);
        RebindGames(selected ?? options.Games.FirstOrDefault());
        // Preserve unsaved whitelist edits: only persist once all controls have been read.
        SaveConfig();
        WriteLog($"扫描完成，识别到 {result.Games.Count} 款已安装游戏。");
        if (result.Games.Count == 0) WriteLog("未找到可识别的已安装 Windows 游戏。扫描范围为 Steam / Epic 登记的游戏库。");
        foreach (string warning in result.Warnings.Take(8)) WriteLog("跳过：" + warning);
    });

    private async Task PreviewAsync()
    {
        await PerformAsync(async token =>
        {
            SaveConfig();
            if (editing == null) throw new InvalidOperationException("请先添加并选择游戏。");
            var plan = await Task.Run(() => GameReadyPolicy.Plan(platform.Snapshot(), options, editing), token);
            preview.Items.Clear();
            foreach (var item in plan.OrderBy(p => !p.ShouldClose).ThenBy(p => p.Process.Path))
            {
                var row = new ListViewItem(Path.GetFileName(item.Process.Path) + " [" + item.Process.Id + "]");
                row.SubItems.Add(item.ShouldClose ? "将关闭" : item.Reason);
                row.SubItems.Add(item.Process.Path);
                if (item.ShouldClose) row.ForeColor = UIColors.TextCrit;
                preview.Items.Add(row);
            }
            SwitchPage(2);
            emptyResults.Visible = preview.Items.Count == 0;
            WriteLog($"预览：{plan.Count(p => p.ShouldClose)} 个进程将尝试退出。此操作未关闭任何应用。");
        });
    }

    private async Task StartAsync()
    {
        await PerformAsync(async token =>
        {
            SaveConfig();
            if (editing == null) throw new InvalidOperationException("请先选择游戏。");
            if (!accepted) throw new InvalidOperationException("请先在游戏配置页确认退出策略，并配置需要保留的应用。");
            GameReadyService.Validate(options, editing);
            var selected = editing;
            var service = new GameReadyService(platform, store);
            var progress = new Progress<string>(WriteLog);
            var session = await Task.Run(() => service.RunAsync(options, selected, progress, token), token);
            WriteLog($"{session.Stage}。可用内存：{session.MemoryBefore / 1073741824d:F2} → {session.MemoryAfter / 1073741824d:F2} GB。");
            WatchGame(selected);
            ShowSession();
        });
    }

    private async Task RestoreAsync() => await PerformAsync(async token =>
    {
        var progress = new Progress<string>(WriteLog);
        await Task.Run(() => new GameReadyService(platform, store).RestoreAsync(progress, token), token);
        ShowSession();
    });

    private async Task PerformAsync(Func<CancellationToken, Task> action)
    {
        if (busy) return;
        busy = true;
        operation?.Dispose();
        operation = new CancellationTokenSource();
        tabs.Enabled = false;
        navigation.Enabled = false;
        foreach (var button in idleButtons) button.Enabled = false;
        cancel.Enabled = true;
        cancel.Visible = true;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { WriteLog("已取消后续操作，已关闭的应用可以恢复。"); ShowSession(); }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            busy = false;
            tabs.Enabled = true;
            navigation.Enabled = true;
            foreach (var button in idleButtons) button.Enabled = true;
            cancel.Enabled = false;
            cancel.Visible = false;
        }
    }

    private void ShowSession()
    {
        var session = store.LoadSession();
        preview.Items.Clear();
        emptyResults.Visible = session == null || session.Apps.Count == 0;
        if (session == null) { WriteLog("没有待恢复会话。首次使用请先配置保留名单，并预览关闭范围。"); return; }
        foreach (var app in session.Apps)
        {
            var row = new ListViewItem(app.Label);
            row.SubItems.Add(app.Result);
            row.SubItems.Add(app.Path);
            preview.Items.Add(row);
        }
        WriteLog($"上次会话：{session.GameName} · {session.Stage}。恢复仅重新打开应用，不能保证恢复文档或窗口。");
    }

    private void WatchGame(GameProfile game)
    {
        watchedGame?.Dispose();
        watchedGame = null;
        if (!registeredExit)
        {
            registeredExit = true;
            Application.ApplicationExit += (_, _) => { watchedGame?.Dispose(); watchedGame = null; };
        }
        try
        {
            var entry = new[] { game.ProcessPath }.Concat(game.ProcessCandidates)
                .Select(platform.FindGame).FirstOrDefault(p => p != null);
            if (entry == null) return;
            var process = Process.GetProcessById(entry.Id);
            if (process.StartTime.ToUniversalTime().Ticks != entry.StartTicks) { process.Dispose(); return; }
            watchedGame = process;
            string gameName = game.Name;
            var owner = main; // The event must not retain this window and its controls after it is closed.
            process.Exited += (_, _) =>
            {
                process.Dispose();
                if (ReferenceEquals(watchedGame, process)) watchedGame = null;
                if (owner.IsDisposed || !owner.IsHandleCreated) return;
                try
                {
                    owner.BeginInvoke(new Action(() =>
                    {
                        owner.ShowNotification("Game Ready", gameName + " 已退出，可通过菜单中的“恢复应用”重新打开后台应用。", ToolTipIcon.Info);
                        if (current is { IsDisposed: false }) current.WriteLog("游戏已退出，可以恢复应用。");
                    }));
                }
                catch (InvalidOperationException) { }
            };
            process.EnableRaisingEvents = true;
        }
        catch (Exception ex) { WriteLog("无法订阅游戏退出通知：" + ex.Message + "。仍可手动恢复。"); }
    }

    private void WriteLog(string text)
    {
        if (IsDisposed) return;
        if (log.TextLength > 24000) log.Clear();
        log.AppendText(text + Environment.NewLine);
        status.Text = text;
    }
    private void ShowError(Exception ex)
    {
        WriteLog(ex.Message);
        MessageBox.Show(this, ex.Message, "Game Ready", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
