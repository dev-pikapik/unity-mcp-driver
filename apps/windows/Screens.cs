using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;

namespace UDriver;

static class Ui
{
    static readonly Windows.UI.Color[] Palette = [ColorHelper.FromArgb(255, 15, 108, 189), ColorHelper.FromArgb(255, 135, 100, 184), ColorHelper.FromArgb(255, 202, 80, 16),
        ColorHelper.FromArgb(255, 3, 131, 135), ColorHelper.FromArgb(255, 194, 57, 179), ColorHelper.FromArgb(255, 79, 107, 237)];
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public static T Res<T>(string key) => (T)Application.Current.Resources[key];
    public static Brush Brush(string key) => Res<Brush>(key);
    public static Windows.UI.Color Tint(string s) => Palette[s.Sum(c => c) % Palette.Length];

    public static TextBlock Txt(string s, string style = "BodyTextBlockStyle", bool secondary = false)
    {
        var t = new TextBlock { Text = s, Style = Res<Style>(style), TextWrapping = TextWrapping.Wrap };
        if (secondary) t.Foreground = Brush("TextFillColorSecondaryBrush");
        return t;
    }

    public static StackPanel VStack(double spacing, params UIElement?[] kids)
    {
        var s = new StackPanel { Spacing = spacing };
        foreach (var k in kids.OfType<UIElement>()) s.Children.Add(k);
        return s;
    }

    public static StackPanel HStack(double spacing, params UIElement?[] kids)
    {
        var s = VStack(spacing, kids);
        s.Orientation = Orientation.Horizontal;
        foreach (var k in s.Children.OfType<FrameworkElement>()) k.VerticalAlignment = VerticalAlignment.Center;
        return s;
    }

