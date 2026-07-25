using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
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

        /// <summary>
        /// The only Android XR loader the headset build should ever carry. Shared with
        /// <see cref="BuildPhoneAR"/>/<see cref="BuildPhoneTouch"/>, which deliberately
        /// swap it out for their own variant and back again.
        /// </summary>
        public const string PicoLoaderTypeName = "ByteDance.PICO.XR.PXR_Loader";
        static readonly string PicoLoaderGuid = "4d777b5b4090b414d98a02b43502d09c";

        /// <summary>
        /// Defines that must never reach an Android build of the headset app. Unlike
        /// <see cref="EnsureAndroidDefines"/>, which only ever adds symbols, this list is
        /// actively stripped.
        /// </summary>
        static readonly string[] ForbiddenAndroidDefines = { "WEATHERVR_AR" };

        const string DefaultBundleId = "com.weathervr.immersive";
        const string DefaultProductName = "Immersive Weather";
        const string DefaultCompanyName = "WeatherVR";
        const string AppIconPath = "Assets/WeatherVR/Art/PicoWeatherIcon.png";

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

            EnsureAndroidDefines(changes, out bool removedForbiddenDefine);
            EnsureAndroidXrLoader(changes);
            EnsureIdentity(changes);
            EnsureAndroidIcons(changes);
            EnsureAndroidPlayerSettings(changes);
            EnsureGraphics(changes);

            AssetDatabase.SaveAssets();
            foreach (string change in changes) Debug.Log($"[WeatherVR] {change}");

            if (removedForbiddenDefine)
            {
                // Removing a scripting define forces a script recompile that this very
                // invocation cannot see -- BuildPhoneAR.EnableArDefineFromCommandLine
                // documents the same hazard for adding one. Configure() runs immediately
                // before BuildPipeline.BuildPlayer in every build entry point, squarely
                // inside that window, so abort rather than silently build against stale
                // compiled code.
                throw new UnityEditor.Build.BuildFailedException(
                    "[WeatherVR] Android scripting defines were wrong and have been " +
                    "corrected (see the log above). Re-run the build/configure step so " +
                    "the recompile actually happens.");
            }

            return changes;
        }

        // --------------------------------------------------------------- defines

        static void EnsureAndroidDefines(List<string> changes, out bool removedForbiddenDefine)
        {
            var target = NamedBuildTarget.Android;
            string existing = PlayerSettings.GetScriptingDefineSymbols(target);
            var symbols = existing
                .Split(';')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            bool changed = false;
            foreach (string required in new[] { PicoDefine, SpatializerDefine })
            {
                if (symbols.Contains(required)) continue;
                symbols.Add(required);
                changed = true;
                changes.Add($"Added {required} to the Android scripting defines — " +
                            "without it the entire PICO SDK compiles out of the APK.");
            }

            removedForbiddenDefine = false;
            foreach (string forbidden in ForbiddenAndroidDefines)
            {
                if (!symbols.Remove(forbidden)) continue;
                changed = true;
                removedForbiddenDefine = true;
                changes.Add($"Removed {forbidden} from the Android scripting defines — " +
                            "leftover from the phone AR build; it drags AR/ARCore code " +
                            "paths into the headset APK.");
            }

            if (changed) PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", symbols));
        }

        // ------------------------------------------------------------ xr loader

        /// <summary>
        /// Makes the PICO loader the only Android XR loader, and turns the XR manager
        /// on. Both phone build variants (<see cref="BuildPhoneAR"/>,
        /// <see cref="BuildPhoneTouch"/>) call <see cref="Configure"/> and then
        /// immediately swap the loader to their own choice, so asserting PICO here
        /// cannot break them.
        ///
        /// This exists because of a real regression: an empty Android loader list
        /// with the XR manager disabled was committed onto the PICO mainline (see
        /// CLAUDE.md's progress log). With no loader assigned, the PICO SDK's own
        /// manifest post-process step never writes <c>pvr.app.type=vr</c>
        /// (<c>Packages/com.bytedance.pico.xr/Editor/PXR_BuildProcessor.cs</c>), so
        /// PICO OS launches the APK as a flat 2D panel instead of an immersive app —
        /// no stereo, no head tracking, no controllers, and no build error anywhere
        /// to say so.
        /// </summary>
        public static void EnsureAndroidXrLoader(List<string> changes)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[WeatherVR] Skipped the Android XR loader check while in Play mode.");
                return;
            }

            var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            if (settings?.Manager == null)
            {
                Debug.LogError("[WeatherVR] No XR General Settings found for Android; " +
                                "cannot assert the PICO loader.");
                return;
            }

            var manager = settings.Manager;

            // Purge anything that is not PICO. Snapshot first: activeLoaders is the live
            // backing list and RemoveLoader mutates it, so enumerating it directly throws.
            foreach (var loader in manager.activeLoaders.ToArray())
            {
                string typeName = loader.GetType().FullName;
                if (typeName == PicoLoaderTypeName) continue;

                XRPackageMetadataStore.RemoveLoader(manager, typeName, BuildTargetGroup.Android);
                changes.Add($"Removed the {typeName} XR loader from Android — PICO must be the only one.");
            }

            if (!XRPackageMetadataStore.IsLoaderAssigned(PicoLoaderTypeName, BuildTargetGroup.Android))
            {
                XRPackageMetadataStore.AssignLoader(manager, PicoLoaderTypeName, BuildTargetGroup.Android);

                if (XRPackageMetadataStore.IsLoaderAssigned(PicoLoaderTypeName, BuildTargetGroup.Android))
                {
                    changes.Add("Assigned the PICO XR loader for Android.");
                }
                else
                {
                    // AssignLoader rebuilds its ordered list from a metadata cache that
                    // can come back empty on a cold batch-mode run (nothing has ever
                    // opened the interactive XR Plug-in Management window to populate
                    // it) -- in which case it silently "succeeds" having assigned
                    // nothing. Fall back to adding the known loader asset directly.
                    var path = AssetDatabase.GUIDToAssetPath(PicoLoaderGuid);
                    var loader = AssetDatabase.LoadAssetAtPath<UnityEngine.XR.Management.XRLoader>(path);
                    if (loader == null || !manager.TryAddLoader(loader))
                    {
                        Debug.LogError("[WeatherVR] Could not assign the PICO XR loader for " +
                                       "Android. The headset build will launch as a flat 2D panel.");
                    }
                    else
                    {
                        EditorUtility.SetDirty(manager);
                        changes.Add("Assigned the PICO XR loader for Android by direct asset " +
                                    "reference (the XR loader metadata cache was empty).");
                    }
                }
            }

            if (!settings.InitManagerOnStart)
            {
                settings.InitManagerOnStart = true;
                changes.Add("Enabled \"Initialize XR on Startup\" for Android.");
            }

            EditorUtility.SetDirty(settings);
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

        // --------------------------------------------------------------- app icon

        /// <summary>
        /// Assigns the PicoWeather artwork to every Android launcher-icon slot,
        /// including legacy, round and adaptive variants. Keeping this in the
        /// configurator makes command-line builds deterministic and prevents a
        /// clean checkout from silently falling back to Unity's default icon.
        /// </summary>
        static void EnsureAndroidIcons(List<string> changes)
        {
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(AppIconPath);
            if (icon == null)
            {
                throw new UnityEditor.Build.BuildFailedException(
                    $"[WeatherVR] Android app icon is missing at {AppIconPath}.");
            }

            var target = NamedBuildTarget.Android;
            bool changed = false;
            var supportedKinds = PlayerSettings.GetSupportedIconKinds(target);

            foreach (var kind in supportedKinds)
            {
                var slots = PlayerSettings.GetPlatformIcons(target, kind);
                bool kindChanged = false;

                foreach (var slot in slots)
                {
                    var desired = Enumerable.Repeat(icon, slot.maxLayerCount).ToArray();
                    var current = slot.GetTextures();
                    if (current != null && current.SequenceEqual(desired)) continue;

                    slot.SetTextures(desired);
                    kindChanged = true;
                }

                if (!kindChanged) continue;
                PlayerSettings.SetPlatformIcons(target, kind, slots);
                changed = true;
            }

            // Older Android modules can expose no PlatformIconKind entries. Preserve
            // a legacy fallback so the launcher still receives the artwork.
            if (supportedKinds.Length == 0)
            {
                int iconCount = PlayerSettings.GetIconSizes(target, IconKind.Any).Length;
                var desired = Enumerable.Repeat(icon, iconCount).ToArray();
                var current = PlayerSettings.GetIcons(target, IconKind.Any);
                if (current == null || !current.SequenceEqual(desired))
                {
                    PlayerSettings.SetIcons(target, desired, IconKind.Any);
                    changed = true;
                }
            }

            if (changed)
                changes.Add("Assigned the PicoWeather artwork to every Android app icon slot.");
        }

        // ------------------------------------------------------- android player

        static void EnsureAndroidPlayerSettings(List<string> changes)
        {
            if (PlayerSettings.Android.minSdkVersion < AndroidSdkVersions.AndroidApiLevel29)
            {
                PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
                changes.Add("Raised the minimum Android SDK to 29 (PICO/Quest baseline).");
            }

            // ARM64 only — including X86_64 costs the emulator its immersive mode.
            //
            // This used to be ARM64 | X86_64, on the reasoning that x86_64 lets the APK
            // run natively on the PICO Emulator (an x86_64 image) rather than under
            // binary translation. The problem is that PICO ships no x86_64 XR runtime:
            // libopenxr_loader.so, libPxrPlatform.so and the rest exist only under
            // lib/arm64-v8a. Running the x86_64 slice therefore leaves no XR plugin to
            // load, the XR display subsystem never starts, and the app renders to an
            // ordinary Android surface — which the PICO shell frames as a flat 2D panel
            // on the wall. That is not a degraded VR mode; it is the app not being a VR
            // app at all, and it is silent (no error, on device or in the emulator).
            //
            // Measured on the emulator, 2026-07-25, ARM64-only under translation:
            // a steady 60/60 FPS (the emulator's cap), FrmCpu ≈ 5 ms, FrmGpu ≈ 2.5 ms,
            // zero late or skipped frames — so the older "unusably slow" note above it
            // did not reproduce. The cost that is real is startup: ~33 s from launch to
            // first frame versus ~10 s for the x86_64 slice, since IL2CPP's ARM code has
            // to be translated. Steady-state rendering is fine; only loading is slow.
            //
            // Real hardware is ARM64 regardless, so this also removes ~20 MB of native
            // libraries that never ran on a headset.
            const AndroidArchitecture DesiredArchitectures = AndroidArchitecture.ARM64;

            if (PlayerSettings.Android.targetArchitectures != DesiredArchitectures)
            {
                PlayerSettings.Android.targetArchitectures = DesiredArchitectures;
                changes.Add("Set the Android architecture to ARM64 only (x86_64 has no PICO XR runtime).");
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
                "WeatherVR/Water",
                "WeatherVR/GlassSurround",
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
