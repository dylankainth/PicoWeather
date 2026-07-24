using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WeatherVR.Core;
using WeatherVR.Data;
using WeatherVR.Terrain;

namespace WeatherVR.EditorTools
{
    /// <summary>
    /// Renders a high-quality still of the weather map, headlessly.
    ///
    /// The app itself is a VR experience and cannot be screenshotted from outside a
    /// headset, so this builds the same scene an isolated, controllable way and
    /// photographs it: real Shanghai terrain and imagery under a dramatic synthetic
    /// storm, backlit, with a frozen lightning strike lighting the cloud from
    /// within, then run through the HDR post stack (bloom + ACES + vignette).
    ///
    /// It renders far above the on-device budget on purpose — 2x supersampling, the
    /// maximum march step count, full cloud self-shadowing — because this is an
    /// offline frame, not a real-time one.
    /// </summary>
    public static class CaptureTool
    {
        const string OutputPath = "Builds/hero.png";
        const int Width = 1600;
        const int Height = 1000;
        const int Supersample = 2;

        [MenuItem("Tools/WeatherVR/Render Hero Shot", priority = 60)]
        public static void RenderInteractive()
        {
            string path = Render(OutputPath, Width, Height, Supersample);
            if (path != null)
            {
                AssetDatabase.Refresh();
                EditorUtility.RevealInFinder(Path.GetFullPath(path));
            }
        }

