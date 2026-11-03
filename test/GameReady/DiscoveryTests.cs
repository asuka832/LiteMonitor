using LiteMonitor.GameReady;
using System.Text.Json;

internal static class DiscoveryTests
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "LiteMonitor-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string steam = Path.Combine(root, "Steam");
            string library = Path.Combine(root, "ExtraLibrary");
            string gameDir = Path.Combine(library, "steamapps", "common", "TestGame");
            Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
            Directory.CreateDirectory(Path.Combine(gameDir, "Project", "Binaries", "Win64"));
            Directory.CreateDirectory(Path.Combine(gameDir, "Engine", "Binaries"));
            string main = Path.Combine(gameDir, "Project", "Binaries", "Win64", "Project-Win64-Shipping.exe");
            File.WriteAllText(main, "fixture");
            File.WriteAllText(Path.Combine(gameDir, "Engine", "Binaries", "CrashReportClient.exe"), "fixture");
            File.WriteAllText(Path.Combine(gameDir, "Launcher.exe"), "fixture");
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"1\" { \"path\" " + JsonSerializer.Serialize(library) + " \"apps\" { \"123\" \"1\" } } }");
            File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_123.acf"), "\"AppState\" { \"appid\" \"123\" \"name\" \"Test Game\" \"installdir\" \"TestGame\" }");
            File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_999.acf"), "\"AppState\" { \"appid\" \"999\" \"name\" \"Escaping\" \"installdir\" \"../../..\" }");
            var result = GameLibraryDiscovery.Scan(new[] { steam, steam }, Array.Empty<string>(), CancellationToken.None);
            check(result.Games.Count == 1 && result.Games[0].SteamAppId == "123", "自动扫描多 Steam 库并去重，拒绝目录越界");
            check(GameReadyPolicy.SamePath(result.Games[0].ProcessPath, main), "优先识别 Unreal Shipping 主程序，排除启动器与崩溃上报器");
            var protectedProcess = new ProcessEntry { Id = 101, StartTicks = 1, Path = Path.Combine(gameDir, "Launcher.exe") };
            check(!GameReadyPolicy.Plan(new[] { protectedProcess }, new(), result.Games[0])[0].ShouldClose, "扫描游戏的安装目录内启动组件受到保护");
            var sibling = protectedProcess with { Id = 102, Path = gameDir + "-Other\\App.exe" };
            check(GameReadyPolicy.Plan(new[] { sibling }, new(), result.Games[0])[0].ShouldClose, "安装目录保护不匹配同名前缀文件夹");

            string epicDir = Path.Combine(root, "EpicManifests");
            string epicGame = Path.Combine(root, "EpicGame");
            Directory.CreateDirectory(epicDir);
            Directory.CreateDirectory(epicGame);
            File.WriteAllText(Path.Combine(epicGame, "MyGame.exe"), "fixture");
            File.WriteAllText(Path.Combine(epicDir, "test.item"), JsonSerializer.Serialize(new
            {
                DisplayName = "Epic Test", AppName = "AppName", CatalogNamespace = "namespace", CatalogItemId = "item",
                InstallLocation = epicGame, LaunchExecutable = "MyGame.exe", LaunchCommand = "-fromEpic"
            }));
            File.WriteAllText(Path.Combine(epicDir, "broken.item"), "broken");
            File.WriteAllText(Path.Combine(epicDir, "null.item"), "null");
            result = GameLibraryDiscovery.Scan(Array.Empty<string>(), new[] { epicDir }, CancellationToken.None);
            check(result.Games.Count == 1 && result.Games[0].LaunchUri.StartsWith("com.epicgames.launcher://apps/"), "Epic 清单生成官方启动器入口");
            check(result.Warnings.Count > 0, "损坏清单不影响其他游戏并报告异常");
            var existing = new GameProfile { Name = "自定义名称", SteamAppId = "123", ExitSteam = true, SteamDelaySeconds = 45 };
            var options = new GameReadyOptions { Games = new() { existing } };
            var scanned = GameLibraryDiscovery.Scan(new[] { steam }, Array.Empty<string>(), CancellationToken.None);
            GameLibraryDiscovery.Merge(options, scanned.Games);
            GameLibraryDiscovery.Merge(options, scanned.Games);
            check(options.Games.Count == 1 && existing.ExitSteam && existing.SteamDelaySeconds == 45 && existing.Name == "自定义名称", "刷新扫描结果不重复添加或覆盖自定义策略");
            check(GameReadyPolicy.SamePath(existing.ProcessPath, main), "自动补全旧配置中缺失的游戏路径");
            check(GameReadyService.IsEpicLaunchUri(result.Games[0].LaunchUri) && !GameReadyService.IsEpicLaunchUri("https://example.com"),
                "Epic 启动入口校验只接受限定启动协议");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            bool cancelled = false;
            try { GameLibraryDiscovery.Scan(new[] { steam }, Array.Empty<string>(), cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "游戏扫描支持取消");
            string pbz = Path.Combine(library, "steamapps", "common", "PBZ");
            Directory.CreateDirectory(Path.Combine(pbz, "platform", "steam"));
            Directory.CreateDirectory(Path.Combine(pbz, "ProjectPBZ", "Binaries", "Win64"));
            string pbzMain = Path.Combine(pbz, "platform", "steam", "PBZ.exe");
            using (var binary = File.Create(pbzMain)) binary.SetLength(65L * 1024 * 1024);
            File.WriteAllText(Path.Combine(pbz, "ProjectPBZ", "Binaries", "Win64", "8za_v2.exe"), "fixture");
            File.WriteAllText(Path.Combine(pbz, "ProjectPBZ", "Binaries", "Win64", "CheckEnv.exe"), "fixture");
            File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_4115450.acf"), "\"AppState\" { \"appid\" \"4115450\" \"name\" \"Phantom Blade Zero\" \"installdir\" \"PBZ\" }");
            result = GameLibraryDiscovery.Scan(new[] { steam }, Array.Empty<string>(), CancellationToken.None);
            check(GameReadyPolicy.SamePath(result.Games.Single(g => g.SteamAppId == "4115450").ProcessPath, pbzMain),
                "\u5f71\u4e4b\u5203\u96f6\u5e03\u5c40\u8bc6\u522b\u6e38\u620f\u4e3b\u4f53\uff0c\u4e0d\u9009\u62e9\u89e3\u538b\u5de5\u5177\u6216\u73af\u5883\u68c0\u6d4b\u7a0b\u5e8f");
            Directory.CreateDirectory(Path.Combine(steam, "appcache"));
            File.WriteAllBytes(Path.Combine(steam, "appcache", "appinfo.vdf"), SampleAppInfo(4115450, "\u5f71\u4e4b\u5203\u96f6", "\u5f71\u4e4b\u5203\u96f6"));
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("zh-CN");
                result = GameLibraryDiscovery.Scan(new[] { steam }, Array.Empty<string>(), CancellationToken.None);
                var localized = result.Games.Single(g => g.SteamAppId == "4115450");
                check(localized.Name == "\u5f71\u4e4b\u5203\u96f6" && localized.CatalogName == "Phantom Blade Zero", "\u4e2d\u6587\u754c\u9762\u4f7f\u7528 Steam \u7b80\u4f53\u540d\u79f0");
                var saved = new GameProfile { Name = "Phantom Blade Zero", SteamAppId = "4115450", ExitSteam = true, SteamDelaySeconds = 12 };
                var renamed = new GameReadyOptions { Games = new() { saved } };
                GameLibraryDiscovery.Merge(renamed, result.Games);
                check(saved.Name == "\u5f71\u4e4b\u5203\u96f6" && saved.ExitSteam && saved.SteamDelaySeconds == 12, "\u81ea\u52a8\u82f1\u6587\u540d\u66f4\u65b0\u4e3a\u4e2d\u6587\u540d\u5e76\u4fdd\u7559\u7b56\u7565");
                saved.Name = "\u81ea\u5b9a\u4e49\u540d\u79f0";
                GameLibraryDiscovery.Merge(renamed, result.Games);
                check(saved.Name == "\u81ea\u5b9a\u4e49\u540d\u79f0", "\u624b\u52a8\u540d\u79f0\u4e0d\u88ab\u4e2d\u6587\u540d\u8986\u76d6");
                System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("en-US");
                result = GameLibraryDiscovery.Scan(new[] { steam }, Array.Empty<string>(), CancellationToken.None);
                check(result.Games.Single(g => g.SteamAppId == "4115450").Name == "Phantom Blade Zero", "\u975e\u4e2d\u6587\u754c\u9762\u4fdd\u7559\u6e05\u5355\u540d\u79f0");
            }
            finally { System.Globalization.CultureInfo.CurrentUICulture = culture; }

            string dropped = Path.Combine(root, "\u5f71\u4e4b\u5203.url");
            File.WriteAllText(dropped, "[InternetShortcut]\nURL=steam://rungameid/4115450\n");
            var urlGame = GameShortcutImport.Read(dropped)!;
            check(urlGame.SteamAppId == "4115450" && urlGame.Name == "\u5f71\u4e4b\u5203" && urlGame.ProcessPath.Length == 0, "\u62d6\u5165 Steam \u56fe\u6807\u5feb\u6377\u65b9\u5f0f\u8bc6\u522b\u6e38\u620f");
            string exe = Path.Combine(root, "PBZ.exe");
            File.WriteAllText(exe, "fixture");
            var exeGame = GameShortcutImport.Read(exe)!;
            check(GameReadyPolicy.SamePath(exeGame.ProcessPath, exe), "\u62d6\u5165 EXE \u586b\u5199\u4e3b\u7a0b\u5e8f\u8def\u5f84");
            var manual = new GameReadyOptions { Games = new() { new GameProfile { Name = "Phantom Blade Zero", SteamAppId = "4115450" } } };
            var chosen = GameShortcutImport.Apply(manual, null, urlGame);
            check(manual.Games.Count == 1 && chosen.Name == "\u5f71\u4e4b\u5203", "\u62d6\u5165\u56fe\u6807\u5339\u914d\u5df2\u6709 Steam \u6e38\u620f\u5e76\u6539\u7528\u4e2d\u6587\u540d");
            chosen = GameShortcutImport.Apply(manual, chosen, exeGame);
            check(manual.Games.Count == 1 && GameReadyPolicy.SamePath(chosen.ProcessPath, exe), "\u7f3a\u5c11\u8def\u5f84\u7684\u6e38\u620f\u63a5\u53d7\u62d6\u5165\u7684 EXE");
            var imported = GameShortcutImport.Read(dropped)!;
            GameShortcutImport.CompleteFromLibrary(imported, result.Games);
            check(GameReadyPolicy.SamePath(imported.ProcessPath, pbzMain) && imported.Name == "影之刃",
                "Steam 图标从本地清单补齐路径并保留图标名称");
            imported.ProcessPath = exe;
            imported.ProcessPathOverride = true;
            var overrides = new GameReadyOptions { Games = new() { imported } };
            GameLibraryDiscovery.Merge(overrides, result.Games);
            check(GameReadyPolicy.SamePath(imported.ProcessPath, exe), "重新扫描保留手动指定的游戏进程路径");
            check(GameShortcutImport.TryReadSteamAppId("steam.exe -applaunch 99", out string applaunch) && applaunch == "99", "\u8bc6\u522b Steam applaunch \u53c2\u6570");
            string link = Path.Combine(root, "Local.lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            object shell = Activator.CreateInstance(shellType)!;
            object shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { link })!;
            shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exe });
            shortcut.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            var linked = GameShortcutImport.Read(link)!;
            check(GameReadyPolicy.SamePath(linked.ProcessPath, exe) && linked.Name == "Local", "读取本地快捷方式的名称和目标路径");
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            string invalidLink = Path.Combine(root, "NotAGame.lnk");
            object invalidShell = Activator.CreateInstance(shellType)!;
            object invalidShortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, invalidShell, new object[] { invalidLink })!;
            invalidShortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, invalidShortcut, new object[] { dropped });
            invalidShortcut.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, invalidShortcut, null);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(invalidShortcut);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(invalidShell);
            check(GameShortcutImport.Read(invalidLink) == null, "非程序快捷方式不添加空游戏配置");
        }
        finally { Directory.Delete(root, true); }
    }

    private static byte[] SampleAppInfo(uint appId, string simplified, string traditional)
    {
        string[] keys = { "common", "name_localized", "schinese", "tchinese" };
        using var vdf = new MemoryStream();
        void Key(byte type, int index)
        {
            vdf.WriteByte(type);
            vdf.Write(BitConverter.GetBytes((uint)index));
        }
        void TextValue(int index, string value)
        {
            Key(1, index);
            vdf.Write(System.Text.Encoding.UTF8.GetBytes(value));
            vdf.WriteByte(0);
        }
        Key(0, 0);
        Key(0, 1);
        TextValue(2, simplified);
        TextValue(3, traditional);
        vdf.WriteByte(8);
        vdf.WriteByte(8);
        vdf.WriteByte(8);
        byte[] payload = vdf.ToArray();
        using var file = new MemoryStream();
        void U32(uint value) => file.Write(BitConverter.GetBytes(value));
        U32(0x07564429);
        U32(1);
        file.Write(BitConverter.GetBytes(0L));
        U32(appId);
        U32(60 + (uint)payload.Length);
        file.Write(new byte[60]);
        file.Write(payload);
        U32(0);
        long table = file.Position;
        U32((uint)keys.Length);
        foreach (string key in keys)
        {
            file.Write(System.Text.Encoding.UTF8.GetBytes(key));
            file.WriteByte(0);
        }
        file.Position = 8;
        file.Write(BitConverter.GetBytes(table));
        return file.ToArray();
    }
}
