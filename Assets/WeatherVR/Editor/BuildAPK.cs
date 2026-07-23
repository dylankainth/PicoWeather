using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace WeatherVR.EditorTools
{
    /// <summary>
    /// One-command Android build.
    ///
    /// Every setting the build depends on is asserted immediately before it runs
    /// rather than trusted to be right in the project file, because the settings that
    /// break an XR build — a missing scripting define, a stripped shader, the wrong
    /// architecture — all fail silently at build time and only show up on the
    /// headset.
    /// </summary>
    public static class BuildAPK
    {
        const string OutputDirectory = "Builds";
        const string OutputName = "ImmersiveWeather.apk";

        [MenuItem("Tools/WeatherVR/Build APK", priority = 40)]
        public static void BuildInteractive()
        {
            string path = Build(out BuildReport report);

            if (report != null && report.summary.result == BuildResult.Succeeded)
            {
                EditorUtility.DisplayDialog(
                    "WeatherVR",
                    $"Build succeeded.\n\n{path}\n\n" +
                    $"{report.summary.totalSize / (1024 * 1024)} MB in " +
                    $"{report.summary.totalTime.TotalSeconds:F0} s.\n\n" +
                    "Install with:\n  adb install -r \"" + path + "\"",
                    "OK");
                EditorUtility.RevealInFinder(path);
            }
            else
            {
                string message = report == null
                    ? "The build did not start. See the console."
                    : $"Build {report.summary.result} with {report.summary.totalErrors} error(s). " +
                      "See the console.";
                EditorUtility.DisplayDialog("WeatherVR", message, "OK");
            }
        }

        /// <summary>
        /// Runs the build. Safe to call from the command line via
        /// <c>-executeMethod WeatherVR.EditorTools.BuildAPK.BuildFromCommandLine</c>.
        /// </summary>
        public static string Build(out BuildReport report)
        {
            report = null;

            ProjectConfigurator.Configure();
            ProjectConfigurator.EnsureAlwaysIncludedShaders();

            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Debug.LogError("[WeatherVR] No enabled scenes in Build Settings. " +
                               "Run Tools ▸ WeatherVR ▸ Build Scene first.");
                return null;
            }

            if (!scenes.Contains(SceneBuilder.ScenePath))
            {
                Debug.LogWarning($"[WeatherVR] {SceneBuilder.ScenePath} is not in the build. " +
                                 "The APK will launch into whatever scene is at index 0.");
            }

            Directory.CreateDirectory(OutputDirectory);
            string outputPath = Path.Combine(OutputDirectory, OutputName);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            };

            Debug.Log($"[WeatherVR] Building {scenes.Length} scene(s) to {outputPath}…");
            report = BuildPipeline.BuildPlayer(options);

            var summary = report.summary;
            Debug.Log($"[WeatherVR] Build {summary.result}: " +
                      $"{summary.totalSize / (1024 * 1024)} MB, " +
                      $"{summary.totalTime.TotalSeconds:F0} s, " +
                      $"{summary.totalErrors} error(s), {summary.totalWarnings} warning(s).");

            return summary.result == BuildResult.Succeeded ? outputPath : null;
        }

        /// <summary>
        /// Batch-mode entry point. Exits with a non-zero code on failure so CI and
        /// shell scripts can tell whether the build actually worked.
        /// </summary>
        public static void BuildFromCommandLine()
        {
            try
            {
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                {
                    Debug.Log("[WeatherVR] Switching the active build target to Android…");
                    EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
                }

                SceneBuilder.EnsureConfigAsset();

                string path = Build(out BuildReport report);
                bool succeeded = report != null && report.summary.result == BuildResult.Succeeded;

                if (Application.isBatchMode)
                    EditorApplication.Exit(succeeded ? 0 : 1);
                else if (succeeded)
                    Debug.Log($"[WeatherVR] APK at {path}.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[WeatherVR] Build threw: {e}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// Batch-mode entry point that rebuilds the scene and then the APK, for a
        /// clean machine with nothing set up.
        /// </summary>
        public static void BuildEverythingFromCommandLine()
        {
            try
            {
                var config = SceneBuilder.EnsureConfigAsset();
                SceneBuilder.BuildAndSaveSilent(config);
                BuildFromCommandLine();
            }
            catch (Exception e)
            {
                Debug.LogError($"[WeatherVR] Full build threw: {e}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
        }
    }
}
