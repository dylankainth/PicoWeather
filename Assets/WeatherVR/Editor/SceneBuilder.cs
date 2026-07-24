using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WeatherVR.Audio;
using WeatherVR.Core;
using WeatherVR.Data;
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
            // Standing-scale content: the PICO runtime reports floor-relative poses,
            // so the offset stays at zero and the headset supplies the height.
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
            cameraObject.AddComponent<AudioListener>();

            // Same implementation the runtime re-asserts, so the two cannot drift.
            HeadTracking.Ensure(cameraObject);

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

            // The map is no longer placed on a surface and world-locked; it rides with
            // the user. This lazy follow eases it in front of the head, and the carousel
            // runs the identical maths, so the terrain and the UI move together. The
            // secondary controller button re-centres it if it drifts.
            var mapFollow = mapRoot.AddComponent<ComfortFollow>();
            mapFollow.Head = cameraObject.transform;
            mapFollow.Pointer = pointer;
            mapFollow.Distance = 0.95f;
            mapFollow.VerticalOffset = -0.40f;
            mapFollow.FollowSpeed = 8f;
            mapFollow.FaceHead = true;

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
            // Studio sky only (the glass floor was removed): re-tinted per scene so the
            // blue surround shifts with the weather instead of sitting in flat black.
            var environmentObject = new GameObject("Environment");
            var environment = environmentObject.AddComponent<EnvironmentController>();

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
            controller.SunLight = sun;
            controller.SceneDirector = sceneDirector;

            // A sensible starting pose; ComfortFollow snaps it in front of the head on
            // the first frame anyway.
            mapRoot.transform.position = new Vector3(0f, 0.85f, 1.1f);
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