    public static Grid Row(UIElement left, UIElement? right)
    {
        var g = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new() { Width = new(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } } };
        if (left is FrameworkElement l) l.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(left);
        if (right is FrameworkElement r)
        {
            r.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(r, 1);
            g.Children.Add(r);
        }
        return g;
    }

    public static Border CardOf(UIElement child, double pad = 16) => new()
    {
        Child = child, Padding = new(pad), CornerRadius = new(8), BorderThickness = new(1),
        Background = Brush("CardBackgroundFillColorDefaultBrush"), BorderBrush = Brush("CardStrokeColorDefaultBrush"),
    };

    public static Border Tile(string glyph, Windows.UI.Color color, double side = 36) => new()
    {
        Width = side, Height = side, CornerRadius = new(side / 4), Background = new SolidColorBrush(color),
        Child = new FontIcon { Glyph = glyph, FontSize = side / 2, Foreground = new SolidColorBrush(Colors.White) },
    };

    public static Border Avatar(string name, Windows.UI.Color color) => new()
    {
        Width = 36, Height = 36, CornerRadius = new(18), Background = new SolidColorBrush(color),
        Child = new TextBlock
        {
            Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?", Foreground = new SolidColorBrush(Colors.White), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// The button whose click is running; Shell.Act shows it as busy.
    public static Button? Pressed;

    public static Button Btn(string text, Func<Task>? click = null, bool accent = false)
    {
        var b = new Button { Content = text };
        if (accent) b.Style = Res<Style>("AccentButtonStyle");
        if (click != null) b.Click += async (_, _) =>
        {
            Pressed = b;
            try { await click(); }
            finally { if (Pressed == b) Pressed = null; }
        };
        return b;
    }

    public static Button Btn(string text, Action click, bool accent = false) => Btn(text, () => { click(); return Task.CompletedTask; }, accent);

    public static MenuFlyoutItem Item(string text, Func<Task> click)
    {
        var i = new MenuFlyoutItem { Text = text };
        i.Click += async (_, _) => await click();
        return i;
    }

    public static StackPanel Section(string title, string? hint, params UIElement[] rows)
    {
        var head = VStack(2, Txt(title, "BodyStrongTextBlockStyle"), hint == null ? null : Txt(hint, "CaptionTextBlockStyle", true));
        head.Margin = new(0, 8, 0, 4);
        return VStack(4, [head, .. rows]);
    }

    public static ScrollViewer Page(params UIElement?[] kids)
    {
        var v = VStack(8, kids);
        v.Padding = new(36, 0, 36, 36);
        v.MaxWidth = 1000;
        v.HorizontalAlignment = HorizontalAlignment.Left;
        return new ScrollViewer { Content = v };
    }

    public static async Task<BitmapImage> Bitmap(byte[] data)
    {
        var bmp = new BitmapImage();
        using var s = new InMemoryRandomAccessStream();
        await s.WriteAsync(data.AsBuffer());
        s.Seek(0);
        await bmp.SetSourceAsync(s);
        return bmp;
    }

    public static void Clip(string text)
    {
        var p = new DataPackage();
        p.SetText(text);
        Clipboard.SetContent(p);
        Clipboard.Flush();
    }

    public static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    public static string Sentence(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public static string Ago(JsonNode a) => a["agoSec"] == null ? "just connected" : a.I("agoSec") switch
    {
        < 60 => "active now",
        < 3600 and var s => $"idle for {s / 60} min",
        var s => $"idle for {s / 3600} h",
    };
}

sealed class GamesScreen : Screen
{
    static readonly string[] Levels = ["watch", "play", "full"], Names = ["Watch", "Play", "Everything"];
    static readonly string[] Hints = ["Look only: see the screen and read the scene. Nothing is pressed or changed.",
        "Look, press buttons and play. Files stay untouched.", "Look, play and also change scenes, settings and project files."];
    readonly AutoSuggestBox search = new() { PlaceholderText = "Search games", QueryIcon = new SymbolIcon(Symbol.Find), Width = 240 };
    readonly StackPanel tools;
    readonly Dictionary<string, BitmapImage> previews = new();
    readonly Dictionary<string, Image> images = new();
    readonly Border panel = new() { Margin = new(36, 12, 36, 24) };
    Model m = new();
    string? selected;

    public GamesScreen()
    {
        search.TextChanged += (_, _) => Shell.Main.Render();
        var add = Ui.Btn("Add game", null, true);
        add.Flyout = AddMenu();
        tools = Ui.HStack(8, search, add);
    }

    public override string Title => "Games";
    public override UIElement Tools => tools;
    public override string Key(Model m) =>
        string.Join("\n", m.Games.Select(g => g.ToJsonString()).Concat(m.Agents.Select(a => a.S("target") + "|" + a.S("label")))) + search.Text;

    public MenuFlyout AddMenu() => new()
    {
        Items =
        {
            Ui.Item("Built game (.exe)…", async () =>
            {
                var p = new FileOpenPicker();
                p.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(p, Shell.Main.Hwnd);
                if (await p.PickSingleFileAsync() is { } f) await Add(f.Path);
            }),
            Ui.Item("Unity project folder…", async () =>
            {
                var p = new FolderPicker();
                p.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(p, Shell.Main.Hwnd);
                if (await p.PickSingleFolderAsync() is { } f) await Add(f.Path);
            }),
        },
    };

    async Task Add(string path)
    {
        if (await Shell.Main.Act("games/add", ("path", path)) is not { } r) return;
        selected = r.S("id");
        if (r.S("warning") is { Length: > 0 } w) await Shell.Main.Say("Game added", w);
    }

    public void Drop(UIElement e)
    {
        e.AllowDrop = true;
        e.DragOver += (_, a) =>
        {
            if (!a.DataView.Contains(StandardDataFormats.StorageItems)) return;
            a.AcceptedOperation = DataPackageOperation.Link;
            a.DragUIOverride.Caption = "Add to UDriver";
        };
        e.Drop += async (_, a) =>
        {
            if (!a.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var d = a.GetDeferral();
            var items = await a.DataView.GetStorageItemsAsync();
            d.Complete();
            foreach (var i in items) await Add(i.Path);
        };
    }

    static bool Live(JsonNode g) => g.S("state") != "not running";
    string? Player(JsonNode g) => m.Agents.FirstOrDefault(a => a.S("target") is { Length: > 0 } t && (t == g.S("name") || t == g.S("id"))).S("label") is { Length: > 0 } p ? p : null;

    string Meta(JsonNode g)
    {
        var kind = g.S("kind") == "project" ? "Project" : "Build";
        if (!g.B("exists", true)) return kind + " · Missing on disk";
        return Player(g) is { } p ? $"{kind} · {p} is playing" : kind + " · " + Ui.Sentence(g.S("state")) + (g.S("access") == "none" ? " · Not allowed" : "");
    }

    public override void Update(Model m)
    {
        foreach (var g in m.Games.Where(Live)) _ = Fetch(g.S("id"));
    }

    async Task Fetch(string id)
    {
        if (await Api.Bytes("games/preview?id=" + Uri.EscapeDataString(id)) is not { } b) return;
        var bmp = await Ui.Bitmap(b);
        previews[id] = bmp;
        if (images.TryGetValue(id, out var img)) img.Source = bmp;
    }

    public override UIElement Build(Model m)
    {
        this.m = m;
        images.Clear();
        var root = new Grid { RowDefinitions = { new() { Height = new(1, GridUnitType.Star) }, new() { Height = GridLength.Auto } }, Background = new SolidColorBrush(Colors.Transparent) };
        Drop(root);
        if (m.Games.Length == 0)
        {
            var empty = Ui.VStack(8, new FontIcon { Glyph = "", FontSize = 40, Foreground = Ui.Brush("TextFillColorTertiaryBrush") },
                Ui.Txt("Drop a game or project here", "SubtitleTextBlockStyle"), Ui.Txt("Add a built game or a Unity project to let agents see and play it.", "BodyTextBlockStyle", true));
            foreach (var e in empty.Children.OfType<FrameworkElement>()) e.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            root.Children.Add(empty);
            return root;
        }
        if (selected == null || m.Games.All(g => g.S("id") != selected)) selected = m.Games[0].S("id");
        var shown = m.Games.Where(g => g.S("name").Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        var grid = new GridView { SelectionMode = ListViewSelectionMode.Single, Padding = new(36, 0, 36, 0) };
        foreach (var g in shown) grid.Items.Add(Card(g));
        grid.SelectedIndex = shown.FindIndex(g => g.S("id") == selected);
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is not FrameworkElement { Tag: string id }) return;
            selected = id;
            panel.Child = Panel();
        };
        root.Children.Add(grid);
        panel.Child = Panel();
        Grid.SetRow(panel, 1);
        root.Children.Add(panel);
        return root;
    }

    FrameworkElement Card(JsonNode g)
    {
        var id = g.S("id");
        var name = g.S("name");
        var c1 = Ui.Tint(name);
        var c2 = Ui.Tint(name + "~");
        var pic = new Grid
        {
            Height = 150, CornerRadius = new(4),
            Background = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1), GradientStops = { new() { Color = c1, Offset = 0 }, new() { Color = c2, Offset = 1 } } },
        };
        pic.Children.Add(new FontIcon { Glyph = g.S("kind") == "project" ? "" : "", FontSize = 40, Foreground = new SolidColorBrush(ColorHelper.FromArgb(200, 255, 255, 255)) });
        var img = new Image { Stretch = Stretch.UniformToFill, Source = previews.GetValueOrDefault(id) };
        AutomationProperties.SetName(img, "Preview of " + name);
        images[id] = img;
        pic.Children.Add(img);
        if (Live(g))
        {
            var dot = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(Colors.LimeGreen) };
            pic.Children.Add(new Border
            {
                Background = new SolidColorBrush(ColorHelper.FromArgb(170, 0, 0, 0)), CornerRadius = new(10), Padding = new(8, 2, 9, 3), Margin = new(8),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = Ui.HStack(5, dot, new TextBlock { Text = "Live", FontSize = 12, Foreground = new SolidColorBrush(Colors.White) }),
            });
        }
        var title = Ui.Txt(name, "BodyStrongTextBlockStyle");
        title.Margin = new(0, 8, 0, 0);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var card = Ui.CardOf(Ui.VStack(2, pic, title, Ui.Txt(Meta(g), "CaptionTextBlockStyle", true)), 8);
        card.Width = 266;
        card.Tag = id;
        AutomationProperties.SetName(card, name);
        return card;
    }

    UIElement Panel()
    {
        if (m.Games.FirstOrDefault(x => x.S("id") == selected) is not { } g) return new Grid();
        var id = g.S("id");
        var name = g.S("name");
        var lvl = Array.IndexOf(Levels, g.S("access"));
        var hint = Ui.Txt(lvl < 0 ? "Not allowed — set access to use it." : Hints[lvl], "BodyTextBlockStyle", true);
        var levels = new RadioButtons { MaxColumns = 3 };
        foreach (var n in Names) levels.Items.Add(n);
        levels.SelectedIndex = lvl;
        AutomationProperties.SetName(levels, "Agents may");
        levels.SelectionChanged += async (_, _) =>
        {
            if (levels.SelectedIndex is not (>= 0 and var k) || Levels[k] == g.S("access")) return;
            hint.Text = Hints[k];
            await Shell.Main.Act("games/edit", ("id", id), ("access", Levels[k]));
        };
        var running = Live(g);
        var stop = running && g.B("startedByUDriver");
        var start = Ui.Btn(running ? stop ? "Stop" : "Restart" : "Launch", () => Shell.Main.Act(stop ? "games/stop" : "games/start", ("id", id)), true);
        start.IsEnabled = g.B("exists", true);
        var path = g.S("path");
        var folder = Ui.Btn("Open folder", () => { Process.Start("explorer.exe", Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\""); });
        folder.IsEnabled = g.B("exists", true);
        var more = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Flyout = new MenuFlyout
            {
                Items =
                {
                    Ui.Item("Remove from UDriver", async () =>
                    {
                        if (!await Shell.Main.Confirm($"Remove {name} from UDriver?", "Agents lose access to it. The game itself stays on your computer.", "Remove", safe: false)) return;
                        selected = null;
                        await Shell.Main.Act("games/remove", ("id", id));
                    }),
                },
            },
        };
        AutomationProperties.SetName(more, "More options");
        var facts = $"{Ui.Sentence(g.S("state"))} · {(Player(g) is { } p ? p + " is playing" : "No agent")} · Engine: {(g.S("engine") is { Length: > 0 } e ? e : "Unity")}";
        var left = Ui.VStack(6, Ui.Txt($"{name} — what agents may do", "SubtitleTextBlockStyle"), hint, levels, Ui.Txt(facts, "CaptionTextBlockStyle", true));
        return Ui.CardOf(Ui.Row(left, Ui.HStack(8, start, folder, more)), 20);
    }
}

