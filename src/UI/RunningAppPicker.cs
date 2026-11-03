using LiteMonitor.GameReady;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI;

public sealed class RunningAppPicker : Form
{
    private readonly IReadOnlyList<RunningAppChoice> choices;
    private readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox search = new() { Dock = DockStyle.Fill, PlaceholderText = "搜索应用名称、窗口标题或 EXE", AccessibleName = "搜索运行中应用" };
    private readonly CheckBox showBackground = new LiteCheck(false, "显示其他后台进程");
    private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true,
        FullRowSelect = true, HideSelection = false, ShowItemToolTips = true, BorderStyle = BorderStyle.FixedSingle };
    private readonly Label summary = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UIColors.TextSub, AutoEllipsis = true };
    private readonly Label empty = new() { TextAlign = ContentAlignment.MiddleCenter, BackColor = UIColors.CardBg,
        ForeColor = UIColors.TextSub, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
    private readonly ImageList images = new() { ImageSize = new Size(32, 32), ColorDepth = ColorDepth.Depth32Bit };
    private readonly List<Bitmap> ownedImages = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Font bodyFont = new("Microsoft YaHei UI", 9f);
    private readonly Font headingFont = new("Microsoft YaHei UI", 12f, FontStyle.Bold);
    private readonly Button add;
    private bool rebuilding;
    private bool iconsLoaded;
    private bool resourcesDisposed;

    public IReadOnlyList<string> SelectedPaths => selected.ToArray();

    public static Task<IReadOnlyList<RunningAppChoice>> ReadChoicesAsync(CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var snapshot = new WindowsGameReadyPlatform().Snapshot();
        var tray = TrayAppHints.Read();
        token.ThrowIfCancellationRequested();
        return RunningAppCatalog.Build(snapshot, tray.Paths, tray.Labels);
    }, token);

    public RunningAppPicker(IReadOnlyList<RunningAppChoice> choices, IEnumerable<string> existingPaths)
    {
        this.choices = choices;
        var available = choices.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in existingPaths.Select(GameReadyPolicy.NormalizePath))
            if (available.Contains(path)) selected.Add(path);
        Text = "选择需要保留的应用";
        Font = bodyFont;
        BackColor = UIColors.MainBg;
        ForeColor = UIColors.TextMain;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(920, 580);
        MinimumSize = new Size(780, 500);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0) };
        var header = new Panel { Dock = DockStyle.Top, Height = 112 };
        header.Controls.Add(new Label { Text = "选择需要保留的应用", Font = headingFont, AutoSize = true, Location = new Point(0, 18) });
        header.Controls.Add(new Label { Text = "窗口应用优先，其次是托盘匹配。同一程序的多个进程已合并。", ForeColor = UIColors.TextSub,
            AutoSize = true, Location = new Point(0, 49) });
        var filters = new Panel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(0, 4, 0, 7) };
        var searchLabel = new Label { Text = "搜索", Dock = DockStyle.Left, Width = 42, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UIColors.TextSub };
        var toggle = new Panel { Dock = DockStyle.Right, Width = 195, Padding = new Padding(16, 0, 0, 0) };
        showBackground.Dock = DockStyle.Fill;
        toggle.Controls.Add(showBackground);
        filters.Controls.Add(search);
        filters.Controls.Add(toggle);
        filters.Controls.Add(searchLabel);
        search.BringToFront();
        header.Controls.Add(filters);
        list.Columns.Add("应用", 215);
        list.Columns.Add("程序 / 进程数量", 190);
        list.Columns.Add("窗口标题 / 托盘说明", 300);
        list.Columns.Add("程序位置", 420);
        list.SmallImageList = images;
        list.AccessibleName = "可添加到保留名单的应用";
        list.Controls.Add(empty);
        list.SizeChanged += (_, _) => empty.Bounds = new Rectangle(1, 34, Math.Max(0, list.ClientSize.Width - 2), Math.Max(0, list.ClientSize.Height - 36));
        var note = new Label { Dock = DockStyle.Bottom, Height = 38, ForeColor = UIColors.TextSub,
            Text = "此处仅添加应用；移除请返回保留名单。托盘匹配基于系统可读信息，不代表实时图标状态。",
            Padding = new Padding(0, 10, 0, 0) };
        content.Controls.Add(list);
        content.Controls.Add(header);
        content.Controls.Add(note);
        list.BringToFront();
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(20, 15, 20, 15) };
        footer.Paint += (_, e) => { using var pen = new Pen(UIColors.Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        add = new LiteButton("添加选中项", true) { Width = 148, DialogResult = DialogResult.OK, Margin = new Padding(8, 0, 0, 0) };
        var cancel = new LiteButton("取消") { Width = 80, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(add);
        buttons.Controls.Add(cancel);
        footer.Controls.Add(summary);
        footer.Controls.Add(buttons);
        summary.BringToFront();
        Controls.Add(content);
        Controls.Add(footer);
        content.BringToFront();
        AcceptButton = add;
        CancelButton = cancel;
        using (var fallback = SystemIcons.Application.ToBitmap())
        {
            var bitmap = new Bitmap(fallback);
            ownedImages.Add(bitmap);
            images.Images.Add("default", bitmap);
        }
        search.TextChanged += (_, _) => Rebuild();
        showBackground.CheckedChanged += (_, _) => Rebuild();
        list.ItemChecked += (_, e) =>
        {
            if (rebuilding || e.Item.Tag is not RunningAppChoice app) return;
            if (e.Item.Checked) selected.Add(app.Path); else selected.Remove(app.Path);
            UpdateSummary();
        };
        Shown += async (_, _) => await LoadIconsAsync();
        Rebuild();
    }

    private void Rebuild()
    {
        string query = search.Text.Trim();
        rebuilding = true;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            list.Groups.Clear();
            var groups = new[] { new ListViewGroup("已打开窗口"), new ListViewGroup("托盘匹配"), new ListViewGroup("其他后台进程") };
            list.Groups.AddRange(groups);
            foreach (var app in choices)
            {
                bool matches = query.Length == 0 || (app.Name + "\n" + app.Detail + "\n" + app.Path).Contains(query, StringComparison.CurrentCultureIgnoreCase);
                if (!matches || (!showBackground.Checked && app.Priority == 2 && query.Length == 0)) continue;
                var item = new ListViewItem(app.Name, images.Images.ContainsKey(app.Path) ? app.Path : "default")
                { Tag = app, Group = groups[app.Priority], Checked = selected.Contains(app.Path), ToolTipText = app.Name + "\n" + app.Detail + "\n" + app.Path };
                item.SubItems.Add(Path.GetFileName(app.Path) + (app.ProcessCount > 1 ? $"（{app.ProcessCount} 个）" : ""));
                item.SubItems.Add(app.Detail);
                item.SubItems.Add(app.Path);
                list.Items.Add(item);
            }
            empty.Text = query.Length > 0 ? "没有匹配的应用\n\n可尝试应用名称、窗口标题或 EXE 文件名。" :
                "暂未识别到窗口或托盘应用\n\n可以搜索，或勾选“显示其他后台进程”。";
            empty.Visible = list.Items.Count == 0;
        }
        finally { list.EndUpdate(); rebuilding = false; }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        summary.Text = $"已选 {selected.Count} 项 · 显示 {list.Items.Count} / {choices.Count} 个应用" + (iconsLoaded ? "" : " · 正在读取图标…");
        add.Text = selected.Count > 0 ? $"添加选中项（{selected.Count}）" : "添加选中项";
        add.Enabled = selected.Count > 0;
        add.BackColor = add.Enabled ? UIColors.Primary : UIColors.NavSelected;
        add.ForeColor = add.Enabled ? Color.White : UIColors.TextSub;
    }

    private async Task LoadIconsAsync()
    {
        var token = lifetime.Token;
        Dictionary<string, Bitmap>? loaded = null;
        try
        {
            loaded = await Task.Run(() =>
            {
                var result = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var app in choices)
                    {
                        token.ThrowIfCancellationRequested();
                        // Network locations may stall shell icon extraction; retain the default icon there.
                        if (app.Path.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                        try
                        {
                            using var icon = Icon.ExtractAssociatedIcon(app.Path);
                            if (icon != null) result.Add(app.Path, icon.ToBitmap());
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException) { }
                    }
                    return result;
                }
                catch { foreach (var bitmap in result.Values) bitmap.Dispose(); throw; }
            }, token);
            if (IsDisposed || token.IsCancellationRequested) return;
            foreach (var pair in loaded)
            {
                images.Images.Add(pair.Key, pair.Value);
                ownedImages.Add(pair.Value);
            }
            loaded = null; // Ownership transferred to this dialog.
            foreach (ListViewItem item in list.Items)
                if (item.Tag is RunningAppChoice app && images.Images.ContainsKey(app.Path)) item.ImageKey = app.Path;
            iconsLoaded = true;
            UpdateSummary();
        }
        catch (OperationCanceledException) { }
        finally { if (loaded != null) foreach (var bitmap in loaded.Values) bitmap.Dispose(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            lifetime.Cancel();
            base.Dispose(true);
            images.Dispose();
            foreach (var bitmap in ownedImages) bitmap.Dispose();
            lifetime.Dispose();
            bodyFont.Dispose();
            headingFont.Dispose();
            return;
        }
        base.Dispose(disposing);
    }
}
