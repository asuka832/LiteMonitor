using LiteMonitor.GameReady;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI;

public sealed partial class GameReadyForm
{
    private readonly List<Panel> pageViews = new();
    private readonly List<LiteNavBtn> pageNavigation = new();
    private readonly FlowLayoutPanel navigation = new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Panel advanced = new();
    private readonly Label selectedGame = new() { Text = "请选择游戏", ForeColor = UIColors.TextMain };
    private readonly Label gameCount = new() { Text = "已安装游戏", ForeColor = UIColors.TextSub };
    private readonly Label emptyGames = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        Text = "未发现已安装游戏\n\n点击“重新扫描”\n或把图标 / EXE 拖到这里。",
        ForeColor = UIColors.TextSub, BackColor = UIColors.CardBg, Padding = new Padding(12) };
    private readonly Label emptyResults = new() { TextAlign = ContentAlignment.MiddleCenter, Text = "暂无清理与恢复记录\n\n可以先预览关闭范围，或启动一次 Game Ready。",
        ForeColor = UIColors.TextSub, BackColor = UIColors.CardBg, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = UIColors.TextSub };
    private readonly Panel activity = new() { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(20, 0, 20, 0) };
    private readonly Font headingFont = new("Microsoft YaHei UI", 12f, FontStyle.Bold);
    private readonly Font itemFont = new("Microsoft YaHei UI", 9f, FontStyle.Bold);

    private void BuildPresentation()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 160, BackColor = UIColors.SidebarBg, Padding = new Padding(0, 20, 0, 0) };
        sidebar.Controls.Add(navigation);
        var border = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = UIColors.Border };
        sidebar.Controls.Add(border);
        var content = new Panel { Dock = DockStyle.Fill };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(20, 15, 20, 15) };
        bottom.Paint += (_, e) => { using var pen = new Pen(UIColors.Border); e.Graphics.DrawLine(pen, 0, 0, bottom.Width, 0); };
        bottom.Controls.Add(actions);
        var summary = new Panel { Dock = DockStyle.Top, Height = 38 };
        var showLog = new LiteHeaderBtn("查看日志") { Dock = DockStyle.Right, Width = 76 };
        showLog.Click += (_, _) =>
        {
            bool expanded = !log.Visible;
            log.Visible = expanded;
            activity.Height = expanded ? 138 : 40;
            showLog.Text = expanded ? "收起日志" : "查看日志";
        };
        summary.Controls.Add(status);
        summary.Controls.Add(showLog);
        status.BringToFront();
        log.Visible = false;
        log.BorderStyle = BorderStyle.None;
        log.BackColor = UIColors.CardBg;
        log.ForeColor = UIColors.TextSub;
        activity.Controls.Add(log);
        activity.Controls.Add(summary);
        log.BringToFront();
        content.Controls.Add(tabs);
        content.Controls.Add(activity);
        content.Controls.Add(bottom);
        tabs.BringToFront();
        Controls.Add(content);
        Controls.Add(sidebar);
        content.BringToFront();
        foreach (var number in new[] { closeWait, startupWait, steamDelay })
        {
            number.BorderStyle = BorderStyle.None;
            number.BackColor = UIColors.CardBg;
            number.TextAlign = HorizontalAlignment.Right;
            number.AccessibleName = number == closeWait ? "正常退出等待秒数" : number == startupWait ? "游戏启动超时秒数" : "退出 Steam 延迟秒数";
        }
        foreach (var input in new[] { name, appId, executable, processPath, arguments, workingDirectory })
        {
            input.BorderStyle = BorderStyle.FixedSingle;
            input.BackColor = UIColors.CardBg;
            input.ForeColor = UIColors.TextSub;
        }
    }

    private Panel MakePage(string title, string description)
    {
        var page = new BufferedPanel { Dock = DockStyle.Fill, BackColor = UIColors.MainBg, Padding = new Padding(20, 0, 20, 0) };
        var header = new Panel { Dock = DockStyle.Top, Height = 82 };
        header.Controls.Add(new Label { Text = title, Font = headingFont, ForeColor = UIColors.TextMain, AutoSize = true, Location = new Point(0, 18) });
        header.Controls.Add(new Label { Text = description, ForeColor = UIColors.TextSub, AutoSize = true, Location = new Point(0, 50) });
        page.Controls.Add(header);
        return page;
    }

    private void AddPage(Panel page, string title)
    {
        int index = pageViews.Count;
        pageViews.Add(page);
        tabs.Controls.Add(page);
        var button = new LiteNavBtn(title) { AccessibleName = title };
        button.Click += (_, _) => SwitchPage(index);
        navigation.Controls.Add(button);
        pageNavigation.Add(button);
        page.Visible = false;
    }

    private void SwitchPage(int index)
    {
        if (index < 0 || index >= pageViews.Count) return;
        tabs.SuspendLayout();
        for (int i = 0; i < pageViews.Count; i++)
        {
            pageViews[i].Visible = i == index;
            pageNavigation[i].IsActive = i == index;
        }
        pageViews[index].BringToFront();
        tabs.ResumeLayout();
    }

    private static Panel MakeEditor(TextBox input, string help)
    {
        var frame = new Panel { Dock = DockStyle.Fill, BackColor = UIColors.Border, Padding = new Padding(1) };
        input.BorderStyle = BorderStyle.None;
        input.BackColor = UIColors.CardBg;
        input.ForeColor = UIColors.TextMain;
        var interior = new Panel { Dock = DockStyle.Fill, BackColor = UIColors.CardBg, Padding = new Padding(12) };
        var hint = new Label { Text = help, Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 12, 0, 0), ForeColor = UIColors.TextSub };
        interior.Controls.Add(input);
        interior.Controls.Add(hint);
        input.BringToFront();
        frame.Controls.Add(interior);
        return frame;
    }

    private void BuildRecoveryPages()
    {
        var results = MakePage("清理与恢复", "查看关闭范围与处理结果；游戏结束后，可以重新打开本次关闭的应用。");
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44 };
        AddAction("查看恢复记录", () => { ShowSession(); SwitchPage(2); }, toolbar, width: 116);
        var discard = AddAction("放弃本次恢复", () =>
        {
            if (MessageBox.Show(this, "放弃后不再自动恢复本次关闭的应用。记录会保留为备份。", "放弃恢复", MessageBoxButtons.OKCancel) == DialogResult.OK)
            {
                store.DiscardSession();
                preview.Items.Clear();
                emptyResults.Visible = true;
                WriteLog("已放弃恢复，原记录已备份。");
            }
        }, toolbar, width: 116);
        discard.ForeColor = UIColors.TextCrit;
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 14) };
        preview.BorderStyle = BorderStyle.FixedSingle;
        preview.BackColor = UIColors.CardBg;
        preview.ForeColor = UIColors.TextMain;
        preview.HideSelection = false;
        preview.Columns.Add("应用 / 进程", 160);
        preview.Columns.Add("处理规则", 220);
        preview.Columns.Add("程序路径", 400);
        preview.Controls.Add(emptyResults);
        preview.SizeChanged += (_, _) => emptyResults.Bounds = new Rectangle(1, 32, Math.Max(0, preview.ClientSize.Width - 2), Math.Max(0, preview.ClientSize.Height - 34));
        body.Controls.Add(preview);
        body.Controls.Add(toolbar);
        preview.BringToFront();
        results.Controls.Add(body);
        body.BringToFront();
        AddPage(results, "清理与恢复");

        var recovery = MakePage("后台应用恢复", "有窗口的普通应用会自动记录；无窗口应用可在这里补充主程序入口。");
        restartPaths.Lines = options.RestartPaths.ToArray();
        var editor = MakeEditor(restartPaths, "每行填写一个可直接启动的主程序 EXE。恢复会重新打开应用，不保证恢复文档、标签页和窗口状态。");
        editor.Margin = new Padding(0, 0, 0, 14);
        recovery.Controls.Add(editor);
        editor.BringToFront();
        AddPage(recovery, "后台应用恢复");
    }

    private void DrawGame(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= games.Items.Count) return;
        var game = (GameProfile)games.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var brush = new SolidBrush(selected ? UIColors.NavSelected : UIColors.CardBg);
        e.Graphics.FillRectangle(brush, e.Bounds);
        var title = new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 7, e.Bounds.Width - 24, 20);
        var platformLabel = new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 28, e.Bounds.Width - 24, 16);
        TextRenderer.DrawText(e.Graphics, game.Name, itemFont, title, UIColors.TextMain,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        string platformName = game.SteamAppId.Length > 0 ? "Steam" : game.LaunchUri.Length > 0 ? "Epic Games" : "本地游戏";
        TextRenderer.DrawText(e.Graphics, platformName, Font, platformLabel, UIColors.TextSub,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { headingFont.Dispose(); itemFont.Dispose(); }
    }
}
