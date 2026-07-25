#if WEATHERVR_AR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using WeatherVR.Core;

namespace WeatherVR.Phone
{
    /// <summary>
    /// Phone-AR placement: tap a surface to drop the weather map onto it.
    ///
    /// Two things here are deliberate and were learned the hard way on device.
    ///
    /// The map is <b>parked out of view rather than deactivated</b> while waiting to
    /// be placed. Deactivating the root looks equivalent but is not: Unity never runs
    /// Awake on components of an inactive GameObject, so CloudRenderer.Apply would
    /// dereference a null MeshRenderer when WeatherSceneController built the scene,
    /// and the map stayed broken even after it was activated. Parking keeps the whole
    /// hierarchy live and merely invisible.
    ///
    /// And there is a <b>fallback</b>: if ARCore has not produced a plane after a few
    /// seconds — a blank desk, poor light — a tap places the map at a fixed distance
    /// in front of the camera anyway. A demo that shows nothing because the room was
    /// too featureless is worse than one placed slightly imprecisely.
    /// </summary>
    [RequireComponent(typeof(ARRaycastManager))]
    public class ArPlacementController : MonoBehaviour
    {
        [Tooltip("Root of the weather map. Found via WeatherSceneController if unset.")]
        public Transform MapRoot;

        [Tooltip("Plane manager, so planes can be hidden once the map is placed.")]
        public ARPlaneManager PlaneManager;

        [Tooltip("On-screen instructions. Optional.")]
        public Text StatusText;

        [Tooltip("Map edge length in metres for the phone build. Smaller than the " +
                 "headset's 2 m so it fits on a real desk seen through a phone.")]
        public float PhoneMapSizeMeters = 0.6f;

        [Tooltip("Seconds to wait for plane detection before allowing a tap to place " +
                 "the map in front of the camera regardless.")]
        public float FallbackAfterSeconds = 6f;

        [Tooltip("How far in front of the camera the fallback placement sits.")]
        public float FallbackDistance = 0.7f;

        [Tooltip("Hide the detected-plane visualisation once the map is down.")]
        public bool HidePlanesAfterPlacement = true;

        /// <summary>Where the map waits before placement — far below any real surface.</summary>
        static readonly Vector3 ParkPosition = new Vector3(0f, -5000f, 0f);

        ARRaycastManager _raycastManager;
        readonly List<ARRaycastHit> _hits = new List<ARRaycastHit>();
        bool _placed;
        float _elapsed;

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

            // Park, do not deactivate — see the class comment.
            if (MapRoot != null)
            {
                MapRoot.position = ParkPosition;
                MapRoot.localScale = Vector3.one * PhoneMapSizeMeters;
            }
        }

        void Update()
        {
            if (MapRoot == null) return;

            _elapsed += Time.deltaTime;

            if (!_placed) UpdateStatus();

            if (!TryGetTapPosition(out Vector2 screenPosition)) return;

            if (TryResolvePlacement(screenPosition, out Pose pose)) Place(pose);
        }

        /// <summary>Plane hit if we have one; otherwise, after a grace period, in front of the camera.</summary>
        bool TryResolvePlacement(Vector2 screenPosition, out Pose pose)
        {
            if (_raycastManager.Raycast(screenPosition, _hits, TrackableType.PlaneWithinPolygon))
            {
                pose = _hits[0].pose;
                return true;
            }

            var camera = Camera.main;
            if (camera != null && _elapsed >= FallbackAfterSeconds)
            {
                Vector3 forward = camera.transform.forward;
                Vector3 position = camera.transform.position + forward * FallbackDistance;
                // Drop it a little below eye line so it reads as sitting on something.
                position.y -= 0.25f;
                pose = new Pose(position, Quaternion.identity);
                Debug.Log("[WeatherVR] No AR plane under the tap; using the fallback placement.");
                return true;
            }

            pose = default;
            return false;
        }

        void Place(Pose pose)
        {
            // Face the map's north edge away from the viewer, as the headset build does.
            Vector3 toCamera = Camera.main != null
                ? Camera.main.transform.position - pose.position
                : Vector3.back;
            toCamera.y = 0f;
            Quaternion rotation = toCamera.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(-toCamera.normalized, Vector3.up)
                : pose.rotation;

            MapRoot.SetPositionAndRotation(pose.position, rotation);
            MapRoot.localScale = Vector3.one * PhoneMapSizeMeters;

            if (!_placed)
            {
                _placed = true;
                if (HidePlanesAfterPlacement && PlaneManager != null)
                {
                    PlaneManager.enabled = false;
                    foreach (var plane in PlaneManager.trackables) plane.gameObject.SetActive(false);
                }
                SetStatus("Tap again to move the map.");
                Debug.Log($"[WeatherVR] Map placed at {pose.position}.");
            }
        }

        void UpdateStatus()
        {
            int planeCount = PlaneManager != null ? PlaneManager.trackables.count : 0;

            if (planeCount > 0)
                SetStatus("Surface found — tap to place the weather map.");
            else if (_elapsed >= FallbackAfterSeconds)
                SetStatus("No surface detected. Tap anywhere to place it in front of you.");
            else
                SetStatus("Move your phone slowly to scan a surface…");
        }

        void SetStatus(string message)
        {
            if (StatusText != null && StatusText.text != message) StatusText.text = message;
        }

        /// <summary>
        /// A tap that began this frame. Reads the Input System touchscreen, falling
        /// back to the mouse so the scene can be exercised in the editor.
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
