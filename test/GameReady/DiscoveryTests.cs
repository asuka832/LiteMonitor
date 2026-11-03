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
                "影之刃零布局识别游戏主体，不选择解压工具或环境检测程序");
        }
        finally { Directory.Delete(root, true); }
    }
}
