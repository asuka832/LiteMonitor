using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage;

public sealed class GameReadyPage : SettingsPageBase
{
    public GameReadyPage()
    {
        BackColor = UIColors.MainBg;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        panel.Controls.Add(new Label { Text = "Game Ready · 游戏准备", AutoSize = true });
        panel.Controls.Add(new Label { Text = "配置保留名单，一次清理后台并启动游戏，结束后可重新打开应用。\n功能仅按需运行，不增加定时清理任务。配置在独立窗口中保存。", AutoSize = true, Margin = new Padding(0, 15, 0, 20) });
        var open = new LiteButton("打开 Game Ready…", true) { Width = 160 };
        open.Click += (_, _) => { if (MainForm != null) GameReadyForm.Open(MainForm); };
        panel.Controls.Add(open);
        Controls.Add(panel);
    }
}
