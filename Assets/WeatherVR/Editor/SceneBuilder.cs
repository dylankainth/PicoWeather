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

            var cloudObject = new GameObject("VolumetricClouds");
            cloudObject.transform.SetParent(mapRoot.transform, false);
            cloudObject.AddComponent<MeshFilter>();
            cloudObject.AddComponent<MeshRenderer>();
            var clouds = cloudObject.AddComponent<CloudRenderer>();

            var rainObject = new GameObject("Rain");
            rainObject.transform.SetParent(mapRoot.transform, false);
            rainObject.AddComponent<ParticleSystem>();
            var rain = rainObject.AddComponent<RainRenderer>();

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

            // ------------------------------------------------------- environment
            // The glass surround: a frosted floor under the user plus the studio sky,
            // both re-tinted per weather scene so the area around the terrain is a
            // controlled glass environment rather than the old flat black.
            var environmentObject = new GameObject("Environment");
            var environment = environmentObject.AddComponent<EnvironmentController>();
            environment.Head = cameraObject.transform;

            // ------------------------------------------------------------ HUD
            var provenance = BuildProvenancePanel(mapRoot.transform, config);

            // --------------------------------------------------- app controller
            var appObject = new GameObject("WeatherVRApp");
            var dataService = appObject.AddComponent<WeatherDataService>();
            var governor = appObject.AddComponent<PerfGovernor>();
            governor.Clouds = clouds;

            // Switches the whole weather look when the user taps a carousel card,
            // reusing the loaded terrain/imagery for every scene it synthesises.
            var sceneDirector = appObject.AddComponent<WeatherSceneDirector>();
            sceneDirector.Clouds = clouds;
            sceneDirector.Rain = rain;
            sceneDirector.Lightning = lightning;
            sceneDirector.Sun = sun;
            sceneDirector.Environment = environment;
            sceneDirector.MapRoot = mapRoot.transform;

            var controller = appObject.AddComponent<WeatherSceneController>();
            controller.MapRoot = mapRoot.transform;
            controller.DataService = dataService;
            controller.Terrain = terrain;
            controller.Buildings = buildings;
            controller.Clouds = clouds;
            controller.Rain = rain;
            controller.Lightning = lightning;
            controller.Soundscape = soundscape;
            // No MapPlacementController: the map follows the head via ComfortFollow
            // instead of being placed on a surface, so Placement stays null.
            controller.Placement = null;
            controller.Governor = governor;
            controller.SunLight = sun;
            controller.Provenance = provenance;
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

        /// <summary>
        /// A clean, non-overlapping row of three info cards standing just off the
        /// north edge of the map, with a slim title strip above them. Cards are equal
        /// width and centre-spaced by more than a card width apart, so unlike the
        /// original staggered 3D layout (three cards overlapping by design, since
        /// each was forced to the same 0.3 m width but centred only ~0.1-0.2 m apart)
        /// nothing here can overlap regardless of content length.
        /// </summary>
        static ProvenanceLabel BuildProvenancePanel(Transform mapRoot, AppConfig config)
        {
            const float cardWidthMeters = 0.30f;
            const float cardHeightMeters = cardWidthMeters * (240f / 300f); // matches the row cards' aspect ratio
            const float cardGapMeters = 0.045f;
            const float centerSpacingMeters = cardWidthMeters + cardGapMeters;
            const float titleWidthMeters = cardWidthMeters * 3.4f;
            const float titleHeightMeters = titleWidthMeters * (70f / 900f);
            const float titleGapMeters = 0.02f;
            // Every localPosition/localScale under this root is in normalised map
            // units, same as everywhere else under mapRoot (whose own scale is
            // MapSizeMeters) -- a value expressed in real metres must be divided by
            // MapSizeMeters before use here, exactly like BuildCard already does for
            // its own card-size scale. Skipping that for a *position* rather than a
            // *size* is the same unit-convention bug class CLAUDE.md documents.
            float centerSpacing = centerSpacingMeters / config.MapSizeMeters;
            float titleOffset = (cardHeightMeters * 0.5f + titleGapMeters + titleHeightMeters * 0.5f)
                                 / config.MapSizeMeters;

            var root = new GameObject("ProvenancePanel");
            root.transform.SetParent(mapRoot, false);
            root.transform.localPosition = new Vector3(0f, 0.18f, 0.62f);
            root.transform.localRotation = Quaternion.Euler(24f, 180f, 0f);

            BuildCard(root.transform, "TitleCard",
                900f, 70f, titleWidthMeters,
                new Vector3(0f, titleOffset, 0f),
                Quaternion.identity, config, showBackground: false);

            var cards = new ProvenanceLabel.CardInfo[3];

            // Three glass cards in a single flat row: same width, same height, same
            // depth -- only the X offset differs, so they read as one clean HUD strip.
            cards[0] = BuildCard(root.transform, "LocationCard",
                300f, 240f, cardWidthMeters,
                new Vector3(-centerSpacing, 0f, 0f),
                Quaternion.identity, config);

            cards[1] = BuildCard(root.transform, "WeatherCard",
                300f, 240f, cardWidthMeters,
                Vector3.zero,
                Quaternion.identity, config);

            cards[2] = BuildCard(root.transform, "SourcesCard",
                300f, 240f, cardWidthMeters,
                new Vector3(centerSpacing, 0f, 0f),
                Quaternion.identity, config);

            var label = root.AddComponent<ProvenanceLabel>();
            label.Cards = cards;
            return label;
        }

        /// <summary>
        /// Creates one glass-morphism info card: a world-space canvas with a
        /// semi-transparent dark background and a text element.
        /// </summary>
        static ProvenanceLabel.CardInfo BuildCard(
            Transform parent, string name,
            float canvasW, float canvasH, float worldWidthMeters,
            Vector3 localPos, Quaternion localRot, AppConfig config,
            bool showBackground = true)
        {
            var cardObj = new GameObject(name);
            cardObj.transform.SetParent(parent, false);
            cardObj.transform.localPosition = localPos;
            cardObj.transform.localRotation = localRot;

            var canvas = cardObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var canvasRect = cardObj.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(canvasW, canvasH);
            // This card sits under mapRoot, whose own scale is MapSizeMeters, so that
            // ancestor scale multiplies the card's world size too -- divide it back out
            // here or every card renders MapSizeMeters times too big for its target
            // width. Same unit-convention bug class as MapScale; see CLAUDE.md.
            canvasRect.localScale = Vector3.one * (worldWidthMeters / canvasW / config.MapSizeMeters);

            Image bg = null;
            if (showBackground)
            {
                bg = cardObj.AddComponent<Image>();
                bg.color = new Color(0.06f, 0.09f, 0.16f, 0.78f);
            }

            var textObj = new GameObject("Text");
            textObj.transform.SetParent(cardObj.transform, false);

            var text = textObj.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 18;
            text.lineSpacing = 1.1f;
            text.color = new Color(0.90f, 0.94f, 1f);
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = true;

            var textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(16f, 12f);
            textRect.offsetMax = new Vector2(-16f, -12f);

            if (name == "TitleCard")
            {
                text.alignment = TextAnchor.MiddleCenter;
                text.fontSize = 22;
                text.text = "<b><color=#5CB8FF>Immersive Weather</color></b>  " +
                             "<color=#8899AA>· City of London</color>";
            }

            return new ProvenanceLabel.CardInfo { Text = text, Background = bg };
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
