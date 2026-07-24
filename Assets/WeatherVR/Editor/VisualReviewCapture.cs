using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WeatherVR.Core;
using WeatherVR.Data;
using WeatherVR.Terrain;
using WeatherVR.UI.Carousel;
using WeatherVR.Weather;

namespace WeatherVR.EditorTools
{
    /// <summary>
    /// Produces deterministic review frames of the actual runtime shaders, procedural
    /// weather and world-space carousel. This is intentionally separate from the
    /// marketing hero renderer: it is a lightweight visual-regression surface.
    /// </summary>
    public static class VisualReviewCapture
    {
        const string OutputDirectory = "Builds/VisualReview";

        public static void CaptureFromCommandLine()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Directory.CreateDirectory(OutputDirectory);
                Capture(
                    WeatherSceneKind.Clear,
                    Path.Combine(OutputDirectory, "clear-1536x1024.png"),
                    1536,
                    1024);
                Capture(
                    WeatherSceneKind.Thunderstorm,
                    Path.Combine(OutputDirectory, "storm-1024x768.png"),
                    1024,
                    768);
                Debug.Log("[WeatherVR] Visual review captures completed.");
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[WeatherVR] Visual review capture failed: {exception}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
        }

        static void Capture(WeatherSceneKind kind, string outputPath, int width, int height)
        {
            var created = new List<UnityEngine.Object>();
            RenderTexture target = null;
            Texture2D pixels = null;
            RenderTexture previous = RenderTexture.active;

            try
            {
                AppConfig config = SceneBuilder.EnsureConfigAsset();
                WeatherSnapshot snapshot = LoadSnapshot(config, created);

                var cameraObject = new GameObject("Review Camera");
                created.Add(cameraObject);
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.fieldOfView = 48f;
                camera.nearClipPlane = 0.03f;
                camera.farClipPlane = 100f;
                camera.allowHDR = true;
                cameraObject.transform.position = new Vector3(0f, 1.72f, -3.25f);
                cameraObject.transform.LookAt(new Vector3(0f, 0.42f, 0.15f));

                var sky = new Material(Shader.Find("WeatherVR/StudioSky"));
                created.Add(sky);
                RenderSettings.skybox = sky;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;

                var sunObject = new GameObject("Review Sun");
                created.Add(sunObject);
                Light sun = sunObject.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.shadows = LightShadows.None;

                var mapObject = new GameObject("Review Weather Map");
                created.Add(mapObject);
                mapObject.transform.localScale = Vector3.one * config.MapSizeMeters;
                BuildTerrain(mapObject.transform, snapshot, config, created);
                BuildPedestal(mapObject.transform, created);

                var weatherObject = new GameObject("Review Weather");
                created.Add(weatherObject);
                weatherObject.transform.SetParent(mapObject.transform, false);
                WeatherVisuals visuals = weatherObject.AddComponent<WeatherVisuals>();
                visuals.Build(config);

                var environmentObject = new GameObject("Review Environment");
                created.Add(environmentObject);
                EnvironmentController environment =
                    environmentObject.AddComponent<EnvironmentController>();

                var directorObject = new GameObject("Review Director");
                created.Add(directorObject);
                WeatherSceneDirector director =
                    directorObject.AddComponent<WeatherSceneDirector>();
                director.Visuals = visuals;
                director.Sun = sun;
                director.Environment = environment;
                director.Initialize(config);
                director.ApplyKind(kind);

                foreach (ParticleSystem particles in
                         weatherObject.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (particles.isPlaying)
                        particles.Simulate(8f, true, true, true);
                }

                WeatherCarouselDataset dataset = BuildDataset();
                BuiltWeatherCarousel carousel =
                    new WeatherCarouselBuilder().Build(dataset, camera.transform, null);
                created.Add(carousel.Root);
                carousel.Visibility.alpha = 1f;
                carousel.Visibility.interactable = true;
                carousel.Visibility.blocksRaycasts = true;
                carousel.Controller.SelectImmediate(Array.IndexOf(WeatherScene.AllKinds, kind));

                // Match the runtime wall mount, but keep the panel slightly proud of
                // the front edge so spacing and readability are visible in a still.
                carousel.Root.transform.position = new Vector3(0f, 0.18f, -1.34f);
                carousel.Root.transform.rotation = Quaternion.identity;

                target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                target.Create();
                camera.targetTexture = target;
                camera.Render();
                camera.targetTexture = null;

                RenderTexture.active = target;
                pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                pixels.Apply();
                File.WriteAllBytes(outputPath, pixels.EncodeToPNG());
                Debug.Log($"[WeatherVR] Wrote visual review frame {outputPath}.");
            }
            finally
            {
                RenderTexture.active = previous;
                if (target != null) target.Release();
                if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
                foreach (UnityEngine.Object item in created)
                {
                    if (item != null) UnityEngine.Object.DestroyImmediate(item);
                }
            }
        }

