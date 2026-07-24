using System;
using System.Collections;
using UnityEngine;
using WeatherVR.Core;

namespace WeatherVR.Data
{
    /// <summary>
    /// Everything the scene needs to draw itself, plus where each piece came from.
    /// </summary>
    public class WeatherSnapshot
    {
        public GeoBounds Bounds;
        public TerrainHeightfield Terrain;
        public Texture2D Satellite;
        public WeatherDataset Weather;
        public BuildingDataset Buildings;

        /// <summary>Per-source provenance strings, for the in-app label and the logs.</summary>
        public string TerrainSource = "procedural";
        public string SatelliteSource = "procedural";
        public string WeatherSource = "procedural";
        public string BuildingsSource = "procedural";

        public bool IsComplete => Terrain != null && Weather != null && Weather.IsValid;

        public string Describe() =>
            $"terrain: {TerrainSource} · imagery: {SatelliteSource} · " +
            $"buildings: {BuildingsSource} · weather: {WeatherSource}";
    }

    /// <summary>
    /// Assembles a <see cref="WeatherSnapshot"/> from the best source available, in
    /// this order for each layer:
    ///
    ///   1. a live fetch (weather only, and only if <see cref="AppConfig.AllowLiveFetch"/>),
    ///   2. the baked payload in StreamingAssets,
    ///   3. deterministic procedural generation.
    ///
    /// Step 3 always succeeds, so <see cref="Load"/> always produces a drawable
    /// scene. That is deliberate: a hackathon demo must never fail to start because
    /// the venue Wi-Fi is down.
    /// </summary>
    public class WeatherDataService : MonoBehaviour
    {
        public const string TerrainFile = "terrain.bin";
        public const string SatelliteFile = "satellite.jpg";
        public const string BuildingsFile = "buildings.json";
        public const string WeatherFile = "weather.json";

        /// <summary>Grid resolution requested from the live API.</summary>
        const int LiveGridSize = 8;

        /// <summary>Grid resolution of the procedural fallback (cheap, so make it finer).</summary>
        const int ProceduralGridSize = 32;

        /// <summary>Heightfield resolution of the procedural fallback.</summary>
        const int ProceduralTerrainResolution = 384;

        public WeatherSnapshot Snapshot { get; private set; }

        /// <summary>Raised once the snapshot is ready. Fires exactly once per load.</summary>
        public event Action<WeatherSnapshot> Loaded;

        AppConfig Config => AppConfig.Instance;

        /// <summary>
        /// Loads everything. Yields until the snapshot is complete, then raises
        /// <see cref="Loaded"/>.
        /// </summary>
        public IEnumerator Load()
        {
            var config = Config;
            var bounds = GeoBounds.FromCenterSpan(
                config.CenterLatitude, config.CenterLongitude, config.RegionSpanKm);

            var snapshot = new WeatherSnapshot { Bounds = bounds };

            yield return LoadTerrain(snapshot, bounds, config);
            yield return LoadSatellite(snapshot);
            yield return LoadBuildings(snapshot, bounds, config);
            yield return LoadWeather(snapshot, bounds, config);

            Snapshot = snapshot;
            Debug.Log($"[WeatherVR] Data ready — {snapshot.Describe()}");
            Loaded?.Invoke(snapshot);
        }

        // ------------------------------------------------------------- terrain

        IEnumerator LoadTerrain(WeatherSnapshot snapshot, GeoBounds bounds, AppConfig config)
        {
            var read = new StreamingDataReader.Result();
            yield return StreamingDataReader.Read(TerrainFile, read);

            if (read.Success)
            {
                try
                {
                    snapshot.Terrain = TerrainHeightfield.FromBytes(read.Bytes);
                    snapshot.TerrainSource = "SRTM/AWS terrarium (baked)";
                    // Trust the baked file's own georeferencing over the config, so a
                    // bake for a different region still lines up with its imagery.
                    snapshot.Bounds = snapshot.Terrain.Bounds;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[WeatherVR] {TerrainFile} unusable ({e.Message}); generating terrain.");
                    snapshot.Terrain = null;
                }
            }
            else
            {
                Debug.Log($"[WeatherVR] No baked terrain ({read.Error}); generating.");
            }

            if (snapshot.Terrain == null)
            {
                snapshot.Terrain = ProceduralTerrain.Generate(
                    bounds, ProceduralTerrainResolution, config.ProceduralSeed);
                snapshot.TerrainSource = "procedural";
            }
        }

        // ----------------------------------------------------------- satellite