sealed class AgentsScreen : Screen
{
    readonly Button connect = Ui.Btn("Connect agent", () => Shell.Main.Go("access"), true);
    public override string Title => "Agents";
    public override UIElement Tools => connect;

    public override string Key(Model m) => string.Join("\n",
        m.Agents.Select(a => $"{a.S("id")}|{a.S("label")}|{a.S("target")}|{a.S("from")}|{Ui.Ago(a)}|{a.B("disconnected")}|{a.S("client")}")
            .Concat(m.Clients.Select(c => c.ToJsonString())).Concat(m.Games.Select(g => g.S("name") + g.S("access"))));

    public override UIElement Build(Model m)
    {
        var live = m.Live.ToList();
        string Name(JsonNode a) => m.Clients.FirstOrDefault(c => c.S("id") == a.S("client")).S("name") is { Length: > 0 } n ? n : a.S("label") is { Length: > 0 } l ? l : "Agent";

        UIElement Row(JsonNode a)
        {
            var name = Name(a);
            var where = a.S("from") is "" or "this computer" ? "On this computer" : "From anywhere";
            var t = a.S("target");
            var what = t == "" ? Ui.Ago(a) : (m.Games.FirstOrDefault(g => g.S("name") == t || g.S("id") == t).S("access") == "watch" ? "watching " : "playing ") + t;
            var b = Ui.Btn("Disconnect", () => Shell.Main.Act("agents/disconnect", ("id", a.S("id"))));
            AutomationProperties.SetName(b, "Disconnect " + name);
            return Ui.CardOf(Ui.Row(Ui.HStack(12, Ui.Avatar(name, Ui.Tint(name)), Ui.VStack(0, Ui.Txt(name, "BodyStrongTextBlockStyle"), Ui.Txt($"{where} · {what}", "CaptionTextBlockStyle", true))), b), 12);
        }

        var here = live.Select(a => a.S("client")).ToHashSet();
        var hereNames = live.Select(Name).ToHashSet();
        var before = m.Clients.Where(c => c.S("kind") != "stdio" && !here.Contains(c.S("id")) && !hereNames.Contains(c.S("name")) && c.S("status") == "allowed").GroupBy(c => c.S("name")).Select(grp =>
        {
            var name = grp.Key is { Length: > 0 } k ? k : "Agent";
            var kind = grp.First().S("kind") is "remote" or "link" ? "From anywhere" : "On this computer";
            var b = Ui.Btn("Remove", async () =>
            {
                if (!await Shell.Main.Confirm($"Remove {name}?", "It loses access at once and has to ask again to come back.", "Remove", safe: false)) return;
                foreach (var c in grp) await Shell.Main.Act("clients/revoke", ("id", c.S("id")));
            });
            AutomationProperties.SetName(b, "Remove " + name);
            return (UIElement)Ui.CardOf(Ui.Row(Ui.HStack(12, Ui.Avatar(name, Colors.Gray), Ui.VStack(0, Ui.Txt(name, "BodyStrongTextBlockStyle"), Ui.Txt($"{kind} · Not connected now", "CaptionTextBlockStyle", true))), b), 12);
        }).ToArray();

        var connected = live.Count == 0
            ? [Ui.CardOf(Ui.Txt("No agent is connected right now. Agents on this computer connect by themselves; for others choose Connect agent.", "BodyTextBlockStyle", true))]
            : live.Select(Row).ToArray();
        return Ui.Page(Ui.Section("Connected now", null, connected),
            before.Length == 0 ? null : Ui.Section("Allowed before", "These agents may come back without asking again. Remove one to make it ask next time.", before));
    }
}

