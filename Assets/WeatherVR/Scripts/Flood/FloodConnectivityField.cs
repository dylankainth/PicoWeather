using System;
using System.IO;
using System.Text;
using UnityEngine;
using WeatherVR.Data;

namespace WeatherVR.Flood
{
    /// <summary>
    /// Per-cell minimum water level at which a cell becomes hydraulically
    /// connected to the river, baked by <c>tools/fetch_flood.py</c> as
    /// <c>flood.bin</c>. This is what upgrades the storm-surge overlay from a
    /// "bathtub" (every cell below a threshold floods) to something that
    /// respects real Thames tidal defences: a cell only floods once the water
    /// level reaches at least its connection level, which the bake computed as
    /// a minimax path from real river geometry through real defence crest
    /// heights.
    ///
    /// Binary layout, mirroring <see cref="TerrainHeightfield"/>:
    /// <code>
    ///   char[4]  magic      "PWFL"
    ///   int32    version    1
    ///   int32    width
    ///   int32    height
    ///   float64  minLatitude, maxLatitude, minLongitude, maxLongitude
    ///   float32  baseMeters, capMeters   (the quantisation window, metres AOD)
    ///   uint16[] samples    width*height, row-major, south row first,
    ///                       normalised so 0 -> baseMeters, 65535 -> "never
    ///                       floods at any level this app offers"
    /// </code>
    ///
    /// Same grid convention as the terrain heightfield it was computed from —
    /// <see cref="SampleConnectLevel"/> takes the same normalised (u, v) as
    /// <see cref="TerrainHeightfield.SampleElevation"/>.
    /// </summary>
    public class FloodConnectivityField
    {
        public const int CurrentVersion = 1;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("PWFL");

        public int Width { get; private set; }
        public int Height { get; private set; }
        public GeoBounds Bounds { get; private set; }

        /// <summary>Bottom of the quantisation window, metres AOD.</summary>
        public float BaseMeters { get; private set; }

        /// <summary>Top of the quantisation window, metres AOD. A cell whose
        /// connection level is at or above this never floods at any level the
        /// app can request.</summary>
        public float CapMeters { get; private set; }

        ushort[] _samples;

        float Range => Mathf.Max(0.001f, CapMeters - BaseMeters);

        FloodConnectivityField() { }

        /// <summary>Minimum water level (metres AOD) at which this cell connects
        /// to the river, clamped at the edges. <see cref="float.PositiveInfinity"/>
        /// if it never connects within the baked window.</summary>
        public float ConnectLevelAt(int x, int y)
        {
            x = Mathf.Clamp(x, 0, Width - 1);
            y = Mathf.Clamp(y, 0, Height - 1);
            ushort sample = _samples[y * Width + x];
            if (sample >= 65535) return float.PositiveInfinity;
            return BaseMeters + sample / 65535f * Range;
        }

        /// <summary>Bilinear connection level in metres AOD at normalised map
        /// coordinates, matching <see cref="TerrainHeightfield.SampleElevation"/>'s
        /// convention exactly so the two fields can be sampled at the same (u,v).</summary>
        public float SampleConnectLevel(float u, float v)
        {
            float fx = Mathf.Clamp01(u) * (Width - 1);
            float fy = Mathf.Clamp01(v) * (Height - 1);

            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            int x1 = Mathf.Min(x0 + 1, Width - 1);
            int y1 = Mathf.Min(y0 + 1, Height - 1);
            float tx = fx - x0, ty = fy - y0;

            // Infinity propagates through Lerp as intended: a bilinear blend
            // that touches even one "never floods" corner should read as at
            // least partially unconnected, not silently averaged down to a
            // finite number that looks like real data.
            float a = Mathf.Lerp(ConnectLevelAt(x0, y0), ConnectLevelAt(x1, y0), tx);
            float b = Mathf.Lerp(ConnectLevelAt(x0, y1), ConnectLevelAt(x1, y1), tx);
            return Mathf.Lerp(a, b, ty);
        }

        public bool IsValid => Width > 1 && Height > 1 && _samples != null &&
                               _samples.Length == Width * Height;

        /// <summary>
        /// The bathtub fallback: connection level equals the cell's own terrain
        /// elevation, so a level floods a cell the instant it reaches that ground —
        /// no river, no defences, exactly the model this project shipped before the
        /// bake existed. Used when <c>flood.bin</c> is missing or unusable, so a
        /// network-down demo degrades to the old behaviour rather than to nothing.
        /// </summary>
        public static FloodConnectivityField Procedural(TerrainHeightfield terrain)
        {
            var field = new FloodConnectivityField
            {
                Width = terrain.Width,
                Height = terrain.Height,
                Bounds = terrain.Bounds,
                BaseMeters = terrain.MinElevation,
                CapMeters = terrain.MinElevation + Mathf.Max(terrain.ElevationRange, 40f),
            };

            field._samples = new ushort[field.Width * field.Height];
            float range = field.Range;
            for (int y = 0; y < field.Height; y++)
            {
                for (int x = 0; x < field.Width; x++)
                {
                    float elevation = terrain.ElevationAt(x, y);
                    float t = Mathf.Clamp01((elevation - field.BaseMeters) / range);
                    field._samples[y * field.Width + x] = (ushort)Mathf.RoundToInt(t * 65535f);
                }
            }

            return field;
        }

        public static FloodConnectivityField FromBytes(byte[] data)
        {
            if (data == null || data.Length < 4)
                throw new InvalidDataException("flood.bin is empty or truncated.");

            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            byte[] magic = reader.ReadBytes(4);
            for (int i = 0; i < 4; i++)
            {
                if (magic[i] != Magic[i])
                    throw new InvalidDataException(
                        $"flood.bin has bad magic '{Encoding.ASCII.GetString(magic)}', expected 'PWFL'.");
            }

            int version = reader.ReadInt32();
            if (version != CurrentVersion)
                throw new InvalidDataException(
                    $"flood.bin is version {version}, this build reads version {CurrentVersion}.");

            var field = new FloodConnectivityField
            {
                Width = reader.ReadInt32(),
                Height = reader.ReadInt32()
            };

            double minLat = reader.ReadDouble();
            double maxLat = reader.ReadDouble();
            double minLon = reader.ReadDouble();
            double maxLon = reader.ReadDouble();
            field.Bounds = new GeoBounds(minLat, maxLat, minLon, maxLon);

            field.BaseMeters = reader.ReadSingle();
            field.CapMeters = reader.ReadSingle();

            long expected = (long)field.Width * field.Height;
            if (field.Width <= 1 || field.Height <= 1 || expected > 16_777_216L)
                throw new InvalidDataException(
                    $"flood.bin declares an implausible {field.Width}x{field.Height} grid.");

            long remaining = ms.Length - ms.Position;
            if (remaining < expected * 2)
                throw new InvalidDataException(
                    $"flood.bin is truncated: need {expected * 2} sample bytes, found {remaining}.");

            field._samples = new ushort[expected];
            for (long i = 0; i < expected; i++)
                field._samples[i] = reader.ReadUInt16();

            return field;
        }

        public override string ToString() =>
            $"FloodConnectivityField[{Width}x{Height}, {BaseMeters:F1}..{CapMeters:F1} m AOD, {Bounds}]";
    }
}
