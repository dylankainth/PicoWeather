using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;

namespace WeatherVR.EditorTools
{
    /// <summary>
    /// Refuses to let a PICO headset build leave this machine with no working Android
    /// XR loader.
    ///
    /// <see cref="ProjectConfigurator.EnsureAndroidXrLoader"/> is the fix — it runs
    /// inside <c>Configure()</c>, before <c>BuildPipeline.BuildPlayer</c> is even
    /// called, and repairs exactly this. This guard is the belt to that brace: it runs
    /// at <see cref="callbackOrder"/> 2000, deliberately *after* XR Management's own
    /// preprocessor (order 0) has already decided what to bake into the player's
    /// preloaded assets, so by the time this callback fires the moment to fix anything
    /// has already passed — it can only check and refuse. That is exactly the
    /// behaviour we want here: if the loader is still missing at this point, something
    /// bypassed <c>ProjectConfigurator.Configure()</c> (a raw
    /// <c>BuildPipeline.BuildPlayer</c> call, a hand edit made after Configure ran,
    /// etc.), and building anyway produces the exact silent failure this project has
    /// already shipped once — an APK that installs, launches flat as a 2D panel, and
    /// gives no error anywhere that it did.
    /// </summary>
    public sealed class PicoBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => 2000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android) return;
            if (!PicoBuildTarget.IsPico) return;

            var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            bool loaderMissing = settings?.Manager == null ||
                                  !XRPackageMetadataStore.IsLoaderAssigned(
                                      ProjectConfigurator.PicoLoaderTypeName, BuildTargetGroup.Android);
            bool notInitialized = settings == null || !settings.InitManagerOnStart;

            if (loaderMissing || notInitialized)
            {
                throw new BuildFailedException(
                    "[WeatherVR] Refusing to build the PICO headset APK: no Android XR " +
                    "loader is active (or InitManagerOnStart is off). Without one the " +
                    "PICO SDK never stamps pvr.app.type=vr into the manifest, and PICO " +
                    "OS launches the app as a flat 2D panel — no stereo, no head " +
                    "tracking, no controllers. Run Tools ▸ WeatherVR ▸ Configure Player " +
                    "Settings (or BuildAPK.PrepareFromCommandLine in batch mode) and " +
                    "build again.");
            }
        }
    }
}
