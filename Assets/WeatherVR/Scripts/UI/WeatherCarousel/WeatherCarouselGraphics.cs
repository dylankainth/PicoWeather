using UnityEngine;
using UnityEngine.UI;

namespace WeatherVR.UI.Carousel
{
    /// <summary>
    /// Cheap glass-like gradient for mobile XR. It deliberately avoids framebuffer
    /// blur, which is expensive on a standalone headset.
    /// </summary>
    public sealed class WeatherCarouselGlassGraphic : MaskableGraphic
    {
        [SerializeField] Color top = new Color(0.22f, 0.38f, 0.46f, 0.34f);
        [SerializeField] Color bottom = new Color(0.06f, 0.11f, 0.15f, 0.66f);

        public void SetColors(Color topColor, Color bottomColor)
        {
            top = topColor;
            bottom = bottomColor;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect rect = rectTransform.rect;
            UIVertex vertex = UIVertex.simpleVert;

            vertex.color = bottom;
            vertex.position = new Vector3(rect.xMin, rect.yMin);
            vh.AddVert(vertex);
            vertex.position = new Vector3(rect.xMax, rect.yMin);
            vh.AddVert(vertex);

            vertex.color = top;
            vertex.position = new Vector3(rect.xMax, rect.yMax);
            vh.AddVert(vertex);
            vertex.position = new Vector3(rect.xMin, rect.yMax);
            vh.AddVert(vertex);

            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }
    }

    /// <summary>
    /// Procedural weather artwork keeps the whole feature in one folder and avoids
    /// texture imports, atlases and additional draw-call surprises.
    /// </summary>
    public sealed class WeatherCarouselIconGraphic : MaskableGraphic
    {
        WeatherCarouselIcon icon;
        Color accent = new Color(1f, 0.82f, 0.38f);

        public void SetIcon(WeatherCarouselIcon value, Color color)
        {
            icon = value;
            accent = color;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            switch (icon)
            {
                case WeatherCarouselIcon.Sunny:
                    AddSun(vh, Vector2.zero, 32f, accent);
                    break;
                case WeatherCarouselIcon.PartlyCloudy:
                    AddSun(vh, new Vector2(20f, 16f), 22f, accent);
                    AddCloud(vh, new Vector2(-8f, -9f), new Color(0.94f, 0.97f, 1f), 0.66f);
                    break;
                case WeatherCarouselIcon.Cloudy:
                    AddCloud(vh, new Vector2(0f, -2f), new Color(0.88f, 0.94f, 1f), 0.76f);
                    break;
                case WeatherCarouselIcon.Rain:
                    AddCloud(vh, new Vector2(0f, 10f), new Color(0.91f, 0.96f, 1f), 0.70f);
                    AddRain(vh, accent);
                    break;
                case WeatherCarouselIcon.Storm:
                    AddCloud(vh, new Vector2(0f, 12f), new Color(0.78f, 0.82f, 0.94f), 0.72f);
                    AddBolt(vh, accent);
                    break;
            }
        }

        static void AddSun(VertexHelper vh, Vector2 centre, float radius, Color32 color)
        {
            AddCircle(vh, centre, radius, color, 30);
            for (int i = 0; i < 8; i++)
            {
                float angle = i * Mathf.PI * 0.25f;
                Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                AddLine(
                    vh,
                    centre + direction * (radius + 7f),
                    centre + direction * (radius + 17f),
                    4f,
                    color);
            }
        }

        static void AddCloud(VertexHelper vh, Vector2 centre, Color32 color, float scale)
        {
            AddCircle(vh, centre + new Vector2(-37f, -5f) * scale, 29f * scale, color, 20);
            AddCircle(vh, centre + new Vector2(0f, 14f) * scale, 40f * scale, color, 24);
            AddCircle(vh, centre + new Vector2(39f, -3f) * scale, 31f * scale, color, 20);
            AddQuad(
                vh,
                centre + new Vector2(-57f, -28f) * scale,
                centre + new Vector2(61f, 5f) * scale,
                color);
        }

        static void AddRain(VertexHelper vh, Color32 color)
        {
            for (int i = -1; i <= 1; i++)
            {
                Vector2 start = new Vector2(i * 28f + 5f, -19f);
                AddLine(vh, start, start + new Vector2(-7f, -23f), 5f, color);
            }
        }

        static void AddBolt(VertexHelper vh, Color32 color)
        {
            AddPolygon(vh, new[]
            {
                new Vector2(8f, -4f),
                new Vector2(-11f, -34f),
                new Vector2(2f, -34f),
                new Vector2(-6f, -60f),
                new Vector2(27f, -24f),
                new Vector2(12f, -24f)
            }, color);
        }

        static void AddCircle(
            VertexHelper vh,
            Vector2 centre,
            float radius,
            Color32 color,
            int segments)
        {
            int start = vh.currentVertCount;
            UIVertex vertex = UIVertex.simpleVert;
            vertex.color = color;
            vertex.position = centre;
            vh.AddVert(vertex);

            for (int i = 0; i <= segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                vertex.position = centre +
                    new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                vh.AddVert(vertex);
            }

            for (int i = 0; i < segments; i++)
                vh.AddTriangle(start, start + i + 1, start + i + 2);
        }

        static void AddLine(
            VertexHelper vh,
            Vector2 from,
            Vector2 to,
            float width,
            Color32 color)
        {
            Vector2 normal = new Vector2(-(to - from).y, (to - from).x).normalized *
                             width * 0.5f;
            AddPolygon(vh, new[]
            {
                from - normal,
                from + normal,
                to + normal,
                to - normal
            }, color);
        }

        static void AddQuad(VertexHelper vh, Vector2 min, Vector2 max, Color32 color)
        {
            AddPolygon(vh, new[]
            {
                new Vector2(min.x, min.y),
                new Vector2(min.x, max.y),
                new Vector2(max.x, max.y),
                new Vector2(max.x, min.y)
            }, color);
        }

        static void AddPolygon(VertexHelper vh, Vector2[] points, Color32 color)
        {
            int start = vh.currentVertCount;
            UIVertex vertex = UIVertex.simpleVert;
            vertex.color = color;
            foreach (Vector2 point in points)
            {
                vertex.position = point;
                vh.AddVert(vertex);
            }

            for (int i = 1; i < points.Length - 1; i++)
                vh.AddTriangle(start, start + i, start + i + 1);
        }
    }

    public static class WeatherCarouselSprites
    {
        static Sprite rounded;
        static Sprite circle;

        public static Sprite Rounded =>
            rounded != null ? rounded : (rounded = MakeRounded(64, 18));

        public static Sprite Circle =>
            circle != null ? circle : (circle = MakeRounded(64, 32));

        static Sprite MakeRounded(int size, int radius)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Weather Carousel Rounded Sprite",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[size * size];
            float innerMin = radius - 0.5f;
            float innerMax = size - radius - 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float closestX = Mathf.Clamp(x, innerMin, innerMax);
                    float closestY = Mathf.Clamp(y, innerMin, innerMax);
                    float distance = Vector2.Distance(
                        new Vector2(x, y),
                        new Vector2(closestX, closestY));
                    byte alpha = (byte)(Mathf.Clamp01(radius + 0.5f - distance) * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            return Sprite.Create(
                texture,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(radius, radius, radius, radius));
        }
    }
}
