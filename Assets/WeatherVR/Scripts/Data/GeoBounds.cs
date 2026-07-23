using System;
using UnityEngine;

namespace WeatherVR.Data
{
    /// <summary>
    /// An axis-aligned latitude/longitude rectangle, plus the conversions between
    /// geographic coordinates and the map's local space.
    ///
    /// Local map space is normalised to [-0.5, 0.5] on X (east) and Z (north), with
    /// the map plane at Y = 0. Multiplying by <see cref="Core.AppConfig.MapSizeMeters"/>
    /// gives VR metres, so the same local coordinates work regardless of how big the
    /// user has scaled the table map.
    /// </summary>
    [Serializable]
    public struct GeoBounds
    {
        public double MinLatitude;
        public double MaxLatitude;
        public double MinLongitude;
        public double MaxLongitude;

        public double CenterLatitude => (MinLatitude + MaxLatitude) * 0.5;
        public double CenterLongitude => (MinLongitude + MaxLongitude) * 0.5;
        public double LatitudeSpan => MaxLatitude - MinLatitude;
        public double LongitudeSpan => MaxLongitude - MinLongitude;

        public GeoBounds(double minLat, double maxLat, double minLon, double maxLon)
        {
            MinLatitude = minLat;
            MaxLatitude = maxLat;
            MinLongitude = minLon;
            MaxLongitude = maxLon;
        }

        const double MetersPerDegreeLatitude = 111_320.0;

        /// <summary>
        /// Builds a square-on-the-ground region of <paramref name="spanKm"/> edge
        /// length around a centre point. Longitude degrees shrink with the cosine of
        /// latitude, so the longitude span is widened to keep the footprint square in
        /// metres rather than in degrees.
        /// </summary>
        public static GeoBounds FromCenterSpan(double centerLat, double centerLon, double spanKm)
        {
            double halfMeters = spanKm * 1000.0 * 0.5;
            double halfLat = halfMeters / MetersPerDegreeLatitude;

            double metersPerDegreeLon =
                MetersPerDegreeLatitude * Math.Cos(centerLat * Math.PI / 180.0);
            // Guard against the poles, where a degree of longitude collapses to zero.
            if (Math.Abs(metersPerDegreeLon) < 1.0) metersPerDegreeLon = 1.0;
            double halfLon = halfMeters / metersPerDegreeLon;

            return new GeoBounds(
                centerLat - halfLat, centerLat + halfLat,
                centerLon - halfLon, centerLon + halfLon);
        }

        /// <summary>Real-world width of the region in metres (east-west, at the centre latitude).</summary>
        public double WidthMeters =>
            LongitudeSpan * MetersPerDegreeLatitude * Math.Cos(CenterLatitude * Math.PI / 180.0);

        /// <summary>Real-world height of the region in metres (north-south).</summary>
        public double HeightMeters => LatitudeSpan * MetersPerDegreeLatitude;

        /// <summary>
        /// Geographic point to normalised map coordinates in [0,1]: u runs west→east,
        /// v runs south→north. Values outside [0,1] mean the point is off the map.
        /// </summary>
        public Vector2 ToNormalized(double latitude, double longitude)
        {
            float u = (float)((longitude - MinLongitude) / LongitudeSpan);
            float v = (float)((latitude - MinLatitude) / LatitudeSpan);
            return new Vector2(u, v);
        }

        /// <summary>Geographic point to local map space, X/Z in [-0.5, 0.5].</summary>
        public Vector3 ToLocal(double latitude, double longitude, float localY = 0f)
        {
            Vector2 n = ToNormalized(latitude, longitude);
            return new Vector3(n.x - 0.5f, localY, n.y - 0.5f);
        }

        /// <summary>Normalised map coordinates back to a geographic point.</summary>
        public void FromNormalized(float u, float v, out double latitude, out double longitude)
        {
            longitude = MinLongitude + u * LongitudeSpan;
            latitude = MinLatitude + v * LatitudeSpan;
        }

        /// <summary>Local map space back to a geographic point.</summary>
        public void FromLocal(Vector3 local, out double latitude, out double longitude)
            => FromNormalized(local.x + 0.5f, local.z + 0.5f, out latitude, out longitude);

        public bool Contains(double latitude, double longitude) =>
            latitude >= MinLatitude && latitude <= MaxLatitude &&
            longitude >= MinLongitude && longitude <= MaxLongitude;

        public override string ToString() =>
            $"GeoBounds[{MinLatitude:F4}..{MaxLatitude:F4} N, {MinLongitude:F4}..{MaxLongitude:F4} E]";
    }
}
