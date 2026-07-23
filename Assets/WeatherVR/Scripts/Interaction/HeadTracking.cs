using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;

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
        /// Adds a <see cref="TrackedPoseDriver"/> to <paramref name="cameraObject"/>
        /// if it does not already have one. Returns the driver either way.
        /// </summary>
        public static TrackedPoseDriver Ensure(GameObject cameraObject)
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

            return driver;
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
