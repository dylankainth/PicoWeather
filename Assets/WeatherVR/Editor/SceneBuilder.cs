using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WeatherVR.Audio;
using WeatherVR.Core;
using WeatherVR.Data;
using WeatherVR.Flood;
using WeatherVR.Interaction;
using WeatherVR.Terrain;
using WeatherVR.Weather;

namespace WeatherVR.EditorTools
{
    /// <summary>
    /// Builds the demo scene from scratch, deterministically.
    ///
    /// The scene is generated rather than hand-authored on purpose. A Unity scene
    /// file is an opaque blob in review and a merge conflict waiting to happen, and
    /// a hackathon scene that can only be rebuilt by remembering which components
    /// went where is a scene that gets broken and cannot be repaired. Everything
    /// here is reproducible from <c>Tools ▸ WeatherVR ▸ Build Scene</c>.
    /// </summary>
    public static class SceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/WeatherVR.unity";
        const string ConfigPath = "Assets/WeatherVR/Resources/WeatherVRConfig.asset";

        [MenuItem("Tools/WeatherVR/Build Scene", priority = 0)]
        public static void BuildAndSave()
        {
            BuildAndSaveSilent(EnsureConfigAsset());

            EditorUtility.DisplayDialog(
                "WeatherVR",
                $"Scene rebuilt at {ScenePath}.\n\n" +
                "Press Play to run it in the editor, or use\n" +
                "Tools ▸ WeatherVR ▸ Build APK for the headset.",
                "OK");
        }

