package com.weathervr.core

import kotlin.math.sqrt

/**
 * A mesh as plain buffers, with no engine type anywhere in it.
 *
 * This is the shape of the port's answer to "we cannot write the renderer yet": mesh
 * *generation* is arithmetic and belongs here, where it can be tested on a desktop JVM;
 * mesh *upload* is three lines against whatever API the Spatial SDK turns out to expose.
 * Splitting them means the geometry is finished and proven before the SDK arrives.
 *
 * Layout matches what every graphics API wants:
 * - [positions] 3 floats per vertex (x, y, z) in map-local units
 * - [uvs] 2 floats per vertex
 * - [colors] 4 floats per vertex (r, g, b, a), 0..1
 * - [normals] 3 floats per vertex, filled by [recalculateNormals]
 * - [indices] 3 per triangle
 *
 * Winding follows the Unity source: a front-facing triangle (v0, v1, v2) has normal
 * `cross(v1 − v0, v2 − v0)`. Keeping the same convention means the ported builders can
 * be compared against the originals triangle for triangle, and it is the convention the
 * shaders were written against.
 */
class MeshData(val vertexCount: Int, triangleCount: Int) {
    val positions = FloatArray(vertexCount * 3)
    val uvs = FloatArray(vertexCount * 2)
    val colors = FloatArray(vertexCount * 4)
    val normals = FloatArray(vertexCount * 3)
    val indices = IntArray(triangleCount * 3)

    val triangleCount: Int get() = indices.size / 3

    /**
     * True when the vertex count exceeds what a 16-bit index buffer can address.
     *
     * Worth surfacing rather than handling silently: the Unity builder picks an index
     * format from exactly this test, and a mesh that quietly overflows 16-bit indices
     * renders as garbage rather than failing.
     */
    val needs32BitIndices: Boolean get() = vertexCount > 65_000

    fun setPosition(i: Int, x: Float, y: Float, z: Float) {
        positions[i * 3] = x
        positions[i * 3 + 1] = y
        positions[i * 3 + 2] = z
    }

    fun getPosition(i: Int): MapPoint =
        MapPoint(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2])

    fun setUv(i: Int, u: Float, v: Float) {
        uvs[i * 2] = u
        uvs[i * 2 + 1] = v
    }

    fun setColor(i: Int, r: Float, g: Float, b: Float, a: Float) {
        colors[i * 4] = r
        colors[i * 4 + 1] = g
        colors[i * 4 + 2] = b
        colors[i * 4 + 3] = a
    }

    fun setTriangle(t: Int, a: Int, b: Int, c: Int) {
        indices[t * 3] = a
        indices[t * 3 + 1] = b
        indices[t * 3 + 2] = c
    }

    /**
     * The equivalent of Unity's `Mesh.RecalculateNormals`: accumulate each triangle's
     * un-normalised cross product onto its three vertices, then normalise. Leaving the
     * cross product un-normalised is what makes the result area-weighted, so large
     * triangles dominate a shared vertex's normal — matching Unity, and giving smoother
     * shading on an irregular grid than a plain average would.
     */
    fun recalculateNormals() {
        normals.fill(0f)

        var t = 0
        while (t < indices.size) {
            val i0 = indices[t]
            val i1 = indices[t + 1]
            val i2 = indices[t + 2]

            val ax = positions[i1 * 3] - positions[i0 * 3]
            val ay = positions[i1 * 3 + 1] - positions[i0 * 3 + 1]
            val az = positions[i1 * 3 + 2] - positions[i0 * 3 + 2]

            val bx = positions[i2 * 3] - positions[i0 * 3]
            val by = positions[i2 * 3 + 1] - positions[i0 * 3 + 1]
            val bz = positions[i2 * 3 + 2] - positions[i0 * 3 + 2]

            val nx = ay * bz - az * by
            val ny = az * bx - ax * bz
            val nz = ax * by - ay * bx

            for (i in intArrayOf(i0, i1, i2)) {
                normals[i * 3] += nx
                normals[i * 3 + 1] += ny
                normals[i * 3 + 2] += nz
            }

            t += 3
        }

        for (i in 0 until vertexCount) {
            val x = normals[i * 3]
            val y = normals[i * 3 + 1]
            val z = normals[i * 3 + 2]
            val length = sqrt(x * x + y * y + z * z)
            if (length > 1e-12f) {
                normals[i * 3] = x / length
                normals[i * 3 + 1] = y / length
                normals[i * 3 + 2] = z / length
            } else {
                // A degenerate or cancelled normal points up rather than staying zero;
                // a zero normal renders black under any lighting model.
                normals[i * 3] = 0f
                normals[i * 3 + 1] = 1f
                normals[i * 3 + 2] = 0f
            }
        }
    }

    fun getNormal(i: Int): MapPoint =
        MapPoint(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2])

    override fun toString(): String = "MeshData[$vertexCount verts, $triangleCount tris]"
}

/**
 * A growable [MeshData], for builders that cannot count their vertices up front.
 * Compacts to a fixed [MeshData] with [build].
 */
class MeshBuilder {
    private val positions = ArrayList<Float>()
    private val uvs = ArrayList<Float>()
    private val colors = ArrayList<Float>()
    private val indices = ArrayList<Int>()

    val vertexCount: Int get() = positions.size / 3

    fun addVertex(
        x: Float,
        y: Float,
        z: Float,
        u: Float,
        v: Float,
        r: Float,
        g: Float,
        b: Float,
        a: Float = 1f,
    ): Int {
        val index = vertexCount
        positions.add(x); positions.add(y); positions.add(z)
        uvs.add(u); uvs.add(v)
        colors.add(r); colors.add(g); colors.add(b); colors.add(a)
        return index
    }

    fun addTriangle(a: Int, b: Int, c: Int) {
        indices.add(a); indices.add(b); indices.add(c)
    }

    fun build(): MeshData {
        val mesh = MeshData(vertexCount, indices.size / 3)
        for (i in positions.indices) mesh.positions[i] = positions[i]
        for (i in uvs.indices) mesh.uvs[i] = uvs[i]
        for (i in colors.indices) mesh.colors[i] = colors[i]
        for (i in indices.indices) mesh.indices[i] = indices[i]
        mesh.recalculateNormals()
        return mesh
    }
}
