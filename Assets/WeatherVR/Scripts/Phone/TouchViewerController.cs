using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using WeatherVR.Core;

namespace WeatherVR.Phone
{
    /// <summary>
    /// Touch navigation for the phone build: orbit the weather map with your fingers.
    ///
    /// This is the non-AR phone target. There is no camera passthrough, no plane
    /// detection and no XR runtime — just the scene, rendered normally, with the
    /// camera on an orbit rig driven by touch. That removes every failure mode the
    /// AR build had (tracking quality, lighting, surface texture, ARCore device
    /// support) at the cost of the map not sitting on a real table.
    ///
    ///   one finger  drag   — orbit
    ///   two fingers pinch  — zoom
    ///   two fingers drag   — pan
    ///
    /// Mouse input is handled too, so the same scene can be driven in the editor.
    /// </summary>
    public class TouchViewerController : MonoBehaviour
    {
        [Header("Framing")]
        public Transform MapRoot;
        public float Distance = 1.7f;
        public float Yaw = 208f;
        public float Pitch = 26f;

        [Header("Feel")]
        public float OrbitSpeed = 0.22f;
        public float PinchZoomSpeed = 0.004f;
        public float PanSpeed = 0.0016f;
        public float MinDistance = 0.5f;
        public float MaxDistance = 6f;

        [Header("UI")]
        public Text HintText;
        [Tooltip("Seconds the control hint stays on screen.")]
        public float HintSeconds = 6f;

        Camera _camera;
        Vector3 _pivot;
        float _previousPinchDistance;
        bool _pinching;
        float _elapsed;

        void Start()
        {
            _camera = Camera.main;

            // The map sits at the origin; the camera orbits it. Nothing to place, so
            // there is nothing for the user to get wrong before they see anything.
            if (MapRoot != null)
            {
                MapRoot.position = Vector3.zero;
                _pivot = Vector3.up * (AppConfig.Instance.MapSizeMeters * 0.15f);
            }

            Apply();
        }

        void Update()
        {
            _elapsed += Time.deltaTime;
            if (HintText != null && _elapsed > HintSeconds && HintText.enabled)
                HintText.enabled = false;

            if (_camera == null)
            {
                _camera = Camera.main;
                if (_camera == null) return;
            }

            HandleTouch();
            HandleMouse();
            Apply();
        }

        void HandleTouch()
        {
            var screen = Touchscreen.current;
            if (screen == null) return;

            // Collect the touches that are actually down this frame.
            var touches = screen.touches;
            int count = 0;
            Vector2 firstDelta = Vector2.zero, firstPos = Vector2.zero, secondPos = Vector2.zero;
            Vector2 averageDelta = Vector2.zero;

            foreach (var touch in touches)
            {
                if (!touch.press.isPressed) continue;

                Vector2 position = touch.position.ReadValue();
                Vector2 delta = touch.delta.ReadValue();

                if (count == 0) { firstPos = position; firstDelta = delta; }
                else if (count == 1) { secondPos = position; }

                averageDelta += delta;
                count++;
                if (count >= 2) break;
            }

            if (count == 0) { _pinching = false; return; }

            if (count == 1)
            {
                _pinching = false;
                Orbit(firstDelta);
                return;
            }

            // Two fingers: pinch to zoom, and translate the pivot with the average
            // movement so the map can be dragged around the screen.
            averageDelta *= 0.5f;
            float pinchDistance = Vector2.Distance(firstPos, secondPos);

            if (!_pinching)
            {
                _pinching = true;
                _previousPinchDistance = pinchDistance;
            }

            float pinchDelta = pinchDistance - _previousPinchDistance;
            _previousPinchDistance = pinchDistance;

            Distance = Mathf.Clamp(Distance - pinchDelta * PinchZoomSpeed, MinDistance, MaxDistance);
            Pan(averageDelta);
        }

        void HandleMouse()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            if (mouse.leftButton.isPressed) Orbit(mouse.delta.ReadValue());

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                Distance = Mathf.Clamp(Distance - scroll * 0.003f, MinDistance, MaxDistance);
        }

        void Orbit(Vector2 delta)
        {
            Yaw += delta.x * OrbitSpeed;
            Pitch = Mathf.Clamp(Pitch - delta.y * OrbitSpeed * 0.8f, -5f, 85f);
        }

        void Pan(Vector2 delta)
        {
            // Pan in the camera's own plane, scaled by distance so it feels the same
            // whether you are close in or zoomed out.
            Vector3 right = _camera.transform.right;
            Vector3 up = _camera.transform.up;
            _pivot -= (right * delta.x + up * delta.y) * PanSpeed * Distance;
        }

        void Apply()
        {
            if (_camera == null) return;
            Quaternion rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            _camera.transform.position = _pivot + rotation * (Vector3.back * Distance);
            _camera.transform.rotation =
                Quaternion.LookRotation(_pivot - _camera.transform.position, Vector3.up);
        }
    }
}
