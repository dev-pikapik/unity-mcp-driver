using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UDriver.App;

static class Program
{
    static int Main(string[] args)
    {
        bool headless = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        if (args.Length > 0 || headless) return Forward(args.Length > 0 ? args : ["tui"]);
        var app = Adw.Application.New("com.udriver.app", Gio.ApplicationFlags.DefaultFlags);
        Shell? shell = null;
        app.OnActivate += (_, _) => { shell ??= new Shell(app); shell.Window.Present(); };
        app.OnShutdown += (_, _) => Api.StopStarted();
        return app.RunWithSynchronizationContext(null);
    }

    /// Terminal use: `UDriver status`, `UDriver stdio` and the rest go to the server.
    static int Forward(string[] args)
    {
        if (Api.Command() is not var (file, pre))
        {
            Console.Error.WriteLine("UDriver is not installed on this computer yet. Run the UDriver installer once.");
            return 1;
        }
        var psi = new ProcessStartInfo(file);
        foreach (string a in pre.Concat(args)) psi.ArgumentList.Add(a);
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}

sealed class Model
{
    public List<JsonNode> Games = [], Agents = [], Clients = [], Tools = [], Problems = [];
    public JsonNode Remote = new JsonObject();
    public bool Up;
    public string? Problem;
}

enum Section { Games, Agents, Tools, Access, Problems }

sealed class Shell
{
    public readonly Adw.ApplicationWindow Window;
    public readonly Model Model = new();
    readonly Adw.Application app;
    readonly Adw.ToastOverlay toasts = Adw.ToastOverlay.New();
    readonly Adw.NavigationSplitView split = Adw.NavigationSplitView.New();
    readonly Adw.NavigationPage contentPage;
    readonly Gtk.Stack stack = Gtk.Stack.New();
    readonly Gtk.ListBox nav = Gtk.ListBox.New();
    readonly Gtk.Label[] counts = new Gtk.Label[5];
    readonly Gtk.Label status = Gtk.Label.New("Starting UDriver…");
    readonly Gtk.Box dot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    readonly Gtk.Spinner spin = Gtk.Spinner.New();
    readonly Adw.StatusPage loading = Adw.StatusPage.New();
    readonly List<string> working = [];
    /// The button whose click is running; Act shows it as busy. Confirm and Ask carry it over their dialog.
    public static Gtk.Button? Pressed;
    bool loaded;
    readonly Screen[] screens;
    Section section;
    Gtk.Widget? welcome;
    Adw.AlertDialog? approval;
    string? approvalId;
    bool quitting;

    static readonly string Welcomed = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Api.Home + "/.config", "udriver", "welcomed");

    public Shell(Adw.Application app)
    {
        this.app = app;
        Style();
        screens = [new GamesScreen(this), new AgentsScreen(this), new ToolsScreen(this), new AccessScreen(this), new ProblemsScreen(this)];
        foreach (var s in screens) stack.AddNamed(s.Root, s.Title);
        var wait = Gtk.Spinner.New();
        wait.Spinning = true;
        wait.SetSizeRequest(32, 32);
        loading.Title = "Starting UDriver…";
        loading.Child = wait;
        var waitView = Adw.ToolbarView.New();
        waitView.AddTopBar(Adw.HeaderBar.New());
        waitView.Content = loading;
        stack.AddNamed(waitView, "loading");

        Window = Adw.ApplicationWindow.New(app);
        Window.Title = "UDriver";
        Window.SetDefaultSize(1060, 720);
        Window.SetSizeRequest(360, 480);

        contentPage = Adw.NavigationPage.New(stack, "Games");
        split.SetSidebar(Adw.NavigationPage.New(Sidebar(), "UDriver"));
        split.SetContent(contentPage);
        split.MinSidebarWidth = 220;
        toasts.SetChild(split);
        Window.SetContent(toasts);

        var bp = Adw.Breakpoint.New(Adw.BreakpointCondition.Parse("max-width: 640sp"));
        bp.AddSetter(split, "collapsed", new GObject.Value(true));
        Window.AddBreakpoint(bp);

        Actions();
        Select(Section.Games);
        Start();
    }

