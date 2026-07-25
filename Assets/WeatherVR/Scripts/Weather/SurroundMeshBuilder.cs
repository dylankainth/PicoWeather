using UnityEngine;

namespace WeatherVR.Weather
{
    /// <summary>
    /// Builds the geometry for the world-locked glass floor <see cref="EnvironmentController"/>
    /// owns. A single unit quad in the XZ plane, Y = 0 -- all the actual size,
    /// grid spacing and fade radius are shader properties on
    /// <c>WeatherVR/GlassSurround</c>, not baked into the mesh, so tuning the floor's
    /// footprint at runtime never needs a new mesh.
    /// </summary>
    public static class SurroundMeshBuilder
    {
        public static Mesh BuildFloorQuad()
        {
            var mesh = new Mesh { name = "GlassSurroundQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-1f, 0f, -1f),
                new Vector3(-1f, 0f,  1f),
                new Vector3( 1f, 0f,  1f),
                new Vector3( 1f, 0f, -1f),
            };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
