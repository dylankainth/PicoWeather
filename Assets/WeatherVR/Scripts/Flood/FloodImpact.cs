using UnityEngine;
using WeatherVR.Data;

namespace WeatherVR.Flood
{
    /// <summary>
    /// Storm-surge impact numbers for the carousel's readout: how much of the map's
    /// area is submerged and how many buildings are affected, at a given surge level.
    ///
    /// Kept separate from <see cref="FloodRenderer"/>, which only draws the water —
    /// this class never touches a GameObject, mesh or material, so it can be
    /// re-evaluated on every button press (a full heightfield sweep, sub-millisecond
    /// at the 512x512-class resolutions this project uses) without touching rendering
    /// state at all.
    /// </summary>
    public sealed class FloodImpact
    {
        TerrainHeightfield _terrain;
        FloodConnectivityField _flood;
        float[] _buildingConnectLevels;
        int _buildingCount;

        /// <summary>True once <see cref="Prepare"/> has a terrain to sweep.</summary>
        public bool IsReady => _terrain != null;

        /// <summary>Total buildings in the current snapshot's dataset (not just however
        /// many BuildingMeshBuilder actually built into the mesh under MaxBuildings).</summary>
        public int BuildingCount => _buildingCount;

        /// <summary>
        /// Caches the terrain heightfield, the flood connectivity field, and per
        /// building the *connection level* at its footprint centroid — not the bare
        /// terrain elevation. A building can sit on low ground that is nonetheless
        /// behind an intact defence, and counting it as "affected" the instant the
        /// water plane reaches its ground elevation would overstate risk exactly the
        /// way the old bathtub model did; the connection level already accounts for
        /// real defence crest heights along the path from the river, so this and the
        /// rendered water always agree on what actually gets wet.
        /// </summary>
        public void Prepare(WeatherSnapshot snapshot)
        {
            _terrain = snapshot?.Terrain;
            _flood = snapshot?.Flood;

            BuildingRecord[] buildings = snapshot?.Buildings?.buildings;
            GeoBounds bounds = snapshot?.Bounds ?? default;

            if (buildings == null || _terrain == null)
            {
                _buildingConnectLevels = System.Array.Empty<float>();
                _buildingCount = 0;
                return;
            }

            _buildingCount = buildings.Length;
            _buildingConnectLevels = new float[_buildingCount];

            for (int i = 0; i < _buildingCount; i++)
            {
                BuildingRecord record = buildings[i];
                if (record == null || record.PointCount == 0)
                {
                    // No footprint to locate — never counts as flooded at any level.
                    _buildingConnectLevels[i] = float.PositiveInfinity;
                    continue;
                }

                double lat = 0.0, lon = 0.0;
                int n = record.PointCount;
                for (int p = 0; p < n; p++)
                {
                    lat += record.LatitudeAt(p);
                    lon += record.LongitudeAt(p);
                }
                lat /= n;
                lon /= n;

                Vector2 uv = bounds.ToNormalized(lat, lon);
                _buildingConnectLevels[i] = _flood != null && _flood.IsValid
                    ? _flood.SampleConnectLevel(uv.x, uv.y)
                    : _terrain.SampleElevation(uv.x, uv.y);
            }
        }

        /// <summary>
        /// Fraction of the map's terrain area hydraulically connected at or below
        /// <paramref name="levelMeters"/>, and how many buildings have their footprint
        /// centroid connected below it. Falls back to plain terrain elevation (the old
        /// bathtub answer) if no connectivity field is available, so this still
        /// produces a number rather than nothing. Zeroed out if <see cref="Prepare"/>
        /// was never called or found no terrain.
        /// </summary>
        public void Evaluate(float levelMeters, out float submergedFraction, out int buildingsAffected)
        {
            submergedFraction = 0f;
            buildingsAffected = 0;

            if (_terrain == null) return;

            int width = _terrain.Width;
            int height = _terrain.Height;
            if (width <= 0 || height <= 0) return;

            bool haveFlood = _flood != null && _flood.IsValid;
            long submerged = 0;
            long total = (long)width * height;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float threshold = haveFlood ? _flood.ConnectLevelAt(x, y) : _terrain.ElevationAt(x, y);
                    if (threshold <= levelMeters)
                        submerged++;
                }
            }

            submergedFraction = total > 0 ? (float)((double)submerged / total) : 0f;

            for (int i = 0; i < _buildingConnectLevels.Length; i++)
            {
                if (_buildingConnectLevels[i] <= levelMeters)
                    buildingsAffected++;
            }
        }
    }
}