    Gtk.Widget Sidebar()
    {
        var menu = Gio.Menu.New();
        menu.Append("Add Game…", "app.add");
        menu.Append("Check for Updates…", "app.update");
        menu.Append("About UDriver", "app.about");
        menu.Append("Quit", "app.quit");
        var more = Gtk.MenuButton.New();
        more.IconName = "open-menu-symbolic";
        more.TooltipText = "Main Menu";
        more.MenuModel = menu;
        more.Primary = true;

        var title = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        title.Append(Logo(22));
        var name = Gtk.Label.New("UDriver");
        name.AddCssClass("heading");
        title.Append(name);
        var header = Adw.HeaderBar.New();
        header.TitleWidget = title;
        header.PackEnd(more);

        nav.AddCssClass("navigation-sidebar");
        (string Icon, string Name)[] items = [("input-gaming-symbolic", "Games"), ("system-users-symbolic", "Agents"),
            ("applications-system-symbolic", "Tools"), ("network-workgroup-symbolic", "Access from Anywhere"), ("dialog-warning-symbolic", "Problems")];
        for (int i = 0; i < items.Length; i++)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            row.Append(Gtk.Image.NewFromIconName(items[i].Icon));
            var label = Gtk.Label.New(items[i].Name);
            label.Xalign = 0;
            label.Hexpand = true;
            label.Ellipsize = Pango.EllipsizeMode.End;
            row.Append(label);
            counts[i] = Gtk.Label.New("");
            counts[i].AddCssClass("dim-label");
            counts[i].AddCssClass("numeric");
            row.Append(counts[i]);
            nav.Append(row);
        }
        nav.OnRowActivated += (_, e) => { Select((Section)e.Row.GetIndex()); split.ShowContent = true; };

        dot.AddCssClass("dot");
        dot.Valign = Gtk.Align.Center;
        status.Xalign = 0;
        status.Wrap = true;
        status.AddCssClass("caption");
        status.AddCssClass("dim-label");
        var foot = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        foot.AddCssClass("status");
        foot.Append(dot);
        foot.Append(spin);
        foot.Append(status);

