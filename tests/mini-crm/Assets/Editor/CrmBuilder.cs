using System;
using System.IO;
using System.Linq;
using Brightdesk.Crm.UI;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Brightdesk.Crm.EditorTools
{
    public static class CrmBuilder
    {
        const string ScenePath = "Assets/Scenes/Crm.unity";
        const string ThemePath = "Assets/UI/CrmTheme.tss";
        const string PanelSettingsPath = "Assets/UI/CrmPanelSettings.asset";
        const string StylesPath = "Assets/UI/Crm.uss";
        const string FileName = "BrightdeskCRM";

        [MenuItem("Brightdesk/Generate CRM Scene")]
        public static void GenerateScene()
        {
            ConfigurePlayer();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera", typeof(Camera)).GetComponent<Camera>();
            cam.tag = "MainCamera";
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color32(244, 246, 249, 255);
            cam.orthographic = true;

            var go = new GameObject("CrmApp");
            go.SetActive(false);
            go.AddComponent<UIDocument>().panelSettings = LoadOrCreatePanelSettings();
            go.AddComponent<CrmApp>().styles = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylesPath);
            go.SetActive(true);

            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[CrmBuilder] scene saved to " + ScenePath);
        }

        static PanelSettings LoadOrCreatePanelSettings()
        {
            if (!File.Exists(ThemePath))
            {
                File.WriteAllText(ThemePath, "@import url(\"unity-theme://default\");\n");
                AssetDatabase.ImportAsset(ThemePath);
            }
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(settings, PanelSettingsPath);
            }
            settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1280, 800);
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.match = 0.5f;
            EditorUtility.SetDirty(settings);
            return settings;
        }

        static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "Brightdesk";
            PlayerSettings.productName = "Brightdesk CRM";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, "com.brightdesk.crm");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 800;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            PlayerSettings.usePlayerLog = true;
            PlayerSettings.SplashScreen.show = false;
        }

        [MenuItem("Brightdesk/Build All Players")]
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
            Debug.Log("[CrmBuilder] " + target + " -> " + path + ": " + report.summary.result + ", " + report.summary.totalSize / 1048576 + " MB");
            if (report.summary.result != BuildResult.Succeeded && Application.isBatchMode) EditorApplication.Exit(1);
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
