using UnityEngine;

namespace WeatherVR.Core
{
    /// <summary>
    /// Every conversion between real-world units and the map's local space, in one
    /// place.
    ///
    /// This is a plain struct rather than methods on <see cref="AppConfig"/> for two
    /// reasons. It can be constructed and tested outside the Unity runtime, unlike a
    /// ScriptableObject. And it makes the unit convention explicit at every call
    /// site, which matters because the failure mode here is silent: the map root
    /// carries a scale of <c>MapSizeMeters</c>, so anything under it that computes a
    /// Y coordinate in VR metres instead of in normalised map units is wrong by
    /// exactly that factor and merely looks a bit dramatic. That bug shipped once in
    /// this project — the terrain mesh built relief in metres while the cloud box and
    /// the lightning built theirs in map units, so bolts did not reach the ground.
    ///
    /// Local map space: X and Z span [-0.5, 0.5], Y uses the same units, and the map
    /// root's scale turns all three into VR metres.
    /// </summary>
    public readonly struct MapScale
    {
        /// <summary>Edge length of the map in VR metres.</summary>
        public readonly float MapSizeMeters;

        /// <summary>Edge length of the region on the ground, in real metres.</summary>
        public readonly float RegionSpanMeters;

        /// <summary>Altitude exaggeration relative to horizontal. 1 is true scale.</summary>
        public readonly float VerticalExaggeration;

        /// <summary>Extra exaggeration applied to terrain relief only.</summary>
        public readonly float TerrainReliefExaggeration;

        /// <summary>Altitude in metres that sits at the base of the rendered column.</summary>
        public readonly float AtmosphereFloorMeters;

        /// <summary>Extra exaggeration applied to building height only.</summary>
        public readonly float BuildingHeightExaggeration;

        /// <param name="buildingHeightExaggeration">
        /// Optional, defaulting to true scale, so the five-argument construction used by
        /// <c>tools/verify_logic/Verify.cs</c> and any other out-of-runtime caller keeps
        /// compiling and keeps meaning "no building exaggeration".
        /// </param>
        public MapScale(float mapSizeMeters, float regionSpanMeters, float verticalExaggeration,
                        float terrainReliefExaggeration, float atmosphereFloorMeters,
                        float buildingHeightExaggeration = 1f)
        {
            MapSizeMeters = Mathf.Max(mapSizeMeters, 1e-4f);
            RegionSpanMeters = Mathf.Max(regionSpanMeters, 1f);
            VerticalExaggeration = verticalExaggeration;
            TerrainReliefExaggeration = terrainReliefExaggeration;
            AtmosphereFloorMeters = atmosphereFloorMeters;
            BuildingHeightExaggeration = Mathf.Max(buildingHeightExaggeration, 0f);
        }

        /// <summary>VR metres per real-world metre, horizontally.</summary>
        public float Horizontal => MapSizeMeters / RegionSpanMeters;

        /// <summary>VR metres per real-world metre, vertically.</summary>
        public float Vertical => Horizontal * VerticalExaggeration;

        /// <summary>Real-world altitude in metres to VR metres above the map plane.</summary>
        public float AltitudeToVr(float altitudeMeters)
            => (altitudeMeters - AtmosphereFloorMeters) * Vertical;

        /// <summary>VR metres to normalised map units.</summary>
        public float MetersToMapUnits(float meters) => meters / MapSizeMeters;

        /// <summary>Real-world altitude in metres to map-local Y.</summary>
        public float AltitudeToMapUnits(float altitudeMeters)
            => MetersToMapUnits(AltitudeToVr(altitudeMeters));

        /// <summary>
        /// Terrain elevation in metres to map-local Y. Sea level and below flattens to
        /// zero: the sea surface is flat, and bathymetry rides in vertex colour instead.
        /// </summary>
        public float TerrainElevationToMapUnits(float elevationMeters)
            => MetersToMapUnits(Mathf.Max(elevationMeters, 0f) * Vertical * TerrainReliefExaggeration);

        /// <summary>
        /// A building's real height in metres to map-local Y, added on top of its
        /// footprint's terrain base. Deliberately uses <see cref="Horizontal"/>, not
        /// <see cref="Vertical"/>: buildings are visible at true scale in a way terrain
        /// relief and cloud altitude are not, so they do not need the atmosphere's
        /// exaggeration and would be distorted by it.
        ///
        /// <see cref="BuildingHeightExaggeration"/> is a separate, explicit multiplier on
        /// top: at 1 this is true scale (a 180 m tower on a 5 km/3 m map is 10.8 cm),
        /// above 1 the skyline is deliberately overstated. The ceiling on it is the cloud
        /// base, not taste -- exaggerate far enough and a tower punches through the deck.
        /// </summary>
        public float BuildingHeightToMapUnits(float heightMeters)
            => MetersToMapUnits(Mathf.Max(heightMeters, 0f) * Horizontal * BuildingHeightExaggeration);

        /// <summary>Denominator of the map's representative fraction, e.g. 25 000 for 1:25 000.</summary>
        public float RepresentativeFraction => 1f / Horizontal;

        public override string ToString() =>
            $"MapScale[{MapSizeMeters:F1} m map, 1:{RepresentativeFraction:N0}, " +
            $"altitude x{VerticalExaggeration:F1}, relief x{TerrainReliefExaggeration:F1}, " +
            $"buildings x{BuildingHeightExaggeration:F1}]";
    }
}
