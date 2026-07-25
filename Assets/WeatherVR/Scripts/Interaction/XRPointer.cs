using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

#if ENABLE_PICO_XR_SDK
using ByteDance.PICO.XR;
#endif

namespace WeatherVR.Interaction
{
    /// <summary>
    /// One pointing source — a controller or a tracked hand — reduced to what map
    /// placement actually needs: a ray, and whether the user is selecting.
    ///
    /// Deliberately built on <see cref="UnityEngine.XR.InputDevices"/> rather than
    /// on XR Interaction Toolkit interactors. The map is a single world-locked
    /// object placed by a raycast; wiring up XRI's interactable graph for that would
    /// be more moving parts, not fewer, and it would tie the placement flow to
    /// action-map assets that have to stay in sync with the scene.
    ///
    /// The PICO hand-tracking path is compiled in only when
    /// <c>ENABLE_PICO_XR_SDK</c> is defined; without it this degrades cleanly to
    /// controllers, which is also what happens on a headset with hand tracking
    /// switched off.
    /// </summary>
    public class XRPointer : MonoBehaviour
    {
        public enum Source
        {
            RightController,
            LeftController
        }

        [Tooltip("Which hand this pointer follows.")]
        public Source Hand = Source.RightController;

        [Tooltip("Transform tracking the controller/hand. If unset, the pointer uses " +
                 "the raw XR device pose relative to the tracking origin.")]
        public Transform PoseSource;

        [Tooltip("Tracking origin the raw XR pose is expressed relative to. Usually the XR rig root.")]
        public Transform TrackingOrigin;

        [Tooltip("Prefer PICO hand tracking when the runtime reports hands as the " +
                 "active input device.")]
        public bool AllowHandTracking = true;

        [Tooltip("Pinch strength above which a hand counts as selecting.")]
        [Range(0.1f, 1f)] public float PinchThreshold = 0.7f;

        [Tooltip("When only one controller is present (the common case), accept its " +
                 "trigger for select regardless of handedness rather than reporting " +
                 "untracked. Also widens the fallback device searches below to a " +
                 "device with no resolvable handedness at all.")]
        public bool AcceptEitherHandInput = true;

        /// <summary>Which strategy most recently produced a controller pose. Logged
        /// (once, on change) by <c>XrBootDiagnostics</c> and readable at runtime --
        /// knowing which tier actually won on real hardware is what turns "guessed"
        /// detection breadth into verified detection breadth.</summary>
        public enum PoseTier
        {
            None,
            Legacy,
            LegacyLoose,
            LegacyAny,
            InputSystem,
            Native,
            PoseSource
        }

        public PoseTier Tier { get; private set; } = PoseTier.None;

        static readonly List<InputDevice> DeviceBuffer = new List<InputDevice>();

        /// <summary>Origin of the pointing ray, in world space.</summary>
        public Vector3 Origin { get; private set; }

        /// <summary>Direction of the pointing ray, in world space, normalised.</summary>
        public Vector3 Direction { get; private set; } = Vector3.forward;

        /// <summary>True while the pose is being tracked. A stale ray is worse than none.</summary>
        public bool IsTracked { get; private set; }

        /// <summary>True while the user is selecting (trigger held, or pinching).</summary>
        public bool IsSelecting { get; private set; }

        /// <summary>True on the frame selection begins.</summary>
        public bool SelectPressedThisFrame { get; private set; }

        /// <summary>True on the frame selection ends.</summary>
        public bool SelectReleasedThisFrame { get; private set; }

        /// <summary>True while the secondary button (menu/grip) is held.</summary>
        public bool IsSecondaryHeld { get; private set; }

        /// <summary>True on the frame the secondary button goes down.</summary>
        public bool SecondaryPressedThisFrame { get; private set; }

        /// <summary>True when the pose came from tracked hands rather than a controller.</summary>
        public bool UsingHandTracking { get; private set; }

        bool _wasSelecting;
        bool _wasSecondaryHeld;

        public Ray Ray => new Ray(Origin, Direction);

