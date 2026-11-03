using LiteMonitor.GameReady;

internal static class RunningAppTests
{
    public static void Run(Action<bool, string> check)
    {
        var processes = new[]
        {
            new ProcessEntry { Id = 1, Path = @"C:\Apps\browser.exe", HasWindow = false },
            new ProcessEntry { Id = 2, Path = @"C:\Apps\browser.exe", HasWindow = true, Title = "我的网页" },
            new ProcessEntry { Id = 3, Path = @"C:\Apps\steam.exe" },
            new ProcessEntry { Id = 4, Path = @"C:\Apps\helper.exe" },
            new ProcessEntry { Id = 5, Path = @"C:\Windows\system.exe", ProtectionReason = "系统服务" }
        };
        var choices = RunningAppCatalog.Build(processes, new Dictionary<string, string> { [@"c:/Apps/steam.exe"] = "Steam" },
            Array.Empty<string>(), p => p.EndsWith("browser.exe") ? "浏览器" : Path.GetFileNameWithoutExtension(p));
        check(choices.Count == 3, "运行中应用按 EXE 路径合并并排除已自动保护的系统组件");
        check(choices[0].HasWindow && choices[0].Name == "浏览器" && choices[0].ProcessCount == 2, "窗口应用优先且保留友好名称和进程数量");
        check(choices[0].Detail == "我的网页", "显示窗口标题帮助识别应用");
        check(choices[1].HasTrayMatch && choices[1].Path.EndsWith("steam.exe"), "托盘路径匹配排在普通后台之前");
        check(choices[2].Priority == 2, "普通后台应用放在最后");
        choices = RunningAppCatalog.Build(processes, new Dictionary<string, string>(), new[] { "Steam\n正在运行" },
            p => Path.GetFileNameWithoutExtension(p));
        check(choices.Single(p => p.Path.EndsWith("steam.exe")).HasTrayMatch, "系统可读的托盘图标名称可辅助匹配");
        choices = RunningAppCatalog.Build(new[] { new ProcessEntry { Path = @"C:\Apps\app.exe" } },
            new Dictionary<string, string>(), new[] { "Some other application" }, _ => "app");
        check(!choices[0].HasTrayMatch, "短名称模糊命中不误标为托盘应用");
    }
}
