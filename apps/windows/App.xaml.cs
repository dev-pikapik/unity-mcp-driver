using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppLifecycle;
using Velopack;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;

namespace UDriver;

public partial class App : Application
{
    [STAThread]
    static void Main()
    {
        VelopackApp.Build().Run();
        var me = AppInstance.FindOrRegisterForKey("UDriver-" + Api.Port);
        if (!me.IsCurrent)
        {
            Task.Run(() => me.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask()).Wait();
            return;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }

    public App()
    {
        InitializeComponent();
        if (Enum.TryParse<ApplicationTheme>(Environment.GetEnvironmentVariable("UDRIVER_THEME"), true, out var theme)) RequestedTheme = theme;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        var w = new Shell();
        AppInstance.GetCurrent().Activated += (_, _) => w.DispatcherQueue.TryEnqueue(() => { w.AppWindow.Show(); w.Activate(); });
        w.Activate();
    }
}

sealed class Model
{
    public JsonNode[] Games = [], Agents = [], Clients = [], Tools = [], ToolClients = [], Pending = [], Problems = [];
    public JsonObject Remote = new();
    public IEnumerable<JsonNode> Live => Agents.Where(a => !a.B("disconnected"));
}

abstract class Screen
{
    public abstract string Title { get; }
    public virtual UIElement? Tools => null;
    public abstract string Key(Model m);
    public abstract UIElement Build(Model m);
    public virtual void Update(Model m) { }
}

sealed partial class Shell : Window
{
    public static Shell Main = null!;
    public readonly Model M = new();
    public readonly IntPtr Hwnd;
    readonly Dictionary<string, Screen> screens;
    readonly Dictionary<string, TextBlock> counts = new();
    readonly NavigationView nav;
    readonly TextBlock title = Ui.Txt("", "TitleTextBlockStyle");
    readonly ContentControl tools = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly Grid header = new() { Margin = new(36, 20, 36, 16) };
    readonly Border body = new();
    readonly Ellipse dot = new() { Width = 8, Height = 8 };
    readonly TextBlock status = Ui.Txt("Starting UDriver…", "CaptionTextBlockStyle", true);
    readonly ProgressRing spin = new() { Width = 12, Height = 12, MinWidth = 12, MinHeight = 12 };
    readonly List<string> working = [];
    readonly DispatcherQueueTimer timer;
    static readonly string Welcomed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UDriver", "welcomed");
    string page = "games", lastKey = "", approvalId = "";
    string? problem;
    bool up, busy, again, stuck, polled, welcome;
    ContentDialog? open, approval;

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

    public Shell()
    {
        Main = this;
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "UDriver";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "udriver.ico"));
        var scale = GetDpiForWindow(Hwnd) / 96.0;
        AppWindow.Resize(new((int)(1180 * scale), (int)(800 * scale)));
        screens = new() { ["games"] = new GamesScreen(), ["agents"] = new AgentsScreen(), ["tools"] = new ToolsScreen(), ["access"] = new AccessScreen(), ["problems"] = new ProblemsScreen() };