        /// <summary>Batch-mode entry point. Exits non-zero on failure.</summary>
        public static void RenderFromCommandLine()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                string path = Render(OutputPath, Width, Height, Supersample);
                if (Application.isBatchMode) EditorApplication.Exit(path != null ? 0 : 1);
            }
            catch (Exception e)
            {
                Debug.LogError($"[WeatherVR] Hero render threw: {e}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
        }

        // ---------------------------------------------------------------- render

        public static string Render(string outputPath, int width, int height, int supersample)
        {
            var config = SceneBuilder.EnsureConfigAsset();
            int rw = width * supersample;
            int rh = height * supersample;

            // The real Yangtze delta is nearly flat; at the runtime relief boost it
            // crumples into spiky noise under a low camera. Calm it for the hero so it
            // reads as the delta it is, and restore afterwards.
            float savedRelief = config.TerrainReliefExaggeration;
            config.TerrainReliefExaggeration = 5f;

            // Compress the atmosphere for the shot: at the runtime x4 the low/mid/high
            // decks stretch into separate floating slabs; x2.8 pulls the storm into one
            // connected mass sitting over the city.
            float savedVertical = config.VerticalExaggeration;
            config.VerticalExaggeration = 2.8f;

            var created = new System.Collections.Generic.List<UnityEngine.Object>();
            RenderTexture hdr = null, ldr = null, bloomA = null, bloomB = null;
            Material sky = null, post = null;

            try
            {
                var snapshot = BuildSnapshot(config);

                // --- environment ------------------------------------------------
                sky = new Material(Shader.Find("WeatherVR/StudioSky"));
                // Sun low and to the back-right, so the camera looks into a backlit
                // storm — the arrangement that produces silver-lined cloud edges.
                Vector3 sunDir = new Vector3(0.35f, 0.16f, 0.92f).normalized;
                sky.SetVector("_SunDir", sunDir);
                RenderSettings.skybox = sky;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = new Color(0.10f, 0.13f, 0.20f);
                RenderSettings.ambientEquatorColor = new Color(0.06f, 0.07f, 0.10f);
                RenderSettings.ambientGroundColor = new Color(0.02f, 0.02f, 0.03f);
                RenderSettings.fog = false;

                var sunObject = new GameObject("Sun");
                created.Add(sunObject);
                var sun = sunObject.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.color = new Color(1.0f, 0.86f, 0.66f);
                sun.intensity = 1.5f;
                sun.shadows = LightShadows.None;
                // Point the light *from* the sun direction used by the sky glow.
                sunObject.transform.rotation = Quaternion.LookRotation(-sunDir, Vector3.up);

                // A cool fill from the opposite side keeps the shadowed cloud faces
                // from going dead black.
                var fillObject = new GameObject("Fill");
                created.Add(fillObject);
                var fill = fillObject.AddComponent<Light>();
                fill.type = LightType.Directional;
                fill.color = new Color(0.35f, 0.45f, 0.65f);
                fill.intensity = 0.4f;
                fill.shadows = LightShadows.None;
                fillObject.transform.rotation = Quaternion.Euler(35f, 200f, 0f);

                // --- map --------------------------------------------------------
                var mapRoot = new GameObject("WeatherMap");
                created.Add(mapRoot);
                mapRoot.transform.position = new Vector3(0f, 0f, 0f);
                mapRoot.transform.localScale = Vector3.one * config.MapSizeMeters;

                BuildTerrain(mapRoot.transform, snapshot, config, created);

                // --- camera -----------------------------------------------------
                var cameraObject = new GameObject("CaptureCamera");
                created.Add(cameraObject);
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.fieldOfView = 34f;
                camera.nearClipPlane = 0.03f;
                camera.farClipPlane = 100f;
                camera.allowHDR = true;
                camera.depthTextureMode = DepthTextureMode.Depth;
                // A three-quarter view pulled back far enough to hold the whole 2 m
                // map with the storm towering over it.
                camera.fieldOfView = 37f;
                cameraObject.transform.position = new Vector3(2.6f, 1.85f, -2.95f);
                cameraObject.transform.LookAt(new Vector3(0f, 0.42f, 0f));

                // --- render -----------------------------------------------------
                hdr = new RenderTexture(rw, rh, 24, RenderTextureFormat.ARGBHalf)
                { name = "HeroHDR" };
                hdr.Create();
                camera.targetTexture = hdr;
                camera.Render();
                camera.targetTexture = null;

                // --- post -------------------------------------------------------
                post = new Material(Shader.Find("WeatherVR/PostFX"));
                post.SetFloat("_Threshold", 1.35f);   // only the brightest edges bloom
                post.SetFloat("_Knee", 0.7f);
                post.SetFloat("_BloomIntensity", 0.7f);
                post.SetFloat("_Exposure", 0.95f);
                post.SetFloat("_Vignette", 1.2f);
                post.SetFloat("_Saturation", 1.12f);
                post.SetColor("_Lift", new Color(0.012f, 0.016f, 0.028f, 0f));

                int bw = rw / 2, bh = rh / 2;
                bloomA = RenderTexture.GetTemporary(bw, bh, 0, RenderTextureFormat.ARGBHalf);
                bloomB = RenderTexture.GetTemporary(bw, bh, 0, RenderTextureFormat.ARGBHalf);

                Graphics.Blit(hdr, bloomA, post, 0);                 // bright pass
                for (int i = 0; i < 3; i++)                          // separable blur x3
                {
                    post.SetVector("_BlurDir", new Vector2(1f, 0f));
                    Graphics.Blit(bloomA, bloomB, post, 1);
                    post.SetVector("_BlurDir", new Vector2(0f, 1f));
                    Graphics.Blit(bloomB, bloomA, post, 1);
                }

                // Linear read/write: the composite shader does its own sRGB encode, so
                // the hardware must not also convert on store.
                ldr = new RenderTexture(rw, rh, 0, RenderTextureFormat.ARGB32,
                                        RenderTextureReadWrite.Linear)
                { name = "HeroLDR" };
                post.SetTexture("_BloomTex", bloomA);
                Graphics.Blit(hdr, ldr, post, 2);                    // composite

                // --- downsample + encode ---------------------------------------
                string path = Encode(ldr, width, height, outputPath);
                Debug.Log($"[WeatherVR] Hero shot written to {path} " +
                          $"({snapshot.Describe()}).");
                return path;
            }
            catch (Exception e)
            {
                Debug.LogError($"[WeatherVR] Hero render failed: {e}");
                return null;
            }
            finally
            {
                if (bloomA != null) RenderTexture.ReleaseTemporary(bloomA);
                if (bloomB != null) RenderTexture.ReleaseTemporary(bloomB);
                if (hdr != null) hdr.Release();
                if (ldr != null) ldr.Release();
                foreach (var obj in created) if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
                if (sky != null) UnityEngine.Object.DestroyImmediate(sky);
                if (post != null) UnityEngine.Object.DestroyImmediate(post);
                config.TerrainReliefExaggeration = savedRelief;
                config.VerticalExaggeration = savedVertical;
            }
        }

        // --------------------------------------------------------------- pieces

        static WeatherSnapshot BuildSnapshot(AppConfig config)
        {
            var bounds = GeoBounds.FromCenterSpan(
                config.CenterLatitude, config.CenterLongitude, config.RegionSpanKm);

            var snapshot = new WeatherSnapshot { Bounds = bounds };

            // Real Shanghai ground if it has been baked, procedural otherwise.
            byte[] terrainBytes = StreamingDataReader.ReadImmediate(WeatherDataService.TerrainFile);
            if (terrainBytes != null)
            {
                try
                {
                    snapshot.Terrain = TerrainHeightfield.FromBytes(terrainBytes);
                    snapshot.Bounds = snapshot.Terrain.Bounds;
                    snapshot.TerrainSource = "baked";
                }
                catch { snapshot.Terrain = null; }
            }
            if (snapshot.Terrain == null)
                snapshot.Terrain = ProceduralTerrain.Generate(bounds, 384, config.ProceduralSeed);

            byte[] satelliteBytes = StreamingDataReader.ReadImmediate(WeatherDataService.SatelliteFile);
            if (satelliteBytes != null)
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, true, false);
                if (tex.LoadImage(satelliteBytes))
                {
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.anisoLevel = 8;
                    snapshot.Satellite = tex;
                    snapshot.SatelliteSource = "baked";
                }
                else Debug.LogWarning("[WeatherVR] satellite.jpg failed to decode.");
            }
            else Debug.LogWarning($"[WeatherVR] satellite.jpg not found at " +
                                  $"{StreamingDataReader.PathFor(WeatherDataService.SatelliteFile)}");
            if (snapshot.Satellite == null)
                snapshot.Satellite = ProceduralSatellite.Generate(snapshot.Terrain, 1024, config.ProceduralSeed);