        void Update()
        {
            bool selecting = false;
            bool secondary = false;
            bool tracked = false;
            UsingHandTracking = false;

            if (AllowHandTracking && TryReadHand(ref selecting, ref tracked))
            {
                UsingHandTracking = true;
            }
            else
            {
                tracked = TryReadController(ref selecting, ref secondary);
            }

            IsTracked = tracked;

            SelectPressedThisFrame = selecting && !_wasSelecting;
            SelectReleasedThisFrame = !selecting && _wasSelecting;
            IsSelecting = selecting;
            _wasSelecting = selecting;

            SecondaryPressedThisFrame = secondary && !_wasSecondaryHeld;
            IsSecondaryHeld = secondary;
            _wasSecondaryHeld = secondary;
        }

        // -------------------------------------------------------- controllers

        /// <summary>
        /// Tries each detection strategy in order, first pose wins. The tiers below
        /// were widened after a real-hardware test came back with no ray and no
        /// button presses at all -- <c>XrBootDiagnostics</c>'s unfiltered
        /// <c>InputDevices.GetDevices</c> dump is what should decide which of these
        /// tiers is actually load-bearing on the device in hand; the rest are cheap
        /// insurance against a controller reporting characteristics slightly
        /// differently than expected.
        /// </summary>
        bool TryReadController(ref bool selecting, ref bool secondary)
        {
            bool ok =
                TryReadLegacy(ref selecting, ref secondary, out PoseTier tier) ||
                TryReadLegacyAny(ref selecting, ref secondary, out tier) ||
                TryReadInputSystemController(ref selecting, ref secondary, out tier) ||
                TryReadNativeController(ref selecting, ref secondary, out tier) ||
                TryReadPoseSourceFallback(ref selecting, ref secondary, out tier);

            PoseTier resolved = ok ? tier : PoseTier.None;
            if (resolved != Tier)
            {
                Tier = resolved;
                Debug.Log($"[WeatherVR] XRPointer ({Hand}) pose source: {Tier}.");
            }

            return ok;
        }

        /// <summary>Tiers 1/2, unchanged from the original implementation: a device
        /// matching HeldInHand|Controller|Right-or-Left, falling back (when
        /// <see cref="AcceptEitherHandInput"/>) to HeldInHand|Controller with no
        /// handedness bit at all. Most PICO controllers are found here.</summary>
        bool TryReadLegacy(ref bool selecting, ref bool secondary, out PoseTier tier)
        {
            var characteristics = InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.Controller;
            characteristics |= Hand == Source.RightController
                ? InputDeviceCharacteristics.Right
                : InputDeviceCharacteristics.Left;

            DeviceBuffer.Clear();
            InputDevices.GetDevicesWithCharacteristics(characteristics, DeviceBuffer);
            if (DeviceBuffer.Count > 0)
            {
                tier = PoseTier.Legacy;
                return TryReadLegacyDevice(DeviceBuffer[0], ref selecting, ref secondary);
            }

            if (AcceptEitherHandInput)
            {
                var eitherController =
                    InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.Controller;
                InputDevices.GetDevicesWithCharacteristics(eitherController, DeviceBuffer);
                if (DeviceBuffer.Count > 0)
                {
                    tier = PoseTier.LegacyLoose;
                    return TryReadLegacyDevice(DeviceBuffer[0], ref selecting, ref secondary);
                }
            }

            tier = PoseTier.None;
            return false;
        }

        /// <summary>Tier 3: any non-headset legacy device that resolves a position and
        /// rotation, even with no HeldInHand/Controller characteristic bit set --
        /// covers a controller the runtime reports under an unexpected characteristics
        /// mask, or none at all.</summary>
        bool TryReadLegacyAny(ref bool selecting, ref bool secondary, out PoseTier tier)
        {
            tier = PoseTier.None;

            DeviceBuffer.Clear();
            InputDevices.GetDevices(DeviceBuffer);

            InputDevice best = default;
            bool haveBest = false;
            bool bestMatchesHand = false;

            foreach (var device in DeviceBuffer)
            {
                if ((device.characteristics & InputDeviceCharacteristics.HeadMounted) != 0)
                    continue;
                if (!device.TryGetFeatureValue(CommonUsages.devicePosition, out _) ||
                    !device.TryGetFeatureValue(CommonUsages.deviceRotation, out _))
                    continue;

                bool matchesHand =
                    (Hand == Source.RightController &&
                     (device.characteristics & InputDeviceCharacteristics.Right) != 0) ||
                    (Hand == Source.LeftController &&
                     (device.characteristics & InputDeviceCharacteristics.Left) != 0) ||
                    NameMatchesHand(device.name);

                if (!matchesHand && !AcceptEitherHandInput) continue;

                if (!haveBest || (matchesHand && !bestMatchesHand))
                {
                    best = device;
                    haveBest = true;
                    bestMatchesHand = matchesHand;
                }
            }

            if (!haveBest) return false;

            tier = PoseTier.LegacyAny;
            return TryReadLegacyDevice(best, ref selecting, ref secondary);
        }