sealed class ToolsScreen : Screen
{
    static readonly (string Title, string Level, string Hint)[] Groups =
        [("Look", "watch", "See the screen and read the scene."), ("Play", "play", "Press buttons and play."), ("Change", "full", "Change scenes, settings and project files.")];
    readonly ComboBox who = new() { MinWidth = 220 };
    readonly StackPanel tools;
    string client = "";
    bool filling;

    public ToolsScreen()
    {
        AutomationProperties.SetName(who, "Settings for");
        who.SelectionChanged += (_, _) =>
        {
            if (filling || who.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            client = id;
            who.DispatcherQueue.TryEnqueue(() => Shell.Main.Render());
        };
        tools = Ui.HStack(8, Ui.Txt("For", "BodyTextBlockStyle", true), who);
    }

    public override string Title => "Tools";
    public override UIElement Tools => tools;
    public override string Key(Model m) => string.Join("\n", m.Tools.Select(t => t.ToJsonString()).Concat(m.ToolClients.Select(c => c.ToJsonString()))) + client;

    public override UIElement Build(Model m)
    {
        filling = true;
        var want = m.ToolClients.Select(c => (Id: c.S("id"), Name: c.S("name") is { Length: > 0 } n ? n : "Agent")).Prepend((Id: "", Name: "All agents")).ToArray();
        if (!want.SequenceEqual(who.Items.OfType<ComboBoxItem>().Select(i => ((string)i.Tag, (string)i.Content))))
        {
            who.Items.Clear();
            foreach (var (id, name) in want) who.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        }
        if (want.All(c => c.Id != client)) client = "";
        var pick = who.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == client);
        if (!ReferenceEquals(who.SelectedItem, pick)) who.SelectedItem = pick;
        filling = false;
        var forName = ((ComboBoxItem)who.SelectedItem).Content;

        bool On(JsonNode t) => t.B("enabled", true) && (client == "" || t.A("offFor").All(x => x.GetValue<string>() != client));

        UIElement Row(IGrouping<string, JsonNode> f, bool first)
        {
            var names = f.Select(t => t.S("name")).ToArray();
            var count = f.Count(On);
            var mixed = count > 0 && count < names.Length;
            var sw = new ToggleSwitch { IsOn = count > 0, MinWidth = 0, IsEnabled = client == "" || f.Any(t => t.B("enabled", true)) };
            AutomationProperties.SetName(sw, f.Key);
            sw.Toggled += async (_, _) =>
            {
                var form = new List<(string, string)> { ("tool", string.Join(",", names)), ("enabled", sw.IsOn ? "true" : "false") };
                if (client != "") form.Add(("client", client));
                await Shell.Main.Act("tools/set", [.. form]);
            };
            var note = Ui.Txt((mixed ? "some off · " : "") + string.Join(" · ", names), "CaptionTextBlockStyle", !mixed);
            note.FontFamily = Ui.Mono;
            if (mixed) note.Foreground = Ui.Brush("SystemFillColorCautionBrush");
            var row = new Border
            {
                Padding = new(16, 8, 16, 8), BorderBrush = Ui.Brush("DividerStrokeColorDefaultBrush"), BorderThickness = new(0, first ? 0 : 1, 0, 0),
                Child = Ui.Row(Ui.VStack(0, Ui.Txt(f.Key, "BodyStrongTextBlockStyle"), note), sw),
            };
            ToolTipService.SetToolTip(row, sw.IsEnabled ? string.Join("\n", f.Select(t => $"{t.S("name")}: {(t.S("title") is { Length: > 0 } x ? x : t.S("description"))}")) : "Switched off for every agent. Turn it on under All agents first.");
            return row;
        }

        var sections = Groups.Select(grp =>
        {
            var list = m.Tools.Where(t => t.S("group") is { Length: > 0 } gr ? gr == grp.Title : t.S("access") == grp.Level)
                .GroupBy(t => t.S("feature") is { Length: > 0 } f ? f : t.S("title") is { Length: > 0 } x ? x : t.S("name")).ToList();
            if (list.Count == 0) return null;
            var card = Ui.CardOf(Ui.VStack(0, list.Select((t, i) => Row(t, i == 0)).ToArray()), 0);
            return (UIElement)Ui.Section(grp.Title, grp.Hint, card);
        }).ToArray();
        var hint = Ui.Txt(client == "" ? "A switched-off feature is closed for every agent, including over the internet. A switch shows on while any of its tools is on." : $"These switches apply only to {forName}.", "BodyTextBlockStyle", true);
        return Ui.Page([hint, .. sections]);
    }
}

