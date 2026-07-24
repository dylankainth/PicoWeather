using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace WeatherVR.EditorTools
{
    /// <summary>
    /// Builds a flat Windows desktop version so the app can be tried on a plain PC,
    /// without a headset. <see cref="WeatherVR.Desktop.DesktopPreview"/> supplies the
    /// mouse-and-keyboard camera at runtime; this just produces the executable.
    /// </summary>
    public static class BuildDesktop
    {
        const string OutputDir = "Builds/Desktop";
        const string ExeName = "ImmersiveWeather.exe";

        [MenuItem("Tools/WeatherVR/Build Desktop Preview (Windows)", priority = 41)]
        public static void BuildInteractive()
        {
            string path = Build(out BuildReport report);
            bool ok = report != null && report.summary.result == BuildResult.Succeeded;
            EditorUtility.DisplayDialog("WeatherVR",
                ok ? $"Desktop build succeeded.\n\n{path}\n\nDouble-click it to run."
                   : "Desktop build failed — see the console.",
                "OK");
            if (ok) EditorUtility.RevealInFinder(Path.GetFullPath(path));
        }

        public static string Build(out BuildReport report)
        {
            report = null;

            SceneBuilder.EnsureConfigAsset();
            if (!File.Exists(SceneBuilder.ScenePath))
                SceneBuilder.BuildAndSaveSilent(SceneBuilder.EnsureConfigAsset());

            // Shaders looked up by name must survive stripping here too.
            ProjectConfigurator.EnsureAlwaysIncludedShaders();

            string[] scenes = { SceneBuilder.ScenePath };
            Directory.CreateDirectory(OutputDir);
            string outputPath = Path.Combine(OutputDir, ExeName);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                // Windowed and non-fullscreen by default so it is easy to alt-tab out of.
                options = BuildOptions.None
            };

            Debug.Log($"[WeatherVR] Building Windows desktop preview to {outputPath}…");
            report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log($"[WeatherVR] Desktop build {summary.result}: " +
                      $"{summary.totalSize / (1024 * 1024)} MB, {summary.totalErrors} error(s).");

            return summary.result == BuildResult.Succeeded ? outputPath : null;
        }

        /// <summary>Batch entry point. Switches to Windows, builds, exits non-zero on failure.</summary>
        public static void BuildFromCommandLine()
        {
            try
            {
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                {
                    Debug.Log("[WeatherVR] Switching active build target to StandaloneWindows64…");
                    EditorUserBuildSettings.SwitchActiveBuildTarget(
                        BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
                }

                var config = SceneBuilder.EnsureConfigAsset();
                SceneBuilder.BuildAndSaveSilent(config);

                string path = Build(out BuildReport report);
                bool ok = report != null && report.summary.result == BuildResult.Succeeded;
                if (ok) Debug.Log($"[WeatherVR] Desktop preview at {path}");
                if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
            }
            catch (Exception e)
            {
                Debug.LogError($"[WeatherVR] Desktop build threw: {e}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
        }
    }
}
