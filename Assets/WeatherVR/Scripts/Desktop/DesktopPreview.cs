using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using WeatherVR.Core;
using WeatherVR.Interaction;

namespace WeatherVR.Desktop
{
    /// <summary>
    /// Lets the app be explored flat, on a plain monitor, with mouse and keyboard.
    ///
    /// There is no PICO device emulator that runs the APK with tracking, so this is
    /// how you "try it on the PC": when no XR headset is present, it takes over the
    /// main camera as an orbit rig, drops the map on the table for you, and turns off
    /// the pointer-driven placement that has nothing to point with. On a real headset
    /// none of this runs — it bails the moment it sees an active XR device.
    ///
    /// It installs itself, so no scene change is needed: press Play, or launch the
    /// desktop build, and it appears.
    /// </summary>
    public class DesktopPreview : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ConfigureForDesktop()
        {
            // Never on a headset.
            if (Application.isMobilePlatform) return;

            // The weather is now chosen from the carousel, and the scene opens on a
            // bright default, so there is no need to force the demo storm — doing so
            // just made the flat preview open in permanent rain.
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (Application.isMobilePlatform) return;
            var host = new GameObject("DesktopPreview");
            host.AddComponent<DesktopPreview>();
            DontDestroyOnLoad(host);
        }

        [Header("Framing")]
        public float Distance = 3.0f;
        public float Yaw = 208f;
        public float Pitch = 24f;

        [Header("Feel")]
        public float OrbitSpeed = 0.16f;
        public float ZoomSpeed = 0.0035f;
        public float PanSpeed = 1.1f;

        Camera _camera;
        Vector3 _pivot = new Vector3(0f, 0.28f, 0f);
        bool _active;
        bool _showHelp = true;

        IEnumerator Start()
        {
            // Give XR a moment to come up, so a real (or simulated) headset wins.
            yield return new WaitForSeconds(0.4f);
            if (XRSettings.isDeviceActive)
            {
                enabled = false;
                yield break;
            }

            _camera = Camera.main;
            if (_camera == null)
            {
                Debug.LogWarning("[WeatherVR] DesktopPreview found no main camera.");
                enabled = false;
                yield break;
            }

            // The head-tracking driver would fight the orbit rig for the transform.
            var driver = _camera.GetComponent<TrackedPoseDriver>();
            if (driver != null) driver.enabled = false;

            // Place the map for the user at the origin.
            var controller = FindObjectOfType<WeatherSceneController>();
            Transform mapRoot = controller != null ? controller.MapRoot : null;

            if (mapRoot != null)
            {
                mapRoot.position = Vector3.zero;
                mapRoot.rotation = Quaternion.identity;
            }

            // The head-follow would glue the map (and anything else that follows) to the
            // orbit camera, defeating the orbit. Disable it so the flat preview can look
            // around a map parked at the origin.
            foreach (var follow in FindObjectsOfType<ComfortFollow>())
                follow.enabled = false;

            if (mapRoot != null) _pivot = mapRoot.position + Vector3.up * 0.28f;

            _active = true;
            Apply();
            Debug.Log("[WeatherVR] Desktop preview active — no headset detected. " +
                      "Drag to orbit, scroll to zoom, WASD/QE to move.");
        }

        void Update()
        {
            if (!_active || _camera == null) return;

            var mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.leftButton.isPressed || mouse.rightButton.isPressed)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    Yaw += delta.x * OrbitSpeed;
                    Pitch = Mathf.Clamp(Pitch - delta.y * OrbitSpeed * 0.8f, -12f, 85f);
                }

                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                    Distance = Mathf.Clamp(Distance - scroll * ZoomSpeed, 0.7f, 9f);
            }

            var kb = Keyboard.current;
            if (kb != null)
            {
                float speed = PanSpeed * Time.deltaTime * (kb.leftShiftKey.isPressed ? 2.4f : 1f);
                Vector3 forward = Quaternion.Euler(0f, Yaw, 0f) * Vector3.forward;
                Vector3 right = Quaternion.Euler(0f, Yaw, 0f) * Vector3.right;

                if (kb.wKey.isPressed) _pivot += forward * speed;
                if (kb.sKey.isPressed) _pivot -= forward * speed;
                if (kb.aKey.isPressed) _pivot -= right * speed;
                if (kb.dKey.isPressed) _pivot += right * speed;
                if (kb.eKey.isPressed) _pivot += Vector3.up * speed;
                if (kb.qKey.isPressed) _pivot -= Vector3.up * speed;

                if (kb.hKey.wasPressedThisFrame) _showHelp = !_showHelp;
                if (kb.escapeKey.wasPressedThisFrame) Quit();
            }

            Apply();
        }

        void Apply()
        {
            Quaternion rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            _camera.transform.position = _pivot + rotation * (Vector3.back * Distance);
            _camera.transform.rotation = Quaternion.LookRotation(_pivot - _camera.transform.position, Vector3.up);
        }

        void OnGUI()
        {
            if (!_active || !_showHelp) return;

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = new Color(0.92f, 0.95f, 1f) }
            };
            const string help = "Immersive Weather — desktop preview\n" +
                                "Drag: orbit    Scroll: zoom    WASD / Q E: move    H: hide    Esc: quit";
            GUI.Box(new Rect(10, 10, 470, 46), GUIContent.none);
            GUI.Label(new Rect(20, 14, 460, 40), help, style);
        }

        static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