sealed class AccessScreen : Screen
{
    static readonly Windows.UI.Color Blue = ColorHelper.FromArgb(255, 15, 108, 189), Violet = ColorHelper.FromArgb(255, 135, 100, 184), Gray = ColorHelper.FromArgb(255, 112, 112, 112);

    public override string Title => "Access from anywhere";

    public override string Key(Model m)
    {
        var r = m.Remote.DeepClone().AsObject();
        r.Remove("onlineSec");
        return r.ToJsonString();
    }

    public override UIElement Build(Model m)
    {
        var r = m.Remote;
        var on = r.B("on");
        var sw = new ToggleSwitch { IsOn = on, MinWidth = 0 };
        AutomationProperties.SetName(sw, "Access from anywhere");
        sw.Toggled += async (_, _) => await Flip(sw.IsOn, r);
        var state = r.S("state");
        var sub = r.S("error") is { Length: > 0 } e ? e : on && state != "online" && state != "" ? "Connecting…" : "Through a private, encrypted link. Turn off to close it at once.";
        var top = Ui.CardOf(Ui.Row(Ui.HStack(12, Ui.Tile("", Blue), Ui.VStack(2, Ui.Txt("Agents on other devices can reach your games", "BodyStrongTextBlockStyle"), Ui.Txt(sub, "BodyTextBlockStyle", true))), sw));
        if (!on) return Ui.Page([top, .. Setup(r), Local(), .. Cloudflare(r)]);

        var address = r.S("address");
        var addr = Ui.Txt(address);
        addr.FontFamily = Ui.Mono;
        addr.IsTextSelectionEnabled = true;
        var addrCard = Ui.CardOf(Ui.Row(Ui.VStack(4, Ui.Txt("Address", "CaptionTextBlockStyle", true), addr), Ui.Btn("Copy", () => Ui.Clip(address))));

        var qr = new Image { Width = 140, Height = 140 };
        AutomationProperties.SetName(qr, "QR code to open UDriver on your phone");
        _ = Qr(qr, r.S("panel") is { Length: > 0 } p ? p : address);
        var right = Ui.VStack(6, new Border { Background = new SolidColorBrush(Colors.White), CornerRadius = new(6), Padding = new(6), Child = qr },
            Ui.Txt("Open on your phone", "CaptionTextBlockStyle", true));
        right.Children[1].SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        var connectCard = Ui.CardOf(Ui.Row(Ui.VStack(4, Ui.Txt("Connect a new agent", "BodyStrongTextBlockStyle"),
            Ui.Txt("Paste the address into the agent. When it connects for the first time, this computer asks you to allow it. Press Allow if the agent's page shows the same check number.", "BodyTextBlockStyle", true)), right), 20);
        return Ui.Page([top, addrCard, connectCard, Local(), .. Cloudflare(r)]);
    }

