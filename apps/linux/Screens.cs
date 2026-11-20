using System.Text.Json;
using System.Text.Json.Nodes;
using QRCoder;
using static UDriver.App.JsonExt;

namespace UDriver.App;

/// A content page with its own header; rebuilt only when the data behind it changes.
abstract class Screen
{
    protected readonly Shell Shell;
    protected readonly Adw.ToolbarView View = Adw.ToolbarView.New();
    protected readonly Adw.HeaderBar Header = Adw.HeaderBar.New();
    protected Model M = new();
    string last = "";

    protected Screen(Shell shell)
    {
        Shell = shell;
        View.AddTopBar(Header);
    }

    public Gtk.Widget Root => View;
    public abstract string Title { get; }
    public abstract void Show(Model m);
    protected bool Changed(string key) { if (key == last) return false; last = key; return true; }
    protected void Forget() => last = "";

    protected static Gtk.Button Button(string label, Action click, string? style = null)
    {
        var b = Gtk.Button.NewWithLabel(label);
        b.Valign = Gtk.Align.Center;
        if (style != null) b.AddCssClass(style);
        b.OnClicked += (_, _) => click();
        return b;
    }

    protected static Gtk.Label Text(string text, params string[] css)
    {
        var l = Gtk.Label.New(text);
        l.Wrap = true;
        l.Xalign = 0;
        foreach (string c in css) l.AddCssClass(c);
        return l;
    }

    protected static Adw.ActionRow Row(string title, string? subtitle = null)
    {
        var r = Adw.ActionRow.New();
        r.Title = GLib.Markup.EscapeText(title);
        if (subtitle != null) r.Subtitle = GLib.Markup.EscapeText(subtitle);
        return r;
    }

    protected static Adw.PreferencesGroup Group(string? title = null, string? description = null)
    {
        var g = Adw.PreferencesGroup.New();
        if (title != null) g.Title = GLib.Markup.EscapeText(title);
        if (description != null) g.Description = GLib.Markup.EscapeText(description);
        return g;
    }
}

/// A screen made of preference groups that are thrown away and rebuilt together.
abstract class ListScreen : Screen
{
    protected readonly Adw.PreferencesPage Page = Adw.PreferencesPage.New();
    readonly List<Adw.PreferencesGroup> groups = [];

    protected ListScreen(Shell shell) : base(shell) => View.Content = Page;

    protected void Fill(IEnumerable<Adw.PreferencesGroup> next)
    {
        foreach (var g in groups) Page.Remove(g);
        groups.Clear();
        foreach (var g in next) { Page.Add(g); groups.Add(g); }
    }
}

sealed class GamesScreen : Screen
{
    public override string Title => "Games";
    static readonly (string Id, string Label, string Hint)[] Levels =
    [
        ("watch", "Watch", "Look only: see the screen and read the scene. Nothing is pressed or changed."),
        ("play", "Play", "Look, press buttons and play. Files stay untouched."),
        ("full", "Everything", "Look, play and also change scenes, settings and project files."),
    ];

    readonly Gtk.FlowBox grid = Gtk.FlowBox.New();
    readonly Gtk.Box body = Gtk.Box.New(Gtk.Orientation.Vertical, 24);
    readonly Gtk.Stack pages = Gtk.Stack.New();
    readonly Gtk.SearchEntry search = Gtk.SearchEntry.New();
    readonly Dictionary<string, Gtk.Picture> pictures = [];
    readonly Dictionary<string, Gdk.Texture> previews = [];
    Adw.PreferencesGroup? details;
    string? selected;
    string detailKey = "";
    bool fetching;

