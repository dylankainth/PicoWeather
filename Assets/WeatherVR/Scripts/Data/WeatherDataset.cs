using System;
using UnityEngine;

namespace WeatherVR.Data
{
    /// <summary>
    /// One grid cell of the weather model. Fields mirror the variable names used by
    /// both ERA5 and Open-Meteo so the two ingest paths produce identical JSON.
    /// </summary>
    [Serializable]
    public class WeatherCell
    {
        /// <summary>Total cloud cover, 0..1.</summary>
        public float cloudTotal;

        /// <summary>Cloud cover below ~2 km, 0..1.</summary>
        public float cloudLow;

        /// <summary>Cloud cover ~2–6 km, 0..1.</summary>
        public float cloudMid;

        /// <summary>Cloud cover above ~6 km, 0..1.</summary>
        public float cloudHigh;

        /// <summary>Surface precipitation rate, mm/hour.</summary>
        public float precipitationMmHr;

        /// <summary>Convective available potential energy, J/kg.</summary>
        public float capeJkg;

        /// <summary>Derived 0..1 lightning likelihood (see <see cref="Atmosphere.LightningPotential"/>).</summary>
        public float lightningPotential;

        /// <summary>2 m air temperature, °C.</summary>
        public float temperatureC;

        /// <summary>Eastward wind component at 10 m, m/s.</summary>
        public float windU;

        /// <summary>Northward wind component at 10 m, m/s.</summary>
        public float windV;

        public float CloudCoverFor(Atmosphere.Layer layer) => layer switch
        {
            Atmosphere.Layer.Low => cloudLow,
            Atmosphere.Layer.Mid => cloudMid,
            Atmosphere.Layer.High => cloudHigh,
            _ => 0f
        };
    }

    /// <summary>
    /// Altitude extent of one cloud layer, resolved at bake time so the runtime
    /// does not have to redo the barometric conversion.
    /// </summary>
    [Serializable]
    public class WeatherLayer
    {
        public string name;
        public float basePressureHpa;
        public float topPressureHpa;
        public float baseAltitudeM;
        public float topAltitudeM;
    }

    /// <summary>
    /// The full weather snapshot: a regular lat/lon grid of <see cref="WeatherCell"/>
    /// covering the map region, plus provenance.
    ///
    /// Serialised with <see cref="JsonUtility"/>, which means: no dictionaries, no
    /// jagged arrays, no nullable value types. Cells are stored row-major with
    /// <c>index = y * gridWidth + x</c>, x running west→east and y south→north.
    /// </summary>
    [Serializable]
    public class WeatherDataset
    {
        public const int CurrentSchemaVersion = 1;

        public int schemaVersion = CurrentSchemaVersion;

        /// <summary>ISO-8601 UTC timestamp of the observation/analysis time.</summary>
        public string observationTimeUtc;

        /// <summary>ISO-8601 UTC timestamp of when this file was baked.</summary>
        public string generatedUtc;

        /// <summary>"open-meteo", "era5", or "procedural".</summary>
        public string source = "procedural";

        /// <summary>Human-readable attribution shown in the app's about panel.</summary>
        public string attribution = "";

        public double minLatitude;
        public double maxLatitude;
        public double minLongitude;
        public double maxLongitude;

        public int gridWidth;
        public int gridHeight;

        public WeatherLayer[] layers = Array.Empty<WeatherLayer>();
        public WeatherCell[] cells = Array.Empty<WeatherCell>();

        public GeoBounds Bounds =>
            new GeoBounds(minLatitude, maxLatitude, minLongitude, maxLongitude);

        public bool IsValid =>
            gridWidth > 1 && gridHeight > 1 &&
            cells != null && cells.Length == gridWidth * gridHeight &&
            maxLatitude > minLatitude && maxLongitude > minLongitude;

        public WeatherCell CellAt(int x, int y)
        {
            x = Mathf.Clamp(x, 0, gridWidth - 1);
            y = Mathf.Clamp(y, 0, gridHeight - 1);
            return cells[y * gridWidth + x];
        }

        /// <summary>
        /// Bilinearly samples a per-cell scalar at normalised map coordinates.
        /// The grid is treated as samples at cell centres, so a 12×12 grid spans
        /// u,v ∈ [0,1] with the first sample at u = 0.
        /// </summary>
        public float SampleBilinear(float u, float v, Func<WeatherCell, float> selector)
        {
            if (!IsValid) return 0f;

            float fx = Mathf.Clamp01(u) * (gridWidth - 1);
            float fy = Mathf.Clamp01(v) * (gridHeight - 1);

            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            int x1 = Mathf.Min(x0 + 1, gridWidth - 1);
            int y1 = Mathf.Min(y0 + 1, gridHeight - 1);

            float tx = fx - x0;
            float ty = fy - y0;

            float c00 = selector(CellAt(x0, y0));
            float c10 = selector(CellAt(x1, y0));
            float c01 = selector(CellAt(x0, y1));
            float c11 = selector(CellAt(x1, y1));

            return Mathf.Lerp(Mathf.Lerp(c00, c10, tx), Mathf.Lerp(c01, c11, tx), ty);
        }

        public WeatherLayer LayerFor(Atmosphere.Layer layer)
        {
            int i = (int)layer;
            if (layers != null && i < layers.Length) return layers[i];
            return DefaultLayer(layer);
        }

        public static WeatherLayer DefaultLayer(Atmosphere.Layer layer) => new WeatherLayer
        {
            name = layer.ToString().ToLowerInvariant(),
            basePressureHpa = (float)Atmosphere.LayerBasePressureHpa(layer),
            topPressureHpa = (float)Atmosphere.LayerTopPressureHpa(layer),
            baseAltitudeM = (float)Atmosphere.LayerBaseAltitudeMeters(layer),
            topAltitudeM = (float)Atmosphere.LayerTopAltitudeMeters(layer)
        };

        public static WeatherLayer[] DefaultLayers() => new[]
        {
            DefaultLayer(Atmosphere.Layer.Low),
            DefaultLayer(Atmosphere.Layer.Mid),
            DefaultLayer(Atmosphere.Layer.High)
        };

        /// <summary>Summary line for the in-app provenance label.</summary>
        public string Describe()
        {
            string when = string.IsNullOrEmpty(observationTimeUtc) ? "unknown time" : observationTimeUtc;
            return $"{source} · {gridWidth}×{gridHeight} grid · {when}";
        }
    }
}