    static UIElement[] Cloudflare(JsonNode r)
    {
        if (!r.B("signedIn")) return [];
        var signOut = Ui.Btn("Turn off and sign out", async () =>
        {
            if (!await Shell.Main.Confirm("Turn off and sign out of Cloudflare?",
                "Access from anywhere turns off and UDriver forgets its Cloudflare sign-in on this computer. Your tunnel stays in your Cloudflare account, so you can sign in and turn it on again later.",
                "Turn off and sign out", safe: false)) return;
            if (await Shell.Main.Act("remote/signout") is { } j && j.S("note") is { Length: > 0 } note) await Shell.Main.Say("Signed out of UDriver's Cloudflare sign-in", note);
        });
        return [Ui.CardOf(Ui.Row(Ui.HStack(12, Ui.Tile("\uE753", Gray), Ui.VStack(2, Ui.Txt("Cloudflare", "BodyStrongTextBlockStyle"),
            Ui.Txt(r.S("domain") is { Length: > 0 } d ? "Signed in · domain " + d : "Signed in", "BodyTextBlockStyle", true))), signOut))];
    }

    static async Task Qr(Image target, string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        target.Source = await Ui.Bitmap(new PngByteQRCode(data).GetGraphic(8));
    }

    UIElement[] Setup(JsonNode r)
    {
        if (!r.B("cloudflared", true))
            return [Ui.CardOf(Ui.Row(Ui.Txt("To reach your games from other devices, UDriver first downloads a small helper for the private link."),
                Ui.Btn("Download helper", () => Shell.Main.Act("remote/install"), true)))];
        if (!r.B("saved") && !r.B("signedIn"))
        {
            var url = r.S("loginUrl");
            return [Ui.CardOf(Ui.Row(Ui.Txt("Sign in to Cloudflare once to create your private link. It is free."),
                Ui.Btn(url == "" ? "Sign in" : "Open sign-in page", () => url == "" ? Shell.Main.Act("remote/login") : Task.Run(() => Ui.Open(url)), true)))];
        }
        return [];
    }