        static WeatherCarouselDataset BuildDataset()
        {
            WeatherCarouselDataset dataset = null;
            var load = new SceneKindCarouselDataProvider().Load(
                null,
                value => dataset = value,
                message => throw new InvalidOperationException(message));
            while (load.MoveNext()) { }
            return dataset;
        }

        static WeatherSnapshot LoadSnapshot(
            AppConfig config,
            List<UnityEngine.Object> created)
        {
            var snapshot = new WeatherSnapshot
            {
                Bounds = GeoBounds.FromCenterSpan(
                    config.CenterLatitude,
                    config.CenterLongitude,
                    config.RegionSpanKm)
            };

            byte[] terrainBytes =
                StreamingDataReader.ReadImmediate(WeatherDataService.TerrainFile);
            if (terrainBytes != null)
                snapshot.Terrain = TerrainHeightfield.FromBytes(terrainBytes);
            if (snapshot.Terrain == null)
                snapshot.Terrain = ProceduralTerrain.Generate(
                    snapshot.Bounds,
                    256,
                    config.ProceduralSeed);

            byte[] satelliteBytes =
                StreamingDataReader.ReadImmediate(WeatherDataService.SatelliteFile);
            if (satelliteBytes != null)
            {
                var satellite = new Texture2D(2, 2, TextureFormat.RGB24, true, false);
                if (satellite.LoadImage(satelliteBytes))
                {
                    satellite.wrapMode = TextureWrapMode.Clamp;
                    snapshot.Satellite = satellite;
                    created.Add(satellite);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(satellite);
                }
            }

            if (snapshot.Satellite == null)
            {
                snapshot.Satellite = ProceduralSatellite.Generate(
                    snapshot.Terrain,
                    1024,
                    config.ProceduralSeed);
                created.Add(snapshot.Satellite);
            }

            return snapshot;
        }

        static void BuildTerrain(
            Transform parent,
            WeatherSnapshot snapshot,
            AppConfig config,
            List<UnityEngine.Object> created)
        {
            Mesh mesh = TerrainMeshBuilder.Build(
                snapshot.Terrain,
                config.TerrainMeshResolution,
                config);
            created.Add(mesh);

            var terrainObject = new GameObject("Review Terrain");
            created.Add(terrainObject);
            terrainObject.transform.SetParent(parent, false);
            terrainObject.AddComponent<MeshFilter>().sharedMesh = mesh;

            var material = new Material(Shader.Find("WeatherVR/TerrainSurface"));
            created.Add(material);
            material.SetTexture("_MainTex", snapshot.Satellite);
            material.SetFloat("_Brightness", 0.88f);
            material.SetFloat("_Saturation", 0.98f);
            material.SetFloat("_LandSmoothness", 0.02f);
            material.SetFloat("_WaterSmoothness", 0.32f);
            terrainObject.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        static void BuildPedestal(Transform parent, List<UnityEngine.Object> created)
        {
            Mesh mesh = PedestalMeshBuilder.Build();
            created.Add(mesh);
            var pedestalObject = new GameObject("Review Pedestal");
            created.Add(pedestalObject);
            pedestalObject.transform.SetParent(parent, false);
            pedestalObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(Shader.Find("WeatherVR/Pedestal"));
            created.Add(material);
            pedestalObject.AddComponent<MeshRenderer>().sharedMaterial = material;
        }
    }
}
