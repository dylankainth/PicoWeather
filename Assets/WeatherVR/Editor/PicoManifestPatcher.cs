using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

namespace WeatherVR.EditorTools
{
    /// <summary>
    /// Guarantees the PICO hand-tracking manifest entries end up in the APK.
    ///
    /// Why this exists rather than trusting the SDK: PICO OS refuses to launch an app
    /// it classifies as "ControllerOnly" while no controller is connected — on the
    /// PICO Emulator the launch is intercepted and parked in a pending queue:
    ///
    ///   AppStartInterceptManager: Intercepted for ControllerOnly:
    ///       Controller paired but not connected. Pkg: com.weathervr.immersive
    ///
    /// An app avoids that classification by declaring hand-tracking support. The SDK
    /// is supposed to write those entries when <c>handTracking</c> is enabled in
    /// PXR_ProjectSetting, but with the setting verifiably enabled three minutes
    /// before the build ran, the generated manifest still came out with only
    /// <c>controller=1</c> and no <c>handtracking</c> entry or permission. Rather
    /// than depend on that path, this writes the entries directly.
    ///
    /// Runs with a high callback order so it lands after the PICO SDK's own manifest
    /// pass; otherwise the SDK's "disabled" branch would strip these again.
    /// </summary>
    public class PicoManifestPatcher : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 1000;

        const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

        public void OnPostGenerateGradleAndroidProject(string projectPath)
        {
            string manifestPath = Path.Combine(projectPath, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning($"[WeatherVR] No manifest to patch at {manifestPath}");
                return;
            }

            var document = new XmlDocument();
            document.Load(manifestPath);

            var manifest = document.DocumentElement;
            if (manifest == null) return;

            var application = manifest.SelectSingleNode("application") as XmlElement;
            if (application == null)
            {
                Debug.LogWarning("[WeatherVR] Manifest has no <application> element; skipping patch.");
                return;
            }

            bool changed = false;

            changed |= UseLifecycleAwareActivity(application);

            // Declaring both means "controllers and hands", which is what the app
            // actually supports: XRPointer reads controllers, and falls back to the
            // PICO hand aim state when the runtime reports hands as active.
            changed |= SetMetaData(document, application, "handtracking", "1");
            changed |= SetMetaData(document, application, "controller", "1");
            changed |= AddPermission(document, manifest, "com.picovr.permission.HAND_TRACKING");

            if (changed)
            {
                document.Save(manifestPath);
                Debug.Log("[WeatherVR] Patched AndroidManifest: hand tracking declared " +
                          "so PICO OS does not gate launch on a connected controller.");
            }
        }

        static bool UseLifecycleAwareActivity(XmlElement application)
        {
            foreach (XmlNode node in application.SelectNodes("activity"))
            {
                if (node is not XmlElement activity) continue;
                string name = activity.GetAttribute("name", AndroidNamespace);
                if (name != "com.unity3d.player.UnityPlayerActivity" &&
                    name != "com.weathervr.WeatherVRActivity")
                    continue;

                if (name == "com.weathervr.WeatherVRActivity")
                    return false;

                activity.SetAttribute(
                    "name",
                    AndroidNamespace,
                    "com.weathervr.WeatherVRActivity");
                return true;
            }

            Debug.LogWarning(
                "[WeatherVR] Unity launcher activity was not found; foreground intro " +
                "replay cannot be wired.");
            return false;
        }

        /// <summary>Adds or updates an <c>&lt;meta-data&gt;</c> entry under application.</summary>
        static bool SetMetaData(XmlDocument document, XmlElement application, string name, string value)
        {
            foreach (XmlNode node in application.SelectNodes("meta-data"))
            {
                if (node is not XmlElement element) continue;
                if (element.GetAttribute("name", AndroidNamespace) != name) continue;

                if (element.GetAttribute("value", AndroidNamespace) == value) return false;
                element.SetAttribute("value", AndroidNamespace, value);
                return true;
            }

            var created = document.CreateElement("meta-data");
            created.SetAttribute("name", AndroidNamespace, name);
            created.SetAttribute("value", AndroidNamespace, value);
            application.AppendChild(created);
            return true;
        }

        /// <summary>Adds a <c>&lt;uses-permission&gt;</c> if it is not already declared.</summary>
        static bool AddPermission(XmlDocument document, XmlElement manifest, string name)
        {
            foreach (XmlNode node in manifest.SelectNodes("uses-permission"))
            {
                if (node is XmlElement element &&
                    element.GetAttribute("name", AndroidNamespace) == name)
                {
                    return false;
                }
            }

            var created = document.CreateElement("uses-permission");
            created.SetAttribute("name", AndroidNamespace, name);
            manifest.AppendChild(created);
            return true;
        }
    }
}
