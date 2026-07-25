using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;

namespace WeatherVR.Interaction
{
    /// <summary>
    /// Makes the camera follow the headset.
    ///
    /// One implementation, called from two places: <c>SceneBuilder</c> bakes it into
    /// the generated scene, and <c>WeatherSceneController</c> re-asserts it at
    /// startup. The duplication is deliberate. Without a pose driver the XR runtime
    /// still initialises, the app still renders in stereo, and the view simply does
    /// not respond to the user's head — there is no error anywhere, and the failure
    /// is only visible while wearing the headset. That is far too quiet a way to
    /// break, and it *did* break once here, when the scene was generated before this
    /// code existed and then kept being used. The runtime check means a scene of any
    /// vintage still tracks correctly.
    /// </summary>
    public static class HeadTracking
    {
        /// <summary>
        /// PICO reports poses eye-level-relative when stage mode is off. If the
        /// runtime ever reports that mode (whatever <c>PXR_ProjectSetting.stageMode</c>
        /// says), lifting the rig by roughly a standing eye height keeps world Y
        /// meaning "metres above the floor" either way -- see
        /// <see cref="EnsureFloorOrigin"/>.
        /// </summary>
        const float DefaultEyeHeightMetres = 1.6f;

        /// <summary>
        /// Adds a <see cref="TrackedPoseDriver"/> to <paramref name="cameraObject"/>
        /// if it does not already have one. Returns the driver either way.
        /// </summary>
        /// <param name="cameraOffset">
        /// The rig's camera-offset transform (parent of the camera). Optional, but
        /// without it a device that reports eye-level-relative poses cannot be
        /// compensated for -- see <see cref="EnsureFloorOrigin"/>.
        /// </param>
        public static TrackedPoseDriver Ensure(GameObject cameraObject, Transform cameraOffset = null)
        {
            if (cameraObject == null) return null;

            var driver = cameraObject.GetComponent<TrackedPoseDriver>();
            bool created = driver == null;
            if (created) driver = cameraObject.AddComponent<TrackedPoseDriver>();

            driver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            // BeforeRender as well as Update. Updating only on Update leaves a frame of
            // head latency, which in VR is felt rather than seen.
            driver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;

            // Bindings are built in code rather than referencing an .inputactions asset,
            // so the scene carries no external dependency that can go stale.
            if (!HasBinding(driver.positionInput))
                driver.positionInput = new InputActionProperty(
                    CreateAction("HMD Position", "<XRHMD>/centerEyePosition", "Vector3"));

            if (!HasBinding(driver.rotationInput))
                driver.rotationInput = new InputActionProperty(
                    CreateAction("HMD Rotation", "<XRHMD>/centerEyeRotation", "Quaternion"));

            if (created && Application.isPlaying)
            {
                Debug.LogWarning(
                    "[WeatherVR] The camera had no TrackedPoseDriver, so head tracking " +
                    "would not have worked. Added one at runtime — rebuild the scene " +
                    "with Tools > WeatherVR > Build Scene to bake it in properly.");
            }

            if (Application.isPlaying)
                EnsureFloorOrigin(cameraOffset);

            return driver;
        }

        /// <summary>
        /// Makes world Y mean "metres above the floor" regardless of which tracking
        /// origin mode the runtime actually reports.
        ///
        /// This project's CameraOffset math (see SceneBuilder.Populate) has always
        /// assumed the runtime reports floor-relative poses, based on
        /// <c>PXR_ProjectSetting.stageMode</c>. That assumption briefly drifted out of
        /// sync with the actual project setting (see CLAUDE.md) with no error anywhere
        /// -- only a comment claiming it was true. Asserting it here, at the one place
        /// that can actually query and correct the origin mode, means the assumption
        /// cannot silently go stale again: it is either made true, or compensated for.
        /// </summary>
        static void EnsureFloorOrigin(Transform cameraOffset)
        {
            var subsystems = new List<XRInputSubsystem>();
            SubsystemManager.GetInstances(subsystems);
            if (subsystems.Count == 0) return;

            var subsystem = subsystems[0];
            if (subsystem.GetTrackingOriginMode() != TrackingOriginModeFlags.Floor &&
                (subsystem.GetSupportedTrackingOriginModes() & TrackingOriginModeFlags.Floor) != 0)
            {
                subsystem.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
            }

            if (cameraOffset == null) return;

            bool floorRelative = subsystem.GetTrackingOriginMode() == TrackingOriginModeFlags.Floor;
            cameraOffset.localPosition = floorRelative ? Vector3.zero : Vector3.up * DefaultEyeHeightMetres;

            Debug.Log($"[WeatherVR] XRBOOT origin mode after HeadTracking.Ensure: " +
                      $"{subsystem.GetTrackingOriginMode()} " +
                      $"(offset={(floorRelative ? 0f : DefaultEyeHeightMetres)}m)");
        }

        static InputAction CreateAction(string name, string binding, string controlType)
        {
            var action = new InputAction(name, InputActionType.Value, binding,
                                         expectedControlType: controlType);
            action.Enable();
            return action;
        }

        static bool HasBinding(InputActionProperty property)
        {
            var action = property.action;
            return action != null && action.bindings.Count > 0;
        }
    }
}