    UIElement Local()
    {
        var copy = new SplitButton
        {
            Content = "Copy setup",
            Flyout = new MenuFlyout { Items = { Ui.Item("For apps that start UDriver themselves", CopyCommand), Ui.Item("For apps that connect to UDriver…", CopyKey) } },
        };
        copy.Click += async (_, _) => await CopyCommand();
        ToolTipService.SetToolTip(copy, "Copies the setup for apps that start UDriver themselves. The arrow has the option for apps that connect over the network.");
        AutomationProperties.SetName(copy, "Copy setup for an app on this computer");
        return Ui.CardOf(Ui.Row(Ui.HStack(12, Ui.Tile("", Violet), Ui.VStack(2, Ui.Txt("Apps on this computer", "BodyStrongTextBlockStyle"),
            Ui.Txt("Local models connect directly, without the internet.", "BodyTextBlockStyle", true))), copy));
    }

    async Task CopyCommand()
    {
        if (Api.Server() is not { } s)
        {
            await Shell.Main.Say("UDriver is not fully installed", "Run the UDriver installer once, then try again.");
            return;
        }
        var server = new JsonObject { ["command"] = s.File };
        if (s.Args.Length > 0) server["args"] = new JsonArray(s.Args.Select(a => (JsonNode?)a).ToArray());
        Ui.Clip(new JsonObject { ["mcpServers"] = new JsonObject { ["udriver"] = server } }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        await Shell.Main.Say("Setup copied", "Paste it into the app's MCP settings. The app starts UDriver by itself when it needs it.");
    }

    async Task CopyKey()
    {
        var box = new TextBox { PlaceholderText = "App name" };
        if (!await Shell.Main.Confirm("Which app is this for?", "UDriver makes a private key for this app. You can remove it later under Agents.", "Copy setup", extra: box)) return;
        if (box.Text.Trim() is not { Length: > 0 } name || await Shell.Main.Act("local-clients", ("name", name)) is not { } r) return;
        Ui.Clip(r.S("mcpJson") is { Length: > 0 } j ? j : r["mcpJson"]?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "");
        await Shell.Main.Say("Setup copied", "Paste it into the app's MCP settings. It connects while UDriver is running.");
    }

    async Task Flip(bool v, JsonNode r)
    {
        if (v)
        {
            if (r.B("saved")) { await Shell.Main.Act("remote/on"); return; }
            if (r.B("signedIn"))
            {
                var box = new TextBox { PlaceholderText = "games.example.com" };
                if (await Shell.Main.Confirm("Choose an address", "Type the full address on your Cloudflare domain, for example games.example.com.", "Turn on", extra: box))
                {
                    await Shell.Main.Act("remote/on", ("host", box.Text.Trim()));
                    return;
                }
            }
            else await Shell.Main.Say("Finish setup first", "Follow the step below this switch, then turn it on.");
        }
        else if (await Shell.Main.Confirm("Turn off access from anywhere?", "Agents and devices that connect over the internet lose access at once.", "Turn off", safe: false))
        {
            await Shell.Main.Act("remote/off");
            return;
        }
        Shell.Main.Render(true);
    }
}

sealed class ProblemsScreen : Screen
{
    public override string Title => "Problems";
    public override string Key(Model m) => string.Join("\n", m.Problems.Select(p => $"{p.S("id")}|{p.S("count")}|{p.S("lastSeen")}"));