    public GamesScreen(Shell shell) : base(shell)
    {
        var add = Gtk.Button.NewFromIconName("list-add-symbolic");
        add.TooltipText = "Add Game";
        add.ActionName = "app.add";
        Header.PackStart(add);
        var find = Gtk.ToggleButton.New();
        find.IconName = "system-search-symbolic";
        find.TooltipText = "Search";
        Header.PackEnd(find);

        var bar = Gtk.SearchBar.New();
        var clamp = Adw.Clamp.New();
        clamp.MaximumSize = 400;
        clamp.Child = search;
        bar.Child = clamp;
        bar.ConnectEntry(search);
        bar.KeyCaptureWidget = View;
        find.BindProperty("active", bar, "search-mode-enabled", GObject.BindingFlags.Bidirectional);
        search.PlaceholderText = "Search games";
        search.OnSearchChanged += (_, _) => Show(M);
        View.AddTopBar(bar);

        grid.AddCssClass("games");
        grid.SelectionMode = Gtk.SelectionMode.Single;
        grid.Homogeneous = true;
        grid.ColumnSpacing = 18;
        grid.RowSpacing = 18;
        grid.MinChildrenPerLine = 1;
        grid.MaxChildrenPerLine = 4;
        grid.Valign = Gtk.Align.Start;
        grid.Vexpand = false;
        grid.OnChildActivated += (_, e) => { selected = e.Child.GetName(); Details(); };

        body.Append(grid);
        body.MarginTop = body.MarginBottom = 24;
        body.MarginStart = body.MarginEnd = 12;
        var wide = Adw.Clamp.New();
        wide.MaximumSize = 1100;
        wide.Child = body;
        var scroll = Gtk.ScrolledWindow.New();
        scroll.Child = wide;
        scroll.HscrollbarPolicy = Gtk.PolicyType.Never;

        var empty = Adw.StatusPage.New();
        empty.IconName = "input-gaming-symbolic";
        empty.Title = "No games yet";
        empty.Description = "Add a built game or a Unity project. Nothing inside it changes.";
        var first = Button("Add Game", () => Shell.ChooseGame(), "suggested-action");
        first.AddCssClass("pill");
        first.Halign = Gtk.Align.Center;
        empty.Child = first;
        var none = Adw.StatusPage.New();
        none.IconName = "system-search-symbolic";
        none.Title = "No games found";
        none.Description = "Try a different name.";

        pages.AddNamed(scroll, "list");
        pages.AddNamed(empty, "empty");
        pages.AddNamed(none, "none");
        View.Content = pages;
    }

