using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BuildArena
{
    const string Scene = "Assets/Arena.unity";

    public static void Mac() => Build(BuildTarget.StandaloneOSX, "Mac/ZombieArena.app");
    public static void Linux() => Build(BuildTarget.StandaloneLinux64, "Linux/ZombieArena.x86_64");
    public static void Windows() => Build(BuildTarget.StandaloneWindows64, "Windows/ZombieArena.exe");

    static void Build(BuildTarget target, string path)
    {
        Configure();
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { Scene },
            locationPathName = "Builds/" + path,
            target = target,
            options = BuildOptions.Development,
        });
        var s = report.summary;
        Debug.Log($"BuildArena {target}: {s.result}, {s.totalErrors} error(s), {s.totalWarnings} warning(s) -> {s.outputPath}");
        if (Application.isBatchMode && s.result != BuildResult.Succeeded) EditorApplication.Exit(1);
    }

    static void Configure()
    {
        if (!File.Exists(Scene)) EditorSceneManager.SaveScene(EditorSceneManager.NewScene(NewSceneSetup.EmptyScene), Scene);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(Scene, true) };
        PlayerSettings.productName = "ZombieArena";
        PlayerSettings.runInBackground = true;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);

        var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
        settings.FindProperty("activeInputHandler").intValue = 2;
        settings.ApplyModifiedProperties();

        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var tags = tagManager.FindProperty("tags");
        foreach (var tag in new[] { "Zombie", "Chicken" })
        {
            bool found = false;
            for (int i = 0; i < tags.arraySize; i++) found |= tags.GetArrayElementAtIndex(i).stringValue == tag;
            if (found) continue;
            tags.InsertArrayElementAtIndex(tags.arraySize);
            tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
        }
        tagManager.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
    }
}