        /// <summary>
        /// Rebuilds and saves the scene without any dialogs, so batch mode and CI can
        /// call it.
        /// </summary>
        public static void BuildAndSaveSilent(AppConfig config)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Populate(config);

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);

            AddSceneToBuildSettings();

            Debug.Log($"[WeatherVR] Scene built and saved to {ScenePath}.");
        }

        /// <summary>Creates the whole hierarchy in the active scene.</summary>
        public static void Populate(AppConfig config)
        {
            // ---------------------------------------------------------- XR rig
            var rig = new GameObject("XRRig");
            var cameraOffset = new GameObject("CameraOffset");
            cameraOffset.transform.SetParent(rig.transform, false);
            // Standing-scale content: the PICO runtime is expected to report
            // floor-relative poses, so the offset stays at zero and the headset
            // supplies the height. HeadTracking.Ensure (below) asserts this against
            // PXR_ProjectSetting.stageMode at runtime and compensates the offset if it
            // turns out false, rather than trusting this comment to stay true -- see
            // its own header for why: this exact assumption drifted out of sync with
            // the project setting once already, silently.
            cameraOffset.transform.localPosition = Vector3.zero;

            var cameraObject = new GameObject("MainCamera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(cameraOffset.transform, false);

            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.nearClipPlane = 0.02f;   // the map is at arm's length
            camera.farClipPlane = 200f;
            // The cloud shader clips its raymarch against scene depth.
            camera.depthTextureMode = DepthTextureMode.Depth;
            // Nothing in the project needs HDR (PostFX.shader is editor-only, used
            // by CaptureTool for offline screenshots, and is not in the always-
            // included shader list). An FP16 4x-MSAA tile buffer at this resolution
            // is real bandwidth on a Quest-class GPU for zero visible benefit.
            camera.allowHDR = false;
            cameraObject.AddComponent<AudioListener>();

            // Same implementation the runtime re-asserts, so the two cannot drift.
            HeadTracking.Ensure(cameraObject, cameraOffset.transform);

            // Controller anchors. XRPointer prefers raw device poses, but a transform
            // gives the editor and the XR device simulator something to work with.
            var rightAnchor = new GameObject("RightControllerAnchor");
            rightAnchor.transform.SetParent(cameraOffset.transform, false);
            rightAnchor.transform.localPosition = new Vector3(0.2f, -0.25f, 0.15f);

            var pointer = rightAnchor.AddComponent<XRPointer>();
            pointer.Hand = XRPointer.Source.RightController;
            pointer.PoseSource = rightAnchor.transform;
            pointer.TrackingOrigin = rig.transform;

            var rayVisual = rightAnchor.AddComponent<LineRenderer>();
            ConfigureRayVisual(rayVisual);
            XRPointerVisual.Ensure(rightAnchor, pointer);

            var content = BuildWeatherContent(config);

            // The table rides the head instead of being world-locked: it eases in
            // front of the user, stays world-upright, and the secondary/menu button
            // re-centres it. WeatherCarouselFeature mounts the carousel on this same
            // map root's pedestal wall (see WeatherCarouselFollower), so it comes
            // along for free -- no separate wiring needed.
            //
            // Distance/VerticalOffset are NOT the values this component originally
            // shipped with (0.95 / -0.40): the carousel panel sits
            // WallHalfExtent(0.62) * MapSizeMeters(3.0) = 1.86 m toward the user from
            // the map centre, so a 0.95 m follow distance would put the panel *behind*
            // the user's head.
            //
            // 2.95, not the 2.45 that was tested against a 2 m map: the map grew to 3 m,
            // which pushes both its near edge and the wall-mounted panel half a metre
            // closer to the user for free. Adding that half metre back to the follow
            // distance keeps the framing that was actually tested -- panel ~1.09 m from
            // the head, near table edge ~1.45 m -- rather than a panel at 0.41 m, which
            // is inside comfortable reading distance and behind the map's own overhang.
            // If MapSizeMeters changes again, this and WallHalfExtent move together.
            // Added here, in Populate, and nowhere inside BuildWeatherContent itself --
            // both phone build variants (BuildPhoneTouch.BuildSceneSilent, and
            // ArSceneBuilder for phone AR) call BuildWeatherContent directly and must
            // NOT pick up a head-follow map; keep this component's construction out of
            // that shared method.
            var mapFollow = content.MapRoot.gameObject.AddComponent<ComfortFollow>();
            mapFollow.Head = cameraObject.transform;
            mapFollow.Pointer = pointer;
            mapFollow.Distance = 2.95f;
            mapFollow.VerticalOffset = -0.55f;
            mapFollow.FollowSpeed = 3.0f;
            mapFollow.FaceHead = true;
            mapFollow.YawDeadzoneDegrees = 25f;

            // PICO exposes both thumbsticks through CommonUsages.primary2DAxis.
            // Install the lightweight locomotion component on the rig rather than
            // bringing in an XR Interaction Toolkit locomotion prefab. The runtime
            // bootstrap repeats this idempotently for already-generated scenes.
            ControllerLocomotion.Ensure(rig, cameraObject.transform, mapFollow);
        }

        /// <summary>
        /// Everything that is the same on every platform: lighting, the map root and
        /// its renderers, audio, lightning, the provenance HUD, and the app
        /// controller. The rig and the placement mechanism differ per platform and are
        /// wired by the caller, which is what lets the phone-AR scene reuse all of
        /// this instead of duplicating it.
        /// </summary>
        public struct WeatherContent
        {
            public Transform MapRoot;
            public GameObject App;
            public WeatherSceneController Controller;
            public Light Sun;
        }

        public static WeatherContent BuildWeatherContent(AppConfig config)
        {
            // ------------------------------------------------------- lighting
            var sunObject = new GameObject("SunLight");
            var sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.None;   // no shadow pass in the mobile budget
            sun.intensity = 1.3f;
            sunObject.transform.rotation = Quaternion.Euler(50f, 150f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.30f, 0.34f, 0.40f);

            // A controlled studio backdrop rather than Unity's default blue sky: the
            // app is a tabletop exhibit, not something that appears to float outdoors.
            // Previously only WeatherVR/EditorTools/CaptureTool assigned this shader
            // (for screenshots); the runtime scene never set RenderSettings.skybox at
            // all, so Play mode fell back to Unity's built-in procedural sky.
            var skyShader = Shader.Find("WeatherVR/StudioSky");
            if (skyShader != null)
            {
                var skyMaterial = new Material(skyShader) { name = "StudioSky (runtime)" };
                // The direction *towards* the sun, for the shader's glow term, is the
                // opposite of the light's own forward (a directional light's forward
                // is the direction light travels, i.e. away from the sun).
                skyMaterial.SetVector("_SunDir", -sunObject.transform.forward);
                RenderSettings.skybox = skyMaterial;
            }
            else
            {
                Debug.LogWarning("[WeatherVR] StudioSky shader not found; leaving the default skybox.");
            }

            // Faint depth cue matching the sky's horizon colour -- negligible this
            // close to the tabletop (a few percent at arm's length), more noticeable
            // toward the edges of the tracked space.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.15f;
            RenderSettings.fogColor = new Color(0.050f, 0.065f, 0.095f);

            // ------------------------------------------------------- map root
            var mapRoot = new GameObject("WeatherMap");
            mapRoot.transform.localScale = Vector3.one * config.MapSizeMeters;

            // The map follows the head (ComfortFollow is added in Populate, once the
            // XR rig's camera exists) -- it is not world-locked. The carousel mounts on
            // whichever pedestal wall the user is facing (see WeatherCarouselFollower),
            // riding this same map root, so it comes along for the same reason.

            var pedestalObject = new GameObject("Pedestal");
            pedestalObject.transform.SetParent(mapRoot.transform, false);
            pedestalObject.AddComponent<MeshFilter>();
            pedestalObject.AddComponent<MeshRenderer>();
            pedestalObject.AddComponent<PedestalRenderer>();

            var terrainObject = new GameObject("TerrainMesh");
            terrainObject.transform.SetParent(mapRoot.transform, false);
            var terrain = terrainObject.AddComponent<TerrainRenderer>();

            var buildingsObject = new GameObject("BuildingsMesh");
            buildingsObject.transform.SetParent(mapRoot.transform, false);
            var buildings = buildingsObject.AddComponent<BuildingRenderer>();

            // Manual storm-surge overlay, off by default (see FloodRenderer.SetSurge).
            var waterObject = new GameObject("WaterSurface");
            waterObject.transform.SetParent(mapRoot.transform, false);
            var flood = waterObject.AddComponent<FloodRenderer>();

            // ---------------------------------------------------------- audio
            var audioRoot = new GameObject("Audio");
            audioRoot.transform.SetParent(mapRoot.transform, false);
            var soundscape = audioRoot.AddComponent<AmbientSoundscape>();
            soundscape.Seed = config.ProceduralSeed;

            // ------------------------------------------------------ new weather
            // Built-from-scratch weather (soft cloud deck, rain/snow, lightning flash).
            // Lives under the map root in map-local units, so it sits above the terrain
            // and travels with the table. The old volumetric-cloud / particle-rain /
            // lightning-bolt renderers are no longer created anywhere.
            var visualsObject = new GameObject("WeatherVisuals");
            visualsObject.transform.SetParent(mapRoot.transform, false);
            var visuals = visualsObject.AddComponent<WeatherVisuals>();

            // ------------------------------------------------------- environment
            // Studio sky + the world-locked glass floor, both re-tinted per scene so
            // the surround shifts with the weather instead of sitting in flat black.
            var environmentObject = new GameObject("Environment");
            var environment = environmentObject.AddComponent<EnvironmentController>();
            environment.MapRoot = mapRoot.transform;

            // --------------------------------------------------- app controller
            var appObject = new GameObject("WeatherVRApp");
            var dataService = appObject.AddComponent<WeatherDataService>();

            // Switches the whole weather look when the user taps a carousel card.
            var sceneDirector = appObject.AddComponent<WeatherSceneDirector>();
            sceneDirector.Visuals = visuals;
            sceneDirector.Sun = sun;
            sceneDirector.Environment = environment;

            var controller = appObject.AddComponent<WeatherSceneController>();
            controller.MapRoot = mapRoot.transform;
            controller.DataService = dataService;
            controller.Terrain = terrain;
            controller.Buildings = buildings;
            controller.Soundscape = soundscape;
            controller.Flood = flood;
            controller.SunLight = sun;
            controller.SceneDirector = sceneDirector;

            return new WeatherContent
            {
                MapRoot = mapRoot.transform,
                App = appObject,
                Controller = controller,
                Sun = sun
            };
        }

        static void ConfigureRayVisual(LineRenderer line)
        {
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.widthMultiplier = 0.004f;
            line.numCapVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;

            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            if (shader != null)
            {
                var material = new Material(shader) { name = "PointerRay" };
                line.sharedMaterial = material;
            }

            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.55f, 0.78f, 1f), 0f),
                    new GradientColorKey(new Color(0.85f, 0.93f, 1f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0.15f, 0f),
                    new GradientAlphaKey(0.85f, 1f)
                });
            line.colorGradient = gradient;
        }

        // ------------------------------------------------------------- config

        /// <summary>
        /// Creates the config asset in Resources if it is missing, so
        /// <see cref="AppConfig.Instance"/> resolves to something a designer can edit
        /// rather than to code defaults.
        /// </summary>
        public static AppConfig EnsureConfigAsset()
        {
            var existing = AssetDatabase.LoadAssetAtPath<AppConfig>(ConfigPath);
            if (existing != null)
            {
                AppConfig.Override(existing);
                return existing;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            var config = ScriptableObject.CreateInstance<AppConfig>();
            AssetDatabase.CreateAsset(config, ConfigPath);
            AssetDatabase.SaveAssets();

            AppConfig.Override(config);
            Debug.Log($"[WeatherVR] Created {ConfigPath}.");
            return config;
        }

        static void AddSceneToBuildSettings()
        {
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);

            // Our scene must be index 0 — it is what the APK launches into.
            scenes.RemoveAll(s => s.path == ScenePath);
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));

            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
