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
        const float mapSizeMeters = 2.0f;
        const float atmosphereCeiling = 12_000f;
        var scale = new MapScale(mapSizeMeters, 5_000f, 0.4f, 1.2f, 200f);

        Check("1 VR metre is 2.5 km", Math.Abs(scale.RepresentativeFraction - 2500) < 1,
              $"1:{scale.RepresentativeFraction:N0}");

        float cloudAt2km = scale.AltitudeToVr(2000f);
        Check("2 km cloud sits within reach", cloudAt2km > 0.05f && cloudAt2km < 0.6f,
              $"{cloudAt2km * 100:F1} cm above the map");

        float columnHeight = (atmosphereCeiling - 200f) * scale.Vertical;
        Check("atmosphere column fits on the table",
              columnHeight > 0.5f && columnHeight < 3f, $"{columnHeight:F2} m tall");
        Check("column is comparable to the map width, so it reads as a volume",
              columnHeight > mapSizeMeters * 0.4f && columnHeight < mapSizeMeters * 1.5f,
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

        Console.WriteLine($"\n{(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED")}\n");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