        var scroll = Gtk.ScrolledWindow.New();
        scroll.Child = nav;
        scroll.Vexpand = true;
        scroll.HscrollbarPolicy = Gtk.PolicyType.Never;
        var body = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        body.Append(scroll);
        body.Append(foot);
        var view = Adw.ToolbarView.New();
        view.AddTopBar(header);
        view.Content = body;
        return view;
    }

    void Actions()
    {
        void Add(string name, Action run, params string[] keys)
        {
            var a = Gio.SimpleAction.New(name, null);
            a.OnActivate += (_, _) => run();
            app.AddAction(a);
            if (keys.Length > 0) app.SetAccelsForAction("app." + name, keys);
        }
        Add("add", ChooseGame, "<Control>o");
        Add("update", CheckUpdates);
        Add("about", About);
        Add("quit", () => app.Quit(), "<Control>q");
        for (int i = 0; i < 5; i++) { var s = (Section)i; Add("go" + i, () => Select(s), $"<Control>{i + 1}"); }
    }

    public void Select(Section s)
    {
        section = s;
        var screen = screens[(int)s];
        stack.VisibleChildName = loaded ? screen.Title : "loading";
        contentPage.Title = screen.Title;
        nav.SelectRow(nav.GetRowAtIndex((int)s));
        screen.Show(Model);
    }

    public static Gtk.Image Logo(int size)
    {
        var img = Gtk.Image.NewFromPaintable(Texture(LogoBytes()));
        img.PixelSize = size;
        return img;
    }

    public static Gdk.Texture Texture(byte[] png) => Gdk.Texture.NewFromBytes(GLib.Bytes.New(png));

    // MARK: data

    async void Start()
    {
        if (await Api.EnsureServer() is { } problem) { Model.Problem = problem; Render(); }
        while (!quitting)
        {
            await Poll();
            await Task.Delay(1500);
        }
    }

    async Task Poll()
    {
        try { Approvals((await Api.Json("/panel/api/pending")).L("pending")); } catch { }
        await Refresh();
    }

    public async Task Refresh()
    {
        try
        {
            var g = Api.Json("/panel/api/games");
            var a = Api.Json("/panel/api/agents");
            var c = Api.Json("/panel/api/clients");
            var r = Api.Json("/panel/api/remote");
            var t = Api.Json("/panel/api/tools");
            var p = Api.Json("/panel/api/problems");
            await Task.WhenAll(g, a, c, r, t, p);
            Model.Games = g.Result.L("games");
            Model.Agents = a.Result.L("agents");
            Model.Clients = c.Result.L("clients");
            Model.Remote = r.Result;
            Model.Tools = t.Result.L("tools");
            Model.Problems = p.Result.L("problems");
            Model.Up = true;
            Model.Problem = null;
        }
        catch (Exception e)
        {
            Model.Up = await Api.Running();
            if (!Model.Up && !quitting) await Api.EnsureServer();
            Model.Problem = Model.Up ? Message(e) : "UDriver is not running";
        }
        Render();
    }

    void Render()
    {
        if (quitting) return;
        counts[0].Label_ = Model.Games.Count > 0 ? Model.Games.Count.ToString() : "";
        int live = Model.Agents.Count(a => !a.B("disconnected"));
        counts[1].Label_ = live > 0 ? live.ToString() : "";
        counts[3].Label_ = Model.Remote.B("on") ? "On" : "Off";
        counts[4].Label_ = Model.Problems.Count > 0 ? Model.Problems.Count.ToString() : "";
        Footer();
        if (!loaded && Model.Up) { loaded = true; stack.VisibleChildName = screens[(int)section].Title; }
        else if (!loaded && Model.Problem != null) { loading.Title = "UDriver could not start"; loading.Description = GLib.Markup.EscapeText(Model.Problem); loading.Child = null; }
        screens[(int)section].Show(Model);
        WelcomeIfNeeded();
    }

    void Footer()
    {
        string? work = working.LastOrDefault() ?? (!Model.Up && Model.Problem == null ? "Starting UDriver…" : null);
        status.Label_ = work ?? Model.Problem ?? "Ready on this computer";
        foreach (string c in new[] { "ok", "warn", "bad" }) dot.RemoveCssClass(c);
        dot.AddCssClass(!Model.Up ? "bad" : Model.Problem != null ? "warn" : "ok");
        dot.Visible = work == null;
        spin.Visible = spin.Spinning = work != null;
    }

    static string Working(string path) => path.Replace("/panel/api/", "") switch
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

    static string Message(Exception e) => e switch
    {
        ServerError => e.Message,
        TaskCanceledException => "UDriver took too long to answer.",
        HttpRequestException => "UDriver is not running",
        _ => e.Message,
    };

    /// Runs a server action, shows its error if any, then refreshes. Meanwhile the clicked button is disabled
    /// and says what is happening, and the sidebar shows a spinner with the same words.
    public async void Act(string path, Dictionary<string, string>? form = null, Action<JsonNode>? then = null, int timeout = 20)
    {
        string text = Working(path);
        var b = Pressed;
        Pressed = null;
        string? label = b?.Label;
        if (b != null)
        {
            var s = Gtk.Spinner.New();
            s.Spinning = true;
            var busy = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            busy.Append(s);
            busy.Append(Gtk.Label.New(text));
            b.Sensitive = false;
            b.Child = busy;
        }
        working.Add(text);
        Footer();
        try { var j = await Api.Json(path, form ?? [], timeout); then?.Invoke(j); }
        catch (Exception e) { Alert(Message(e)); }
        await Refresh();
        working.Remove(text);
        Footer();
        if (b != null) { b.Label = label; b.Sensitive = true; }
    }

    // MARK: dialogs

    public void Alert(string heading, string? body = null)
    {
        var d = Adw.AlertDialog.New(heading, body);
        d.AddResponse("ok", "OK");
        d.Present(Window);
    }

    public void Toast(string text) => toasts.AddToast(Adw.Toast.New(text));

    public void Confirm(string heading, string body, string action, Action yes, Action? no = null, bool destructive = true)
    {
        var d = Adw.AlertDialog.New(heading, body);
        d.AddResponse("cancel", "Cancel");
        d.AddResponse("go", action);
        d.SetResponseAppearance("go", destructive ? Adw.ResponseAppearance.Destructive : Adw.ResponseAppearance.Suggested);
        d.SetCloseResponse("cancel");
        var p = Pressed;
        d.OnResponse += (_, e) => { Pressed = p; if (e.Response == "go") yes(); else no?.Invoke(); Pressed = null; };
        d.Present(Window);
    }

    public void Ask(string heading, string body, string placeholder, string action, Action<string> done, Action? no = null)
    {
        var d = Adw.AlertDialog.New(heading, body);
        var entry = Adw.EntryRow.New();
        entry.Title = placeholder;
        var list = Gtk.ListBox.New();
        list.AddCssClass("boxed-list");
        list.SelectionMode = Gtk.SelectionMode.None;
        list.Append(entry);
        d.ExtraChild = list;
        d.AddResponse("cancel", "Cancel");
        d.AddResponse("go", action);
        d.SetResponseAppearance("go", Adw.ResponseAppearance.Suggested);
        d.SetDefaultResponse("go");
        d.SetCloseResponse("cancel");
        bool went = false;
        var p = Pressed;
        entry.OnEntryActivated += (_, _) => { went = true; d.ForceClose(); Pressed = p; done(entry.GetText()); Pressed = null; };
        d.OnResponse += (_, e) => { if (went) return; Pressed = p; if (e.Response == "go") done(entry.GetText()); else no?.Invoke(); Pressed = null; };
        d.Present(Window);
        entry.GrabFocus();
    }

    public void Copy(string text, string? toast = "Copied")
    {
        Window.GetClipboard().SetText(text);
        if (toast != null) Toast(toast);
    }

    public void Open(string uri)
    {
        try { Gtk.UriLauncher.New(uri).LaunchAsync(Window).ContinueWith(_ => { }); }
        catch { Alert("Could not open it.", uri); }
    }

    // MARK: approvals

    void Approvals(List<JsonNode> pending)
    {
        if (approvalId != null && pending.All(p => p.S("id") != approvalId))
        {
            var d = approval;
            approval = null;
            approvalId = null;
            d?.ForceClose();
        }
        if (approval == null && pending.FirstOrDefault() is { } next) Approve(next);
    }

    void Approve(JsonNode p)
    {
        string id = p.S("id");
        string who = p.S("app") is { Length: > 0 } a ? a : "An agent";
        var d = Adw.AlertDialog.New(who + " wants to use your games", null);
        var number = Gtk.Label.New(p.S("check"));
        number.AddCssClass("bignum");
        var hint = Gtk.Label.New("Allow only if the sign-in page shows the same number.");
        hint.Wrap = true;
        hint.Justify = Gtk.Justification.Center;
        var to = Gtk.Label.New("Access goes to " + (p.S("to") is { Length: > 0 } t ? t : "this computer"));
        to.Wrap = true;
        to.Justify = Gtk.Justification.Center;
        to.AddCssClass("dim-label");
        to.AddCssClass("caption");
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        box.Append(number);
        box.Append(hint);
        box.Append(to);
        d.ExtraChild = box;
        d.AddResponse("deny", "Deny");
        d.AddResponse("allow", "Allow");
        d.SetResponseAppearance("allow", Adw.ResponseAppearance.Suggested);
        d.SetCloseResponse("deny");
        d.OnResponse += (_, e) =>
        {
            if (approvalId != id) return;
            approval = null;
            approvalId = null;
            Act(e.Response == "allow" ? "/panel/api/pending/approve" : "/panel/api/pending/deny", new() { ["id"] = id });
        };
        approval = d;
        approvalId = id;
        if (welcome != null) CloseWelcome();
        Window.Present();
        d.Present(Window);
    }

    // MARK: games

    public void ChooseGame()
    {
        var d = Adw.AlertDialog.New("Add a game", "Choose a built game or a Unity project folder. UDriver adds a small helper to it.");
        d.AddResponse("cancel", "Cancel");
        d.AddResponse("project", "Unity Project…");
        d.AddResponse("build", "Built Game…");
        d.SetResponseAppearance("build", Adw.ResponseAppearance.Suggested);
        d.SetCloseResponse("cancel");
        d.OnResponse += (_, e) => { if (e.Response != "cancel") PickGame(e.Response); };
        d.Present(Window);
    }

    async void PickGame(string pick)
    {
        var files = Gtk.FileDialog.New();
        files.Title = pick == "build" ? "Choose a built game" : "Choose a Unity project folder";
        files.AcceptLabel = "Add";
        try
        {
            var f = pick == "build" ? await files.OpenAsync(Window) : await files.SelectFolderAsync(Window);
            if (f?.GetPath() is { } path) AddGame(path);
        }
        catch { }
    }

    public void AddGame(string path, Action? done = null) => Act("/panel/api/games/add", new() { ["path"] = path }, j =>
    {
        if (j.S("warning") is { Length: > 0 } w) Alert("Game added", w);
        done?.Invoke();
    });

    // MARK: welcome

    void WelcomeIfNeeded()
    {
        if (welcome != null && Model.Games.Count > 0) { Mark(); CloseWelcome(); }
        if (welcome == null && Model.Up && Model.Games.Count == 0 && approval == null && !File.Exists(Welcomed)) ShowWelcome();
    }

    static void Mark()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Welcomed)!);
        File.WriteAllText(Welcomed, "");
    }

    void CloseWelcome()
    {
        welcome = null;
        Window.SetContent(toasts);
    }

    void ShowWelcome()
    {
        var page = Adw.StatusPage.New();
        page.Paintable = Texture(LogoBytes());
        page.Title = "Welcome to UDriver";
        page.Description = "Let AI agents see and play your Unity games — with your permission.";
        page.AddCssClass("welcome");
        var cards = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        cards.Homogeneous = true;
        foreach (var (icon, tint, title, text) in new[]
        {
            ("input-gaming-symbolic", "green", "Add a game", "Drop a built game or a Unity project. UDriver adds a small helper to it."),
            ("system-users-symbolic", "blue", "Connect an agent", "You approve each one. You decide what it may do in every game."),
            ("network-workgroup-symbolic", "violet", "Use from anywhere", "Optional. Off until you turn it on, and closed with one switch."),
        })
        {
            var card = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
            card.AddCssClass("card");
            card.AddCssClass("intro");
            card.Append(Tile(icon, tint));
            var h = Gtk.Label.New(title);
            h.AddCssClass("heading");
            h.Xalign = 0;
            var b = Gtk.Label.New(text);
            b.Wrap = true;
            b.Xalign = 0;
            b.AddCssClass("dim-label");
            card.Append(h);
            card.Append(b);
            cards.Append(card);
        }
        var go = Gtk.Button.NewWithLabel("Add Your First Game");
        go.AddCssClass("pill");
        go.AddCssClass("suggested-action");
        go.OnClicked += (_, _) => ChooseGame();
        var later = Gtk.Button.NewWithLabel("Not Now");
        later.AddCssClass("pill");
        later.OnClicked += (_, _) => { Mark(); CloseWelcome(); };
        var buttons = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        buttons.Halign = Gtk.Align.Center;
        buttons.Append(go);
        buttons.Append(later);
        var body = Gtk.Box.New(Gtk.Orientation.Vertical, 32);
        body.Append(cards);
        body.Append(buttons);
        var clamp = Adw.Clamp.New();
        clamp.MaximumSize = 760;
        clamp.Child = body;
        page.Child = clamp;
        var view = Adw.ToolbarView.New();
        var header = Adw.HeaderBar.New();
        header.ShowTitle = false;
        view.AddTopBar(header);
        view.Content = page;
        welcome = view;
        Window.SetContent(view);
    }

    static byte[] LogoBytes()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png")!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    public static Gtk.Widget Tile(string icon, string tint)
    {
        var img = Gtk.Image.NewFromIconName(icon);
        img.AddCssClass("tile");
        img.AddCssClass(tint);
        img.Halign = Gtk.Align.Start;
        img.Valign = Gtk.Align.Center;
        return img;
    }

    // MARK: updates

    async void CheckUpdates()
    {
        JsonNode u;
        working.Add("Checking for updates…");
        Footer();
        try { u = await Api.Json("/panel/api/update"); }
        catch (Exception e) { Alert("Could not check for updates.", Message(e)); return; }
        finally { working.Remove("Checking for updates…"); Footer(); }
        if (!u.B("installed")) { Alert("Updates are not available for this copy.", "Only a copy installed from a UDriver download can update itself."); return; }
        if (u.S("error") is { Length: > 0 } err) { Alert("Could not check for updates.", err); return; }
        if (!u.B("available") || u.S("version") is not { Length: > 0 } next) { Alert("UDriver is up to date.", $"You have version {u.S("current")}."); return; }
        Confirm($"UDriver {next} is available.", $"You have version {u.S("current")}. UDriver will download the update, close, and open again.", "Update Now", async () =>
        {
            var wait = Adw.AlertDialog.New("Updating UDriver…", "This takes a minute. UDriver opens again when it is done.");
            var spin = Gtk.Spinner.New();
            spin.Spinning = true;
            spin.SetSizeRequest(32, 32);
            wait.ExtraChild = spin;
            wait.Present(Window);
            try { await Api.Json("/panel/api/update/apply", new() { ["restart"] = "true" }, 600); }
            catch (Exception e) { wait.ForceClose(); Alert("Could not update UDriver.", Message(e)); return; }
            quitting = true;
            for (int i = 0; i < 50 && await Api.Running(); i++) await Task.Delay(200);
            app.Quit();
        }, destructive: false);
    }

    void About()
    {
        var a = Adw.AboutDialog.New();
        a.ApplicationName = "UDriver";
        a.ApplicationIcon = "com.udriver.app";
        a.DeveloperName = "Vladislav Pesotchi";
        a.Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
        a.Comments = "Let AI agents see and play your Unity games — with your permission.";
        a.Copyright = "© Vladislav Pesotchi";
        a.Present(Window);
    }

    // MARK: style

    static void Style()
    {
        const string css = """
            .dot { min-width: 8px; min-height: 8px; border-radius: 4px; background: alpha(currentColor, .3); }
            .dot.ok { background: @success_color; } .dot.warn { background: @warning_color; } .dot.bad { background: @error_color; }
            .status { padding: 10px 14px; }
            .tile { min-width: 36px; min-height: 36px; border-radius: 9px; color: white; -gtk-icon-size: 18px; }
            .tile.green { background: #2ec27e; } .tile.blue { background: #3584e4; } .tile.violet { background: #9141ac; } .tile.gray { background: #77767b; }
            .intro { padding: 16px; }
            .welcome .title { font-size: 26pt; }
            .bignum { font-size: 40pt; font-weight: 800; font-feature-settings: "tnum"; letter-spacing: 4px; }
            .mono { font-family: monospace; }
            .chip { padding: 2px 10px; border-radius: 99px; background: alpha(currentColor, .1); font-size: 9pt; font-weight: 600; }
            .chip.accent { background: alpha(@accent_bg_color, .18); color: @accent_color; }
            .live { padding: 2px 8px; margin: 8px; border-radius: 6px; background: #e01b24; color: white; font-size: 8pt; font-weight: 800; }
            flowbox.games > flowboxchild { padding: 0; border-radius: 14px; outline: none; }
            flowbox.games > flowboxchild:selected { background: none; box-shadow: 0 0 0 3px @accent_bg_color; }
            .game { border-radius: 14px; }
            .shot { border-radius: 12px 12px 0 0; }
            .scene0 { background: linear-gradient(160deg, #1c71d8, #26a269); } .scene1 { background: linear-gradient(160deg, #813d9c, #e66100); }
            .scene2 { background: linear-gradient(160deg, #241f31, #1a5fb4); } .scene3 { background: linear-gradient(160deg, #c64600, #f5c211); }
            .scene4 { background: linear-gradient(160deg, #26a269, #1c71d8); } .scene5 { background: linear-gradient(160deg, #3d3846, #c01c28); }
            .scene image { color: alpha(white, .55); -gtk-icon-size: 40px; }
            """;
        var p = Gtk.CssProvider.New();
        p.LoadFromString(css);
        Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, p, 600);
    }
}
