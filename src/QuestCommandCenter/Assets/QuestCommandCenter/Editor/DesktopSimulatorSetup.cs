using System;
using System.IO;
using QuestCommandCenter.DesktopSimulator;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuestCommandCenter.Editor
{
    public static class DesktopSimulatorSetup
    {
        private const string ScenePath = "Assets/QuestCommandCenter/Scenes/DesktopSimulator.unity";

        public static void ConfigureAndBuild()
        {
            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildTargetGroup.Standalone,
                    BuildTarget.StandaloneLinux64))
            {
                throw new InvalidOperationException("Unity could not switch to Linux standalone.");
            }

            PlayerSettings.companyName = "Quest Command Center";
            PlayerSettings.productName = "Quest Command Center Simulator";
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.runInBackground = true;
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "DesktopSimulator";
            new GameObject("Quest Command Center Desktop Simulator")
                .AddComponent<DesktopSimulatorApp>();
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();

            var outputDirectory = Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "../../../Builds/DesktopSimulator"));
            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, "QuestCommandCenterSimulator.x86_64");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = outputPath,
                target = BuildTarget.StandaloneLinux64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    "Desktop simulator build failed: "
                    + report.summary.result
                    + ", "
                    + report.summary.totalErrors
                    + " errors.");
            }

            Debug.Log("Desktop simulator executable: " + outputPath);
        }
    }
}
