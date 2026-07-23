using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using WeatherVR.Audio;
using WeatherVR.Clouds;
using WeatherVR.Core;
using WeatherVR.Data;
using WeatherVR.Interaction;
using WeatherVR.Lightning;
using WeatherVR.Terrain;

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
            RenderSettings.fog = false;

            // ------------------------------------------------------- map root
            var mapRoot = new GameObject("WeatherMap");
            mapRoot.transform.localScale = Vector3.one * config.MapSizeMeters;

            var terrainObject = new GameObject("TerrainMesh");
            terrainObject.transform.SetParent(mapRoot.transform, false);
            var terrain = terrainObject.AddComponent<TerrainRenderer>();

            var cloudObject = new GameObject("VolumetricClouds");
            cloudObject.transform.SetParent(mapRoot.transform, false);
            cloudObject.AddComponent<MeshFilter>();
            cloudObject.AddComponent<MeshRenderer>();
            var clouds = cloudObject.AddComponent<CloudRenderer>();

            // ---------------------------------------------------------- audio
            var audioRoot = new GameObject("Audio");
            audioRoot.transform.SetParent(mapRoot.transform, false);
            var soundscape = audioRoot.AddComponent<AmbientSoundscape>();
            soundscape.Seed = config.ProceduralSeed;

            var lightningObject = new GameObject("LightningDirector");
            lightningObject.transform.SetParent(mapRoot.transform, false);
            var thunder = lightningObject.AddComponent<ThunderAudio>();
            thunder.Seed = config.ProceduralSeed;
            var lightning = lightningObject.AddComponent<LightningDirector>();

            // ------------------------------------------------------------ HUD
            var provenance = BuildProvenancePanel(mapRoot.transform, config);

            // --------------------------------------------------- app controller
            var appObject = new GameObject("WeatherVRApp");
            var dataService = appObject.AddComponent<WeatherDataService>();
            var governor = appObject.AddComponent<PerfGovernor>();
            governor.Clouds = clouds;

            var placement = appObject.AddComponent<MapPlacementController>();
            placement.MapRoot = mapRoot.transform;
            placement.Pointer = pointer;
            placement.Head = cameraObject.transform;
            placement.RayVisual = rayVisual;

            var controller = appObject.AddComponent<WeatherSceneController>();
            controller.MapRoot = mapRoot.transform;
            controller.DataService = dataService;
            controller.Terrain = terrain;
            controller.Clouds = clouds;
            controller.Lightning = lightning;
            controller.Soundscape = soundscape;
            controller.Placement = placement;
            controller.Governor = governor;
            controller.SunLight = sun;
            controller.Provenance = provenance;

            // Somewhere sensible to look before the user places it.
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

        /// <summary>
        /// A world-space text panel standing just off the north edge of the map, sized
        /// and positioned in map-local units so it follows the map wherever it is put.
        /// </summary>
        static ProvenanceLabel BuildProvenancePanel(Transform mapRoot, AppConfig config)
        {
            var panel = new GameObject("ProvenancePanel");
            panel.transform.SetParent(mapRoot, false);
            // Just beyond the far edge, tilted up towards a standing viewer.
            panel.transform.localPosition = new Vector3(0f, 0.18f, 0.62f);
            panel.transform.localRotation = Quaternion.Euler(24f, 180f, 0f);

            var canvasObject = new GameObject("Canvas");
            canvasObject.transform.SetParent(panel.transform, false);

            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var canvasRect = canvas.GetComponent<RectTransform>();
            // 1 map unit = MapSizeMeters, so 0.5 x 0.3 units is about 1 m x 0.6 m of
            // panel at the default 2 m map.
            canvasRect.sizeDelta = new Vector2(520f, 330f);
            canvasRect.localScale = Vector3.one * (0.5f / 520f);

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(canvasObject.transform, false);

            var text = textObject.AddComponent<Text>();
            text.text = "Loading weather data…";
            // The built-in legacy font is the only one guaranteed to exist without
            // importing TextMeshPro's essential resources first.
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 20;
            text.lineSpacing = 1.15f;
            text.color = new Color(0.90f, 0.94f, 1f);
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = true;

            var textRect = text.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(12f, 12f);
            textRect.offsetMax = new Vector2(-12f, -12f);

            var label = panel.AddComponent<ProvenanceLabel>();
            label.Text = text;
            return label;
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