    static bool Live(JsonNode g) => g.S("state") != "not running";
    string? Player(JsonNode g) => M.Agents.FirstOrDefault(a => !a.B("disconnected") && (a.S("target") == g.S("name") || a.S("target") == g.S("id")))?.S("label") is { Length: > 0 } p ? p : null;
    static string Sentence(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    string Meta(JsonNode g)
    {
        string kind = g.S("kind") == "project" ? "Project" : "Build";
        if (!g.B("exists", true)) return kind + " · Missing on disk";
        if (Player(g) is { } p) return $"{kind} · {p} is playing";
        return kind + " · " + Sentence(g.S("state")) + (g.S("access") == "none" ? " · Not allowed" : "");
    }

    static string Place(string path) => path.StartsWith(Api.Home + "/") ? "~" + path[Api.Home.Length..] : path;

    public override void Show(Model m)
    {
        M = m;
        string q = search.GetText().Trim();
        var shown = m.Games.Where(g => q.Length == 0 || g.S("name").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        pages.VisibleChildName = m.Games.Count == 0 ? "empty" : shown.Count == 0 ? "none" : "list";
        if (!shown.Any(g => g.S("id") == selected)) selected = shown.FirstOrDefault()?.S("id");
        if (Changed(Key(q, Key(shown.Select(g => Key(g.S("id"), g.S("name"), g.S("kind"), Meta(g), Live(g))).ToArray())))) Cards(shown);
        Details();
        Previews(shown);
    }

    void Cards(List<JsonNode> shown)
    {
        grid.RemoveAll();
        pictures.Clear();
        foreach (var g in shown) grid.Append(Card(g));
        for (int i = 0; grid.GetChildAtIndex(i) is { } c; i++)
            if (c.GetName() == selected) grid.SelectChild(c);
    }

    Gtk.FlowBoxChild Card(JsonNode g)
    {
        string id = g.S("id"), name = g.S("name");
        var scene = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        scene.AddCssClass("scene");
        scene.AddCssClass("scene" + name.Sum(ch => ch) % 6);
        scene.SetSizeRequest(220, 140);
        var icon = Gtk.Image.NewFromIconName(g.S("kind") == "project" ? "folder-symbolic" : "input-gaming-symbolic");
        icon.Vexpand = true;
        scene.Append(icon);
        var pic = Gtk.Picture.New();
        pic.ContentFit = Gtk.ContentFit.Cover;
        pic.Visible = false;
        if (previews.TryGetValue(id, out var t)) { pic.Paintable = t; pic.Visible = true; }
        pictures[id] = pic;
        var top = Gtk.Overlay.New();
        top.Child = scene;
        top.AddOverlay(pic);
        top.AddCssClass("shot");
        top.Overflow = Gtk.Overflow.Hidden;
        if (Live(g))
        {
            var live = Gtk.Label.New("LIVE");
            live.AddCssClass("live");
            live.Halign = Gtk.Align.Start;
            live.Valign = Gtk.Align.Start;
            top.AddOverlay(live);
        }
        var title = Gtk.Label.New(name);
        title.AddCssClass("heading");
        title.Xalign = 0;
        title.Ellipsize = Pango.EllipsizeMode.End;
        var meta = Gtk.Label.New(Meta(g));
        meta.AddCssClass("caption");
        meta.AddCssClass("dim-label");
        meta.Xalign = 0;
        meta.Ellipsize = Pango.EllipsizeMode.End;
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        text.MarginTop = text.MarginBottom = 10;
        text.MarginStart = text.MarginEnd = 12;
        text.Append(title);
        text.Append(meta);
        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        card.AddCssClass("card");
        card.AddCssClass("game");
        card.Overflow = Gtk.Overflow.Hidden;
        card.Append(top);
        card.Append(text);
        var child = Gtk.FlowBoxChild.New();
        child.Child = card;
        child.SetName(id);
        child.TooltipText = name;
        return child;
    }

    async void Previews(List<JsonNode> shown)
    {
        if (fetching) return;
        fetching = true;
        foreach (var g in shown)
        {
            string id = g.S("id");
            Gdk.Texture? t = null;
            if (Live(g))
                try
                {
                    var (d, status) = await Api.Data("/panel/api/games/preview?id=" + Uri.EscapeDataString(id), timeout: 5);
                    if (status == 200) t = Shell.Texture(d);
                }
                catch { }
            if (t != null) previews[id] = t; else if (!Live(g)) previews.Remove(id);
            if (!pictures.TryGetValue(id, out var pic)) continue;
            pic.Paintable = previews.GetValueOrDefault(id);
            pic.Visible = pic.Paintable != null;
        }
        fetching = false;
    }

    void Details()
    {
        var g = M.Games.FirstOrDefault(x => x.S("id") == selected);
        string key = g == null ? "" : Key(g, Player(g));
        if (key == detailKey) return;
        detailKey = key;
        if (details != null) body.Remove(details);
        details = null;
        if (g == null) return;

        string id = g.S("id"), name = g.S("name"), path = g.S("path"), access = g.S("access") is { Length: > 0 } a ? a : "play";
        bool live = Live(g), exists = g.B("exists", true), stop = live && g.B("startedByUDriver");
        var grp = Group(name);
        grp.HeaderSuffix = Button("Remove…", () => Shell.Confirm($"Remove {name} from UDriver?", "Agents lose access to it. The game itself stays on this computer.", "Remove",
            () => Shell.Act("/panel/api/games/remove", new() { ["id"] = id })), "flat");

        var may = Row("Agents may", Levels.FirstOrDefault(l => l.Id == access).Hint ?? "Not allowed — set access to use it.");
        var toggles = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        toggles.AddCssClass("linked");
        toggles.Valign = Gtk.Align.Center;
        Gtk.ToggleButton? lead = null;
        foreach (var l in Levels)
        {
            var t = Gtk.ToggleButton.NewWithLabel(l.Label);
            t.SetGroup(lead);
            lead ??= t;
            t.Active = l.Id == access;
            t.OnToggled += (_, _) =>
            {
                if (!t.Active || l.Id == access) return;
                may.Subtitle = l.Hint;
                Shell.Act("/panel/api/games/edit", new() { ["id"] = id, ["access"] = l.Id });
            };
            toggles.Append(t);
        }
        may.AddSuffix(toggles);
        grp.Add(may);

        var state = Row(!exists ? "Missing on disk" : live ? Sentence(g.S("state")) : "Not running", Place(path));
        var run = Button(live ? (stop ? "Stop" : "Restart") : "Launch", () => Shell.Act(stop ? "/panel/api/games/stop" : "/panel/api/games/start", new() { ["id"] = id }, timeout: 60),
            live ? null : "suggested-action");
        run.Sensitive = exists;
        state.AddSuffix(run);
        var open = Button("Open Folder", () => Shell.Open(Gio.FileHelper.NewForPath(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path).GetUri()));
        open.Sensitive = exists;
        state.AddSuffix(open);
        grp.Add(state);
        var engine = Row("Engine", g.S("engine") is { Length: > 0 } e ? e : "Unity");
        engine.AddCssClass("property");
        grp.Add(engine);

        body.Append(grp);
        details = grp;
    }
}

sealed class AgentsScreen : ListScreen
{
    public override string Title => "Agents";

    public AgentsScreen(Shell shell) : base(shell)
    {
        var connect = Gtk.Button.New();
        connect.Child = Adw.ButtonContent.New();
        ((Adw.ButtonContent)connect.Child).IconName = "list-add-symbolic";
        ((Adw.ButtonContent)connect.Child).Label = "Connect Agent";
        connect.TooltipText = "Get a one-time code for a new agent";
        connect.AddCssClass("suggested-action");
        connect.OnClicked += (_, _) => Shell.Select(Section.Access);
        Header.PackEnd(connect);
    }

    static string Ago(int? sec) => sec switch
    {
        null => "just connected",
        < 60 => "active now",
        < 3600 => $"idle for {sec / 60} min",
        _ => $"idle for {sec / 3600} h",
    };

    public override void Show(Model m)
    {
        M = m;
        var others = m.Clients.Where(c => c.S("kind") != "stdio").ToList();
        if (!Changed(Key(Key(m.Agents.Select(a => Key(a.S("id"), a.S("label"), a.S("target"), a.S("from"), (a.I("agoSec") ?? 0) / 60, a.B("disconnected"))).ToArray()),
            Key(others.ToArray()), Key(m.Games.Select(g => g.S("name") + g.S("access")).ToArray())))) return;

        var live = m.Agents.Where(a => !a.B("disconnected")).ToList();
        string Name(JsonNode a) => m.Clients.FirstOrDefault(c => c.S("id") == a.S("client"))?.S("name") is { Length: > 0 } n ? n : a.S("label") is { Length: > 0 } l ? l : "Agent";

        var now = Group("Connected now");
        if (live.Count == 0)
        {
            var row = Row("No agent is connected right now", "Agents on this computer connect by themselves; for others choose Connect Agent.");
            now.Add(row);
        }
        foreach (var a in live)
        {
            string name = Name(a), id = a.S("id"), target = a.S("target");
            string from = a.S("from") is "" or "this computer" ? "On this computer" : "From anywhere";
            var row = Row(name, $"{from} · {Ago(a.I("agoSec"))}");
            row.AddPrefix(Shell.Tile(from == "From anywhere" ? "network-workgroup-symbolic" : "computer-symbolic", "blue"));
            bool watch = M.Games.FirstOrDefault(g => g.S("name") == target || g.S("id") == target)?.S("access") == "watch";
            var chip = Gtk.Label.New(target.Length == 0 ? "Idle" : (watch ? "Watching " : "Playing ") + target);
            chip.AddCssClass("chip");
            if (target.Length > 0 && !watch) chip.AddCssClass("accent");
            chip.Valign = Gtk.Align.Center;
            chip.Ellipsize = Pango.EllipsizeMode.End;
            chip.MaxWidthChars = 24;
            row.AddSuffix(chip);
            row.AddSuffix(Button("Disconnect", () => Shell.Act("/panel/api/agents/disconnect", new() { ["id"] = id })));
            now.Add(row);
        }

        var here = live.Select(a => a.S("client")).ToHashSet();
        var hereNames = live.Select(Name).ToHashSet();
        var before = others.Where(c => c.S("status") == "allowed" && !here.Contains(c.S("id")) && !hereNames.Contains(c.S("name"))).GroupBy(c => c.S("name")).ToList();
        var groups = new List<Adw.PreferencesGroup> { now };
        if (before.Count > 0)
        {
            var past = Group("Allowed before", "These agents may come back without asking again. Remove one to make it ask next time.");
            foreach (var c in before)
            {
                string name = c.Key.Length > 0 ? c.Key : "Agent";
                var first = c.First();
                string kind = first.S("kind") is "remote" or "link" ? "From anywhere" : "On this computer";
                var ids = c.Select(x => x.S("id")).ToList();
                var row = Row(name, kind + " · Not connected now");
                row.AddPrefix(Shell.Tile(kind == "From anywhere" ? "network-workgroup-symbolic" : "computer-symbolic", "gray"));
                row.AddSuffix(Button("Remove", () => Shell.Confirm($"Remove {name}?", "It loses access at once and has to ask again to come back.", "Remove",
                    () => ids.ForEach(i => Shell.Act("/panel/api/clients/revoke", new() { ["id"] = i })))));
                past.Add(row);
            }
            groups.Add(past);
        }
        Fill(groups);
    }
}

sealed class ToolsScreen : ListScreen
{
    public override string Title => "Tools";
    readonly Dictionary<string, (Adw.ActionRow Row, Gtk.Switch Switch)> rows = [];
    readonly Adw.ComboRow who = Adw.ComboRow.New();
    readonly Adw.PreferencesGroup top;
    List<string> ids = [""];
    bool setting;

    public ToolsScreen(Shell shell) : base(shell)
    {
        who.Title = "For";
        who.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected" && !setting) { Forget(); Show(M); } };
        top = Group();
        top.Add(who);
        Page.Add(top);
    }

    string Client => who.Selected < ids.Count ? ids[(int)who.Selected] : "";

    static string Feature(JsonNode t) => t.S("feature") is { Length: > 0 } f ? f : t.S("title") is { Length: > 0 } x ? x : t.S("name");

    bool On(JsonNode t) => t.B("enabled", true) && (Client.Length == 0 || !t.L("offFor").Any(x => x.GetValue<string>() == Client));

    public override void Show(Model m)
    {
        M = m;
        var clients = m.Clients.Where(c => c.S("id").Length > 0).ToList();
        string current = Client;
        var nextIds = new List<string> { "" }.Concat(clients.Select(c => c.S("id"))).ToList();
        if (!nextIds.SequenceEqual(ids))
        {
            setting = true;
            ids = nextIds;
            who.Model = Gtk.StringList.New(new[] { "All Agents" }.Concat(clients.Select(c => c.S("name") is { Length: > 0 } n ? n : "Agent")).ToArray());
            who.Selected = (uint)Math.Max(0, ids.IndexOf(current));
            setting = false;
        }
        top.Description = Client.Length == 0 ? "A switched-off feature is closed for every agent, including over the internet. A switch shows on while any of its tools is on."
            : $"These switches apply only to {clients.FirstOrDefault(c => c.S("id") == Client)?.S("name") ?? "this agent"}.";

        if (Changed(Key(Client, Key(m.Tools.Select(t => t.S("name") + Feature(t) + t.S("group")).ToArray()))))
        {
            rows.Clear();
            var groups = new List<Adw.PreferencesGroup>();
            foreach (var part in m.Tools.GroupBy(t => t.S("group") is { Length: > 0 } g ? g : "Other"))
            {
                var grp = Group(part.Key);
                foreach (var f in part.GroupBy(Feature))
                {
                    string names = string.Join(",", f.Select(t => t.S("name")));
                    var row = Row(f.Key);
                    var sw = Gtk.Switch.New();
                    sw.Valign = Gtk.Align.Center;
                    sw.OnNotify += (_, e) =>
                    {
                        if (e.Pspec.GetName() != "active" || setting) return;
                        var form = new Dictionary<string, string> { ["tool"] = names, ["enabled"] = sw.Active ? "true" : "false" };
                        if (Client.Length > 0) form["client"] = Client;
                        Shell.Act("/panel/api/tools/set", form);
                    };
                    row.AddSuffix(sw);
                    row.ActivatableWidget = sw;
                    rows[f.Key] = (row, sw);
                    grp.Add(row);
                }
                groups.Add(grp);
            }
            Fill(groups);
        }
        setting = true;
        foreach (var f in m.Tools.GroupBy(Feature))
        {
            if (!rows.TryGetValue(f.Key, out var r)) continue;
            int on = f.Count(On), all = f.Count();
            string names = GLib.Markup.EscapeText(string.Join(" · ", f.Select(t => t.S("name"))));
            r.Row.Subtitle = on > 0 && on < all ? "<span foreground=\"#e5a50a\">some off · </span>" + names : names;
            r.Switch.Sensitive = Client.Length == 0 || f.Any(t => t.B("enabled", true));
            r.Row.TooltipText = r.Switch.Sensitive ? string.Join("\n", f.Select(t => t.S("name") + ": " + (t.S("title") is { Length: > 0 } x ? x : t.S("description"))))
                : "Switched off for every agent. Turn it on under All Agents first.";
            if (r.Switch.Active != on > 0) r.Switch.Active = on > 0;
        }
        setting = false;
    }
}

sealed class AccessScreen : ListScreen
{
    public override string Title => "Access from Anywhere";
    JsonNode? code;
    DateTime codeUntil;
    Gtk.Label? countdown;
    bool ticking;

    public AccessScreen(Shell shell) : base(shell) { }

    public override void Show(Model m)
    {
        M = m;
        if (DateTime.Now > codeUntil) code = null;
        var r = m.Remote.DeepClone().AsObject();
        r.Remove("onlineSec");
        if (!Changed(Key(r, code))) return;

        bool on = r.B("on");
        string state = r.S("state");
        string sub = r.S("error") is { Length: > 0 } e ? e : on && state != "online" && state.Length > 0 ? "Connecting…" : "Through a private, encrypted link. Turn off to close it at once.";
        var main = Group();
        var sw = Adw.SwitchRow.New();
        sw.Title = "Agents on other devices can reach your games";
        sw.Subtitle = GLib.Markup.EscapeText(sub);
        sw.Active = on;
        sw.AddPrefix(Shell.Tile("network-workgroup-symbolic", "blue"));
        sw.OnNotify += (_, e) => { if (e.Pspec.GetName() == "active" && sw.Active != on) Flip(sw.Active, r); };
        main.Add(sw);
        var groups = new List<Adw.PreferencesGroup> { main };

        if (!on)
        {
            if (Setup(r) is { } s) groups.Add(s);
        }
        else
        {
            string address = r.S("address");
            var where = Group();
            var addr = Row("Address", address);
            addr.SubtitleSelectable = true;
            addr.AddCssClass("property");
            addr.AddSuffix(Button("Copy", () => Shell.Copy(address)));
            where.Add(addr);
            groups.Add(where);
            groups.Add(Code());
        }
        groups.Add(Local());
        Fill(groups);
    }

    Adw.PreferencesGroup Code()
    {
        var g = Group("Code for a new agent");
        if (code == null)
        {
            g.Description = "A new agent asks for a code the first time it connects from another device. Each code works once.";
            g.HeaderSuffix = Button("New Code", NewCode, "suggested-action");
            return g;
        }
        string c = code.S("code");
        var big = Gtk.Label.New(c);
        big.AddCssClass("code");
        big.Selectable = true;
        big.Xalign = 0;
        countdown = Text("", "dim-label");
        Tick();
        var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        buttons.Append(Button("Copy", () => Shell.Copy(c)));
        buttons.Append(Button("New Code", NewCode));
        var left = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
        left.Hexpand = true;
        left.Valign = Gtk.Align.Center;
        left.Append(big);
        left.Append(countdown);
        left.Append(buttons);
        var qr = Gtk.Picture.NewForPaintable(Shell.Texture(new PngByteQRCode(new QRCodeGenerator().CreateQrCode(code.S("panel") is { Length: > 0 } p ? p : code.S("address"), QRCodeGenerator.ECCLevel.M)).GetGraphic(8)));
        qr.SetSizeRequest(140, 140);
        qr.CanShrink = false;
        var right = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        right.Append(qr);
        right.Append(Text("Open on your phone", "caption", "dim-label"));
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 20);
        box.AddCssClass("card");
        box.MarginTop = 4;
        box.Append(left);
        box.Append(right);
        left.MarginStart = left.MarginTop = left.MarginBottom = 18;
        right.MarginEnd = right.MarginTop = right.MarginBottom = 18;
        g.Add(box);
        return g;
    }

    Adw.PreferencesGroup? Setup(JsonNode r)
    {
        if (r["cloudflared"] is JsonValue v && v.TryGetValue(out bool has) && !has)
        {
            var g = Group();
            var row = Row("Download the helper", "To reach your games from other devices, UDriver first downloads a small helper for the private link.");
            row.AddSuffix(Button("Download Helper", () => Shell.Act("/panel/api/remote/install", timeout: 300), "suggested-action"));
            g.Add(row);
            return g;
        }
        if (!r.B("saved") && !r.B("signedIn"))
        {
            string url = r.S("loginUrl");
            var g = Group();
            var row = Row("Sign in to Cloudflare", "Sign in to Cloudflare once to create your private link. It is free.");
            row.AddSuffix(Button(url.Length == 0 ? "Sign In" : "Open Sign-In Page", () =>
            {
                if (url.Length > 0) Shell.Open(url); else Shell.Act("/panel/api/remote/login", then: j => { if (j.S("loginUrl") is { Length: > 0 } u) Shell.Open(u); });
            }, "suggested-action"));
            g.Add(row);
            return g;
        }
        return null;
    }

    void Flip(bool on, JsonNode r)
    {
        void Undo() { Forget(); Show(M); }
        if (on)
        {
            if (r.B("saved")) { Shell.Act("/panel/api/remote/on", timeout: 120); return; }
            if (r.B("signedIn"))
            {
                Shell.Ask("Choose an address", $"Type a name under {(r.S("domain") is { Length: > 0 } d ? d : "your domain")}, for example games.", "Name", "Turn On",
                    host => Shell.Act("/panel/api/remote/on", new() { ["host"] = host }, timeout: 120), Undo);
                return;
            }
            Shell.Alert("Finish setup first", "Follow the step below this switch, then turn it on.");
            Undo();
            return;
        }
        Shell.Confirm("Turn off access from anywhere?", "Agents and devices that connect over the internet lose access at once.", "Turn Off",
            () => Shell.Act("/panel/api/remote/off"), Undo);
    }

    void NewCode() => Shell.Act("/panel/api/remote/code", then: j =>
    {
        code = j;
        codeUntil = DateTime.Now.AddSeconds(j.I("expiresSec") ?? 600);
        Forget();
        Run();
    });

    async void Run()
    {
        if (ticking) return;
        ticking = true;
        while (code != null)
        {
            await Task.Delay(1000);
            Tick();
        }
        ticking = false;
    }

    void Tick()
    {
        int left = (int)(codeUntil - DateTime.Now).TotalSeconds;
        if (left <= 0) { code = null; Show(M); return; }
        countdown?.SetLabel($"Works once, expires in {left / 60}:{left % 60:00}. Paste it on the page the agent opens.");
    }

    Adw.PreferencesGroup Local()
    {
        var g = Group("Apps on this computer", "Local models connect directly, without the internet.");
        var menu = Gio.Menu.New();
        menu.Append("For Apps That Start UDriver Themselves", "setup.command");
        menu.Append("For Apps That Connect to UDriver…", "setup.key");
        var actions = Gio.SimpleActionGroup.New();
        var command = Gio.SimpleAction.New("command", null);
        command.OnActivate += (_, _) => CopyCommand();
        var key = Gio.SimpleAction.New("key", null);
        key.OnActivate += (_, _) => CopyKey();
        actions.AddAction(command);
        actions.AddAction(key);
        var pick = Adw.SplitButton.New();
        pick.Label = "Copy Setup";
        pick.MenuModel = menu;
        pick.Valign = Gtk.Align.Center;
        pick.TooltipText = "Copies the setup for apps that start UDriver themselves. The arrow has the option for apps that connect over the network.";
        pick.OnClicked += (_, _) => CopyCommand();
        var row = Row("Copy the setup into an app", "Paste it into the app's MCP settings.");
        row.AddPrefix(Shell.Tile("computer-symbolic", "violet"));
        row.AddSuffix(pick);
        row.InsertActionGroup("setup", actions);
        g.Add(row);
        return g;
    }

    void CopyCommand()
    {
        var (file, args) = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image ? (image, new[] { "stdio" })
            : Api.Command() is var (f, a) ? (f, a) : ("dotnet", new[] { Api.Dll });
        var j = new JsonObject { ["mcpServers"] = new JsonObject { ["udriver"] = new JsonObject { ["command"] = file, ["args"] = new JsonArray(args.Select(x => (JsonNode)x).ToArray()) } } };
        Shell.Copy(j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), null);
        Shell.Alert("Setup copied", "Paste it into the app's MCP settings. The app starts UDriver by itself when it needs it.");
    }

    void CopyKey() => Shell.Ask("Which app is this for?", "UDriver makes a private key for this app. You can remove it later under Agents.", "App name", "Copy Setup",
        name => Shell.Act("/panel/api/local-clients", new() { ["name"] = name.Trim().Length > 0 ? name.Trim() : "App on this computer" }, j =>
        {
            Shell.Copy(j["mcpJson"] is JsonValue s && s.TryGetValue(out string? text) ? text : j["mcpJson"]?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "", null);
            Shell.Alert("Setup copied", "Paste it into the app's MCP settings. It connects while UDriver is running.");
        }));
}
