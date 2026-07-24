using System;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace WeatherVR.EditorTools
{
    /// <summary>
    /// Adds AR Foundation and the ARCore provider for the phone-AR build.
    ///
    /// Uses the Package Manager API rather than editing <c>manifest.json</c> by hand
    /// so Unity resolves whichever versions are actually compatible with this editor,
    /// instead of us guessing a version string that may not exist for 2022.3.
    /// </summary>
    public static class AddArPackages
    {
        static readonly string[] Packages =
        {
            "com.unity.xr.arfoundation",
            "com.unity.xr.arcore"
        };

        [MenuItem("Tools/WeatherVR/Add AR Foundation Packages", priority = 80)]
        public static void AddInteractive() => Run();

        /// <summary>Batch entry point; exits non-zero if a package cannot be added.</summary>
        public static void AddFromCommandLine()
        {
            bool ok = Run();
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        static bool Run()
        {
            foreach (string package in Packages)
            {
                Debug.Log($"[WeatherVR] Adding package {package}…");
                AddRequest request = Client.Add(package);

                // Batch mode has no editor loop to pump the request, so spin it here.
                while (!request.IsCompleted) System.Threading.Thread.Sleep(100);

                if (request.Status == StatusCode.Success)
                {
                    Debug.Log($"[WeatherVR] Added {request.Result.packageId}");
                }
                else
                {
                    Debug.LogError($"[WeatherVR] Failed to add {package}: {request.Error?.message}");
                    return false;
                }
            }

            AssetDatabase.Refresh();
            Debug.Log("[WeatherVR] AR packages resolved.");
            return true;
        }
    }
}
