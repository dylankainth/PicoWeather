using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.Management;

namespace WeatherVR.EditorTools
{
    /// <summary>
    /// Builds the phone-AR variant: ARCore instead of PICO, ARCore's XR loader
    /// instead of PICO's, and the AR scene instead of the headset scene.
    ///
    /// The two Android variants cannot coexist in one build. XR Plug-in Management
    /// keeps one loader list per build target, and Android is the build target for
    /// both PICO and a phone — so this swaps the loader over, builds, and the PICO
    /// build swaps it back. That is why each variant has its own entry point rather
    /// than a shared one with a flag.
    /// </summary>
    public static class BuildPhoneAR
    {
        const string OutputPath = "Builds/ImmersiveWeatherAR.apk";
        const string ArDefine = "WEATHERVR_AR";

        const string ArCoreLoader = "UnityEngine.XR.ARCore.ARCoreLoader";
        const string PicoLoader = "ByteDance.PICO.XR.PXR_Loader";

        [MenuItem("Tools/WeatherVR/Build Phone AR APK", priority = 42)]
        public static void BuildInteractive()
        {
            string path = Build(out BuildReport report);
            bool ok = report != null && report.summary.result == BuildResult.Succeeded;
            EditorUtility.DisplayDialog("WeatherVR",
                ok ? $"Phone AR build succeeded.\n\n{path}\n\nInstall with:\n  adb install -r \"{path}\""
                   : "Phone AR build failed — see the console.", "OK");
            if (ok) EditorUtility.RevealInFinder(Path.GetFullPath(path));
        }

        /// <summary>
        /// Adds the WEATHERVR_AR define. Must happen before anything tries to compile
        /// the AR-only scripts, so it is its own batch step: adding a define triggers
        /// a domain reload, and code compiled in the same invocation would not see it.
        /// </summary>
        public static void EnableArDefineFromCommandLine()
        {
            var target = NamedBuildTarget.Android;
            var symbols = PlayerSettings.GetScriptingDefineSymbols(target)
                .Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            if (!symbols.Contains(ArDefine))
            {
                symbols.Add(ArDefine);
                PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", symbols));
                AssetDatabase.SaveAssets();
                Debug.Log($"[WeatherVR] Added {ArDefine} to Android defines.");
            }
            else
            {
                Debug.Log($"[WeatherVR] {ArDefine} already present.");
            }

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        public static string Build(out BuildReport report)
        {
            report = null;
            PicoBuildTarget.IsPico = false;

            ProjectConfigurator.Configure();
            ProjectConfigurator.EnsureAlwaysIncludedShaders();
            SwitchToArCore();

            string[] scenes;

#if WEATHERVR_AR
            var config = SceneBuilder.EnsureConfigAsset();
            ArSceneBuilder.BuildAndSaveSilent(config);
            scenes = new[] { ArSceneBuilder.ScenePath };
#else
            // The AR scripts only compile once WEATHERVR_AR is set, and setting a
            // define forces a domain reload, so enabling it has to be a separate
            // batch-mode invocation before this one.
            Debug.LogError("[WeatherVR] WEATHERVR_AR is not defined; run " +
                           "BuildPhoneAR.EnableArDefineFromCommandLine first.");
            return null;
#endif

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = OutputPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            };

            Debug.Log($"[WeatherVR] Building phone AR APK to {OutputPath}…");
            report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log($"[WeatherVR] Phone AR build {summary.result}: " +
                      $"{summary.totalSize / (1024 * 1024)} MB, {summary.totalErrors} error(s).");

            return summary.result == BuildResult.Succeeded ? OutputPath : null;
        }

        /// <summary>
        /// Makes ARCore the active Android XR loader and removes PICO's, which would
        /// otherwise try to initialise a headset runtime on a phone.
        /// </summary>
        static void SwitchToArCore()
        {
            var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            if (settings == null || settings.Manager == null)
            {
                Debug.LogWarning("[WeatherVR] No XR settings for Android; skipping loader swap.");
                return;
            }

            if (XRPackageMetadataStore.IsLoaderAssigned(PicoLoader, BuildTargetGroup.Android))
            {
                XRPackageMetadataStore.RemoveLoader(settings.Manager, PicoLoader, BuildTargetGroup.Android);
                Debug.Log("[WeatherVR] Removed the PICO XR loader for the phone build.");
            }

            if (!XRPackageMetadataStore.IsLoaderAssigned(ArCoreLoader, BuildTargetGroup.Android))
            {
                bool ok = XRPackageMetadataStore.AssignLoader(settings.Manager, ArCoreLoader, BuildTargetGroup.Android);
                Debug.Log($"[WeatherVR] Assigned the ARCore XR loader: {ok}");
            }

            // ARCore does not support Vulkan, and ProjectConfigurator sets Vulkan first
            // for the headset build — the player build hard-fails otherwise:
            //   "You have enabled the Vulkan graphics API, which is not supported by
            //    ARCore."
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
                new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
            Debug.Log("[WeatherVR] Graphics API set to OpenGLES3 only (ARCore requirement).");

            settings.InitManagerOnStart = true;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Puts the PICO loader back, for returning to headset builds. Delegates the
        /// loader swap itself to <see cref="ProjectConfigurator.EnsureAndroidXrLoader"/>
        /// — this used to reimplement the swap here and forgot to re-enable
        /// <c>InitManagerOnStart</c>, so running it by hand left the XR manager
        /// disabled even with the right loader assigned. One implementation now,
        /// same one <see cref="ProjectConfigurator.Configure"/> asserts on every
        /// headset build.
        /// </summary>
        [MenuItem("Tools/WeatherVR/Switch XR back to PICO", priority = 43)]
        public static void SwitchToPico()
        {
            var changes = new System.Collections.Generic.List<string>();
            ProjectConfigurator.EnsureAndroidXrLoader(changes);

            // Restore Vulkan for the headset build; the AR build forces GLES3-only.
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[]
            {
                UnityEngine.Rendering.GraphicsDeviceType.Vulkan,
                UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3
            });

            AssetDatabase.SaveAssets();
            foreach (string change in changes) Debug.Log($"[WeatherVR] {change}");
            Debug.Log("[WeatherVR] XR loader switched back to PICO, Vulkan restored.");
        }

        public static void BuildFromCommandLine()
        {
            try
            {
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                {
                    EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
                }

                string path = Build(out BuildReport report);
                bool ok = report != null && report.summary.result == BuildResult.Succeeded;
                if (ok) Debug.Log($"[WeatherVR] Phone AR APK at {path}");
                if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
            }
            catch (Exception e)
            {
                Debug.LogError($"[WeatherVR] Phone AR build threw: {e}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
        }
    }
}
