using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using UnityEngine.XR.Management;

namespace WeatherVR.Core
{
    /// <summary>
    /// Logs one block of ground truth about XR bring-up, tagged "XRBOOT" so it is
    /// greppable in <c>adb logcat</c>, plus an on-screen banner in the one case that
    /// matters most: no XR loader ever became active.
    ///
    /// Exists because this exact failure has already shipped once, silently: an empty
    /// Android XR loader list was committed onto the PICO mainline (see CLAUDE.md's
    /// progress log), and the resulting APK installed, launched, and ran as a flat 2D
    /// panel — no stereo, no head tracking, no controllers, and no error anywhere. On
    /// device that is indistinguishable from "the app is simply broken". This makes
    /// that state loud instead, and also dumps the raw <see cref="InputDevices"/> list
    /// so a real controller's actual <see cref="InputDeviceCharacteristics"/> can be
    /// read off logcat rather than guessed at from documentation.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class XrBootDiagnostics : MonoBehaviour
    {
        const int WaitFrames = 8;

        bool _fatal;
        string _fatalReason = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (FindObjectOfType<XrBootDiagnostics>() != null) return;
            var host = new GameObject("XR Boot Diagnostics");
            host.AddComponent<XrBootDiagnostics>();
            DontDestroyOnLoad(host);
        }

        IEnumerator Start()
        {
            // Give the loader/input subsystem a few frames to report its first pose,
            // same reasoning WeatherSceneBootstrap uses before placing the exhibit.
            for (int i = 0; i < WaitFrames; i++)
                yield return null;

            Report();
        }

        void Report()
        {
            var settings = XRGeneralSettings.Instance;
            XRManagerSettings manager = settings != null ? settings.Manager : null;
            XRLoader active = manager != null ? manager.activeLoader : null;

            var loaderNames = new StringBuilder();
            int loaderCount = 0;
            if (manager != null)
            {
                foreach (var loader in manager.activeLoaders)
                {
                    if (loaderNames.Length > 0) loaderNames.Append(", ");
                    loaderNames.Append(loader.GetType().Name);
                    loaderCount++;
                }
            }

            Debug.Log($"[WeatherVR] XRBOOT settings={(settings != null ? "ok" : "NULL")} " +
                      $"initOnStart={(settings != null && settings.InitManagerOnStart)} " +
                      $"loaders={loaderCount} [{loaderNames}] " +
                      $"activeLoader={(active != null ? active.GetType().Name : "NULL")}");

            Debug.Log($"[WeatherVR] XRBOOT XRSettings enabled={XRSettings.enabled} " +
                      $"deviceActive={XRSettings.isDeviceActive} " +
                      $"device=\"{XRSettings.loadedDeviceName}\" stereo={XRSettings.stereoRenderingMode}");

            var inputSubsystems = new List<XRInputSubsystem>();
            SubsystemManager.GetInstances(inputSubsystems);
            if (inputSubsystems.Count > 0)
            {
                var sub = inputSubsystems[0];
                Debug.Log($"[WeatherVR] XRBOOT origin mode={sub.GetTrackingOriginMode()} " +
                          $"supported={sub.GetSupportedTrackingOriginModes()}");
            }
            else
            {
                Debug.Log("[WeatherVR] XRBOOT origin: no XRInputSubsystem present.");
            }

            // The unfiltered dump. XRPointer only ever looks at devices matching
            // HeldInHand|Controller|Right/Left -- this is what tells us whether a real
            // PICO controller actually reports those characteristics, instead of
            // guessing from documentation.
            var devices = new List<InputDevice>();
            InputDevices.GetDevices(devices);
            Debug.Log($"[WeatherVR] XRBOOT devices={devices.Count}");
            foreach (var device in devices)
            {
                device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked);
                bool hasPos = device.TryGetFeatureValue(CommonUsages.devicePosition, out _);
                bool hasRot = device.TryGetFeatureValue(CommonUsages.deviceRotation, out _);
                Debug.Log($"[WeatherVR] XRBOOT device \"{device.name}\" " +
                          $"chars={device.characteristics} tracked={tracked} " +
                          $"hasPos={hasPos} hasRot={hasRot}");
            }

            // Non-zero control counts mean <XRHMD>/centerEyePosition|Rotation actually
            // resolved to a device -- exactly the silent failure HeadTracking's own
            // header warns about but never checks.
            var camera = Camera.main;
            var driver = camera != null ? camera.GetComponent<TrackedPoseDriver>() : null;
            int posControls = driver != null && driver.positionInput.action != null
                ? driver.positionInput.action.controls.Count : -1;
            int rotControls = driver != null && driver.rotationInput.action != null
                ? driver.rotationInput.action.controls.Count : -1;
            Debug.Log($"[WeatherVR] XRBOOT hmd driver={(driver != null)} " +
                      $"posControls={posControls} rotControls={rotControls}");

            // Flat editor Play and the desktop preview correctly have no active XR
            // loader -- only a mobile/device build with none is a real failure.
            if (Application.isMobilePlatform && active == null)
            {
                _fatal = true;
                _fatalReason =
                    "No XR loader initialised. This build is running as a flat 2D " +
                    "panel: no stereo, no head tracking, no controllers. Fix: " +
                    "Tools > WeatherVR > Configure Player Settings, then rebuild.";
                Debug.LogError("[WeatherVR] XRBOOT FATAL: " + _fatalReason);
            }
        }

        void OnGUI()
        {
            if (!_fatal) return;

            // OnGUI needs no EventSystem and no Canvas -- there is neither on
            // Android -- and in Built-in RP it draws into the eye buffer, so this is
            // visible both in VR and in the flat 2D window this exact failure produces.
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                wordWrap = true,
                normal = { textColor = new Color(1f, 0.55f, 0.5f) }
            };
            GUI.Box(new Rect(10, 10, 620, 96), GUIContent.none);
            GUI.Label(new Rect(20, 16, 600, 86), "XRBOOT FATAL\n" + _fatalReason, style);
        }
    }
}
