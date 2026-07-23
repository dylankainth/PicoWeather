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
                  Math.Abs(field.Bounds.CenterLatitude - 31.23) < 0.01 &&
                  Math.Abs(field.Bounds.CenterLongitude - 121.47) < 0.01,
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

        // AppConfig is a ScriptableObject and cannot be instantiated outside the
        // Unity runtime (CreateInstance is a native ECall), so the scale chain is
        // recomputed here from the same defaults. Keep these in step with AppConfig.
        Console.WriteLine("\n== Scale chain (AppConfig defaults, recomputed) ==");
        const float mapSizeMeters = 2.0f;
        const float regionSpanMeters = 50_000f;
        const float verticalExaggeration = 4.0f;
        const float atmosphereFloor = 200f;
        const float atmosphereCeiling = 12_000f;

        float horizontalScale = mapSizeMeters / regionSpanMeters;
        float verticalScale = horizontalScale * verticalExaggeration;
        float cloudAt2km = (2000f - atmosphereFloor) * verticalScale;
        float columnHeight = (atmosphereCeiling - atmosphereFloor) * verticalScale;

        Check("1 VR metre is 25 km", Math.Abs(1f / horizontalScale - 25000) < 1,
              $"1:{1f / horizontalScale:N0}");
        Check("2 km cloud sits within reach", cloudAt2km > 0.05f && cloudAt2km < 0.6f,
              $"{cloudAt2km * 100:F1} cm above the map");
        Check("atmosphere column fits on the table",
              columnHeight > 0.5f && columnHeight < 3f, $"{columnHeight:F2} m tall");
        Check("column is comparable to the map width, so it reads as a volume",
              columnHeight > mapSizeMeters * 0.4f && columnHeight < mapSizeMeters * 1.5f,
              $"{columnHeight:F2} m tall vs {mapSizeMeters:F1} m wide");

        Console.WriteLine($"\n{(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED")}\n");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
