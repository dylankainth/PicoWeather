using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace WeatherVR.EditorTools
{
    /// <summary>
    /// Puts the player settings into the state a standalone PICO/Quest build needs.
    ///
    /// This exists because the project as handed over had a genuine, silent bug:
    /// <c>ENABLE_PICO_XR_SDK</c> was defined for Standalone but not for Android, and
    /// every runtime file in the PICO SDK is wrapped in <c>#if ENABLE_PICO_XR_SDK</c>.
    /// The APK would therefore have built and installed with the entire SDK compiled
    /// out — no hand tracking, no PICO-specific features, and no compiler error to
    /// say so. Settings that matter get asserted in code so they cannot drift back.
    /// </summary>
    public static class ProjectConfigurator
    {
        public const string PicoDefine = "ENABLE_PICO_XR_SDK";
        public const string SpatializerDefine = "PICO_SPATIALIZER";

        const string DefaultBundleId = "com.weathervr.immersive";
        const string DefaultProductName = "Immersive Weather";
        const string DefaultCompanyName = "WeatherVR";

        [MenuItem("Tools/WeatherVR/Configure Player Settings", priority = 20)]
        public static void ConfigureInteractive()
        {
            var changes = Configure();
            EditorUtility.DisplayDialog(
                "WeatherVR",
                changes.Count == 0
                    ? "Player settings were already correct."
                    : "Applied:\n\n  • " + string.Join("\n  • ", changes),
                "OK");
        }

        /// <summary>
        /// Applies every required setting. Returns a description of what it changed,
        /// so the caller can report it rather than silently mutating the project.
        /// </summary>
        public static List<string> Configure()
        {
            var changes = new List<string>();

            EnsureAndroidDefines(changes);
            EnsureIdentity(changes);
            EnsureAndroidPlayerSettings(changes);
            EnsureGraphics(changes);

            AssetDatabase.SaveAssets();
            foreach (string change in changes) Debug.Log($"[WeatherVR] {change}");
            return changes;
        }

        // --------------------------------------------------------------- defines

        static void EnsureAndroidDefines(List<string> changes)
        {
            var target = NamedBuildTarget.Android;
            string existing = PlayerSettings.GetScriptingDefineSymbols(target);
            var symbols = existing
                .Split(';')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            bool added = false;
            foreach (string required in new[] { PicoDefine, SpatializerDefine })
            {
                if (symbols.Contains(required)) continue;
                symbols.Add(required);
                added = true;
                changes.Add($"Added {required} to the Android scripting defines — " +
                            "without it the entire PICO SDK compiles out of the APK.");
            }

            if (added) PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", symbols));
        }

        // -------------------------------------------------------------- identity

        static void EnsureIdentity(List<string> changes)
        {
            if (string.IsNullOrEmpty(PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android)) ||
                PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android).StartsWith("com.DefaultCompany"))
            {
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, DefaultBundleId);
                changes.Add($"Set the Android package name to {DefaultBundleId}.");
            }

            if (PlayerSettings.productName != DefaultProductName)
            {
                PlayerSettings.productName = DefaultProductName;
                changes.Add($"Set the product name to \"{DefaultProductName}\".");
            }

            if (PlayerSettings.companyName == "DefaultCompany")
            {
                PlayerSettings.companyName = DefaultCompanyName;
                changes.Add($"Set the company name to \"{DefaultCompanyName}\".");
            }
        }

        // ------------------------------------------------------- android player

        static void EnsureAndroidPlayerSettings(List<string> changes)
        {
            if (PlayerSettings.Android.minSdkVersion < AndroidSdkVersions.AndroidApiLevel29)
            {
                PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
                changes.Add("Raised the minimum Android SDK to 29 (PICO/Quest baseline).");
            }

            // ARM64 is what real PICO/Quest hardware runs. X86_64 exists purely so the
            // build also runs natively on the PICO Emulator, which is an x86_64 image:
            // an ARM64-only APK does install there (the image ships libhoudini and
            // advertises arm64-v8a) but every instruction is binary-translated, which
            // made the app unusably slow while the host sat at 22% CPU and 6% GPU.
            // Shipping both costs roughly 30 MB of extra native libraries.
            const AndroidArchitecture DesiredArchitectures =
                AndroidArchitecture.ARM64 | AndroidArchitecture.X86_64;

            if (PlayerSettings.Android.targetArchitectures != DesiredArchitectures)
            {
                PlayerSettings.Android.targetArchitectures = DesiredArchitectures;
                changes.Add("Set Android architectures to ARM64 + X86_64 (device + emulator).");
            }

            if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) != ScriptingImplementation.IL2CPP)
            {
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
                changes.Add("Switched the Android scripting backend to IL2CPP (required for ARM64).");
            }

            // Managed stripping past Low can strip types only referenced through the
            // XR loader's reflection, which fails at runtime rather than at build.
            if (PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.Android) > ManagedStrippingLevel.Low)
            {
                PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Low);
                changes.Add("Lowered managed stripping to Low so XR loader reflection survives.");
            }

            if (PlayerSettings.defaultInterfaceOrientation != UIOrientation.LandscapeLeft)
            {
                PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
                changes.Add("Set the default orientation to landscape.");
            }
        }

        // ------------------------------------------------------------- graphics

        static void EnsureGraphics(List<string> changes)
        {
            if (PlayerSettings.colorSpace != ColorSpace.Linear)
            {
                // Linear is what makes the cloud scattering integrate correctly; in
                // gamma space the volumetrics wash out at low densities.
                PlayerSettings.colorSpace = ColorSpace.Linear;
                changes.Add("Switched the colour space to Linear (required for correct " +
                            "volumetric scattering).");
            }

            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            var desired = new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 };
            if (!apis.SequenceEqual(desired))
            {
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, desired);
                changes.Add("Set the Android graphics APIs to Vulkan, then OpenGLES3 as fallback.");
            }

            if (!PlayerSettings.MTRendering)
            {
                PlayerSettings.MTRendering = true;
                changes.Add("Enabled multithreaded rendering.");
            }

            if (!PlayerSettings.graphicsJobs)
            {
                PlayerSettings.graphicsJobs = true;
                changes.Add("Enabled graphics jobs.");
            }

            // MSAA matters more than resolution on a mobile tile GPU: the map's edges
            // and the lightning ribbons alias badly without it, and 4x is nearly free
            // in tile memory.
            if (QualitySettings.antiAliasing < 4)
            {
                QualitySettings.antiAliasing = 4;
                changes.Add("Set MSAA to 4x.");
            }

            if (QualitySettings.vSyncCount != 0)
            {
                QualitySettings.vSyncCount = 0;
                changes.Add("Disabled vSync (the XR compositor owns presentation).");
            }

            if (QualitySettings.shadows != ShadowQuality.Disable)
            {
                // Nothing in the scene casts shadows; leaving the pass enabled costs a
                // shadow map render for no pixels.
                QualitySettings.shadows = ShadowQuality.Disable;
                changes.Add("Disabled real-time shadows (nothing in the scene casts them).");
            }
        }

        /// <summary>
        /// Makes sure the shaders the app looks up by name at runtime survive into the
        /// build. Nothing in the scene references them as assets, so without this they
        /// are stripped and every <c>Shader.Find</c> returns null on device — a bug
        /// that only ever reproduces on the headset.
        /// </summary>
        [MenuItem("Tools/WeatherVR/Add Shaders to Always-Included", priority = 21)]
        public static void EnsureAlwaysIncludedShaders()
        {
            string[] shaderNames =
            {
                "WeatherVR/TerrainSurface",
                "WeatherVR/Buildings",
                "WeatherVR/StudioSky",
                "WeatherVR/Pedestal",
            };

            var graphicsSettings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")
                                                .FirstOrDefault();
            if (graphicsSettings == null)
            {
                Debug.LogError("[WeatherVR] Could not open GraphicsSettings.asset.");
                return;
            }

            var serialized = new SerializedObject(graphicsSettings);
            var included = serialized.FindProperty("m_AlwaysIncludedShaders");
            if (included == null)
            {
                Debug.LogError("[WeatherVR] GraphicsSettings has no m_AlwaysIncludedShaders property.");
                return;
            }

            var present = new HashSet<Shader>();
            for (int i = 0; i < included.arraySize; i++)
            {
                if (included.GetArrayElementAtIndex(i).objectReferenceValue is Shader shader)
                    present.Add(shader);
            }

            int added = 0;
            foreach (string name in shaderNames)
            {
                var shader = Shader.Find(name);
                if (shader == null)
                {
                    Debug.LogWarning($"[WeatherVR] Shader \"{name}\" not found; skipping.");
                    continue;
                }
                if (present.Contains(shader)) continue;

                included.InsertArrayElementAtIndex(included.arraySize);
                included.GetArrayElementAtIndex(included.arraySize - 1).objectReferenceValue = shader;
                present.Add(shader);
                added++;
            }

            if (added > 0)
            {
                serialized.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
                Debug.Log($"[WeatherVR] Added {added} shader(s) to Always Included Shaders.");
            }
            else
            {
                Debug.Log("[WeatherVR] All WeatherVR shaders were already always-included.");
            }
        }
    }
}
