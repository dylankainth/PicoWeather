#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace WeatherVR.IntroEarth.Editor
{
    /// <summary>
    /// Renders the exact runtime globe so its appearance can be reviewed without a
    /// headset. This is a preview utility only and is excluded from player builds.
    /// </summary>
    public static class EarthIntroPreview
    {
        const string OutputPath = "Builds/earth-intro-preview.png";
        const int Width = 1100;
        const int Height = 900;

        [MenuItem("Tools/WeatherVR/Render Earth Intro Preview", priority = 61)]
        public static void RenderInteractive()
        {
            string path = Render();
            if (!string.IsNullOrEmpty(path))
                EditorUtility.RevealInFinder(Path.GetFullPath(path));
        }

        public static void RenderFromCommandLine()
        {
            try
            {
                string path = Render();
                if (Application.isBatchMode)
                    EditorApplication.Exit(string.IsNullOrEmpty(path) ? 1 : 0);
            }
            catch (Exception exception)
            {
                Debug.LogError("[WeatherVR] Earth preview failed: " + exception);
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
            }
        }

        public static string Render()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Shader shader = Resources.Load<Shader>("IntroEarth");
            if (shader == null)
                throw new InvalidOperationException("IntroEarth shader was not imported.");

            Material earthMaterial = NewMaterial(
                shader, "Preview Earth Surface", Color.white);
            Material grid = NewMaterial(
                shader, "Preview Grid", new Color(0.52f, 0.82f, 0.94f, 0.28f));
            Material marker = NewMaterial(
                shader, "Preview London", new Color(1.0f, 0.48f, 0.27f, 1f));

            LowPolyEarthBuilder.Visual earth = null;
            RenderTexture target = null;
            Texture2D image = null;
            GameObject cameraObject = null;

            try
            {
                var host = new GameObject("Earth Preview");
                earth = LowPolyEarthBuilder.Build(
                    host.transform, earthMaterial, grid, marker);

                Vector3 london = LowPolyEarthBuilder.DirectionFromLatitudeLongitude(
                    LowPolyEarthBuilder.LondonLatitude,
                    LowPolyEarthBuilder.LondonLongitude);
                earth.Root.transform.rotation =
                    Quaternion.FromToRotation(london, Vector3.back);
                earth.Root.transform.localScale = Vector3.one * 1.25f;
                earth.LondonMarker.localScale = Vector3.one * 0.021f;

                cameraObject = new GameObject("Preview Camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.008f, 0.014f, 0.030f);
                camera.fieldOfView = 31f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 20f;
                camera.transform.position = new Vector3(0f, 0.015f, -2.05f);
                camera.transform.LookAt(Vector3.zero);

                target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
                {
                    name = "Earth Intro Preview",
                    antiAliasing = 4
                };
                target.Create();
                camera.targetTexture = target;
                camera.Render();
                camera.targetTexture = null;

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = target;
                image = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                image.Apply();
                RenderTexture.active = previous;

                Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
                File.WriteAllBytes(OutputPath, image.EncodeToPNG());
                Debug.Log($"[WeatherVR] Earth intro preview written to {OutputPath}.");
                return OutputPath;
            }
            finally
            {
                if (target != null) target.Release();
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (earth?.Root != null) UnityEngine.Object.DestroyImmediate(earth.Root);
                if (earth?.Meshes != null)
                {
                    foreach (Mesh mesh in earth.Meshes)
                        if (mesh != null)
                            UnityEngine.Object.DestroyImmediate(mesh);
                }
                UnityEngine.Object.DestroyImmediate(earthMaterial);
                UnityEngine.Object.DestroyImmediate(grid);
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        static Material NewMaterial(Shader shader, string name, Color colour)
        {
            var material = new Material(shader) { name = name };
            material.SetColor("_Color", colour);
            return material;
        }
    }
}
#endif
