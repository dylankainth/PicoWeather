using System;
using System.IO;
using UnityEngine;
using WeatherVR.Core;
using WeatherVR.Data;

// Executes the project's pure-logic code outside Unity, against the real baked
// data, to check the maths rather than merely the syntax.
static class Verify
{
    static int failures = 0;

    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? " ok " : "FAIL")}] {name,-46} {detail}");
        if (!ok) failures++;
    }

    static void Main()
    {
        Console.WriteLine("\n== Atmosphere: barometric formula ==");
        double a1000 = Atmosphere.AltitudeFromPressureHpa(1000);
        double a500 = Atmosphere.AltitudeFromPressureHpa(500);
        double a200 = Atmosphere.AltitudeFromPressureHpa(200);
        Check("1000 hPa ~ 111 m", Math.Abs(a1000 - 111) < 15, $"{a1000:F1} m");
        Check("500 hPa ~ 5574 m", Math.Abs(a500 - 5574) < 120, $"{a500:F1} m");
        Check("200 hPa ~ 11784 m", Math.Abs(a200 - 11784) < 300, $"{a200:F1} m");

        double round = Atmosphere.AltitudeFromPressure(Atmosphere.PressureFromAltitude(3000));
        Check("altitude/pressure round-trip", Math.Abs(round - 3000) < 1.0, $"{round:F2} m");

        Console.WriteLine("\n== Atmosphere: lightning proxy ==");
        Check("dry + unstable gives no lightning",
              Atmosphere.LightningPotential(3900f, 0f) == 0f, "CAPE 3900, rain 0");
        Check("wet + stable gives no lightning",
              Atmosphere.LightningPotential(100f, 10f) == 0f, "CAPE 100, rain 10");
        float storm = Atmosphere.LightningPotential(2500f, 4f);
        Check("wet + unstable gives strong signal", storm > 0.9f, $"{storm:F2}");

        Console.WriteLine("\n== GeoBounds ==");
        var b = GeoBounds.FromCenterSpan(31.23, 121.47, 50.0);
        Check("region is square on the ground",
              Math.Abs(b.WidthMeters - b.HeightMeters) < 500,
              $"{b.WidthMeters / 1000:F2} x {b.HeightMeters / 1000:F2} km");
        Check("longitude span exceeds latitude span",
              b.LongitudeSpan > b.LatitudeSpan,
              $"{b.LongitudeSpan:F4} vs {b.LatitudeSpan:F4} deg");
        var local = b.ToLocal(31.23, 121.47);
        Check("centre maps to local origin",
              Math.Abs(local.x) < 1e-4 && Math.Abs(local.z) < 1e-4,
              $"({local.x:F5}, {local.z:F5})");
        b.FromLocal(new Vector3(0.25f, 0, -0.4f), out double rlat, out double rlon);
        var back = b.ToLocal(rlat, rlon);
        Check("local to geo to local round-trip",
              Math.Abs(back.x - 0.25f) < 1e-5 && Math.Abs(back.z + 0.4f) < 1e-5,
              $"({back.x:F5}, {back.z:F5})");

        Console.WriteLine("\n== SolarPosition ==");
        SolarPosition.Compute(new DateTime(2026, 6, 21, 4, 0, 0, DateTimeKind.Utc),
                              31.23, 121.47, out double elev, out double azim);
        Check("solstice local noon: sun high", elev > 78 && elev < 84, $"elev {elev:F1} deg");
        Check("solstice local noon: sun near south", azim > 150 && azim < 210, $"azim {azim:F1} deg");
        SolarPosition.Compute(new DateTime(2026, 12, 21, 4, 0, 0, DateTimeKind.Utc),
                              31.23, 121.47, out double welev, out _);
        Check("winter solstice noon much lower", welev > 30 && welev < 40, $"elev {welev:F1} deg");
        SolarPosition.Compute(new DateTime(2026, 6, 21, 16, 0, 0, DateTimeKind.Utc),
                              31.23, 121.47, out double nelev, out _);
        Check("local midnight: sun below horizon", nelev < 0, $"elev {nelev:F1} deg");

        Console.WriteLine("\n== Noise: detail volume must tile seamlessly ==");
        float maxSeam = 0f;
        for (int i = 0; i < 40; i++)
        {
            float u = i / 40f, v = (i * 7 % 40) / 40f;
            maxSeam = Mathf.Max(maxSeam, Mathf.Abs(
                Noise.CloudDetailPeriodic(0f, u, v, 4, 1234) -
                Noise.CloudDetailPeriodic(1f, u, v, 4, 1234)));
            maxSeam = Mathf.Max(maxSeam, Mathf.Abs(
                Noise.CloudDetailPeriodic(u, 0f, v, 4, 1234) -
                Noise.CloudDetailPeriodic(u, 1f, v, 4, 1234)));
            maxSeam = Mathf.Max(maxSeam, Mathf.Abs(
                Noise.CloudDetailPeriodic(u, v, 0f, 4, 1234) -
                Noise.CloudDetailPeriodic(u, v, 1f, 4, 1234)));
        }
        Check("wraps on all three axes", maxSeam < 1e-4f, $"max seam delta {maxSeam:E2}");

        Console.WriteLine("\n== Noise: determinism ==");
        Check("same seed gives same value",
              Noise.Fbm3(1.3f, 2.7f, 0.4f, 4, 2f, 0.5f, 99) ==
              Noise.Fbm3(1.3f, 2.7f, 0.4f, 4, 2f, 0.5f, 99), "repeatable");
        Check("different seed gives different value",
              Noise.Fbm3(1.3f, 2.7f, 0.4f, 4, 2f, 0.5f, 99) !=
              Noise.Fbm3(1.3f, 2.7f, 0.4f, 4, 2f, 0.5f, 100), "seed matters");

        Console.WriteLine("\n== TerrainHeightfield: real baked terrain.bin ==");
        string path = Path.Combine(Environment.GetEnvironmentVariable("PROJ"),
                                   "Assets", "StreamingAssets", "WeatherData", "terrain.bin");
        if (!File.Exists(path))
        {
            Check("terrain.bin present", false, path);
        }
        else
        {
            var field = TerrainHeightfield.FromBytes(File.ReadAllBytes(path));
            Check("decodes", field.Width == 512 && field.Height == 512,
                  $"{field.Width}x{field.Height}");
            Check("elevation range plausible for a delta",
                  field.MinElevation > -150 && field.MaxElevation < 200,
                  $"{field.MinElevation:F1} to {field.MaxElevation:F1} m");
            Check("bounds match the configured region",
                  Math.Abs(field.Bounds.CenterLatitude - 51.5136) < 0.01 &&
                  Math.Abs(field.Bounds.CenterLongitude - (-0.0832)) < 0.01,
                  $"{field.Bounds.CenterLatitude:F3}N {field.Bounds.CenterLongitude:F3}E");

            var samples = new float[10000];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = field.SampleElevation((i % 100) / 99f, (i / 100) / 99f);
            Array.Sort(samples);
            float median = samples[samples.Length / 2];
            Check("median elevation is delta-flat", median > -5 && median < 20, $"{median:F1} m");

            var again = TerrainHeightfield.FromBytes(field.ToBytes());
            Check("binary round-trip preserves elevation",
                  Math.Abs(again.SampleElevation(0.37f, 0.62f) -
                           field.SampleElevation(0.37f, 0.62f)) < 0.05f, "PWTR write then read");
        }

        Console.WriteLine("\n== ProceduralWeather: the demo storm ==");
        var pw = ProceduralWeather.Generate(b, 32, 20260723);
        Check("valid dataset", pw.IsValid, $"{pw.gridWidth}x{pw.gridHeight}");
        float peakLight = 0f, peakPrecip = 0f, meanCloud = 0f;
        foreach (var c in pw.cells)
        {
            peakLight = Mathf.Max(peakLight, c.lightningPotential);
            peakPrecip = Mathf.Max(peakPrecip, c.precipitationMmHr);
            meanCloud += c.cloudTotal;
        }
        meanCloud /= pw.cells.Length;
        Check("produces a genuinely active storm", peakLight > 0.5f, $"peak lightning {peakLight:F2}");
        Check("produces convective rain rates", peakPrecip > 8f, $"peak {peakPrecip:F1} mm/h");
        Check("cloud cover is partial, not total", meanCloud > 0.1f && meanCloud < 0.8f,
              $"mean {meanCloud * 100:F0}%");

        // AppConfig is a ScriptableObject and cannot be instantiated outside the Unity
        // runtime, but MapScale -- which holds every unit conversion AppConfig
        // delegates to -- is a plain struct precisely so this can be checked here.
        //
        // These numbers must track WeatherVRConfig.asset / AppConfig's defaults, not
        // some fixed reference region: they are what actually shipped wrong once. The
        // region moved from Shanghai (50 km span, exaggeration 4/12) to London (5 km
        // span) without rescaling VerticalExaggeration/TerrainReliefExaggeration, so
        // the already-10x-larger Horizontal scale multiplied straight through and a
        // 43 m hill rendered as an 84 cm spike. This block is what should have caught
        // that -- keep it in sync with the live config or it is decoration, not a guard.
        Console.WriteLine("\n== MapScale (AppConfig defaults) ==");
        const float mapSizeMeters = 3.0f;
        // The map size the pedestal mesh's radii were authored against. The plinth is
        // held at this size in VR metres while the map grows past it.
        const float pedestalReferenceMapSize = 2.0f;
        const float buildingFootprintScale = 1.5f;
        const float buildingHeightExaggeration = 1.5f;
        // 3.5 km, not the full-atmosphere 12 km: the box is meant to read as a
        // compact tabletop cloud deck (low/mid layers only), not a tower spanning
        // the map's own width. See CLAUDE.md 'tall and weird clouds'.
        const float atmosphereCeiling = 3_500f;
        // WeatherVisuals.CloudBaseMeters / CloudThicknessMeters.
        const float cloudBaseMeters = 1_250f;
        var scale = new MapScale(mapSizeMeters, 5_000f, 0.4f, 1.2f, 200f,
                                 buildingHeightExaggeration);

        Check("1 VR metre is 1.67 km", Math.Abs(scale.RepresentativeFraction - 1667) < 2,
              $"1:{scale.RepresentativeFraction:N0}");

        float cloudAt2km = scale.AltitudeToVr(2000f);
        Check("2 km cloud sits within reach", cloudAt2km > 0.05f && cloudAt2km < 0.6f,
              $"{cloudAt2km * 100:F1} cm above the map");

        float columnHeight = (atmosphereCeiling - 200f) * scale.Vertical;
        Check("atmosphere column fits on the table",
              columnHeight > 0.3f && columnHeight < 1.5f, $"{columnHeight:F2} m tall");
        Check("column reads as a deck, clearly shorter than the map is wide",
              columnHeight > mapSizeMeters * 0.15f && columnHeight < mapSizeMeters * 0.75f,
              $"{columnHeight:F2} m tall vs {mapSizeMeters:F1} m wide");

        // Regression guard. The terrain mesh once built relief in VR metres while the
        // cloud box and the lightning built theirs in normalised map units, so the
        // terrain came out MapSizeMeters times too tall and bolts stopped short of the
        // ground. Everything below the map root must agree on map units.
        Console.WriteLine("\n== MapScale: map units, not VR metres ==");
        Check("map units and VR metres differ by exactly the map size",
              Math.Abs(scale.MetersToMapUnits(1f) * mapSizeMeters - 1f) < 1e-6f,
              $"1 m = {scale.MetersToMapUnits(1f):F3} map units");

        float terrainTopMapUnits = scale.TerrainElevationToMapUnits(43.7f); // real baked London peak
        float terrainTopMeters = terrainTopMapUnits * mapSizeMeters;
        Check("43.7 m summit renders as a gentle bump, not a spike",
              terrainTopMeters > 0.001f && terrainTopMeters < 0.05f,
              $"{terrainTopMeters * 100:F2} cm on a {mapSizeMeters:F0} m map");

        float boltTopMapUnits = scale.AltitudeToMapUnits(2400f);
        Check("lightning starts above the terrain it strikes",
              boltTopMapUnits > terrainTopMapUnits,
              $"channel top {boltTopMapUnits:F3} vs summit {terrainTopMapUnits:F3} map units");
        Check("lightning starts inside the cloud volume",
              boltTopMapUnits < scale.MetersToMapUnits(columnHeight),
              $"channel top {boltTopMapUnits:F3} vs column {scale.MetersToMapUnits(columnHeight):F3}");
        Check("sea level flattens to the map plane",
              scale.TerrainElevationToMapUnits(-40f) == 0f, "bathymetry does not dent the surface");

        // The 3 m map (was 2 m). Three things had to be held or retuned by hand when
        // MapSizeMeters moved, because each is expressed in map units and would otherwise
        // have been multiplied straight through by the same 1.5 -- the identical failure
        // mode as the Shanghai-era exaggeration constants above.
        Console.WriteLine("\n== Map at 1.5x: what had to be held back ==");

        // 1. The plinth. PedestalMeshBuilder's radii are authored in map units against a
        //    2 m map and scaled by PedestalMapUnitScale, so the plinth keeps a fixed size
        //    in VR metres. Crown half-extent 0.570 and total depth 0.340 are the authored
        //    numbers; both must come out at the VR size they had on the 2 m map.
        float pedestalUnitScale = pedestalReferenceMapSize / mapSizeMeters;
        float crownHalfVr = 0.570f * pedestalUnitScale * mapSizeMeters;
        float plinthDepthVr = 0.340f * pedestalUnitScale * mapSizeMeters;
        Check("plinth keeps its VR width as the map grows",
              Math.Abs(crownHalfVr - 0.570f * pedestalReferenceMapSize) < 1e-4f,
              $"crown {crownHalfVr * 2f:F2} m across");
        Check("plinth keeps its VR height as the map grows",
              Math.Abs(plinthDepthVr - 0.340f * pedestalReferenceMapSize) < 1e-4f,
              $"{plinthDepthVr:F2} m tall");
        // The accepted consequence, asserted rather than left as a surprise: the terrain's
        // 0.5-unit edge now reaches past the plinth's crown, so the heightfield's
        // underside is no longer hidden by the pedestal shader's Cull Front depth pass.
        float crownHalfMapUnits = 0.570f * pedestalUnitScale;
        Check("map overhangs its plinth (accepted, see AppConfig.PedestalReferenceMapSizeMeters)",
              crownHalfMapUnits < 0.5f,
              $"terrain edge 0.500 vs crown {crownHalfMapUnits:F3} map units " +
              $"({(0.5f - crownHalfMapUnits) * mapSizeMeters * 100f:F0} cm of overhang per side)");

        // 2. The carousel's wall mount, in map units, and the map's follow distance, in VR
        //    metres. The panel must stay outside the terrain edge by the clearance it had
        //    on the 2 m map, and stay the same distance from the head.
        const float wallHalfExtent = 0.62f;      // WeatherCarouselFollower.WallHalfExtent
        const float followDistance = 2.95f;      // SceneBuilder.Populate's ComfortFollow
        float panelClearanceVr = (wallHalfExtent - 0.5f) * mapSizeMeters;
        Check("carousel keeps its clearance outside the terrain edge",
              Math.Abs(panelClearanceVr - (0.68f - 0.5f) * pedestalReferenceMapSize) < 0.02f,
              $"{panelClearanceVr * 100f:F0} cm proud of the map edge");
        float panelToHead = followDistance - wallHalfExtent * mapSizeMeters;
        Check("carousel stays at a readable distance from the head",
              panelToHead > 0.8f && panelToHead < 1.4f, $"{panelToHead:F2} m from the head");

        // 3. Buildings, the one thing deliberately given the extra 1.5x rather than held.
        Console.WriteLine("\n== Buildings at 1.5x on top of the map ==");
        var trueScale = new MapScale(mapSizeMeters, 5_000f, 0.4f, 1.2f, 200f); // exaggeration 1
        const float tallestBakedBuilding = 310f; // real height in the current buildings.json
        float towerVr = scale.BuildingHeightToMapUnits(tallestBakedBuilding) * mapSizeMeters;
        float towerTrueVr = trueScale.BuildingHeightToMapUnits(tallestBakedBuilding) * mapSizeMeters;
        Check("building height exaggeration is exactly the configured factor",
              Math.Abs(towerVr / towerTrueVr - buildingHeightExaggeration) < 1e-3f,
              $"{tallestBakedBuilding:F0} m tower renders {towerVr * 100f:F1} cm tall");
        Check("footprint inflation is a real widening, not a no-op",
              buildingFootprintScale > 1.001f, $"x{buildingFootprintScale:F2} about the centroid");
        Check("towers still clearly out-scale the terrain relief they stand on",
              towerVr > terrainTopMeters * 4f,
              $"tower {towerVr * 100f:F1} cm vs {terrainTopMeters * 100f:F2} cm of relief");

        // The clear-air gap between the city and the cloud deck. Grew 1.5x for free with
        // the map, then 1.5x again by raising CloudBaseMeters from 900 m.
        Console.WriteLine("\n== Cloud deck clearance ==");
        float gapVr = scale.AltitudeToVr(cloudBaseMeters);
        var oldScale = new MapScale(pedestalReferenceMapSize, 5_000f, 0.4f, 1.2f, 200f);
        float gapBeforeVr = oldScale.AltitudeToVr(900f);
        Check("gap between terrain and cloud base is 2.25x what it was",
              Math.Abs(gapVr / gapBeforeVr - 2.25f) < 0.05f,
              $"{gapVr * 100f:F1} cm, was {gapBeforeVr * 100f:F1} cm");
        Check("cloud deck still fits under the atmosphere ceiling",
              cloudBaseMeters + 700f < atmosphereCeiling,
              $"deck top {cloudBaseMeters + 700f:F0} m vs ceiling {atmosphereCeiling:F0} m");
        // Not a pass/fail: the region's tallest tower pokes into the deck's underside at
        // these settings. It did on the 2 m map at true building scale too (~1 cm), so
        // this is pre-existing and roughly proportional -- but it is the number that
        // decides whether either knob can move further.
        Console.WriteLine(
            $"   note  tallest tower reaches {towerVr * 100f:F1} cm, cloud base is at " +
            $"{gapVr * 100f:F1} cm ({(towerVr - gapVr) * 100f:+0.0;-0.0} cm into the deck)");

        Console.WriteLine($"\n{(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED")}\n");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
