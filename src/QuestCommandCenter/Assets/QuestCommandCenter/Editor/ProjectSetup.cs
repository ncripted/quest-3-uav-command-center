using System;
using System.IO;
using System.Linq;
using QuestCommandCenter.Adapters.DjiSpark;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Meta;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

namespace QuestCommandCenter.Editor
{
    public static class ProjectSetup
    {
        private const string ApplicationId = "com.dronecommand.quest";
        private const string ScenePath = "Assets/QuestCommandCenter/Scenes/QuestShell.unity";
        private const string OpenXrLoaderType = "UnityEngine.XR.OpenXR.OpenXRLoader";

        public static void ConfigureProject()
        {
            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
            {
                throw new InvalidOperationException("Unity could not switch the active build target to Android.");
            }

            ConfigurePlayerSettings();
            ConfigureXr();
            CreateShellScene();
            AssetDatabase.SaveAssets();
            Debug.Log("Quest Command Center project configuration completed.");
        }

        public static void ConfigureAndBuild()
        {
            ConfigureProject();

            var outputDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../Builds"));
            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, "QuestCommandCenter-dev.apk");
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = outputPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.Development
            };

            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Android build failed: {report.summary.result}, {report.summary.totalErrors} errors.");
            }

            Debug.Log($"Quest Command Center APK: {outputPath} ({report.summary.totalSize} bytes)");
        }

        private static void ConfigurePlayerSettings()
        {
            PlayerSettings.companyName = "Quest Command Center";
            PlayerSettings.productName = "Quest Command Center";
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, ApplicationId);
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });

            var projectSettingsObjects = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (projectSettingsObjects.Length > 0)
            {
                var serializedSettings = new SerializedObject(projectSettingsObjects[0]);
                var activeInputHandler = serializedSettings.FindProperty("activeInputHandler");
                if (activeInputHandler != null)
                {
                    activeInputHandler.intValue = 1;
                    serializedSettings.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }

        private static void ConfigureXr()
        {
            XRGeneralSettingsPerBuildTarget perTargetSettings;
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out perTargetSettings)
                || perTargetSettings == null)
            {
                EnsureAssetFolder("Assets/XR/Settings");
                perTargetSettings = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(
                    perTargetSettings,
                    "Assets/XR/Settings/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTargetSettings, true);
            }

            if (!perTargetSettings.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
            {
                perTargetSettings.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            }

            var generalSettings = perTargetSettings.SettingsForBuildTarget(BuildTargetGroup.Android);
            generalSettings.InitManagerOnStart = true;
            if (!XRPackageMetadataStore.AssignLoader(
                    generalSettings.Manager,
                    OpenXrLoaderType,
                    BuildTargetGroup.Android))
            {
                throw new InvalidOperationException("Could not assign the OpenXR loader for Android.");
            }

            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
            var openXrSettings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (openXrSettings == null)
            {
                throw new InvalidOperationException("OpenXR settings were not created for Android.");
            }

            var metaQuestFeature = EnableFeature<MetaQuestFeature>(openXrSettings);
            ConfigureQuestThreeTarget(metaQuestFeature);
            EnableFeature<ARSessionFeature>(openXrSettings);
            EnableFeature<ARCameraFeature>(openXrSettings);
            EnableFeature<MetaQuestTouchPlusControllerProfile>(openXrSettings);
            EnableFeature<OculusTouchControllerProfile>(openXrSettings);
            EditorUtility.SetDirty(generalSettings);
            EditorUtility.SetDirty(openXrSettings);
        }

        private static T EnableFeature<T>(OpenXRSettings settings) where T : UnityEngine.XR.OpenXR.Features.OpenXRFeature
        {
            var feature = settings.GetFeature<T>();
            if (feature == null)
            {
                throw new InvalidOperationException($"OpenXR feature {typeof(T).Name} is unavailable.");
            }

            feature.enabled = true;
            EditorUtility.SetDirty(feature);
            return feature;
        }

        private static void ConfigureQuestThreeTarget(MetaQuestFeature feature)
        {
            var serializedFeature = new SerializedObject(feature);
            var targetDevices = serializedFeature.FindProperty("targetDevices");
            for (var index = 0; targetDevices != null && index < targetDevices.arraySize; index++)
            {
                var targetDevice = targetDevices.GetArrayElementAtIndex(index);
                var manifestName = targetDevice.FindPropertyRelative("manifestName");
                var enabled = targetDevice.FindPropertyRelative("enabled");
                enabled.boolValue = manifestName != null && manifestName.stringValue == "eureka";
            }

            var optimizeBufferDiscards = serializedFeature.FindProperty("optimizeBufferDiscards");
            if (optimizeBufferDiscards != null)
            {
                optimizeBufferDiscards.boolValue = false;
            }

            serializedFeature.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void CreateShellScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "QuestShell";

            var originObject = new GameObject("XR Origin");
            var origin = originObject.AddComponent<XROrigin>();
            origin.Origin = originObject;
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;

            var cameraOffset = new GameObject("Camera Offset");
            cameraOffset.transform.SetParent(originObject.transform, false);
            origin.CameraFloorOffsetObject = cameraOffset;

            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(cameraOffset.transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.015f, 0.025f, 0.04f, 0f);
            cameraObject.AddComponent<AudioListener>();
            ConfigureHeadTracking(cameraObject.AddComponent<TrackedPoseDriver>());
            cameraObject.AddComponent<ARCameraManager>();
            origin.Camera = camera;

            new GameObject("AR Session", typeof(ARSession), typeof(ARInputManager));

            var canvasObject = new GameObject("Command Center Canvas", typeof(RectTransform));
            // Field layout: a wide command-center board with a dedicated 16:9
            // video viewport. Persistent telemetry and controls live outside the
            // camera image so readable text never trades away flight visibility.
            canvasObject.transform.position = new Vector3(0f, 1.55f, 2.2f);
            canvasObject.transform.localScale = Vector3.one * 0.001f;
            var canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(2400f, 1200f);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 1f;
            canvasObject.AddComponent<GraphicRaycaster>();

            var background = CreatePanel(
                canvasObject.transform,
                "Command Center Background",
                Vector2.zero,
                new Vector2(2400f, 1200f));
            background.color = new Color(0.008f, 0.015f, 0.025f, 0.97f);

            var diagnosticsObject = new GameObject("DjiSdkDiagnosticBridge");
            diagnosticsObject.transform.SetParent(canvasObject.transform, false);
            var diagnostics = diagnosticsObject.AddComponent<DjiSdkDiagnosticBridge>();

            var videoFrame = CreatePanel(
                background.transform,
                "Unobstructed FPV Viewport",
                new Vector2(65f, 0f),
                new Vector2(1640f, 931f));
            videoFrame.color = new Color(0.02f, 0.035f, 0.055f, 1f);

            var fpvPlaceholder = CreateText(videoFrame.transform, "FPV Placeholder", 36, TextAnchor.MiddleCenter);
            SetRect(fpvPlaceholder.rectTransform, Vector2.zero, new Vector2(1620f, 911f));
            fpvPlaceholder.text = "WAITING FOR FPV VIDEO\n\nCommand-center HUD is available offline";
            fpvPlaceholder.color = new Color(0.26f, 0.34f, 0.43f, 1f);

            var videoSurface = CreateRawImage(
                videoFrame.transform,
                "Spark FPV Display",
                Vector2.zero,
                new Vector2(1620f, 911f));
            videoSurface.color = Color.white;
            videoSurface.uvRect = new Rect(0f, 1f, 1f, -1f);
            videoSurface.enabled = false;

            var topBar = CreatePanel(
                background.transform,
                "Top Flight Status",
                new Vector2(0f, 550f),
                new Vector2(2400f, 100f));
            topBar.color = new Color(0.008f, 0.018f, 0.028f, 1f);
            var vehicleText = CreateText(topBar.transform, "Vehicle Identity", 30, TextAnchor.MiddleLeft);
            SetRect(vehicleText.rectTransform, new Vector2(-750f, 0f), new Vector2(850f, 82f));
            vehicleText.color = new Color(0.55f, 1f, 0.72f, 1f);
            var topStatusText = CreateText(topBar.transform, "Top Telemetry", 23, TextAnchor.MiddleRight);
            SetRect(topStatusText.rectTransform, new Vector2(690f, 0f), new Vector2(960f, 82f));
            topStatusText.color = Color.white;

            var videoLabelText = CreateText(topBar.transform, "FPV Status", 22, TextAnchor.MiddleCenter);
            SetRect(videoLabelText.rectTransform, new Vector2(0f, 0f), new Vector2(420f, 82f));
            videoLabelText.color = new Color(0.65f, 0.72f, 0.82f, 1f);
            videoSurface.gameObject.AddComponent<QuestCommandCenter.UI.QuestFpvPresenter>().Initialize(videoSurface, diagnostics);

            var telemetryPanel = CreatePanel(
                background.transform,
                "Detailed Telemetry Panel",
                new Vector2(-985f, 0f),
                new Vector2(430f, 930f));
            telemetryPanel.color = new Color(0.008f, 0.018f, 0.028f, 1f);
            var telemetryDetailText = CreateText(
                telemetryPanel.transform,
                "Detailed Telemetry",
                22,
                TextAnchor.UpperLeft);
            telemetryDetailText.rectTransform.anchorMin = Vector2.zero;
            telemetryDetailText.rectTransform.anchorMax = Vector2.one;
            telemetryDetailText.rectTransform.offsetMin = new Vector2(20f, 18f);
            telemetryDetailText.rectTransform.offsetMax = new Vector2(-20f, -18f);
            telemetryDetailText.lineSpacing = 1.08f;

            var leftRail = CreatePanel(
                background.transform,
                "Flight Action Rail",
                new Vector2(1055f, 290f),
                new Vector2(270f, 350f));
            leftRail.color = new Color(0.008f, 0.018f, 0.028f, 1f);
            var leftRailText = CreateText(leftRail.transform, "Locked Flight Actions", 21, TextAnchor.UpperCenter);
            leftRailText.rectTransform.anchorMin = Vector2.zero;
            leftRailText.rectTransform.anchorMax = Vector2.one;
            leftRailText.rectTransform.offsetMin = new Vector2(8f, 10f);
            leftRailText.rectTransform.offsetMax = new Vector2(-8f, -14f);
            leftRailText.text = "↓\nTAKEOFF\nLOCKED\n\n⌂  RTH\nLOCKED\n\n◎  LAND\nLOCKED";
            leftRailText.color = new Color(0.9f, 0.94f, 0.98f, 1f);

            var rightRail = CreatePanel(
                background.transform,
                "Camera Action Rail",
                new Vector2(1055f, -175f),
                new Vector2(270f, 550f));
            rightRail.color = new Color(0.008f, 0.018f, 0.028f, 1f);
            var cameraControlText = CreateText(
                rightRail.transform,
                "Camera Controls",
                20,
                TextAnchor.UpperCenter);
            cameraControlText.rectTransform.anchorMin = Vector2.zero;
            cameraControlText.rectTransform.anchorMax = Vector2.one;
            cameraControlText.rectTransform.offsetMin = new Vector2(5f, 8f);
            cameraControlText.rectTransform.offsetMax = new Vector2(-5f, -14f);
            rightRail.gameObject
                .AddComponent<QuestCommandCenter.UI.QuestCameraControlPanel>()
                .Initialize(cameraControlText, diagnostics);

            var alertPanel = CreatePanel(
                background.transform,
                "Critical Alert Panel",
                new Vector2(65f, 489f),
                new Vector2(1640f, 42f));
            alertPanel.color = new Color(0.12f, 0.055f, 0.015f, 1f);
            var alertText = CreateText(alertPanel.transform, "Critical Alert", 21, TextAnchor.MiddleCenter);
            alertText.rectTransform.anchorMin = Vector2.zero;
            alertText.rectTransform.anchorMax = Vector2.one;
            alertText.rectTransform.offsetMin = new Vector2(10f, 4f);
            alertText.rectTransform.offsetMax = new Vector2(-10f, -4f);

            var flightControlBadge = CreatePanel(
                background.transform,
                "Flight Control Badge",
                new Vector2(65f, -489f),
                new Vector2(1640f, 42f));
            flightControlBadge.color = new Color(0.025f, 0.075f, 0.13f, 1f);
            var flightControlBadgeText = CreateText(
                flightControlBadge.transform,
                "Flight Control Badge Text",
                21,
                TextAnchor.MiddleCenter);
            flightControlBadgeText.rectTransform.anchorMin = Vector2.zero;
            flightControlBadgeText.rectTransform.anchorMax = Vector2.one;
            flightControlBadgeText.rectTransform.offsetMin = new Vector2(10f, 3f);
            flightControlBadgeText.rectTransform.offsetMax = new Vector2(-10f, -3f);

            var preflightPanel = CreatePanel(
                background.transform,
                "Integrated Preflight Panel",
                new Vector2(65f, 0f),
                new Vector2(760f, 430f));
            preflightPanel.color = new Color(0.006f, 0.012f, 0.02f, 0.97f);
            var preflightText = CreateText(
                preflightPanel.transform,
                "Integrated Preflight Text",
                23,
                TextAnchor.UpperLeft);
            preflightText.rectTransform.anchorMin = Vector2.zero;
            preflightText.rectTransform.anchorMax = Vector2.one;
            preflightText.rectTransform.offsetMin = new Vector2(20f, 16f);
            preflightText.rectTransform.offsetMax = new Vector2(-20f, -16f);
            preflightText.lineSpacing = 1.05f;
            preflightText.color = new Color(0.95f, 0.97f, 1f, 1f);

            var bottomBar = CreatePanel(
                background.transform,
                "Bottom Primary Telemetry",
                new Vector2(0f, -550f),
                new Vector2(2400f, 100f));
            bottomBar.color = new Color(0.008f, 0.018f, 0.028f, 1f);
            var bottomStatusText = CreateText(bottomBar.transform, "Primary Flight Telemetry", 29, TextAnchor.MiddleCenter);
            bottomStatusText.rectTransform.anchorMin = Vector2.zero;
            bottomStatusText.rectTransform.anchorMax = Vector2.one;
            bottomStatusText.rectTransform.offsetMin = new Vector2(20f, 4f);
            bottomStatusText.rectTransform.offsetMax = new Vector2(-20f, -4f);
            bottomStatusText.color = Color.white;

            // The reticle is the only persistent graphic drawn over live video.
            CreateReticle(videoFrame.transform);

            var dashboard = background.gameObject
                .AddComponent<QuestCommandCenter.UI.QuestDashboardPresenter>();
            dashboard.Initialize(
                    vehicleText,
                    topStatusText,
                    bottomStatusText,
                    null,
                    alertText,
                    videoLabelText,
                    null,
                    telemetryDetailText,
                    diagnostics);
            diagnosticsObject
                .AddComponent<QuestCommandCenter.XRInput.QuestCameraInput>()
                .Initialize(diagnostics);
            var controllerInput = diagnosticsObject
                .AddComponent<QuestCommandCenter.XRInput.QuestControllerInput>();
            var simulatorRelay = diagnosticsObject
                .AddComponent<QuestCommandCenter.XRInput.DesktopSimulatorRelay>();
            simulatorRelay.Initialize(controllerInput);
            controllerInput.Initialize(simulatorRelay);
            var simulatorFlightActionCoordinator = diagnosticsObject
                .AddComponent<QuestCommandCenter.XRInput.QuestFlightActionCoordinator>();
            simulatorFlightActionCoordinator.Initialize(controllerInput, simulatorRelay);
            // Retained only for compatibility with the launch-only shadow path.
            // QuestManualFlightCoordinator is the single production authority.
            simulatorFlightActionCoordinator.enabled = false;
            var manualFlightCoordinator = diagnosticsObject
                .AddComponent<QuestCommandCenter.XRInput.QuestManualFlightCoordinator>();
            manualFlightCoordinator.Initialize(
                controllerInput,
                diagnostics,
                diagnostics,
                diagnostics,
                simulatorRelay,
                simulatorFlightActionCoordinator);
            dashboard.InitializeFlightActionSource(manualFlightCoordinator);
            dashboard.InitializeControlStateSource(manualFlightCoordinator);
            flightControlBadge.gameObject
                .AddComponent<QuestCommandCenter.UI.QuestManualFlightStatusPanel>()
                .Initialize(
                    flightControlBadgeText,
                    flightControlBadge,
                    preflightText,
                    preflightPanel.gameObject,
                    leftRailText,
                    manualFlightCoordinator);
            diagnosticsObject
                .AddComponent<QuestCommandCenter.XRInput.QuestFlightSessionLogger>()
                .Initialize(
                    diagnostics,
                    controllerInput,
                    manualFlightCoordinator,
                    manualFlightCoordinator);
            diagnosticsObject
                .AddComponent<QuestCommandCenter.XRInput.QuestHardwareShadowControl>()
                .Initialize(
                    controllerInput,
                    diagnostics,
                    simulatorRelay,
                    simulatorFlightActionCoordinator,
                    manualFlightCoordinator);
            diagnosticsObject
                .AddComponent<QuestCommandCenter.XRInput.QuestHeadGimbalInput>()
                .Initialize(camera.transform, diagnostics);

            var eventSystem = new GameObject("Event System");
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<InputSystemUIInputModule>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        private static void ConfigureHeadTracking(TrackedPoseDriver driver)
        {
            driver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            driver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
            driver.ignoreTrackingState = false;
            driver.positionInput = CreateAction("Head Position", "Vector3", "<XRHMD>/centerEyePosition");
            driver.rotationInput = CreateAction("Head Rotation", "Quaternion", "<XRHMD>/centerEyeRotation");
            driver.trackingStateInput = CreateAction("Head Tracking State", "Integer", "<XRHMD>/trackingState");
        }

        private static InputActionProperty CreateAction(string name, string expectedControlType, string binding)
        {
            var action = new InputAction(name, InputActionType.Value, binding, expectedControlType: expectedControlType);
            return new InputActionProperty(action);
        }

        private static void CreateReticle(Transform parent)
        {
            var reticle = new GameObject("Flight Reticle", typeof(RectTransform));
            reticle.transform.SetParent(parent, false);
            var rect = reticle.GetComponent<RectTransform>();
            rect.anchoredPosition = new Vector2(0f, 15f);
            rect.sizeDelta = new Vector2(180f, 180f);

            var green = new Color(0.2f, 1f, 0.42f, 0.9f);
            CreateHudLine(reticle.transform, "Top Left Horizontal", new Vector2(-62f, 75f), new Vector2(42f, 5f), green);
            CreateHudLine(reticle.transform, "Top Left Vertical", new Vector2(-82f, 55f), new Vector2(5f, 42f), green);
            CreateHudLine(reticle.transform, "Top Right Horizontal", new Vector2(62f, 75f), new Vector2(42f, 5f), green);
            CreateHudLine(reticle.transform, "Top Right Vertical", new Vector2(82f, 55f), new Vector2(5f, 42f), green);
            CreateHudLine(reticle.transform, "Bottom Left Horizontal", new Vector2(-62f, -75f), new Vector2(42f, 5f), green);
            CreateHudLine(reticle.transform, "Bottom Left Vertical", new Vector2(-82f, -55f), new Vector2(5f, 42f), green);
            CreateHudLine(reticle.transform, "Bottom Right Horizontal", new Vector2(62f, -75f), new Vector2(42f, 5f), green);
            CreateHudLine(reticle.transform, "Bottom Right Vertical", new Vector2(82f, -55f), new Vector2(5f, 42f), green);
            CreateHudLine(reticle.transform, "Center Horizontal", Vector2.zero, new Vector2(26f, 3f), green);
            CreateHudLine(reticle.transform, "Center Vertical", Vector2.zero, new Vector2(3f, 26f), green);
        }

        private static void CreateMapGrid(Transform parent)
        {
            var gridColor = new Color(0.58f, 0.78f, 0.65f, 0.2f);
            CreateHudLine(parent, "Map Vertical 1", new Vector2(-82f, 0f), new Vector2(2f, 195f), gridColor);
            CreateHudLine(parent, "Map Vertical 2", new Vector2(0f, 0f), new Vector2(2f, 195f), gridColor);
            CreateHudLine(parent, "Map Vertical 3", new Vector2(82f, 0f), new Vector2(2f, 195f), gridColor);
            CreateHudLine(parent, "Map Horizontal 1", new Vector2(0f, -49f), new Vector2(330f, 2f), gridColor);
            CreateHudLine(parent, "Map Horizontal 2", new Vector2(0f, 49f), new Vector2(330f, 2f), gridColor);
        }

        private static Image CreateHudLine(
            Transform parent,
            string name,
            Vector2 position,
            Vector2 size,
            Color color)
        {
            var line = CreatePanel(parent, name, position, size);
            line.color = color;
            return line;
        }

        private static Image CreatePanel(Transform parent, string name, Vector2 position, Vector2 size)
        {
            var panelObject = new GameObject(name, typeof(RectTransform));
            panelObject.transform.SetParent(parent, false);
            var rect = panelObject.GetComponent<RectTransform>();
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return panelObject.AddComponent<Image>();
        }

        private static RawImage CreateRawImage(Transform parent, string name, Vector2 position, Vector2 size)
        {
            var imageObject = new GameObject(name, typeof(RectTransform));
            imageObject.transform.SetParent(parent, false);
            var rect = imageObject.GetComponent<RectTransform>();
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return imageObject.AddComponent<RawImage>();
        }

        private static Text CreateText(Transform parent, string name, int fontSize, TextAnchor alignment)
        {
            var textObject = new GameObject(name, typeof(RectTransform));
            textObject.transform.SetParent(parent, false);
            var text = textObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.fontStyle = FontStyle.Bold;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = true;
            var outline = textObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.92f);
            outline.effectDistance = new Vector2(1.4f, -1.4f);
            outline.useGraphicAlpha = true;
            return text;
        }

        private static void SetRect(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static void EnsureAssetFolder(string path)
        {
            var segments = path.Split('/');
            var current = segments[0];
            foreach (var segment in segments.Skip(1))
            {
                var next = current + "/" + segment;
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, segment);
                }
                current = next;
            }
        }
    }
}