        IEnumerator LoadSatellite(WeatherSnapshot snapshot)
        {
            var read = new StreamingDataReader.Result();
            yield return StreamingDataReader.Read(SatelliteFile, read);

            if (read.Success)
            {
                // linear:false — this is colour imagery, not a data map.
                var texture = new Texture2D(2, 2, TextureFormat.RGB24, mipChain: true, linear: false);
                if (texture.LoadImage(read.Bytes, markNonReadable: true))
                {
                    texture.name = "SatelliteBasemap";
                    texture.wrapMode = TextureWrapMode.Clamp;
                    texture.anisoLevel = 4;
                    snapshot.Satellite = texture;
                    snapshot.SatelliteSource = "ESRI World Imagery (baked)";
                    yield break;
                }

                Destroy(texture);
                Debug.LogWarning($"[WeatherVR] {SatelliteFile} failed to decode; synthesising imagery.");
            }
            else
            {
                Debug.Log($"[WeatherVR] No baked imagery ({read.Error}); synthesising.");
            }

            snapshot.Satellite = ProceduralSatellite.Generate(
                snapshot.Terrain, 1024, Config.ProceduralSeed);
            snapshot.SatelliteSource = "procedural";
        }

        // ------------------------------------------------------------ buildings

        IEnumerator LoadBuildings(WeatherSnapshot snapshot, GeoBounds bounds, AppConfig config)
        {
            var read = new StreamingDataReader.Result();
            yield return StreamingDataReader.Read(BuildingsFile, read);

            if (read.Success)
            {
                BuildingDataset baked = null;
                try
                {
                    baked = JsonUtility.FromJson<BuildingDataset>(read.Text);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[WeatherVR] {BuildingsFile} unusable ({e.Message}); generating buildings.");
                }

                if (baked != null && baked.IsValid)
                {
                    snapshot.Buildings = baked;
                    snapshot.BuildingsSource = $"{baked.source} (baked)";
                    yield break;
                }
            }
            else
            {
                Debug.Log($"[WeatherVR] No baked buildings ({read.Error}); generating.");
            }

            snapshot.Buildings = ProceduralBuildings.Generate(bounds, config.ProceduralSeed);
            snapshot.BuildingsSource = "procedural";
        }

        // ------------------------------------------------------------- weather

        IEnumerator LoadWeather(WeatherSnapshot snapshot, GeoBounds bounds, AppConfig config)
        {
            if (config.ForceProceduralWeather)
            {
                Debug.Log("[WeatherVR] ForceProceduralWeather is on; generating a synthetic storm.");
                snapshot.Weather = ProceduralWeather.Generate(
                    snapshot.Bounds, ProceduralGridSize, config.ProceduralSeed);
                snapshot.WeatherSource = "procedural (demo mode)";
                yield break;
            }

            if (config.AllowLiveFetch)
            {
                var live = new OpenMeteoClient.Result();
                yield return OpenMeteoClient.Fetch(
                    snapshot.Bounds, LiveGridSize, config.LiveFetchTimeoutSeconds, live);

                if (live.Success)
                {
                    snapshot.Weather = live.Dataset;
                    snapshot.WeatherSource = "Open-Meteo (live)";
                    yield break;
                }

                Debug.Log($"[WeatherVR] Live weather unavailable ({live.Error}); trying baked data.");
            }

            var read = new StreamingDataReader.Result();
            yield return StreamingDataReader.Read(WeatherFile, read);

            if (read.Success)
            {
                WeatherDataset baked = null;
                try
                {
                    baked = JsonUtility.FromJson<WeatherDataset>(read.Text);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[WeatherVR] {WeatherFile} unusable ({e.Message}).");
                }

                if (baked != null && baked.IsValid)
                {
                    if (baked.schemaVersion != WeatherDataset.CurrentSchemaVersion)
                    {
                        Debug.LogWarning(
                            $"[WeatherVR] {WeatherFile} is schema v{baked.schemaVersion}, " +
                            $"this build expects v{WeatherDataset.CurrentSchemaVersion}. Using it anyway.");
                    }
                    snapshot.Weather = baked;
                    snapshot.WeatherSource = $"{baked.source} (baked)";
                    yield break;
                }
            }
            else
            {
                Debug.Log($"[WeatherVR] No baked weather ({read.Error}); generating.");
            }

            snapshot.Weather = ProceduralWeather.Generate(
                snapshot.Bounds, ProceduralGridSize, config.ProceduralSeed);
            snapshot.WeatherSource = "procedural";
        }
    }
}
