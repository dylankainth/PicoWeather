using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace WeatherVR.Interaction
{
    /// <summary>
    /// PICO-compatible controller locomotion for the lightweight WeatherVR rig.
    ///
    /// The project deliberately does not depend on an XR Interaction Toolkit
    /// locomotion prefab. PICO exposes both controller sticks through the standard
    /// XR <see cref="CommonUsages.primary2DAxis"/> usage (the PICO SDK's own
    /// controller animator reads the same value), so this remains compatible with
    /// PICO OpenXR while keeping the generated scene self-contained.
    /// </summary>
    [DefaultExecutionOrder(-120)]
    public sealed class ControllerLocomotion : MonoBehaviour
    {
        static readonly List<InputDevice> DeviceBuffer = new List<InputDevice>();

        [Tooltip("Tracked headset camera used to make movement head-relative.")]
        public Transform Head;

        [Tooltip("World-anchored exhibit that must not chase artificial rig movement.")]
        public ComfortFollow ExhibitAnchor;

        [Tooltip("Maximum horizontal walking speed in metres per second.")]
        public float MoveSpeed = 1.6f;

        [Tooltip("Radial thumbstick deadzone.")]
        [Range(0f, 0.9f)]
        public float MoveDeadzone = 0.18f;

        [Tooltip("Maximum right-stick smooth-turn speed in degrees per second.")]
        [Range(0f, 180f)]
        public float SmoothTurnSpeed = 75f;

        [Tooltip("Maximum right-stick dolly speed toward or away from the exhibit.")]
        public float ZoomSpeed = 1.2f;

        [Tooltip("Closest comfortable horizontal distance from the exhibit centre.")]
        public float MinimumZoomDistance = 0.55f;

        [Tooltip("Furthest horizontal distance allowed from the exhibit centre.")]
        public float MaximumZoomDistance = 5f;

        InputDevice _leftController;
        InputDevice _rightController;
        bool _reportedInput;

        /// <summary>
        /// Adds locomotion to an existing generated rig, or repairs its references
        /// when an older baked scene is loaded.
        /// </summary>
        public static ControllerLocomotion Ensure(
            GameObject rig, Transform head = null, ComfortFollow exhibitAnchor = null)
        {
            if (rig == null) return null;

            var locomotion = rig.GetComponent<ControllerLocomotion>();
            if (locomotion == null)
                locomotion = rig.AddComponent<ControllerLocomotion>();

            if (head != null) locomotion.Head = head;
            if (exhibitAnchor != null) locomotion.ExhibitAnchor = exhibitAnchor;
            return locomotion;
        }

        void OnEnable()
        {
            if (!Application.isPlaying) return;
            InputDevices.deviceConnected += OnDeviceChanged;
            InputDevices.deviceDisconnected += OnDeviceDisconnected;
            RefreshDevices();
        }

        void OnDisable()
        {
            InputDevices.deviceConnected -= OnDeviceChanged;
            InputDevices.deviceDisconnected -= OnDeviceDisconnected;
        }

        void OnValidate()
        {
            MoveSpeed = Mathf.Max(0f, MoveSpeed);
            MoveDeadzone = Mathf.Clamp(MoveDeadzone, 0f, 0.9f);
            SmoothTurnSpeed = Mathf.Clamp(SmoothTurnSpeed, 0f, 180f);
            ZoomSpeed = Mathf.Max(0f, ZoomSpeed);
            MinimumZoomDistance = Mathf.Max(0.1f, MinimumZoomDistance);
            MaximumZoomDistance =
                Mathf.Max(MinimumZoomDistance + 0.1f, MaximumZoomDistance);
        }

        void Update()
        {
            if (Head == null)
            {
                if (Camera.main == null) return;
                Head = Camera.main.transform;
            }

            bool haveLeft = TryReadAxis(true, ref _leftController, out Vector2 leftAxis);
            bool haveRight = TryReadAxis(false, ref _rightController, out Vector2 rightAxis);

            // Some runtimes expose a connected controller without a handedness flag.
            // In that case both lookups can resolve to the same device. Treat it as
            // the movement controller rather than applying movement and turning from
            // one stick at the same time.
            if (haveLeft && haveRight && _leftController.Equals(_rightController))
                haveRight = false;

            Vector2 moveAxis = haveLeft
                ? ApplyRadialDeadzone(leftAxis, MoveDeadzone)
                : Vector2.zero;
            Vector2 viewAxis = haveRight
                ? ApplyRadialDeadzone(rightAxis, MoveDeadzone)
                : Vector2.zero;

            bool moved = MoveRig(moveAxis);
            bool turned = SmoothTurn(viewAxis.x);
            bool zoomed = ZoomRig(viewAxis.y);

            if (moved || turned || zoomed)
            {
                // Artificial locomotion should move the user around the exhibit, not
                // make ComfortFollow drag the exhibit back in front of the user.
                ExhibitAnchor?.PreserveWorldPoseAfterRigMove();

                if (!_reportedInput)
                {
                    _reportedInput = true;
                    Debug.Log(
                        "[WeatherVR] Controller locomotion active: left stick moves, " +
                        "right stick smooth-turns and zooms.");
                }
            }
        }

        bool MoveRig(Vector2 axis)
        {
            if (axis.sqrMagnitude <= 0f || MoveSpeed <= 0f)
                return false;

            Vector3 direction = ComputeMoveDirection(axis, Head.forward);
            transform.position += direction * (MoveSpeed * Time.unscaledDeltaTime);
            return true;
        }

        bool SmoothTurn(float horizontal)
        {
            if (Mathf.Abs(horizontal) <= 0f || SmoothTurnSpeed <= 0f)
                return false;

            float angle = ComputeSmoothTurnDegrees(
                horizontal, SmoothTurnSpeed, Time.unscaledDeltaTime);
            if (Mathf.Approximately(angle, 0f))
                return false;

            transform.RotateAround(Head.position, Vector3.up, angle);
            return true;
        }

        bool ZoomRig(float vertical)
        {
            if (Mathf.Abs(vertical) <= 0f || ZoomSpeed <= 0f)
                return false;

            Vector3 exhibitPosition = ExhibitAnchor != null
                ? ExhibitAnchor.transform.position
                : Head.position + Head.forward;
            Vector3 direction = ComputeZoomDirection(
                vertical, Head.position, exhibitPosition, Head.forward);
            if (direction.sqrMagnitude <= 0f)
                return false;

            float distance = Vector3.Distance(
                new Vector3(Head.position.x, 0f, Head.position.z),
                new Vector3(exhibitPosition.x, 0f, exhibitPosition.z));
            float step = Mathf.Abs(vertical) * ZoomSpeed * Time.unscaledDeltaTime;

            if (ExhibitAnchor != null)
            {
                float available = vertical > 0f
                    ? distance - MinimumZoomDistance
                    : MaximumZoomDistance - distance;
                step = Mathf.Min(step, Mathf.Max(0f, available));
            }

            if (step <= 0f)
                return false;

            transform.position += direction * step;
            return true;
        }

        void RefreshDevices()
        {
            _leftController = FindController(true);
            _rightController = FindController(false);
        }

        void OnDeviceChanged(InputDevice unused)
        {
            RefreshDevices();
        }

        void OnDeviceDisconnected(InputDevice device)
        {
            if (_leftController.Equals(device))
                _leftController = default;
            if (_rightController.Equals(device))
                _rightController = default;
        }

        static bool TryReadAxis(bool left, ref InputDevice device, out Vector2 axis)
        {
            if (!device.isValid)
                device = FindController(left);

            if (device.isValid &&
                device.TryGetFeatureValue(CommonUsages.primary2DAxis, out axis))
                return true;

            // The bridge may reconnect with a new device id without delivering the
            // connection callback until after this frame.
            device = FindController(left);
            if (device.isValid &&
                device.TryGetFeatureValue(CommonUsages.primary2DAxis, out axis))
                return true;

            axis = Vector2.zero;
            return false;
        }

        static InputDevice FindController(bool left)
        {
            InputDeviceCharacteristics hand = left
                ? InputDeviceCharacteristics.Left
                : InputDeviceCharacteristics.Right;

            DeviceBuffer.Clear();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.Controller | hand, DeviceBuffer);

            for (int i = 0; i < DeviceBuffer.Count; i++)
            {
                if (DeviceBuffer[i].TryGetFeatureValue(
                    CommonUsages.primary2DAxis, out _))
                    return DeviceBuffer[i];
            }

            // Real PICO controller characteristic flags have varied between runtime
            // versions. Fall back to any non-HMD device with a stick, while rejecting
            // a device explicitly marked as the other hand.
            DeviceBuffer.Clear();
            InputDevices.GetDevices(DeviceBuffer);
            InputDeviceCharacteristics otherHand = left
                ? InputDeviceCharacteristics.Right
                : InputDeviceCharacteristics.Left;

            for (int i = 0; i < DeviceBuffer.Count; i++)
            {
                InputDevice candidate = DeviceBuffer[i];
                if ((candidate.characteristics & InputDeviceCharacteristics.HeadMounted) != 0 ||
                    (candidate.characteristics & otherHand) != 0)
                    continue;

                if (candidate.TryGetFeatureValue(
                    CommonUsages.primary2DAxis, out _))
                    return candidate;
            }

            return default;
        }

        /// <summary>Applies a radial deadzone without losing full stick range.</summary>
        public static Vector2 ApplyRadialDeadzone(Vector2 value, float deadzone)
        {
            deadzone = Mathf.Clamp(deadzone, 0f, 0.95f);
            float magnitude = value.magnitude;
            if (magnitude <= deadzone)
                return Vector2.zero;

            float scaledMagnitude = Mathf.Clamp01(
                (magnitude - deadzone) / (1f - deadzone));
            return value / magnitude * scaledMagnitude;
        }

        /// <summary>
        /// Converts stick X/Y into a horizontal, head-relative world direction.
        /// Head pitch and roll are intentionally ignored.
        /// </summary>
        public static Vector3 ComputeMoveDirection(Vector2 input, Vector3 headForward)
        {
            headForward.y = 0f;
            if (headForward.sqrMagnitude < 1e-4f)
                headForward = Vector3.forward;
            headForward.Normalize();

            Vector3 right = Vector3.Cross(Vector3.up, headForward);
            Vector3 direction = right * input.x + headForward * input.y;
            if (direction.sqrMagnitude > 1f)
                direction.Normalize();
            return direction;
        }

        /// <summary>Returns this frame's continuous right-stick yaw delta.</summary>
        public static float ComputeSmoothTurnDegrees(
            float input, float degreesPerSecond, float deltaTime)
        {
            return Mathf.Clamp(input, -1f, 1f) *
                   Mathf.Max(0f, degreesPerSecond) *
                   Mathf.Max(0f, deltaTime);
        }

        /// <summary>
        /// Returns a horizontal unit vector toward the exhibit for positive stick Y
        /// and away from it for negative Y. Falls back to headset forward when the
        /// two positions overlap or no exhibit is available.
        /// </summary>
        public static Vector3 ComputeZoomDirection(
            float input,
            Vector3 headPosition,
            Vector3 exhibitPosition,
            Vector3 headForward)
        {
            if (Mathf.Approximately(input, 0f))
                return Vector3.zero;

            Vector3 toward = exhibitPosition - headPosition;
            toward.y = 0f;
            if (toward.sqrMagnitude < 1e-4f)
            {
                toward = headForward;
                toward.y = 0f;
            }
            if (toward.sqrMagnitude < 1e-4f)
                toward = Vector3.forward;

            toward.Normalize();
            return input > 0f ? toward : -toward;
        }
    }
}