    public override UIElement Build(Model m)
    {
        UIElement Row(JsonNode p)
        {
            var what = p.S("title") is { Length: > 0 } t ? t : p.S("what");
            var n = int.TryParse(p.S("count"), out var c) ? c : 1;
            var when = DateTime.TryParse(p.S("lastSeen"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
                ? " · last " + d.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.InvariantCulture) : "";
            var send = Ui.Btn("Send to developer", () => Ui.Open(p.S("issueUrl")));
            var dismiss = Ui.Btn("Dismiss", () => Shell.Main.Act("problems/dismiss", ("id", p.S("id"))));
            AutomationProperties.SetName(dismiss, "Dismiss: " + what);
            return Ui.CardOf(Ui.Row(Ui.VStack(0, Ui.Txt(what, "BodyStrongTextBlockStyle"), Ui.Txt((n > 1 ? $"{n} times" : "Once") + when, "CaptionTextBlockStyle", true)), Ui.HStack(8, send, dismiss)), 12);
        }

        var rows = m.Problems.Length == 0 ? [Ui.CardOf(Ui.Txt("No problems found.", "BodyTextBlockStyle", true))] : m.Problems.Select(Row).ToArray();
        return Ui.Page([.. rows, Ui.Txt("When UDriver itself lets an agent down, a short note appears here. Nothing is sent on its own: Send to developer opens a filled-in page on GitHub, and you decide whether to press Submit there. Passwords, keys and your user name are removed first.", "CaptionTextBlockStyle", true)]);
    }
}
