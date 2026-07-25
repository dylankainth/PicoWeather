using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.UI;
using WeatherVR.Core;
using WeatherVR.Phone;

namespace WeatherVR.EditorTools
{
    /// <summary>
    /// The non-AR phone build: the weather map rendered as an ordinary 3D scene,
    /// navigated by touch.
    ///
    /// Deliberately has no XR runtime and no ARCore. The AR attempt depended on
    /// plane detection working on the user's desk, in their lighting, on their
    /// device — a lot of ways to end up staring at a camera feed with nothing in it.
    /// This build has none of those dependencies: it draws the same scene the headset
    /// draws and lets you orbit it with your fingers.
    /// </summary>
    public static class BuildPhoneTouch
    {
        public const string ScenePath = "Assets/Scenes/WeatherVR_Phone.unity";
        const string OutputPath = "Builds/ImmersiveWeatherPhone.apk";

        const string ArCoreLoader = "UnityEngine.XR.ARCore.ARCoreLoader";
        const string PicoLoader = "ByteDance.PICO.XR.PXR_Loader";

        [MenuItem("Tools/WeatherVR/Build Phone Touch Scene", priority = 3)]
        public static void BuildSceneInteractive()
        {
            BuildSceneSilent(SceneBuilder.EnsureConfigAsset());
            EditorUtility.DisplayDialog("WeatherVR",
                $"Phone touch scene built at {ScenePath}.", "OK");
        }

        public static void BuildSceneSilent(AppConfig config)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ------------------------------------------------------ plain camera
            var cameraObject = new GameObject("MainCamera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.fieldOfView = 55f;          // a phone-ish field of view
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 100f;
            camera.depthTextureMode = DepthTextureMode.Depth;  // the cloud raymarch needs it
            cameraObject.AddComponent<AudioListener>();

            // ------------------------------------------------- weather content
            var content = SceneBuilder.BuildWeatherContent(config);
            content.MapRoot.position = Vector3.zero;

            // The studio sky gives the scene a backdrop instead of flat grey.
            var skyShader = Shader.Find("WeatherVR/StudioSky");
            if (skyShader != null)
            {
                var sky = new Material(skyShader) { name = "PhoneSky" };
                AssetDatabase.CreateAsset(sky, "Assets/WeatherVR/Prefabs/PhoneSky.mat");
                RenderSettings.skybox = sky;
            }

            var hint = BuildHintOverlay();

            var viewer = cameraObject.AddComponent<TouchViewerController>();
            viewer.MapRoot = content.MapRoot;
            viewer.HintText = hint;

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[WeatherVR] Phone touch scene saved to {ScenePath}.");
        }

        static Text BuildHintOverlay()
        {
            var canvasObject = new GameObject("UI");
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasObject.AddComponent<CanvasScaler>().uiScaleMode =
                CanvasScaler.ScaleMode.ScaleWithScreenSize;

            var textObject = new GameObject("Hint");
            textObject.transform.SetParent(canvasObject.transform, false);
            var text = textObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 28;
            text.alignment = TextAnchor.LowerCenter;
            text.color = new Color(1f, 1f, 1f, 0.85f);
            text.text = "One finger: orbit    Two fingers: pinch to zoom, drag to pan";

            var rect = text.rectTransform;
            rect.anchorMin = new Vector2(0.03f, 0.02f);
            rect.anchorMax = new Vector2(0.97f, 0.12f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return text;
        }

        /// <summary>
        /// Turns XR off entirely for Android. Without this the PICO loader would try
        /// to bring up a headset runtime on a phone, and ARCore would ask for a
        /// camera it does not need.
        ///
        /// This method's output — an empty Android loader list with the XR manager
        /// disabled — was once committed onto the PICO mainline (the touch build ran,
        /// its config change got checked in, and nothing put PICO's loader back). The
        /// headset APK then silently built and launched as a flat 2D panel with no
        /// stereo, no head tracking and no controllers. It is safe to keep calling this
        /// for the touch build specifically because
        /// <see cref="ProjectConfigurator.EnsureAndroidXrLoader"/> now re-asserts the
        /// PICO loader at the top of every <c>Configure()</c> call, so a headset build
        /// run after this one repairs itself automatically.
        /// </summary>
        static void DisableXr()
        {
            var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            if (settings?.Manager == null) return;

            foreach (string loader in new[] { ArCoreLoader, PicoLoader })
            {
                if (XRPackageMetadataStore.IsLoaderAssigned(loader, BuildTargetGroup.Android))
                {
                    XRPackageMetadataStore.RemoveLoader(settings.Manager, loader, BuildTargetGroup.Android);
                    Debug.Log($"[WeatherVR] Removed XR loader {loader} for the touch build.");
                }
            }

            settings.InitManagerOnStart = false;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log("[WeatherVR] XR disabled for Android (plain 3D phone build).");
        }

        public static string Build(out BuildReport report)
        {
            PicoBuildTarget.IsPico = false;

            ProjectConfigurator.Configure();
            ProjectConfigurator.EnsureAlwaysIncludedShaders();
            DisableXr();

            var config = SceneBuilder.EnsureConfigAsset();
            BuildSceneSilent(config);

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            };

            Debug.Log($"[WeatherVR] Building phone touch APK to {OutputPath}…");
            report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[WeatherVR] Phone touch build {report.summary.result}: " +
                      $"{report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalErrors} error(s).");

            return report.summary.result == BuildResult.Succeeded ? OutputPath : null;
        }

        [MenuItem("Tools/WeatherVR/Build Phone Touch APK", priority = 44)]
        public static void BuildInteractive()
        {
            string path = Build(out BuildReport report);
            bool ok = report != null && report.summary.result == BuildResult.Succeeded;
            EditorUtility.DisplayDialog("WeatherVR",
                ok ? $"Phone build succeeded.\n\n{path}" : "Phone build failed — see the console.", "OK");
        }

        public static void BuildFromCommandLine()
        {
            try
            {
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                    EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

                string path = Build(out BuildReport report);
                bool ok = report != null && report.summary.result == BuildResult.Succeeded;
                if (ok) Debug.Log($"[WeatherVR] Phone touch APK at {path}");
                if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
            }
            catch (Exception e)
            {
                Debug.LogError($"[WeatherVR] Phone touch build threw: {e}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
        }
    }
}
