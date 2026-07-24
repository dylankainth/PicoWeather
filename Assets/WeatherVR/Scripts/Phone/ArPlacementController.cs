#if WEATHERVR_AR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using WeatherVR.Core;

namespace WeatherVR.Phone
{
    /// <summary>
    /// Phone-AR placement: tap a detected surface to drop the weather map onto it.
    ///
    /// The headset build places the map by raycasting a controller against physics
    /// colliders or a fallback plane. On a phone there is no controller and no room
    /// mesh, so placement instead raycasts the touch point against ARCore's detected
    /// planes. Everything downstream is unchanged — the map root is the same object
    /// the headset build moves, so terrain, clouds, lightning and the provenance
    /// panel all come along for free.
    ///
    /// Guarded by WEATHERVR_AR so the project still compiles when AR Foundation is
    /// not installed (the PICO build does not need it).
    /// </summary>
    [RequireComponent(typeof(ARRaycastManager))]
    public class ArPlacementController : MonoBehaviour
    {
        [Tooltip("Root of the weather map. Found via WeatherSceneController if unset.")]
        public Transform MapRoot;

        [Tooltip("Plane manager, so planes can be hidden once the map is placed.")]
        public ARPlaneManager PlaneManager;

        [Tooltip("Map edge length in metres for the phone build. Smaller than the " +
                 "headset's 2 m so it fits comfortably on a real desk seen through a phone.")]
        public float PhoneMapSizeMeters = 0.6f;

        [Tooltip("Hide the detected-plane visualisation once the map is down.")]
        public bool HidePlanesAfterPlacement = true;

        ARRaycastManager _raycastManager;
        readonly List<ARRaycastHit> _hits = new List<ARRaycastHit>();
        bool _placed;

        /// <summary>True once the user has committed the map to a surface.</summary>
        public bool IsPlaced => _placed;

        void Awake()
        {
            _raycastManager = GetComponent<ARRaycastManager>();
            if (PlaneManager == null) PlaneManager = GetComponent<ARPlaneManager>();

            if (MapRoot == null)
            {
                var controller = FindObjectOfType<WeatherSceneController>();
                if (controller != null) MapRoot = controller.MapRoot;
            }

            // Keep the map out of sight until it has somewhere to sit; otherwise it
            // hangs in mid-air at the origin while the user is still scanning.
            if (MapRoot != null) MapRoot.gameObject.SetActive(false);
        }

        void Update()
        {
            if (MapRoot == null) return;

            if (!TryGetTapPosition(out Vector2 screenPosition)) return;

            if (!_raycastManager.Raycast(screenPosition, _hits, TrackableType.PlaneWithinPolygon))
                return;

            Pose pose = _hits[0].pose;

            // Face the map's "north" edge away from the viewer, matching the headset
            // build, so the first thing you see is the map the right way up.
            Vector3 toCamera = Camera.main != null
                ? Camera.main.transform.position - pose.position
                : Vector3.back;
            toCamera.y = 0f;
            Quaternion rotation = toCamera.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(-toCamera.normalized, Vector3.up)
                : pose.rotation;

            MapRoot.SetPositionAndRotation(pose.position, rotation);
            MapRoot.localScale = Vector3.one * PhoneMapSizeMeters;
            MapRoot.gameObject.SetActive(true);

            if (!_placed)
            {
                _placed = true;
                if (HidePlanesAfterPlacement && PlaneManager != null)
                {
                    PlaneManager.enabled = false;
                    foreach (var plane in PlaneManager.trackables) plane.gameObject.SetActive(false);
                }
                Debug.Log($"[WeatherVR] Map placed on an AR plane at {pose.position}.");
            }
        }

        /// <summary>
        /// A tap that began this frame. Reads the new Input System's touchscreen, and
        /// falls back to the mouse so the same scene can be exercised in the editor.
        /// </summary>
        static bool TryGetTapPosition(out Vector2 position)
        {
            position = default;

            var touchscreen = Touchscreen.current;
            if (touchscreen != null && touchscreen.primaryTouch.press.wasPressedThisFrame)
            {
                position = touchscreen.primaryTouch.position.ReadValue();
                return true;
            }

            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                position = mouse.position.ReadValue();
                return true;
            }

            return false;
        }
    }
}
#endif
