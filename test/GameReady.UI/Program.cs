using System.Reflection;
using LiteMonitor.src.UI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        string output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/game-ready-preview");
        Directory.CreateDirectory(output);
        if (args.Contains("--picker"))
        {
            RenderPicker(output);
            return;
        }
        using var form = (Form)Activator.CreateInstance(typeof(GameReadyForm), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object?[] { null }, null)!;
        form.Show();
        Application.DoEvents(); // Dispatch Shown before checking whether automatic scanning is busy.
        var timeout = DateTime.UtcNow.AddSeconds(30);
        var busyField = typeof(GameReadyForm).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        while ((bool)busyField.GetValue(form)!)
        {
            if (DateTime.UtcNow > timeout) throw new Exception("Automatic discovery did not finish within 30 seconds.");
            Application.DoEvents();
            Thread.Sleep(20);
        }
        var tabs = (Panel)typeof(GameReadyForm).GetField("tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        Application.DoEvents();
        var gameList = (ListBox)typeof(GameReadyForm).GetField("games", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var pbz = gameList.Items.Cast<LiteMonitor.GameReady.GameProfile>().FirstOrDefault(g => g.SteamAppId == "4115450");
        if (pbz != null) gameList.SelectedItem = pbz;
        var actions = (FlowLayoutPanel)typeof(GameReadyForm).GetField("actions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        if (tabs.RectangleToScreen(tabs.ClientRectangle).Bottom > actions.RectangleToScreen(actions.ClientRectangle).Top ||
            actions.Controls.OfType<Button>().All(b => b.Text != "清理并启动游戏"))
            throw new Exception("The action bar must be visible below the tabs.");
        var pages = (List<Panel>)typeof(GameReadyForm).GetField("pageViews", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var switchPage = typeof(GameReadyForm).GetMethod("SwitchPage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 0; i < pages.Count; i++)
        {
            switchPage.Invoke(form, new object[] { i });
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(output, "tab-" + i + ".png"));
        }
        switchPage.Invoke(form, new object[] { 0 });
        form.Size = form.MinimumSize;
        Application.DoEvents();
        using (var compact = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(compact, new Rectangle(Point.Empty, form.Size));
            compact.Save(Path.Combine(output, "compact.png"));
        }
        var advanced = (Panel)typeof(GameReadyForm).GetField("advanced", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        advanced.Visible = true;
        ((ScrollableControl)advanced.Parent!).ScrollControlIntoView(advanced);
        Application.DoEvents();
        using (var expanded = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(expanded, new Rectangle(Point.Empty, form.Size));
            expanded.Save(Path.Combine(output, "advanced.png"));
        }
        advanced.Visible = false;
        var rebind = typeof(GameReadyForm).GetMethod("RebindGames", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var options = (LiteMonitor.GameReady.GameReadyOptions)typeof(GameReadyForm).GetField("options", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        options.Games.Clear();
        rebind.Invoke(form, new object?[] { null });
        Application.DoEvents();
        using (var empty = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(empty, new Rectangle(Point.Empty, form.Size));
            empty.Save(Path.Combine(output, "empty.png"));
        }
        // Repeated open/close should not register a process watcher or run any cleanup.
        if (typeof(GameReadyForm).GetField("watchedGame", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) != null)
            throw new Exception("Opening configuration must not create a game watcher.");
        form.Close();
        Console.WriteLine("PASS rendered all Game Ready tabs without cleaning or launching any app: " + output);
    }

    private static void RenderPicker(string output)
    {
        var choices = RunningAppPicker.ReadChoicesAsync().GetAwaiter().GetResult();
        Console.WriteLine($"Read-only picker: {choices.Count} applications; {choices.Count(p => p.HasWindow)} with windows; {choices.Count(p => p.HasTrayMatch)} tray matches.");
        using var picker = new RunningAppPicker(choices, Array.Empty<string>());
        picker.Show();
        Application.DoEvents();
        var loadedField = typeof(RunningAppPicker).GetField("iconsLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(bool)loadedField.GetValue(picker)!)
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Picker icon loading timed out.");
            Application.DoEvents();
            Thread.Sleep(20);
        }
        void Capture(string name)
        {
            Application.DoEvents();
            using var bitmap = new Bitmap(picker.Width, picker.Height);
            picker.DrawToBitmap(bitmap, new Rectangle(Point.Empty, picker.Size));
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
        var list = (ListView)typeof(RunningAppPicker).GetField("list", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
        if (list.Items.Cast<ListViewItem>().Any(i => ((LiteMonitor.GameReady.RunningAppChoice)i.Tag!).Priority == 2))
            throw new Exception("Background-only applications must be hidden by default.");
        Capture("picker");
        if (list.Items.Count > 0)
        {
            list.Items[0].Checked = true;
            string selected = picker.SelectedPaths.Single();
            var search = (TextBox)typeof(RunningAppPicker).GetField("search", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
            search.Text = "NoSuchApplication-99132";
            if (!picker.SelectedPaths.Contains(selected) || list.Items.Count != 0) throw new Exception("Search must preserve checked applications.");
            Capture("picker-empty");
            search.Clear();
            if (!list.Items.Cast<ListViewItem>().Any(i => i.Checked && ((LiteMonitor.GameReady.RunningAppChoice)i.Tag!).Path == selected))
                throw new Exception("Clearing search must restore visible check marks.");
        }
        var toggle = (CheckBox)typeof(RunningAppPicker).GetField("showBackground", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
        toggle.Checked = true;
        Capture("picker-background");
        picker.Size = picker.MinimumSize;
        Capture("picker-compact");
        if (list.SmallImageList?.Images.Count < 2 && choices.Any(p => File.Exists(p.Path))) throw new Exception("Existing applications should have extracted icons.");
        picker.Close();
        Console.WriteLine("PASS picker icons, grouping, filtering, selection persistence and compact rendering.");
    }
}
