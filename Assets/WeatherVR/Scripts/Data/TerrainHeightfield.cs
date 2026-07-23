using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace WeatherVR.Data
{
    /// <summary>
    /// A regular grid of elevations covering <see cref="Bounds"/>.
    ///
    /// Stored on disk as <c>terrain.bin</c>, a compact little-endian format chosen
    /// over GeoTIFF so the runtime needs no image-decoding dependency:
    ///
    /// <code>
    ///   char[4]  magic      "PWTR"
    ///   int32    version    1
    ///   int32    width
    ///   int32    height
    ///   float64  minLatitude, maxLatitude, minLongitude, maxLongitude
    ///   float32  minElevation, maxElevation   (metres)
    ///   uint16[] samples    width*height, row-major, south row first,
    ///                       normalised so 0 -> minElevation, 65535 -> maxElevation
    /// </code>
    ///
    /// 16-bit normalised samples give ~0.02 m precision over a 1 km range, far
    /// finer than the 90 m-class source data warrants, at half the size of float32.
    /// </summary>
    public class TerrainHeightfield
    {
        public const int CurrentVersion = 1;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("PWTR");

        public int Width { get; private set; }
        public int Height { get; private set; }
        public GeoBounds Bounds { get; private set; }

        /// <summary>Lowest elevation in the field, metres above sea level.</summary>
        public float MinElevation { get; private set; }

        /// <summary>Highest elevation in the field, metres above sea level.</summary>
        public float MaxElevation { get; private set; }

        /// <summary>Row-major normalised samples, south row first. 0..65535.</summary>
        ushort[] _samples;

        public float ElevationRange => Mathf.Max(0.001f, MaxElevation - MinElevation);

        TerrainHeightfield() { }

        public TerrainHeightfield(int width, int height, GeoBounds bounds,
                                  float minElevation, float maxElevation)
        {
            Width = width;
            Height = height;
            Bounds = bounds;
            MinElevation = minElevation;
            MaxElevation = maxElevation;
            _samples = new ushort[width * height];
        }

        // ------------------------------------------------------------- sampling

        /// <summary>Raw sample at grid coordinates, clamped at the edges. Metres.</summary>
        public float ElevationAt(int x, int y)
        {
            x = Mathf.Clamp(x, 0, Width - 1);
            y = Mathf.Clamp(y, 0, Height - 1);
            return MinElevation + _samples[y * Width + x] / 65535f * ElevationRange;
        }

        /// <summary>Normalised 0..1 sample at grid coordinates, clamped at the edges.</summary>
        public float NormalizedAt(int x, int y)
        {
            x = Mathf.Clamp(x, 0, Width - 1);
            y = Mathf.Clamp(y, 0, Height - 1);
            return _samples[y * Width + x] / 65535f;
        }

        public void SetElevation(int x, int y, float elevationMeters)
        {
            float t = Mathf.Clamp01((elevationMeters - MinElevation) / ElevationRange);
            _samples[y * Width + x] = (ushort)Mathf.RoundToInt(t * 65535f);
        }

        /// <summary>Bilinear elevation in metres at normalised map coordinates.</summary>
        public float SampleElevation(float u, float v)
        {
            float fx = Mathf.Clamp01(u) * (Width - 1);
            float fy = Mathf.Clamp01(v) * (Height - 1);

            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            int x1 = Mathf.Min(x0 + 1, Width - 1);
            int y1 = Mathf.Min(y0 + 1, Height - 1);
            float tx = fx - x0, ty = fy - y0;

            float a = Mathf.Lerp(ElevationAt(x0, y0), ElevationAt(x1, y0), tx);
            float b = Mathf.Lerp(ElevationAt(x0, y1), ElevationAt(x1, y1), tx);
            return Mathf.Lerp(a, b, ty);
        }

        // ---------------------------------------------------------------- I/O

        public static TerrainHeightfield FromBytes(byte[] data)
        {
            if (data == null || data.Length < 4)
                throw new InvalidDataException("terrain.bin is empty or truncated.");

            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            byte[] magic = reader.ReadBytes(4);
            for (int i = 0; i < 4; i++)
            {
                if (magic[i] != Magic[i])
                    throw new InvalidDataException(
                        $"terrain.bin has bad magic '{Encoding.ASCII.GetString(magic)}', expected 'PWTR'.");
            }

            int version = reader.ReadInt32();
            if (version != CurrentVersion)
                throw new InvalidDataException(
                    $"terrain.bin is version {version}, this build reads version {CurrentVersion}.");

            var field = new TerrainHeightfield
            {
                Width = reader.ReadInt32(),
                Height = reader.ReadInt32()
            };

            double minLat = reader.ReadDouble();
            double maxLat = reader.ReadDouble();
            double minLon = reader.ReadDouble();
            double maxLon = reader.ReadDouble();
            field.Bounds = new GeoBounds(minLat, maxLat, minLon, maxLon);

            field.MinElevation = reader.ReadSingle();
            field.MaxElevation = reader.ReadSingle();

            long expected = (long)field.Width * field.Height;
            if (field.Width <= 1 || field.Height <= 1 || expected > 16_777_216L)
                throw new InvalidDataException(
                    $"terrain.bin declares an implausible {field.Width}x{field.Height} grid.");

            long remaining = ms.Length - ms.Position;
            if (remaining < expected * 2)
                throw new InvalidDataException(
                    $"terrain.bin is truncated: need {expected * 2} sample bytes, found {remaining}.");

            field._samples = new ushort[expected];
            for (long i = 0; i < expected; i++)
                field._samples[i] = reader.ReadUInt16();

            return field;
        }

        public byte[] ToBytes()
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(Magic);
                writer.Write(CurrentVersion);
                writer.Write(Width);
                writer.Write(Height);
                writer.Write(Bounds.MinLatitude);
                writer.Write(Bounds.MaxLatitude);
                writer.Write(Bounds.MinLongitude);
                writer.Write(Bounds.MaxLongitude);
                writer.Write(MinElevation);
                writer.Write(MaxElevation);
                foreach (ushort s in _samples) writer.Write(s);
            }
            return ms.ToArray();
        }

        public override string ToString() =>
            $"TerrainHeightfield[{Width}x{Height}, {MinElevation:F0}..{MaxElevation:F0} m, {Bounds}]";
    }
}
