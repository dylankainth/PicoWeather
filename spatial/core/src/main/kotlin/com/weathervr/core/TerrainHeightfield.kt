package com.weathervr.core

import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.max

/**
 * A regular grid of elevations covering [bounds].
 *
 * Stored on disk as `terrain.bin`, a compact little-endian format chosen over GeoTIFF
 * so the runtime needs no image-decoding dependency:
 *
 * ```
 *   char[4]  magic      "PWTR"
 *   int32    version    1
 *   int32    width
 *   int32    height
 *   float64  minLatitude, maxLatitude, minLongitude, maxLongitude
 *   float32  minElevation, maxElevation   (metres)
 *   uint16[] samples    width*height, row-major, south row first,
 *                       normalised so 0 -> minElevation, 65535 -> maxElevation
 * ```
 *
 * 16-bit normalised samples give ~0.02 m precision over a 1 km range, far finer than
 * the 90 m-class source data warrants, at half the size of float32.
 *
 * The format is produced by `tools/fetch_terrain.py` and is **unchanged by this port**
 * — the same baked files feed the Unity build and this one, which is what makes the
 * Python pipeline worth keeping. Note the file is explicitly little-endian while the
 * JVM's `ByteBuffer` defaults to big-endian; getting that wrong reads plausible-looking
 * garbage rather than failing, hence the explicit order below and the round-trip test.
 */
class TerrainHeightfield private constructor(
    val width: Int,
    val height: Int,
    val bounds: GeoBounds,
    /** Lowest elevation in the field, metres above sea level. */
    val minElevation: Float,
    /** Highest elevation in the field, metres above sea level. */
    val maxElevation: Float,
    /** Row-major normalised samples, south row first, held as unsigned 0..65535. */
    private val samples: ShortArray,
) {
    constructor(
        width: Int,
        height: Int,
        bounds: GeoBounds,
        minElevation: Float,
        maxElevation: Float,
    ) : this(width, height, bounds, minElevation, maxElevation, ShortArray(width * height))

    val elevationRange: Float get() = max(0.001f, maxElevation - minElevation)

    private fun sampleAt(index: Int): Int = samples[index].toInt() and 0xFFFF

    // ------------------------------------------------------------------- sampling

    /** Raw sample at grid coordinates, clamped at the edges. Metres. */
    fun elevationAt(x: Int, y: Int): Float {
        val cx = x.clampTo(0, width - 1)
        val cy = y.clampTo(0, height - 1)
        return minElevation + sampleAt(cy * width + cx) / 65535f * elevationRange
    }

    /** Normalised 0..1 sample at grid coordinates, clamped at the edges. */
    fun normalizedAt(x: Int, y: Int): Float {
        val cx = x.clampTo(0, width - 1)
        val cy = y.clampTo(0, height - 1)
        return sampleAt(cy * width + cx) / 65535f
    }

    fun setElevation(x: Int, y: Int, elevationMeters: Float) {
        val t = ((elevationMeters - minElevation) / elevationRange).clamp01()
        samples[y * width + x] = roundToIntHalfUp(t * 65535f).toShort()
    }

    /** Bilinear elevation in metres at normalised map coordinates. */
    fun sampleElevation(u: Float, v: Float): Float {
        val fx = u.clamp01() * (width - 1)
        val fy = v.clamp01() * (height - 1)

        val x0 = floorToInt(fx)
        val y0 = floorToInt(fy)
        val x1 = minOf(x0 + 1, width - 1)
        val y1 = minOf(y0 + 1, height - 1)
        val tx = fx - x0
        val ty = fy - y0

        val a = lerp(elevationAt(x0, y0), elevationAt(x1, y0), tx)
        val b = lerp(elevationAt(x0, y1), elevationAt(x1, y1), tx)
        return lerp(a, b, ty)
    }

    // ------------------------------------------------------------------------ I/O

    fun toBytes(): ByteArray {
        val buffer = ByteBuffer
            .allocate(HEADER_BYTES + samples.size * 2)
            .order(ByteOrder.LITTLE_ENDIAN)

        buffer.put(MAGIC)
        buffer.putInt(CURRENT_VERSION)
        buffer.putInt(width)
        buffer.putInt(height)
        buffer.putDouble(bounds.minLatitude)
        buffer.putDouble(bounds.maxLatitude)
        buffer.putDouble(bounds.minLongitude)
        buffer.putDouble(bounds.maxLongitude)
        buffer.putFloat(minElevation)
        buffer.putFloat(maxElevation)
        for (s in samples) buffer.putShort(s)
        return buffer.array()
    }

    override fun toString(): String =
        "TerrainHeightfield[${width}x$height, %.0f..%.0f m, $bounds]"
            .format(minElevation, maxElevation)

    companion object {
        const val CURRENT_VERSION = 1
        private val MAGIC = byteArrayOf('P'.code.toByte(), 'W'.code.toByte(), 'T'.code.toByte(), 'R'.code.toByte())

        /** 4 magic + 3 int32 + 4 float64 + 2 float32. */
        private const val HEADER_BYTES = 4 + 4 + 4 + 4 + 8 * 4 + 4 * 2

        /**
         * Parses `terrain.bin`. Throws [InvalidTerrainDataException] with a specific
         * reason rather than returning null — a corrupt bake is worth a loud failure,
         * and callers treat a *missing* file (the expected case) separately.
         */
        fun fromBytes(data: ByteArray?): TerrainHeightfield {
            // Order matters: check the magic before the length. Any file at all is more
            // likely to be the wrong file than a truncated right one, and "bad magic"
            // tells the user that, where "truncated" sends them looking for a disk error.
            if (data == null || data.size < 4) {
                throw InvalidTerrainDataException("terrain.bin is empty or truncated.")
            }

            val buffer = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN)

            val magic = ByteArray(4).also { buffer.get(it) }
            if (!magic.contentEquals(MAGIC)) {
                throw InvalidTerrainDataException(
                    "terrain.bin has bad magic '${String(magic, Charsets.US_ASCII)}', expected 'PWTR'.",
                )
            }

            if (data.size < HEADER_BYTES) {
                throw InvalidTerrainDataException(
                    "terrain.bin is truncated: header needs $HEADER_BYTES bytes, found ${data.size}.",
                )
            }

            val version = buffer.int
            if (version != CURRENT_VERSION) {
                throw InvalidTerrainDataException(
                    "terrain.bin is version $version, this build reads version $CURRENT_VERSION.",
                )
            }

            val width = buffer.int
            val height = buffer.int

            val bounds = GeoBounds(
                minLatitude = buffer.double,
                maxLatitude = buffer.double,
                minLongitude = buffer.double,
                maxLongitude = buffer.double,
            )

            val minElevation = buffer.float
            val maxElevation = buffer.float

            val expected = width.toLong() * height.toLong()
            if (width <= 1 || height <= 1 || expected > 16_777_216L) {
                throw InvalidTerrainDataException(
                    "terrain.bin declares an implausible ${width}x$height grid.",
                )
            }

            val remaining = buffer.remaining()
            if (remaining < expected * 2) {
                throw InvalidTerrainDataException(
                    "terrain.bin is truncated: need ${expected * 2} sample bytes, found $remaining.",
                )
            }

            val samples = ShortArray(expected.toInt())
            for (i in samples.indices) samples[i] = buffer.short

            return TerrainHeightfield(width, height, bounds, minElevation, maxElevation, samples)
        }
    }
}

class InvalidTerrainDataException(message: String) : RuntimeException(message)
