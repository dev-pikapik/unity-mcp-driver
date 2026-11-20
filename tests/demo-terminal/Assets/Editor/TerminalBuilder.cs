using System;
using System.IO;
using System.Linq;
using Northwind.Trading.UI;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Northwind.Trading.EditorTools
{
    public static class TerminalBuilder
    {
        const string ScenePath = "Assets/Scenes/Terminal.unity";
        const string AccountPath = "Assets/Data/DemoAccount.asset";
        const string ProductName = "Northwind Terminal (Demo)";
        const string FileName = "NorthwindTerminal";

        static readonly Color Bg = Hex("0B0E13");
        static readonly Color PanelBg = Hex("141A22");
        static readonly Color HeaderBg = Hex("10151C");
        static readonly Color FieldBg = Hex("0C1117");
        static readonly Color RowBg = Hex("19212B");
        static readonly Color TextMain = Hex("D7DEE8");
        static readonly Color TextMuted = Hex("7D8A99");
        static readonly Color Accent = Hex("F0B90B");
        static readonly Color Up = Hex("26A69A");
        static readonly Color Down = Hex("EF5350");

        static Font font;
        static Sprite rounded;

        [MenuItem("Northwind/Generate Terminal Scene")]
        public static void GenerateScene()
        {
            ConfigurePlayer();
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            rounded = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera", typeof(Camera)).GetComponent<Camera>();
            cam.tag = "MainCamera";
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Bg;
            cam.orthographic = true;
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            var market = new GameObject("MarketData").AddComponent<MarketData>();
            var feed = new GameObject("FeedClient").AddComponent<FeedClient>();
            feed.market = market;
            var trading = new GameObject("TradingService").AddComponent<TradingService>();
            trading.account = LoadOrCreateAccount();
            trading.market = market;

            var canvasGo = new GameObject("TerminalCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 800);
            scaler.matchWidthOrHeight = 0.5f;
            var root = canvasGo.transform;
            var view = canvasGo.AddComponent<TerminalView>();
            view.market = market;
            view.trading = trading;
            view.feed = feed;

            BuildHeader(root, view);
            BuildChart(root, view);
            BuildOpenOrders(root, view);
            BuildPositions(root, view);
            BuildOrderBook(root, view, market);
            BuildTicket(root, view);
            BuildAccount(root, view);

            var status = Panel("StatusBar", root, 0, 776, 1280, 24, HeaderBg, false);
            view.statusText = Label("StatusText", status, "Connecting to demo feed...", 14, 0, 1252, 24, 12, TextMuted);

            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[TerminalBuilder] scene saved to " + ScenePath);
        }

        static void BuildHeader(Transform root, TerminalView view)
        {
            var header = Panel("Header", root, 0, 0, 1280, 56, HeaderBg, false);
            Label("Brand", header, "Northwind", 20, 0, 110, 56, 18, Accent, TextAnchor.MiddleLeft, FontStyle.Bold);
            Label("ProductName", header, "Terminal", 118, 0, 90, 56, 18, TextMain);
            var tag = Panel("DemoTag", header, 206, 18, 52, 20, Accent);
            Label("DemoTagText", tag, "DEMO", 0, 0, 52, 20, 11, Bg, TextAnchor.MiddleCenter, FontStyle.Bold);
            view.symbolText = Label("SymbolText", header, "DEMO/USD", 300, 0, 110, 56, 16, TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            view.lastPriceText = Label("LastPriceText", header, "-", 410, 0, 120, 56, 24, TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            view.changeText = Label("ChangeText", header, "", 540, 0, 220, 56, 14, TextMuted);
            view.accountBadgeText = Label("AccountBadge", header, "", 860, 0, 400, 56, 12, TextMuted, TextAnchor.MiddleRight);
        }

        static void BuildChart(Transform root, TerminalView view)
        {
            var panel = Panel("ChartPanel", root, 12, 68, 840, 440, PanelBg);
            Label("ChartTitle", panel, "DEMO/USD  ·  5 second candles", 14, 0, 400, 34, 13, TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            Panel("AverageSwatch", panel, 724, 15, 16, 3, Accent, false);
            view.averageLegend = Label("AverageLegend", panel, "MA(10)", 746, 0, 80, 34, 12, Accent);

            var chartRt = Rect("CandleChart", panel, 12, 40, 760, 372);
            var chart = chartRt.gameObject.AddComponent<CandleChart>();
            chart.raycastTarget = false;
            view.chart = chart;

            var lineRt = Rect("MovingAverageLine", chartRt, 0, 0, 0, 0);
            lineRt.anchorMin = Vector2.zero;
            lineRt.anchorMax = Vector2.one;
            lineRt.offsetMin = lineRt.offsetMax = Vector2.zero;
            var line = lineRt.gameObject.AddComponent<LineSeries>();
            line.color = Accent;
            line.chart = chart;
            line.raycastTarget = false;
            view.averageLine = line;

            chart.priceLabels = Enumerable.Range(0, 6).Select(i =>
            {
                var t = Label("PriceAxis" + (i + 1), chartRt, "", 0, 0, 56, 16, 11, TextMuted);
                t.rectTransform.anchorMin = t.rectTransform.anchorMax = new Vector2(1, 1);
                t.rectTransform.pivot = new Vector2(0, 0.5f);
                t.rectTransform.anchoredPosition = new Vector2(8, -20 - i * 50);
                return t;
            }).ToArray();
            chart.timeLabels = Enumerable.Range(0, 4).Select(i =>
            {
                var t = Label("TimeAxis" + (i + 1), chartRt, "", 0, 0, 80, 16, 11, TextMuted, TextAnchor.MiddleCenter);
                t.rectTransform.anchorMin = t.rectTransform.anchorMax = new Vector2((i + 0.5f) / 4, 0);
                t.rectTransform.pivot = new Vector2(0.5f, 1);
                t.rectTransform.anchoredPosition = new Vector2(0, -4);
                return t;
            }).ToArray();
            var tagBg = Panel("LastPriceTag", chartRt, 0, 0, 56, 18, Accent);
            tagBg.anchorMin = tagBg.anchorMax = new Vector2(1, 1);
            tagBg.pivot = new Vector2(0, 0.5f);
            tagBg.anchoredPosition = new Vector2(4, -100);
            var tagText = Label("LastPriceTagText", tagBg, "", 0, 0, 56, 18, 11, Bg, TextAnchor.MiddleCenter, FontStyle.Bold);
            chart.lastPriceMarker = tagBg;
            chart.lastPriceTag = tagText;
            tagText.rectTransform.anchorMin = Vector2.zero;
            tagText.rectTransform.anchorMax = Vector2.one;
            tagText.rectTransform.offsetMin = tagText.rectTransform.offsetMax = Vector2.zero;
        }

        static void BuildOpenOrders(Transform root, TerminalView view)
        {
            var panel = Panel("OpenOrdersPanel", root, 12, 520, 414, 248, PanelBg);
            view.openOrdersTitle = Label("OpenOrdersTitle", panel, "Open orders", 14, 0, 200, 34, 13, TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            var cancelAll = Button("CancelAllButton", panel, "Cancel all", 314, 6, 88, 22, RowBg, TextMuted, 11);
            UnityEventTools.AddPersistentListener(cancelAll.onClick, view.OnCancelAllClicked);
            ColumnHeader(panel, 36, ("Side", 14, 50), ("Qty", 70, 70), ("Limit", 146, 80), ("Placed", 232, 70));

            var content = List("OpenOrdersList", panel, 6, 56, 402, 186);
            var row = Rect("OrderRowTemplate", content, 0, 0, 402, 28).gameObject;
            row.AddComponent<Image>().color = RowBg;
            row.AddComponent<LayoutElement>().preferredHeight = 28;
            var r = row.AddComponent<OrderRow>();
            r.sideText = Label("Side", row.transform, "BUY", 8, 0, 50, 28, 12, Up, TextAnchor.MiddleLeft, FontStyle.Bold);
            r.quantityText = Label("Quantity", row.transform, "0", 64, 0, 70, 28, 12, TextMain);
            r.priceText = Label("LimitPrice", row.transform, "0.00", 140, 0, 80, 28, 12, TextMain);
            r.timeText = Label("PlacedAt", row.transform, "", 226, 0, 70, 28, 12, TextMuted);
            var cancel = Button("CancelButton", row.transform, "Cancel", 324, 4, 70, 20, Hex("3A2326"), Down, 11);
            UnityEventTools.AddPersistentListener(cancel.onClick, r.OnCancelClicked);
            row.SetActive(false);
            view.orderTemplate = r;
            view.openOrdersEmpty = Label("OpenOrdersEmpty", panel, "No open orders. Limit orders wait here until the price reaches them.", 14, 64, 386, 40, 12, TextMuted);
            view.openOrdersEmpty.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        static void BuildPositions(Transform root, TerminalView view)
        {
            var panel = Panel("PositionsPanel", root, 438, 520, 414, 248, PanelBg);
            Label("PositionsTitle", panel, "Positions", 14, 0, 200, 34, 13, TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            ColumnHeader(panel, 36, ("Symbol", 14, 80), ("Qty", 100, 70), ("Avg price", 172, 80), ("P&L", 256, 80));

            var content = List("PositionsList", panel, 6, 56, 402, 186);
            var row = Rect("PositionRowTemplate", content, 0, 0, 402, 28).gameObject;
            row.AddComponent<Image>().color = RowBg;
            row.AddComponent<LayoutElement>().preferredHeight = 28;
            var r = row.AddComponent<PositionRow>();
            r.symbolText = Label("Symbol", row.transform, "", 8, 0, 86, 28, 12, TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            r.quantityText = Label("Quantity", row.transform, "", 94, 0, 70, 28, 12, TextMain);
            r.averageText = Label("AveragePrice", row.transform, "", 166, 0, 80, 28, 12, TextMain);
            r.pnlText = Label("UnrealizedPnl", row.transform, "", 250, 0, 80, 28, 12, TextMain);
            var close = Button("CloseButton", row.transform, "Close", 324, 4, 70, 20, RowBg, TextMain, 11);
            close.GetComponent<Image>().color = Hex("27313D");
            UnityEventTools.AddPersistentListener(close.onClick, r.OnCloseClicked);
            row.SetActive(false);
            view.positionTemplate = r;
            view.positionsEmpty = Label("PositionsEmpty", panel, "No positions yet. Buy or sell to open one.", 14, 64, 386, 40, 12, TextMuted);
        }

        static void BuildOrderBook(Transform root, TerminalView view, MarketData market)
        {
            var panel = Panel("OrderBookPanel", root, 864, 68, 404, 380, PanelBg);
            var book = panel.gameObject.AddComponent<OrderBookView>();
            book.market = market;
            book.terminal = view;
            Label("OrderBookTitle", panel, "Order book", 14, 0, 200, 30, 13, TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            ColumnHeader(panel, 30, ("Price (USD)", 14, 120), ("Size", 270, 120));
            const int rows = 8;
            book.askPrices = new Text[rows]; book.askSizes = new Text[rows]; book.askDepth = new Image[rows];
            book.bidPrices = new Text[rows]; book.bidSizes = new Text[rows]; book.bidDepth = new Image[rows];
            for (int i = 0; i < rows; i++)
            {
                BookRow(panel, "Ask" + (i + 1), 50 + i * 18, Down, out book.askPrices[i], out book.askSizes[i], out book.askDepth[i], out var b);
                UnityEventTools.AddIntPersistentListener(b.onClick, book.OnAskClicked, i);
                BookRow(panel, "Bid" + (i + 1), 222 + i * 18, Up, out book.bidPrices[i], out book.bidSizes[i], out book.bidDepth[i], out b);
                UnityEventTools.AddIntPersistentListener(b.onClick, book.OnBidClicked, i);
            }
            book.spreadText = Label("SpreadText", panel, "Spread -", 14, 196, 376, 24, 12, TextMuted, TextAnchor.MiddleCenter);
        }

        static void BookRow(Transform panel, string name, float y, Color tone, out Text price, out Text size, out Image depth, out Button button)
        {
            var row = Rect(name + "Row", panel, 8, y, 388, 18);
            var hit = row.gameObject.AddComponent<Image>();
            hit.color = new Color(1, 1, 1, 0.001f);
            button = row.gameObject.AddComponent<Button>();
            button.targetGraphic = hit;
            var cb = button.colors;
            cb.highlightedColor = new Color(1, 1, 1, 0.05f);
            cb.normalColor = new Color(1, 1, 1, 0.001f);
            cb.pressedColor = new Color(1, 1, 1, 0.1f);
            cb.selectedColor = cb.normalColor;
            button.colors = cb;
            var bar = Rect("Depth", row, 0, 0, 0, 0);
            bar.anchorMin = new Vector2(1, 0);
            bar.anchorMax = Vector2.one;
            bar.offsetMin = bar.offsetMax = Vector2.zero;
            depth = bar.gameObject.AddComponent<Image>();
            depth.color = new Color(tone.r, tone.g, tone.b, 0.14f);
            depth.raycastTarget = false;
            price = Label("Price", row, "", 6, 0, 150, 18, 12, tone);
            size = Label("Size", row, "", 230, 0, 150, 18, 12, TextMain, TextAnchor.MiddleRight);
        }

        static void BuildTicket(Transform root, TerminalView view)
        {
            var panel = Panel("OrderTicket", root, 864, 460, 404, 196, PanelBg);
            Label("TicketTitle", panel, "New order", 14, 0, 200, 30, 13, TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            Label("TicketHint", panel, "Leave limit price empty to trade at market", 150, 0, 240, 30, 11, TextMuted, TextAnchor.MiddleRight);
            Label("QuantityLabel", panel, "Quantity", 14, 38, 100, 30, 12, TextMuted);
            view.quantityInput = Input("QuantityInput", panel, "10", "Amount of DEMO", 120, 38, 270, 30);
            view.quantityInput.contentType = InputField.ContentType.DecimalNumber;
            UnityEventTools.AddPersistentListener(view.quantityInput.onEndEdit, new UnityAction<string>(view.OnQuantityEdited));
            Label("LimitPriceLabel", panel, "Limit price", 14, 76, 100, 30, 12, TextMuted);
            view.limitPriceInput = Input("LimitPriceInput", panel, "", "Market", 120, 76, 270, 30);
            view.limitPriceInput.contentType = InputField.ContentType.DecimalNumber;
            var buy = Button("BuyButton", panel, "Buy", 14, 116, 184, 38, Up, Color.white, 15);
            UnityEventTools.AddPersistentListener(buy.onClick, view.OnBuyClicked);
            var sell = Button("SellButton", panel, "Sell", 206, 116, 184, 38, Down, Color.white, 15);
            UnityEventTools.AddPersistentListener(sell.onClick, view.OnSellClicked);
            view.ticketMessage = Label("TicketMessage", panel, "", 14, 160, 376, 30, 12, TextMuted);
            view.ticketMessage.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        static void BuildAccount(Transform root, TerminalView view)
        {
            var panel = Panel("AccountPanel", root, 864, 668, 404, 100, PanelBg);
            Label("AccountTitle", panel, "Account", 14, 0, 200, 28, 13, TextMain, TextAnchor.MiddleLeft, FontStyle.Bold);
            view.cashText = AccountRow(panel, "Cash", 28);
            view.equityText = AccountRow(panel, "Equity", 50);
            view.pnlText = AccountRow(panel, "P&L", 72);
        }

        static Text AccountRow(Transform panel, string caption, float y)
        {
            string id = caption == "P&L" ? "Pnl" : caption;
            Label(id + "Label", panel, caption, 14, y, 150, 22, 12, TextMuted);
            return Label(id + "Value", panel, "-", 180, y, 210, 22, 13, TextMain, TextAnchor.MiddleRight, FontStyle.Bold);
        }

        static void ColumnHeader(Transform panel, float y, params (string text, float x, float w)[] cols)
        {
            foreach (var c in cols)
                Label("Column" + c.text.Split(' ')[0].Replace("&", ""), panel, c.text.ToUpperInvariant(), c.x, y, c.w, 18, 10, TextMuted);
        }

        static RectTransform List(string name, Transform parent, float x, float y, float w, float h)
        {
            var rt = Rect(name, parent, x, y, w, h);
            var scroll = rt.gameObject.AddComponent<ScrollRect>();
            var viewport = Rect("Viewport", rt, 0, 0, 0, 0);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = viewport.offsetMax = Vector2.zero;
            viewport.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            viewport.gameObject.AddComponent<RectMask2D>();
            var content = Rect("Content", viewport, 0, 0, 0, 0);
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 1);
            content.offsetMin = content.offsetMax = Vector2.zero;
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 2;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 20;
            return content;
        }

        static RectTransform Rect(string name, Transform parent, float x, float y, float w, float h)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        static RectTransform Panel(string name, Transform parent, float x, float y, float w, float h, Color color, bool round = true)
        {
            var rt = Rect(name, parent, x, y, w, h);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            if (round)
            {
                img.sprite = rounded;
                img.type = Image.Type.Sliced;
            }
            return rt;
        }

        static Text Label(string name, Transform parent, string text, float x, float y, float w, float h, int size, Color color,
            TextAnchor align = TextAnchor.MiddleLeft, FontStyle style = FontStyle.Normal)
        {
            var t = Rect(name, parent, x, y, w, h).gameObject.AddComponent<Text>();
            t.font = font;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.fontStyle = style;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        static Button Button(string name, Transform parent, string text, float x, float y, float w, float h, Color bg, Color fg, int size)
        {
            var rt = Panel(name, parent, x, y, w, h, bg);
            var img = rt.GetComponent<Image>();
            img.raycastTarget = true;
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            var cb = b.colors;
            cb.highlightedColor = new Color(0.88f, 0.88f, 0.88f);
            cb.pressedColor = new Color(0.7f, 0.7f, 0.7f);
            cb.selectedColor = Color.white;
            b.colors = cb;
            var label = Label("Label", rt, text, 0, 0, w, h, size, fg, TextAnchor.MiddleCenter, FontStyle.Bold);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
            return b;
        }

        static InputField Input(string name, Transform parent, string value, string placeholder, float x, float y, float w, float h)
        {
            var rt = Panel(name, parent, x, y, w, h, FieldBg);
            var img = rt.GetComponent<Image>();
            img.raycastTarget = true;
            var field = rt.gameObject.AddComponent<InputField>();
            field.targetGraphic = img;
            var hint = Label("Placeholder", rt, placeholder, 10, 0, w - 20, h, 13, TextMuted);
            hint.fontStyle = FontStyle.Italic;
            var text = Label("Text", rt, "", 10, 0, w - 20, h, 13, TextMain);
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            field.textComponent = text;
            field.placeholder = hint;
            field.caretColor = TextMain;
            field.customCaretColor = true;
            field.selectionColor = new Color(Accent.r, Accent.g, Accent.b, 0.35f);
            field.text = value;
            return field;
        }

        static Account LoadOrCreateAccount()
        {
            var account = AssetDatabase.LoadAssetAtPath<Account>(AccountPath);
            if (account != null) return account;
            Directory.CreateDirectory(Path.GetDirectoryName(AccountPath));
            account = ScriptableObject.CreateInstance<Account>();
            AssetDatabase.CreateAsset(account, AccountPath);
            return account;
        }

        static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "Northwind";
            PlayerSettings.productName = ProductName;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, "com.northwind.terminal.demo");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 800;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            PlayerSettings.usePlayerLog = true;
            PlayerSettings.SplashScreen.show = false;
        }

        [MenuItem("Northwind/Build All Players")]
        public static void BuildAll()
        {
            if (!File.Exists(ScenePath)) GenerateScene();
            ConfigurePlayer();
            var only = Arg("-targets");
            bool Want(string key) => only == null || only.Split(',').Contains(key);
            if (Want("osx"))
            {
                var settings = Type.GetType("UnityEditor.OSXStandalone.UserBuildSettings, UnityEditor.OSXStandalone.Extensions");
                var prop = settings?.GetProperty("architecture");
                prop?.SetValue(null, Enum.Parse(prop.PropertyType, "ARM64"));
                Build(BuildTarget.StandaloneOSX, "Builds/macOS/" + FileName + ".app");
            }
            if (Want("win")) Build(BuildTarget.StandaloneWindows64, "Builds/Windows/" + FileName + ".exe");
            if (Want("linux")) Build(BuildTarget.StandaloneLinux64, "Builds/Linux/" + FileName + ".x86_64");
        }

        static void Build(BuildTarget target, string path)
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, target);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = path,
                target = target,
                options = BuildOptions.Development
            });
            Debug.Log("[TerminalBuilder] " + target + " -> " + path + ": " + report.summary.result + ", " + report.summary.totalSize / 1048576 + " MB");
            if (report.summary.result != BuildResult.Succeeded && Application.isBatchMode) EditorApplication.Exit(1);
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString("#" + hex, out var c);
            return c;
        }
    }
}
