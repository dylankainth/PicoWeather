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

        bool TryReadController(ref bool selecting, ref bool secondary)
        {
            var characteristics = InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.Controller;
            characteristics |= Hand == Source.RightController
                ? InputDeviceCharacteristics.Right
                : InputDeviceCharacteristics.Left;

            DeviceBuffer.Clear();
            InputDevices.GetDevicesWithCharacteristics(characteristics, DeviceBuffer);
            if (DeviceBuffer.Count == 0)
            {
                // Accept whichever controller the PICO input mode currently exposes.
                var eitherController =
                    InputDeviceCharacteristics.HeldInHand |
                    InputDeviceCharacteristics.Controller;
                InputDevices.GetDevicesWithCharacteristics(eitherController, DeviceBuffer);
            }

            if (DeviceBuffer.Count == 0)
            {
                // The scene anchor is a static visual fallback, not a tracked pose.
                // Treating it as tracked on Android produced a fixed ray and prevented
                // gaze fallback from ever activating.
#if UNITY_EDITOR || UNITY_STANDALONE
                return ApplyPoseSource();
#else
                return false;
#endif
            }

            var device = DeviceBuffer[0];
            if (device.TryGetFeatureValue(CommonUsages.isTracked, out bool deviceTracked) &&
                !deviceTracked)
                return false;

            bool trigger = false;
            device.TryGetFeatureValue(CommonUsages.triggerButton, out trigger);
            device.TryGetFeatureValue(CommonUsages.trigger, out float triggerAxis);
            device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryButton);
            selecting = trigger || triggerAxis > 0.55f || primaryButton;

            bool grip = false;
            if (!device.TryGetFeatureValue(CommonUsages.gripButton, out grip))
            {
                if (device.TryGetFeatureValue(CommonUsages.grip, out float gripAxis))
                    grip = gripAxis > 0.6f;
            }
            device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondaryButton);
            secondary = grip || secondaryButton;

            bool haveRotation = device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation);
            bool havePosition = device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position);
            if (!haveRotation || !havePosition) return false;

            // Device poses are in tracking-origin space, so they have to be pushed
            // through the rig transform to land in world space.
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

            // Keep the visible scene ray attached to the live device pose. Previously
            // PoseSource overrode this data with its static scene transform.
            if (PoseSource != null)
                PoseSource.SetPositionAndRotation(
                    Origin,
                    Quaternion.LookRotation(Direction, Vector3.up));

            return true;
        }

        bool ApplyPoseSource()
        {
            if (PoseSource == null) return false;
            Origin = PoseSource.position;
            Direction = PoseSource.forward;
            return true;
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
