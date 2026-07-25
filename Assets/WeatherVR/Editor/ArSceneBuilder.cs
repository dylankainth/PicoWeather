#if WEATHERVR_AR
using System.IO;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using WeatherVR.Core;
using WeatherVR.Phone;

namespace WeatherVR.EditorTools
{
    /// <summary>
    /// Builds the phone-AR scene: an ARCore rig looking at the same weather content
    /// the headset build uses.
    ///
    /// Only the rig and the placement mechanism differ from the PICO scene — the map,
    /// terrain, clouds, lightning, audio and HUD all come from
    /// <see cref="SceneBuilder.BuildWeatherContent"/>, so the two platforms cannot
    /// drift apart.
    /// </summary>
    public static class ArSceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/WeatherVR_AR.unity";

        [MenuItem("Tools/WeatherVR/Build Phone AR Scene", priority = 2)]
        public static void BuildAndSave()
        {
            BuildAndSaveSilent(SceneBuilder.EnsureConfigAsset());
            EditorUtility.DisplayDialog("WeatherVR",
                $"Phone AR scene built at {ScenePath}.\n\n" +
                "Use Tools ▸ WeatherVR ▸ Build Phone AR APK.", "OK");
        }

        public static void BuildAndSaveSilent(AppConfig config)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ------------------------------------------------------- AR session
            var sessionObject = new GameObject("AR Session");
            sessionObject.AddComponent<ARSession>();
            sessionObject.AddComponent<ARInputManager>();

            // -------------------------------------------------------- XR origin
            var originObject = new GameObject("XR Origin");
            var origin = originObject.AddComponent<XROrigin>();

            var cameraOffset = new GameObject("Camera Offset");
            cameraOffset.transform.SetParent(originObject.transform, false);

            var cameraObject = new GameObject("AR Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(cameraOffset.transform, false);

            var camera = cameraObject.AddComponent<Camera>();
            // Passthrough: ARCameraBackground paints the live feed, so the camera must
            // not clear to a skybox over the top of it.
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 60f;
            // The cloud raymarch clips against scene depth.
            camera.depthTextureMode = DepthTextureMode.Depth;

            cameraObject.AddComponent<AudioListener>();
            cameraObject.AddComponent<ARCameraManager>();
            cameraObject.AddComponent<ARCameraBackground>();
            cameraObject.AddComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();

            origin.Camera = camera;
            origin.CameraFloorOffsetObject = cameraOffset;

            // Plane detection + raycasting drive tap-to-place.
            var planeManager = originObject.AddComponent<ARPlaneManager>();
            // Without a prefab the detected planes are invisible, so the user has no
            // idea whether ARCore found a surface or where it is safe to tap.
            planeManager.planePrefab = CreatePlanePrefab();
            originObject.AddComponent<ARRaycastManager>();

            // ------------------------------------------------- weather content
            var content = SceneBuilder.BuildWeatherContent(config);

            var statusText = BuildStatusOverlay();

            var placement = originObject.AddComponent<ArPlacementController>();
            placement.MapRoot = content.MapRoot;
            placement.PlaneManager = planeManager;
            placement.StatusText = statusText;

            // A phone-sized map: 2 m is right for a headset you walk around, but on a
            // desk seen through a phone it swallows the room.
            placement.PhoneMapSizeMeters = 0.6f;

            // No skybox in AR — the camera feed is the background.
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.45f, 0.50f);

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);

            Debug.Log($"[WeatherVR] Phone AR scene saved to {ScenePath}.");
        }

        const string PlanePrefabPath = "Assets/WeatherVR/Prefabs/ARPlaneVisualiser.prefab";

        /// <summary>
        /// A translucent visualiser so detected planes are actually visible. ARCore
        /// finds surfaces regardless, but with no prefab the user is staring at a raw
        /// camera feed with no feedback about whether it is working.
        /// </summary>
        static GameObject CreatePlanePrefab()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlanePrefabPath));

            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PlanePrefabPath);
            if (existing != null) return existing;

            var temp = new GameObject("ARPlaneVisualiser");
            temp.AddComponent<ARPlane>();
            temp.AddComponent<MeshFilter>();
            temp.AddComponent<ARPlaneMeshVisualizer>();

            var meshRenderer = temp.AddComponent<MeshRenderer>();
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader)
            {
                name = "ARPlaneVisualiser",
                color = new Color(0.35f, 0.75f, 1f, 0.28f)
            };
            AssetDatabase.CreateAsset(material, "Assets/WeatherVR/Prefabs/ARPlaneVisualiser.mat");
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;

            var prefab = PrefabUtility.SaveAsPrefabAsset(temp, PlanePrefabPath);
            Object.DestroyImmediate(temp);
            AssetDatabase.SaveAssets();
            return prefab;
        }

        /// <summary>
        /// Screen-space instructions. AR placement is not discoverable — without a
        /// prompt the user does not know they are meant to scan and then tap.
        /// </summary>
        static UnityEngine.UI.Text BuildStatusOverlay()
        {
            var canvasObject = new GameObject("AR UI");
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasObject.AddComponent<UnityEngine.UI.CanvasScaler>().uiScaleMode =
                UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            var panelObject = new GameObject("StatusPanel");
            panelObject.transform.SetParent(canvasObject.transform, false);
            var panel = panelObject.AddComponent<UnityEngine.UI.Image>();
            panel.color = new Color(0f, 0f, 0f, 0.55f);
            var panelRect = panel.rectTransform;
            panelRect.anchorMin = new Vector2(0.05f, 0.86f);
            panelRect.anchorMax = new Vector2(0.95f, 0.97f);
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;

            var textObject = new GameObject("StatusText");
            textObject.transform.SetParent(panelObject.transform, false);
            var text = textObject.AddComponent<UnityEngine.UI.Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 30;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.text = "Move your phone slowly to scan a surface…";
            var textRect = text.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(12f, 8f);
            textRect.offsetMax = new Vector2(-12f, -8f);

            return text;
        }
    }
}
#endif
