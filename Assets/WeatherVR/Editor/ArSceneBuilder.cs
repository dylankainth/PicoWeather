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
            originObject.AddComponent<ARRaycastManager>();

            // ------------------------------------------------- weather content
            var content = SceneBuilder.BuildWeatherContent(config);

            var placement = originObject.AddComponent<ArPlacementController>();
            placement.MapRoot = content.MapRoot;
            placement.PlaneManager = planeManager;

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
    }
}
#endif