        bool NameMatchesHand(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return false;
            return Hand == Source.RightController
                ? deviceName.IndexOf("Right", System.StringComparison.OrdinalIgnoreCase) >= 0
                : deviceName.IndexOf("Left", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Shared button + pose read for a legacy <see cref="InputDevice"/>. Buttons
        /// are read into the caller's ref params *before* the pose/tracked check, so a
        /// momentary tracking dropout -- isTracked reporting false for one frame --
        /// cannot also discard a genuine trigger or secondary press. Only the return
        /// value (pose obtained or not) is gated on tracking.
        /// </summary>
        bool TryReadLegacyDevice(InputDevice device, ref bool selecting, ref bool secondary)
        {
            device.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger);
            device.TryGetFeatureValue(CommonUsages.trigger, out float triggerAxis);
            device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryButton);
            selecting = trigger || triggerAxis > 0.55f || primaryButton;

            if (!device.TryGetFeatureValue(CommonUsages.gripButton, out bool grip))
            {
                if (device.TryGetFeatureValue(CommonUsages.grip, out float gripAxis))
                    grip = gripAxis > 0.6f;
            }
            device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondaryButton);
            secondary = grip || secondaryButton;

            if (device.TryGetFeatureValue(CommonUsages.isTracked, out bool deviceTracked) &&
                !deviceTracked)
                return false;

