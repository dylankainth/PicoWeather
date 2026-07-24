using System;
using UnityEngine;

namespace WeatherVR.Data
{
    /// <summary>
    /// One building: a flat-roofed extrusion of a ground footprint.
    ///
    /// The footprint is stored flat (<c>[lat0, lon0, lat1, lon1, ...]</c>) rather than
    /// as an array of points, because <see cref="JsonUtility"/> cannot deserialise a
    /// jagged array (an array-of-arrays) at all -- the same constraint
    /// <see cref="WeatherDataset"/> is built around. A single flat array inside a
    /// serialisable class is not jagged and works fine.
    /// </summary>
    [Serializable]
    public class BuildingRecord
    {
        /// <summary>True height above its own footprint, metres. Not an altitude.</summary>
        public float heightMeters;

        /// <summary>Footprint ring, counter-clockwise, as [lat0, lon0, lat1, lon1, ...].</summary>
        public double[] footprintFlat = Array.Empty<double>();

        public int PointCount => footprintFlat.Length / 2;

        public double LatitudeAt(int i) => footprintFlat[i * 2];
        public double LongitudeAt(int i) => footprintFlat[i * 2 + 1];
    }

    /// <summary>
    /// Every building footprint covering the map region, plus provenance.
    /// Serialised with <see cref="JsonUtility"/>; see <see cref="BuildingRecord"/>
    /// for why the footprint is flattened.
    /// </summary>
    [Serializable]
    public class BuildingDataset
    {
        public const int CurrentSchemaVersion = 1;

        public int schemaVersion = CurrentSchemaVersion;

        /// <summary>"openstreetmap-overpass" or "procedural".</summary>
        public string source = "procedural";

        public string attribution = "";
        public string generatedUtc;

        public double minLatitude;
        public double maxLatitude;
        public double minLongitude;
        public double maxLongitude;

        public BuildingRecord[] buildings = Array.Empty<BuildingRecord>();

        public GeoBounds Bounds =>
            new GeoBounds(minLatitude, maxLatitude, minLongitude, maxLongitude);

        /// <summary>
        /// Valid means parseable, not populated: an empty building list for a genuinely
        /// building-free query is still a valid dataset, so <see cref="BuildingRenderer"/>
        /// can tell "no data" from "no buildings here" and only the former falls back to
        /// procedural generation.
        /// </summary>
        public bool IsValid =>
            buildings != null &&
            maxLatitude > minLatitude && maxLongitude > minLongitude;

        public string Describe() => $"{source} · {buildings.Length} building(s)";
    }
}
