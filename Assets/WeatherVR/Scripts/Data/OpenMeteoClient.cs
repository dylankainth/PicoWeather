using System;
using System.Collections;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace WeatherVR.Data
{
    /// <summary>
    /// Live weather ingest from Open-Meteo (https://open-meteo.com).
    ///
    /// The specification calls for ERA5 via the Copernicus CDS. ERA5 needs a
    /// registered API key and its requests are queued asynchronously — a fetch can
    /// take minutes to hours to fulfil, which cannot happen inside an app launch.
    /// Open-Meteo is key-less, synchronous, and exposes the same physical variables,
    /// so it is the live path. The offline baker (<c>tools/fetch_weather.py</c>) can
    /// still pull genuine ERA5 for anyone with CDS credentials, and both writers
    /// emit the identical <see cref="WeatherDataset"/> schema.
    ///
    /// One request covers the whole map: Open-Meteo accepts comma-separated
    /// coordinate lists and returns an array of per-location results.
    /// </summary>
    public static class OpenMeteoClient
    {
        const string Endpoint = "https://api.open-meteo.com/v1/forecast";

        const string CurrentVariables =
            "temperature_2m,precipitation,cloud_cover,cloud_cover_low,cloud_cover_mid," +
            "cloud_cover_high,wind_speed_10m,wind_direction_10m";

        public const string Attribution =
            "Weather data by Open-Meteo.com (CC BY 4.0), ECMWF/DWD/NOAA source models.";

        /// <summary>Outcome of a fetch. Failure is expected offline and is not an error state.</summary>
        public class Result
        {
            public bool Success;
            public WeatherDataset Dataset;
            public string Error;
        }

        /// <summary>
        /// Fetches a <paramref name="gridSize"/>×<paramref name="gridSize"/> snapshot
        /// covering <paramref name="bounds"/>. Never throws.
        /// </summary>
        public static IEnumerator Fetch(GeoBounds bounds, int gridSize, float timeoutSeconds, Result result)
        {
            if (result == null) yield break;
            result.Success = false;
            result.Dataset = null;
            result.Error = null;

            gridSize = Mathf.Clamp(gridSize, 2, 12); // keep the URL and the API load sane

            string url;
            try
            {
                url = BuildUrl(bounds, gridSize);
            }
            catch (Exception e)
            {
                result.Error = "URL build failed: " + e.Message;
                yield break;
            }

            using var request = UnityWebRequest.Get(url);
            request.timeout = Mathf.Max(1, Mathf.CeilToInt(timeoutSeconds));
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                result.Error = $"Open-Meteo request failed: {request.error}";
                yield break;
            }

            try
            {
                result.Dataset = Parse(request.downloadHandler.text, bounds, gridSize);
                result.Success = result.Dataset != null && result.Dataset.IsValid;
                if (!result.Success) result.Error = "Open-Meteo response did not parse into a valid grid.";
            }
            catch (Exception e)
            {
                result.Error = "Open-Meteo parse failed: " + e.Message;
            }
        }

        // ------------------------------------------------------------ request

        static string BuildUrl(GeoBounds bounds, int gridSize)
        {
            var lats = new StringBuilder();
            var lons = new StringBuilder();

            for (int y = 0; y < gridSize; y++)
            {
                double lat = bounds.MinLatitude + y / (double)(gridSize - 1) * bounds.LatitudeSpan;
                for (int x = 0; x < gridSize; x++)
                {
                    double lon = bounds.MinLongitude + x / (double)(gridSize - 1) * bounds.LongitudeSpan;
                    if (lats.Length > 0) { lats.Append(','); lons.Append(','); }
                    lats.Append(lat.ToString("F4", CultureInfo.InvariantCulture));
                    lons.Append(lon.ToString("F4", CultureInfo.InvariantCulture));
                }
            }

            return $"{Endpoint}?latitude={lats}&longitude={lons}" +
                   $"&current={CurrentVariables}&hourly=cape&forecast_days=1&timezone=GMT";
        }

        // -------------------------------------------------------------- parse

        static WeatherDataset Parse(string json, GeoBounds bounds, int gridSize)
        {
            if (string.IsNullOrEmpty(json)) return null;

            Location[] locations;

            // A multi-coordinate request returns a bare JSON array, which JsonUtility
            // cannot deserialise at the top level; wrap it in an object first. A
            // single-coordinate request returns a plain object.
            string trimmed = json.TrimStart();
            if (trimmed.StartsWith("["))
            {
                var wrapper = JsonUtility.FromJson<LocationArray>("{\"items\":" + trimmed + "}");
                locations = wrapper?.items;
            }
            else
            {
                var single = JsonUtility.FromJson<Location>(trimmed);
                locations = single == null ? null : new[] { single };
            }

            int expected = gridSize * gridSize;
            if (locations == null || locations.Length != expected)
                throw new InvalidOperationException(
                    $"expected {expected} locations, got {locations?.Length ?? 0}");

            string observationTime = locations[0].current?.time;
            var dataset = new WeatherDataset
            {
                schemaVersion = WeatherDataset.CurrentSchemaVersion,
                source = "open-meteo",
                attribution = Attribution,
                observationTimeUtc = string.IsNullOrEmpty(observationTime)
                    ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    : observationTime + "Z",
                generatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                minLatitude = bounds.MinLatitude,
                maxLatitude = bounds.MaxLatitude,
                minLongitude = bounds.MinLongitude,
                maxLongitude = bounds.MaxLongitude,
                gridWidth = gridSize,
                gridHeight = gridSize,
                layers = WeatherDataset.DefaultLayers(),
                cells = new WeatherCell[expected]
            };

            for (int i = 0; i < expected; i++)
                dataset.cells[i] = ToCell(locations[i]);

            return dataset;
        }

        static WeatherCell ToCell(Location location)
        {
            var current = location?.current;
            if (current == null) return new WeatherCell();

            float precipitation = Mathf.Max(0f, current.precipitation);
            float cape = FirstCape(location);

            // Open-Meteo reports wind as speed (km/h) + meteorological direction, i.e.
            // the direction the wind blows *from*. Convert to the eastward/northward
            // components the rest of the app advects cloud with.
            float speedMs = Mathf.Max(0f, current.wind_speed_10m) / 3.6f;
            float fromRad = current.wind_direction_10m * Mathf.Deg2Rad;
            float windU = -speedMs * Mathf.Sin(fromRad);
            float windV = -speedMs * Mathf.Cos(fromRad);

            return new WeatherCell
            {
                cloudTotal = Mathf.Clamp01(current.cloud_cover / 100f),
                cloudLow = Mathf.Clamp01(current.cloud_cover_low / 100f),
                cloudMid = Mathf.Clamp01(current.cloud_cover_mid / 100f),
                cloudHigh = Mathf.Clamp01(current.cloud_cover_high / 100f),
                precipitationMmHr = precipitation,
                capeJkg = cape,
                lightningPotential = Atmosphere.LightningPotential(cape, precipitation),
                temperatureC = current.temperature_2m,
                windU = windU,
                windV = windV
            };
        }

        /// <summary>
        /// CAPE only comes back on the hourly series, so take the first hour — which
        /// with <c>forecast_days=1</c> and a GMT timezone is the current day's start,
        /// close enough to "now" for a demo. Missing CAPE degrades to zero, which
        /// simply means no lightning rather than a broken dataset.
        /// </summary>
        static float FirstCape(Location location)
        {
            var hourly = location.hourly;
            if (hourly?.cape == null || hourly.cape.Length == 0) return 0f;

            int index = 0;
            if (hourly.time != null && hourly.time.Length == hourly.cape.Length)
            {
                int nowHour = DateTime.UtcNow.Hour;
                index = Mathf.Clamp(nowHour, 0, hourly.cape.Length - 1);
            }
            return Mathf.Max(0f, hourly.cape[index]);
        }

        // --------------------------------------------------- response schema
        // Only the fields we consume are declared; JsonUtility ignores the rest.

        [Serializable] class LocationArray { public Location[] items; }

        [Serializable]
        class Location
        {
            public double latitude;
            public double longitude;
            public Current current;
            public Hourly hourly;
        }

        [Serializable]
        class Current
        {
            public string time;
            public float temperature_2m;
            public float precipitation;
            public float cloud_cover;
            public float cloud_cover_low;
            public float cloud_cover_mid;
            public float cloud_cover_high;
            public float wind_speed_10m;
            public float wind_direction_10m;
        }

        [Serializable]
        class Hourly
        {
            public string[] time;
            public float[] cape;
        }
    }
}