            bool haveRotation = device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation);
            bool havePosition = device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position);
            if (!haveRotation || !havePosition) return false;

            ApplyPose(position, rotation);
            return true;
        }

        /// <summary>
        /// Tier 4: walks the new Input System's own device list for a generic XR
        /// controller, independent of the legacy <see cref="UnityEngine.XR.InputDevices"/>
        /// bridge above. ProjectSettings has <c>activeInputHandler: Both</c>, so this
        /// second bridge is live for free; it is the tier most likely to catch a
        /// controller the legacy bridge cannot see for whatever reason, since PICO's
        /// own SDK registers a matching device layout
        /// (<c>Packages/com.bytedance.pico.xr/Runtime/InputSystem/DeviceLayouts.cs</c>)
        /// independently of the legacy bridge working at all.
        ///
        /// Compiled only when the Input System package itself compiles XR device
        /// support (<c>UNITY_INPUT_SYSTEM_ENABLE_XR</c>); when it does not, this tier
        /// is simply unavailable rather than a compile error.
        /// </summary>
        bool TryReadInputSystemController(ref bool selecting, ref bool secondary, out PoseTier tier)
        {
            tier = PoseTier.None;
#if UNITY_INPUT_SYSTEM_ENABLE_XR
            var wantedUsage = Hand == Source.RightController
                ? UnityEngine.InputSystem.CommonUsages.RightHand
                : UnityEngine.InputSystem.CommonUsages.LeftHand;

            UnityEngine.InputSystem.XR.XRController best = null;
            bool bestMatchesHand = false;

            foreach (var device in UnityEngine.InputSystem.InputSystem.devices)
            {
                if (device is not UnityEngine.InputSystem.XR.XRController controller) continue;

                bool matchesHand = false;
                foreach (var usage in controller.usages)
                {
                    if (usage == wantedUsage) { matchesHand = true; break; }
                }
                if (!matchesHand && !AcceptEitherHandInput) continue;

                if (best == null || (matchesHand && !bestMatchesHand))
                {
                    best = controller;
                    bestMatchesHand = matchesHand;
                }
            }

            if (best == null) return false;

            try
            {
                var posControl = best.TryGetChildControl<UnityEngine.InputSystem.Controls.Vector3Control>("devicePosition");
                var rotControl = best.TryGetChildControl<UnityEngine.InputSystem.Controls.QuaternionControl>("deviceRotation");
                if (posControl == null || rotControl == null) return false;

                var triggerAxis = best.TryGetChildControl<UnityEngine.InputSystem.Controls.AxisControl>("trigger");
                var triggerPressed = best.TryGetChildControl<UnityEngine.InputSystem.Controls.ButtonControl>("triggerPressed");
                var primaryButton = best.TryGetChildControl<UnityEngine.InputSystem.Controls.ButtonControl>("primaryButton");
                var gripAxis = best.TryGetChildControl<UnityEngine.InputSystem.Controls.AxisControl>("grip");
                var gripPressed = best.TryGetChildControl<UnityEngine.InputSystem.Controls.ButtonControl>("gripPressed");
                var secondaryButton = best.TryGetChildControl<UnityEngine.InputSystem.Controls.ButtonControl>("secondaryButton");
                // Unread anywhere else in the app, and a natural PICO 4 recenter gesture.
                var menuButton = best.TryGetChildControl<UnityEngine.InputSystem.Controls.ButtonControl>("menu");
                var trackedControl = best.TryGetChildControl<UnityEngine.InputSystem.Controls.ButtonControl>("isTracked");

                selecting = (triggerPressed != null && triggerPressed.isPressed) ||
                            (triggerAxis != null && triggerAxis.ReadValue() > 0.55f) ||
                            (primaryButton != null && primaryButton.isPressed);

                secondary = (gripPressed != null && gripPressed.isPressed) ||
                            (gripAxis != null && gripAxis.ReadValue() > 0.6f) ||
                            (secondaryButton != null && secondaryButton.isPressed) ||
                            (menuButton != null && menuButton.isPressed);

                if (trackedControl != null && !trackedControl.isPressed) return false;

                ApplyPose(posControl.ReadValue(), rotControl.ReadValue());
                tier = PoseTier.InputSystem;
                return true;
            }
            catch (System.InvalidOperationException)
            {
                // A control existed under one of these names but with an incompatible
                // type -- treat as "this tier can't read this device", not a crash.
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>
        /// Latched once PICO's native controller pose API turns out to be
        /// unavailable, mirroring <see cref="_handTrackingUnavailable"/> below for the
        /// same reason: this P/Invoke throws on the x86_64 PICO Emulator exactly like
        /// hand tracking does, since both live in the same arm64-only native library.
        /// </summary>
        static bool _nativeControllerUnavailable;

        /// <summary>
        /// Tier 5: PICO's own native controller pose API. Pose only -- button state
        /// comes from tier 4 above, which reads the same controller layout PICO's own
        /// Input System integration registers independently of this native path.
        /// </summary>
        bool TryReadNativeController(ref bool selecting, ref bool secondary, out PoseTier tier)
        {
            tier = PoseTier.None;
#if ENABLE_PICO_XR_SDK && UNITY_ANDROID && !UNITY_EDITOR
            if (_nativeControllerUnavailable) return false;

            try
            {
                var controller = Hand == Source.RightController
                    ? PXR_Input.Controller.RightController
                    : PXR_Input.Controller.LeftController;

                if (!PXR_Input.IsControllerConnected(controller)) return false;

                Vector3 position = PXR_Input.GetControllerPredictPosition(controller, 0d);
                Quaternion rotation = PXR_Input.GetControllerPredictRotation(controller, 0d);

                ApplyPose(position, rotation);
                tier = PoseTier.Native;
                return true;
            }
            catch (System.Exception e) when (e is System.DllNotFoundException ||
                                             e is System.EntryPointNotFoundException)
            {
                _nativeControllerUnavailable = true;
                Debug.LogWarning(
                    "[WeatherVR] PICO native controller pose is unavailable on this " +
                    $"platform ({e.GetType().Name}); relying on the Input System / " +
                    "legacy XR bridges for the rest of the session.");
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>Tier 6: the static scene anchor, editor/desktop only. Treating it
        /// as tracked on Android produced a fixed ray and prevented gaze fallback from
        /// ever activating -- kept exactly as it was.</summary>
        bool TryReadPoseSourceFallback(ref bool selecting, ref bool secondary, out PoseTier tier)
        {
            tier = PoseTier.None;
#if UNITY_EDITOR || UNITY_STANDALONE
            if (!ApplyPoseSource()) return false;
            tier = PoseTier.PoseSource;
            return true;
#else
            return false;
#endif
        }

        bool ApplyPoseSource()
        {
            if (PoseSource == null) return false;
            Origin = PoseSource.position;
            Direction = PoseSource.forward;
            return true;
        }

        /// <summary>
        /// Applies a raw device-space pose to <see cref="Origin"/>/<see cref="Direction"/>,
        /// pushing it through <see cref="TrackingOrigin"/> into world space, and syncs
        /// <see cref="PoseSource"/> so the visible scene ray (see
        /// <c>XRPointerVisual</c>) tracks the live pose rather than a static scene
        /// transform.
        /// </summary>
        void ApplyPose(Vector3 position, Quaternion rotation)
        {
            if (TrackingOrigin != null)
            {
                Origin = TrackingOrigin.TransformPoint(position);
                Direction = TrackingOrigin.TransformDirection(rotation * Vector3.forward).normalized;
            }
            else
            {
                Origin = position;
                Direction = (rotation * Vector3.forward).normalized;
            }

            if (PoseSource != null)
                PoseSource.SetPositionAndRotation(
                    Origin,
                    Quaternion.LookRotation(Direction, Vector3.up));
        }

        // ------------------------------------------------------ hand tracking

        /// <summary>
        /// Latched once the PICO hand-tracking native library turns out to be
        /// unavailable, so the P/Invoke is not retried every frame.
        ///
        /// This is not hypothetical. PICO ships libPxrPlatform.so for arm64-v8a only,
        /// so on the x86_64 PICO Emulator — or any build where the native library is
        /// missing — the very first call throws DllNotFoundException, and without this
        /// latch it threw again on every Update of every pointer. Building an
        /// exception and capturing its stack 60+ times a second is expensive enough to
        /// matter on a frame budget of 13.9 ms.
        /// </summary>
        static bool _handTrackingUnavailable;

        bool TryReadHand(ref bool selecting, ref bool tracked)
        {
#if ENABLE_PICO_XR_SDK && UNITY_ANDROID && !UNITY_EDITOR
            if (_handTrackingUnavailable) return false;

            try
            {
                return TryReadHandNative(ref selecting, ref tracked);
            }
            catch (System.Exception e) when (e is System.DllNotFoundException ||
                                             e is System.EntryPointNotFoundException)
            {
                _handTrackingUnavailable = true;
                Debug.LogWarning(
                    "[WeatherVR] PICO hand tracking is unavailable on this platform " +
                    $"({e.GetType().Name}); falling back to controllers for the rest of " +
                    "the session. Expected on the x86_64 emulator, where PICO's native " +
                    "libraries are arm64-only.");
                return false;
            }
#else
            // PICO's native hand API is not safe to probe from the Windows editor:
            // some SDK builds crash inside the DLL before managed exception handling
            // can run. Controllers and PoseSource remain available for the emulator.
            return false;
#endif
        }

#if ENABLE_PICO_XR_SDK
        bool TryReadHandNative(ref bool selecting, ref bool tracked)
        {
            if (PXR_HandTracking.GetActiveInputDevice() != ActiveInputDevice.HandTrackingActive)
                return false;

            var handType = Hand == Source.RightController ? HandType.HandRight : HandType.HandLeft;
            var aimState = new HandAimState();
            if (!PXR_HandTracking.GetAimState(handType, ref aimState)) return false;

            // The per-finger pinch strengths are private on HandAimState, so the
            // status bitfield is the only supported way to read a pinch. That is fine
            // here: placement is a discrete confirm, not a continuous grip.
            bool computed = (aimState.aimStatus & HandAimStatus.AimComputed) != 0;
            bool rayValid = (aimState.aimStatus & HandAimStatus.AimRayValid) != 0;
            if (!computed || !rayValid) return false;

            var pose = aimState.aimRayPose;
            var localPosition = new Vector3(pose.Position.x, pose.Position.y, pose.Position.z);
            var localRotation = new Quaternion(pose.Orientation.x, pose.Orientation.y,
                                               pose.Orientation.z, pose.Orientation.w);

            if (TrackingOrigin != null)
            {
                Origin = TrackingOrigin.TransformPoint(localPosition);
                Direction = TrackingOrigin.TransformDirection(localRotation * Vector3.forward).normalized;
            }
            else
            {
                Origin = localPosition;
                Direction = (localRotation * Vector3.forward).normalized;
            }

            selecting = (aimState.aimStatus & HandAimStatus.AimIndexPinching) != 0;
            tracked = true;
            return true;
        }
#endif
    }
}