            // A dramatic storm regardless of the real weather, so the hero shot has a
            // storm to show. This is the same synthetic squall line the demo mode uses.
            snapshot.Weather = ProceduralWeather.Generate(snapshot.Bounds, 48, config.ProceduralSeed, phase: 0.44f);
            snapshot.WeatherSource = "procedural storm (hero)";
            return snapshot;
        }

        static void BuildTerrain(Transform parent, WeatherSnapshot snapshot, AppConfig config,
                                 System.Collections.Generic.List<UnityEngine.Object> created)
        {
            var mesh = TerrainMeshBuilder.Build(snapshot.Terrain, config.TerrainMeshResolution, config);
            var obj = new GameObject("Terrain");
            created.Add(obj);
            obj.transform.SetParent(parent, false);
            obj.AddComponent<MeshFilter>().sharedMesh = mesh;

            var material = new Material(Shader.Find("WeatherVR/TerrainSurface"));
            if (snapshot.Satellite != null) material.SetTexture("_MainTex", snapshot.Satellite);
            material.SetFloat("_Brightness", 0.92f);
            material.SetFloat("_Saturation", 1.15f);
            // Kill the specular sun-glint that blows out the flat map under a grazing
            // key light — imagery reads better matte here.
            material.SetFloat("_LandSmoothness", 0f);
            material.SetFloat("_WaterSmoothness", 0.25f);
            material.SetFloat("_WaterSpecular", 0.1f);
            obj.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        static string Encode(RenderTexture source, int width, int height, string outputPath)
        {
            // Downsample the supersampled frame to the target size. Linear, because
            // the pixels are already sRGB-encoded bytes and must be copied verbatim.
            var resolved = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                                                      RenderTextureReadWrite.Linear);
            Graphics.Blit(source, resolved);

            var previous = RenderTexture.active;
            RenderTexture.active = resolved;
            var image = new Texture2D(width, height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            image.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(resolved);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllBytes(outputPath, image.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(image);
            return outputPath;
        }
    }
}
