using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

public static class BuggyGameBuilder
{
    const string ScenePath = "Assets/Scenes/Main.unity";
    static Font font;

    public static void Mac() => Build(BuildTarget.StandaloneOSX);
    public static void Linux() => Build(BuildTarget.StandaloneLinux64);
    public static void Windows() => Build(BuildTarget.StandaloneWindows64);

    public static void All()
    {
        Build(BuildTarget.StandaloneOSX);
        Build(BuildTarget.StandaloneLinux64);
        Build(BuildTarget.StandaloneWindows64);
    }

    static void Build(BuildTarget target)
    {
        GenerateScene();
        string path = target == BuildTarget.StandaloneOSX ? "Builds/mac/BuggyGame.app"
            : target == BuildTarget.StandaloneLinux64 ? "Builds/linux/BuggyGame.x86_64"
            : "Builds/windows/BuggyGame.exe";
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = path,
            target = target,
            options = BuildOptions.Development,
        });
        var s = report.summary;
        Debug.Log($"BuggyGame {target}: {s.result}, {s.totalErrors} error(s) -> {s.outputPath}");
        if (Application.isBatchMode && s.result != BuildResult.Succeeded) EditorApplication.Exit(1);
    }

    static void Configure()
    {
        PlayerSettings.companyName = "UDriverQA";
        PlayerSettings.productName = "BuggyGame";
        PlayerSettings.runInBackground = true;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
        settings.FindProperty("activeInputHandler").intValue = 1;
        settings.ApplyModifiedProperties();
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
    }

    public static void GenerateScene()
    {
        Configure();
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);

        var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
        cam.tag = "MainCamera";
        cam.transform.position = new Vector3(0f, 1f, -10f);
        cam.GetComponent<Camera>().clearFlags = CameraClearFlags.SolidColor;
        cam.GetComponent<Camera>().backgroundColor = new Color(0.12f, 0.14f, 0.2f);
        var light = new GameObject("Light", typeof(Light));
        light.GetComponent<Light>().type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        var player = GameObject.CreatePrimitive(PrimitiveType.Cube);
        player.name = "Player";
        player.transform.position = new Vector3(0f, 1f, 0f);
        player.AddComponent<PlayerMover>();

        new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

        var game = new GameObject("Game");
        var inventory = game.AddComponent<Inventory>();
        var shop = game.AddComponent<Shop>();
        var save = game.AddComponent<SaveSystem>();
        var settings = game.AddComponent<SettingsMenu>();
        var pause = game.AddComponent<PauseMenu>();
        var report = game.AddComponent<ReportBuilder>();
        shop.inventory = save.inventory = report.inventory = inventory;

        var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280f, 720f);
        scaler.matchWidthOrHeight = 1f;
        var canvas = canvasGo.transform;

        var hudRect = MakeRect(canvas, "HUD", new Vector2(0f, 1f), new Vector2(170f, -50f), new Vector2(300f, 80f));
        var hud = hudRect.gameObject.AddComponent<HUD>();
        hud.shop = shop;
        hud.coinsLabel = MakeLabel(hudRect, "Coins", "Coins: 0", new Vector2(0f, 20f), new Vector2(280f, 30f), 22);
        var bar = MakeRect(hudRect, "HealthBar", new Vector2(0.5f, 0.5f), new Vector2(0f, -15f), new Vector2(280f, 18f));
        hud.healthBar = bar.gameObject.AddComponent<RawImage>();

        var menu = MakeRect(canvas, "MainMenu", new Vector2(0f, 0.5f), new Vector2(170f, 0f), new Vector2(260f, 420f));
        MakeButton(menu, "PlayButton", "Play / Shop", new Vector2(0f, 150f), shop.Open);
        MakeButton(menu, "QuestButton", "Quest", new Vector2(0f, 75f), null);
        MakeButton(menu, "SettingsButton", "Settings", new Vector2(0f, 0f), settings.Open);
        var pauseButton = MakeButton(menu, "PauseButton", "Pause", new Vector2(0f, -75f), pause.Toggle);
        pause.label = pauseButton.GetComponentInChildren<Text>();
        MakeButton(menu, "ReportButton", "Report", new Vector2(0f, -150f), report.Build);

        var shopPanel = MakePanel(canvas, "ShopPanel", new Vector2(80f, 0f), new Vector2(620f, 480f));
        shop.panel = shopPanel.gameObject;
        MakeLabel(shopPanel, "Title", "Shop", new Vector2(0f, 200f), new Vector2(560f, 40f), 30);
        MakeButton(shopPanel, "BuyButton", "Buy Potion (50)", new Vector2(0f, 100f), shop.Buy);
        MakeButton(shopPanel, "CloseButton", "Close", new Vector2(0f, 25f), shop.Close);
        var rewardRect = MakePanel(shopPanel, "RewardPanel", new Vector2(0f, -140f), new Vector2(520f, 140f));
        rewardRect.GetComponent<Image>().color = new Color(0.3f, 0.25f, 0.1f, 0.95f);
        MakeLabel(rewardRect, "RewardText", "Daily reward: 100 coins", new Vector2(0f, 35f), new Vector2(480f, 36f), 22);
        var reward = rewardRect.gameObject.AddComponent<RewardPanel>();
        reward.shop = shop;
        reward.claimButton = (RectTransform)MakeButton(rewardRect, "ClaimButton", "Claim reward", new Vector2(0f, -25f), reward.Claim).transform;
        shopPanel.gameObject.SetActive(false);

        var settingsPanel = MakePanel(canvas, "SettingsPanel", new Vector2(80f, 0f), new Vector2(500f, 300f));
        settings.panel = settingsPanel.gameObject;
        MakeLabel(settingsPanel, "Title", "Settings", new Vector2(0f, 100f), new Vector2(440f, 40f), 30);
        MakeLabel(settingsPanel, "VolumeLabel", "Volume: --", new Vector2(0f, 20f), new Vector2(440f, 36f), 22);
        MakeButton(settingsPanel, "BackButton", "Back", new Vector2(0f, -90f), settings.Close);
        settingsPanel.gameObject.SetActive(false);

        var overlay = MakeRect(canvas, "QuestDialog", Vector2.zero, Vector2.zero, Vector2.zero);
        overlay.anchorMin = Vector2.zero;
        overlay.anchorMax = Vector2.one;
        overlay.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
        var dialog = overlay.gameObject.AddComponent<DialogBox>();
        var window = MakePanel(overlay, "Window", Vector2.zero, new Vector2(720f, 300f));
        dialog.body = MakeLabel(window, "Body", "", new Vector2(0f, 40f), new Vector2(660f, 160f), 24);
        dialog.continueButton = MakeButton(window, "ContinueButton", "Continue", new Vector2(220f, -100f), dialog.Continue).gameObject;
        dialog.closeButton = MakeButton(window, "CloseButton", "Close", new Vector2(220f, -100f), dialog.Close).gameObject;
        UnityEventTools.AddPersistentListener(menu.Find("QuestButton").GetComponent<Button>().onClick, dialog.Show);
        dialog.closeButton.SetActive(false);
        overlay.gameObject.SetActive(false);

        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
    }

    static RectTransform MakeRect(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    static RectTransform MakePanel(Transform parent, string name, Vector2 pos, Vector2 size)
    {
        var rt = MakeRect(parent, name, new Vector2(0.5f, 0.5f), pos, size);
        rt.gameObject.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.15f, 0.95f);
        return rt;
    }

    static Text MakeLabel(Transform parent, string name, string text, Vector2 pos, Vector2 size, int fontSize)
    {
        var t = MakeRect(parent, name, new Vector2(0.5f, 0.5f), pos, size).gameObject.AddComponent<Text>();
        t.font = font;
        t.fontSize = fontSize;
        t.text = text;
        t.color = Color.white;
        t.alignment = TextAnchor.MiddleCenter;
        t.raycastTarget = false;
        return t;
    }

    static Button MakeButton(Transform parent, string name, string text, Vector2 pos, UnityAction onClick)
    {
        var rt = MakeRect(parent, name, new Vector2(0.5f, 0.5f), pos, new Vector2(240f, 56f));
        rt.gameObject.AddComponent<Image>().color = new Color(0.25f, 0.4f, 0.7f);
        var button = rt.gameObject.AddComponent<Button>();
        if (onClick != null) UnityEventTools.AddPersistentListener(button.onClick, onClick);
        MakeLabel(rt, "Text", text, Vector2.zero, new Vector2(230f, 50f), 22);
        return button;
    }
}