        var logo = new Image { Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "udriver.png"))), Width = 16, Height = 16 };
        var bar = new Grid { Height = 48, Children = { Ui.HStack(12, logo, Ui.Txt("UDriver", "CaptionTextBlockStyle")) } };
        bar.Children[0].SetValue(FrameworkElement.MarginProperty, new Thickness(16, 0, 0, 0));
        SetTitleBar(bar);

        nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false, IsSettingsVisible = false, OpenPaneLength = 250,
        };
        foreach (var (tag, text, glyph) in new[] { ("games", "Games", ""), ("agents", "Agents", ""), ("tools", "Tools", ""), ("access", "Access from anywhere", ""), ("problems", "Problems", "") })
        {
            var count = Ui.Txt("", "BodyTextBlockStyle", true);
            counts[tag] = count;
            var row = Ui.Row(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis }, count);
            var item = new NavigationViewItem { Tag = tag, Content = row, Icon = new FontIcon { Glyph = glyph }, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, text);
            nav.MenuItems.Add(item);
        }
        nav.FooterMenuItems.Add(new NavigationViewItem { Content = "Check for updates", Icon = new FontIcon { Glyph = "" }, SelectsOnInvoked = false, Tag = "update" });
        var foot = Ui.HStack(8, dot, spin, status);
        foot.Margin = new(18, 4, 16, 12);
        nav.PaneFooter = foot;
        nav.SelectionChanged += (_, e) => { if (e.SelectedItem is NavigationViewItem { Tag: string t }) Go(t); };
        nav.ItemInvoked += async (_, e) => { if (e.InvokedItemContainer?.Tag as string == "update") await CheckUpdates(); };

        header.Children.Add(title);
        header.Children.Add(tools);
        var content = new Grid { RowDefinitions = { new() { Height = GridLength.Auto }, new() { Height = new(1, GridUnitType.Star) } } };
        content.Children.Add(header);
        Grid.SetRow(body, 1);
        content.Children.Add(body);
        nav.Content = content;

        var root = new Grid { RowDefinitions = { new() { Height = GridLength.Auto }, new() { Height = new(1, GridUnitType.Star) } } };
        root.Children.Add(bar);
        Grid.SetRow(nav, 1);
        root.Children.Add(nav);
        Content = root;
        nav.SelectedItem = nav.MenuItems[0];

        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1.5);
        timer.Tick += (_, _) => _ = Poll();
        timer.Start();
        _ = Poll();
    }

    public void Go(string tag)
    {
        welcome = false;
        page = tag;
        var item = nav.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == tag);
        if (!ReferenceEquals(nav.SelectedItem, item)) nav.SelectedItem = item;
        Render(true);
    }

    public void Refresh() => _ = Poll();

    async Task Poll()
    {
        if (busy) { again = true; return; }
        if (!up && stuck) return;
        busy = true;
        try
        {
            if (!up)
            {
                problem = await Api.Ensure();
                up = problem == null;
                stuck = !up;
                if (!up) return;
            }
            M.Pending = (await Api.Get("pending")).A("pending");
            Approvals();
            var (g, a, c, r, t, p) = (Api.Get("games"), Api.Get("agents"), Api.Get("clients"), Api.Get("remote"), Api.Get("tools"), Api.Get("problems"));
            await Task.WhenAll(g, a, c, r, t, p);
            static JsonNode[] Named(JsonNode[] list) { foreach (var x in list.Where(x => x.S("kind") == "link")) x["name"] = "Apps on this computer (setup link)"; return list; }
            (M.Games, M.Agents, M.Clients, M.Remote, M.Tools, M.ToolClients) = (g.Result.A("games"), a.Result.A("agents"), Named(c.Result.A("clients")), r.Result, t.Result.A("tools"), Named(t.Result.A("clients")));
            M.Problems = p.Result.A("problems");
            problem = null;
            if (!polled) welcome = M.Games.Length == 0 && !File.Exists(Welcomed);
            polled = true;
        }
        catch (HttpRequestException) { up = false; }
        catch (TaskCanceledException) { problem = "UDriver is not responding."; }
        catch (Exception e) { problem = e.Message; }
        finally
        {
            busy = false;
            Render();
            if (again) { again = false; _ = Poll(); }
        }
    }

    public void Render(bool force = false)
    {
        var ready = up && polled;
        counts["games"].Text = ready ? M.Games.Length.ToString() : "";
        counts["agents"].Text = ready ? M.Live.Count().ToString() : "";
        counts["access"].Text = ready ? M.Remote.B("on") ? "On" : "Off" : "";
        counts["problems"].Text = ready && M.Problems.Length > 0 ? M.Problems.Length.ToString() : "";
        Footer();
        if (welcome && M.Games.Length > 0) MarkWelcomed();
        var s = screens[page];
        var key = welcome ? "welcome" : !ready ? $"down{problem}{stuck}" : page + "\n" + s.Key(M);
        header.Visibility = ready && !welcome ? Visibility.Visible : Visibility.Collapsed;
        if (force || key != lastKey)
        {
            lastKey = key;
            title.Text = s.Title;
            tools.Content = s.Tools;
            body.Child = welcome ? WelcomeView() : !ready ? DownView() : s.Build(M);
        }
        if (ready && !welcome) s.Update(M);
    }

    void Footer()
    {
        var ready = up && polled;
        var work = working.LastOrDefault() ?? (problem == null && !ready ? "Starting UDriver…" : null);
        dot.Fill = new SolidColorBrush(problem != null ? Colors.IndianRed : Colors.MediumSeaGreen);
        dot.Visibility = work == null ? Visibility.Visible : Visibility.Collapsed;
        spin.IsActive = work != null;
        spin.Visibility = work != null ? Visibility.Visible : Visibility.Collapsed;
        status.Text = work ?? problem ?? "Ready on this computer";
    }

    UIElement DownView()
    {
        if (problem == null || !stuck)
        {
            var wait = Ui.VStack(12, new ProgressRing { IsActive = true, Width = 40, Height = 40 }, Ui.Txt("Starting UDriver…", "BodyTextBlockStyle"));
            wait.HorizontalAlignment = HorizontalAlignment.Center;
            wait.VerticalAlignment = VerticalAlignment.Center;
            return wait;
        }
        var retry = Ui.Btn("Try again", () => { stuck = false; problem = null; Render(); Refresh(); }, true);
        var v = Ui.VStack(16, Ui.Txt("UDriver could not start", "SubtitleTextBlockStyle"), Ui.Txt(problem, "BodyTextBlockStyle", true), retry);
        v.MaxWidth = 460;
        v.HorizontalAlignment = HorizontalAlignment.Center;
        v.VerticalAlignment = VerticalAlignment.Center;
        return v;
    }

    void MarkWelcomed()
    {
        welcome = false;
        Directory.CreateDirectory(Path.GetDirectoryName(Welcomed)!);
        File.WriteAllText(Welcomed, "");
    }

    UIElement WelcomeView()
    {
        var games = (GamesScreen)screens["games"];
        var cards = new[]
        {
            ("", "Add a game", "Drop a built game or a Unity project. UDriver adds a small helper to it."),
            ("", "Connect an agent", "You approve each one. You decide what it may do in every game."),
            ("", "Use from anywhere", "Optional. Off until you turn it on, and closed with one switch."),
        }.Select(c =>
        {
            var card = Ui.CardOf(Ui.VStack(8, new FontIcon { Glyph = c.Item1, FontSize = 24, Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush"), HorizontalAlignment = HorizontalAlignment.Left },
                Ui.Txt(c.Item2, "BodyStrongTextBlockStyle"), Ui.Txt(c.Item3, "BodyTextBlockStyle", true)), 20);
            card.Width = 230;
            return (UIElement)card;
        }).ToArray();
        var add = Ui.Btn("Add your first game", null, true);
        add.Flyout = games.AddMenu();
        var v = Ui.VStack(20,
            new Image { Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "udriver.png"))), Width = 72, Height = 72 },
            Ui.Txt("Welcome to UDriver", "TitleLargeTextBlockStyle"),
            Ui.Txt("Let AI agents see and play your Unity games — with your permission.", "BodyLargeTextBlockStyle", true),
            Ui.HStack(12, cards),
            Ui.HStack(8, add, Ui.Btn("Not now", () => { MarkWelcomed(); Go(page); })));
        foreach (var e in v.Children.OfType<FrameworkElement>()) e.HorizontalAlignment = HorizontalAlignment.Center;
        v.VerticalAlignment = VerticalAlignment.Center;
        v.Padding = new(36);
        var scroll = new ScrollViewer { Content = v, Background = new SolidColorBrush(Colors.Transparent) };
        games.Drop(scroll);
        return scroll;
    }

    public async Task<ContentDialogResult> Dialog(ContentDialog d)
    {
        if (open != null || Content.XamlRoot == null) return ContentDialogResult.None;
        open = d;
        d.XamlRoot = Content.XamlRoot;
        d.Style = Ui.Res<Style>("DefaultContentDialogStyle");
        try { return await d.ShowAsync(); }
        finally { open = null; }
    }

    public Task Say(string title, string text) =>
        Dialog(new ContentDialog { Title = title, Content = Ui.Txt(text), CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close });

    public async Task<bool> Confirm(string title, string text, string yes, string no = "Cancel", bool safe = true, UIElement? extra = null) =>
        await Dialog(new ContentDialog
        {
            Title = title, Content = Ui.VStack(12, Ui.Txt(text), extra), PrimaryButtonText = yes, CloseButtonText = no,
            DefaultButton = safe ? ContentDialogButton.Primary : ContentDialogButton.Close,
        }) == ContentDialogResult.Primary;

    /// Runs a server action, shows its error if any, then refreshes. Meanwhile the clicked button is disabled
    /// and says what is happening, and the sidebar shows a spinner with the same words.
    public async Task<JsonObject?> Act(string path, params (string, string)[] form)
    {
        var text = Working(path);
        var b = Ui.Pressed;
        Ui.Pressed = null;
        var content = b?.Content;
        if (b != null)
        {
            b.IsEnabled = false;
            b.Content = Ui.HStack(8, new ProgressRing { IsActive = true, Width = 16, Height = 16, MinWidth = 16, MinHeight = 16 }, Ui.Txt(text));
        }
        working.Add(text);
        Footer();
        try { return await Api.Post(path, form); }
        catch (Exception e) { await Say("That did not work", e.Message); return null; }
        finally
        {
            await Poll();
            working.Remove(text);
            Footer();
            if (b != null) { b.Content = content; b.IsEnabled = true; }
        }
    }

    static string Working(string path) => path switch
    {
        "games/add" => "Adding the game…",
        "games/start" => "Launching the game…",
        "games/stop" => "Stopping the game…",
        "games/remove" => "Removing the game…",
        "agents/disconnect" => "Disconnecting the agent…",
        "clients/revoke" => "Removing the agent…",
        "pending/approve" => "Allowing the agent…",
        "pending/deny" => "Denying the agent…",
        "remote/install" => "Downloading the helper…",
        "remote/login" => "Signing in to Cloudflare…",
        "remote/on" => "Turning on Access from Anywhere…",
        "remote/off" => "Turning off Access from Anywhere…",
        "remote/signout" => "Signing out of Cloudflare…",
        "local-clients" => "Creating the setup…",
        "problems/dismiss" => "Dismissing…",
        _ => "Saving…",
    };

    void Approvals()
    {
        if (approval != null)
        {
            if (M.Pending.All(p => p.S("id") != approvalId)) approval.Hide();
            return;
        }
        if (open == null && M.Pending.FirstOrDefault() is { } p) _ = Approve(p);
    }

    async Task Approve(JsonNode p)
    {
        var id = p.S("id");
        var number = Ui.Txt(p.S("check"), "TitleLargeTextBlockStyle");
        number.FontSize = 48;
        number.HorizontalAlignment = HorizontalAlignment.Center;
        var to = Ui.Txt("Access goes to " + (p.S("to") is { Length: > 0 } t ? t : "this computer"), "CaptionTextBlockStyle", true);
        to.HorizontalAlignment = HorizontalAlignment.Center;
        var box = new Border { Background = Ui.Brush("SubtleFillColorSecondaryBrush"), CornerRadius = new(8), Padding = new(16, 12, 16, 16), Child = Ui.VStack(2, number, to) };
        var app = p.S("app") is { Length: > 0 } n ? n : "An agent";
        var d = new ContentDialog
        {
            Title = $"Allow {app}?",
            Content = Ui.VStack(16, Ui.Txt("It wants to use your games. Allow only if the sign-in page shows the same number."), box),
            PrimaryButtonText = "Allow", SecondaryButtonText = "Deny", PrimaryButtonStyle = Ui.Res<Style>("AccentButtonStyle"),
        };
        approval = d;
        approvalId = id;
        Activate();
        var r = await Dialog(d);
        approval = null;
        if (r == ContentDialogResult.Primary) await Act("pending/approve", ("id", id));
        else if (r == ContentDialogResult.Secondary) await Act("pending/deny", ("id", id));
    }

    async Task CheckUpdates()
    {
        JsonObject u;
        working.Add("Checking for updates…");
        Footer();
        try { u = await Api.Get("update"); }
        catch (Exception e) { await Say("Could not check for updates.", e.Message); return; }
        finally { working.Remove("Checking for updates…"); Footer(); }
        var current = u.S("current");
        if (!u.B("installed")) await Say("Updates are not available for this copy.", "Only a copy installed from a UDriver download can update itself.");
        else if (u.S("error") is { Length: > 0 } err) await Say("Could not check for updates.", err);
        else if (!u.B("available")) await Say("UDriver is up to date.", $"You have version {current}.");
        else if (await Confirm($"UDriver {u.S("version")} is available.", $"You have version {current}. UDriver will download the update, close, and open again.", "Update now", "Later"))
        {
            working.Add("Downloading the update…");
            Footer();
            try { await Api.Json("update/apply", new() { ["restart"] = "true" }, 600); Application.Current.Exit(); }
            catch (Exception e) { await Say("The update did not finish.", e.Message); }
        }
    }
}
