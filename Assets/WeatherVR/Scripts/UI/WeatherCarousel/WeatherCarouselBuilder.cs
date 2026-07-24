using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WeatherVR.Interaction;

namespace WeatherVR.UI.Carousel
{
    public sealed class BuiltWeatherCarousel
    {
        public GameObject Root;
        public CanvasGroup Visibility;
        public WeatherCarouselController Controller;
    }

    /// <summary>
    /// Builds the complete world-space interface from code. There is no prefab or
    /// hand-authored scene object to merge, so the feature remains self-contained.
    /// </summary>
    public sealed class WeatherCarouselBuilder
    {
        const float CanvasScale = 0.00084f;
        const float CardWidth = 200f;
        const float CardHeight = 220f;
        const float CardSpacing = 14f;
        const float ViewportWidth = 920f;

        Font font;

        public BuiltWeatherCarousel Build(
            WeatherCarouselDataset dataset,
            Transform head,
            XRPointer pointer)
        {
            font = FindBilingualFont();
            EnsureDesktopEventSystem();

            var root = new GameObject("PICO Immersive Weather Carousel");
            var follower = root.AddComponent<WeatherCarouselFollower>();
            follower.Head = head;

            var canvasObject = new GameObject(
                "Bilingual Glass Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster),
                typeof(CanvasGroup));
            canvasObject.transform.SetParent(root.transform, false);

            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            canvas.sortingOrder = 50;

            RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(1280f, 440f);
            canvasRect.localScale = Vector3.one * CanvasScale;
            canvasRect.localPosition = Vector3.zero;
            canvasRect.localRotation = Quaternion.identity;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 2f;
            scaler.referencePixelsPerUnit = 100f;

            CanvasGroup visibility = canvasObject.GetComponent<CanvasGroup>();
            visibility.alpha = 0f;
            visibility.interactable = false;
            visibility.blocksRaycasts = false;

            CreateGlassPanel(canvasRect);
            CreateHeader(canvasRect, dataset);

            ScrollRect scrollRect;
            RectTransform content;
            RectTransform viewport;
            WeatherCarouselController controller;
            List<WeatherCarouselCardView> cards;
            List<RectTransform> cardRects;
            CreateDeck(
                canvasRect,
                dataset,
                out scrollRect,
                out content,
                out viewport,
                out controller,
                out cards,
                out cardRects);

            List<Image> dots = CreatePagination(canvasRect, dataset.Items.Length);

            CreateMetrics(
                canvasRect,
                out Text temperature,
                out Text rain,
                out Text humidity,
                out Text wind);

            var xrInput = root.AddComponent<WeatherCarouselInput>();
            xrInput.Configure(canvasRect, viewport, controller, pointer);

            CreateNavigationButton(
                canvasRect,
                xrInput,
                new Vector2(-538f, 20f),
                "<",
                controller.Previous,
                "Previous forecast");
            CreateNavigationButton(
                canvasRect,
                xrInput,
                new Vector2(538f, 20f),
                ">",
                controller.Next,
                "Next forecast");

            for (int i = 0; i < cardRects.Count; i++)
            {
                int capturedIndex = i;
                xrInput.RegisterCard(cardRects[i], () => controller.Select(capturedIndex));
            }

            controller.Configure(
                scrollRect,
                content,
                CardWidth + CardSpacing,
                dataset,
                cards,
                dots,
                temperature,
                rain,
                humidity,
                wind);

            return new BuiltWeatherCarousel
            {
                Root = root,
                Visibility = visibility,
                Controller = controller
            };
        }

        void CreateGlassPanel(RectTransform parent)
        {
            Image shadow = CreateImage(
                "Soft Shadow",
                parent,
                new Color(0f, 0f, 0f, 0.36f),
                WeatherCarouselSprites.Rounded);
            SetRect(shadow.rectTransform, new Vector2(1170f, 402f), new Vector2(0f, -10f));

            Image edge = CreateImage(
                "Glass Edge",
                parent,
                new Color(0.75f, 0.89f, 1f, 0.64f),
                WeatherCarouselSprites.Rounded);
            SetRect(edge.rectTransform, new Vector2(1164f, 396f), Vector2.zero);

            Image surface = CreateImage(
                "Glass Surface",
                parent,
                new Color(0.06f, 0.11f, 0.19f, 0.80f),
                WeatherCarouselSprites.Rounded);
            SetRect(surface.rectTransform, new Vector2(1158f, 390f), Vector2.zero);
            surface.gameObject.AddComponent<Mask>().showMaskGraphic = true;

            WeatherCarouselGlassGraphic gradient =
                CreateGraphic<WeatherCarouselGlassGraphic>("Glass Gradient", surface.rectTransform);
            Stretch(gradient.rectTransform);
            gradient.raycastTarget = false;
            gradient.SetColors(
                new Color(0.38f, 0.55f, 0.82f, 0.32f),
                new Color(0.03f, 0.06f, 0.13f, 0.76f));

            Image topReflection = CreateImage(
                "Top Reflection",
                surface.rectTransform,
                new Color(1f, 1f, 1f, 0.10f),
                WeatherCarouselSprites.Rounded);
            topReflection.raycastTarget = false;
            topReflection.rectTransform.anchorMin = new Vector2(0f, 0.68f);
            topReflection.rectTransform.anchorMax = Vector2.one;
            topReflection.rectTransform.offsetMin = new Vector2(8f, 0f);
            topReflection.rectTransform.offsetMax = new Vector2(-8f, -6f);
        }

        void CreateHeader(RectTransform parent, WeatherCarouselDataset dataset)
        {
            Text location = CreateText(
                "Location English",
                parent,
                dataset.LocationEnglish,
                25,
                FontStyle.Bold,
                Color.white);
            SetRect(location.rectTransform, new Vector2(360f, 32f), new Vector2(-385f, 168f));
            location.alignment = TextAnchor.MiddleLeft;

            Text locationChinese = CreateText(
                "Location Chinese",
                parent,
                dataset.LocationChinese,
                13,
                FontStyle.Normal,
                new Color(0.83f, 0.90f, 0.98f, 0.76f));
            SetRect(
                locationChinese.rectTransform,
                new Vector2(360f, 22f),
                new Vector2(-385f, 143f));
            locationChinese.alignment = TextAnchor.MiddleLeft;

            Image pillEdge = CreateImage(
                "Source Pill Edge",
                parent,
                new Color(0.68f, 0.94f, 1f, 0.40f),
                WeatherCarouselSprites.Rounded);
            SetRect(pillEdge.rectTransform, new Vector2(142f, 50f), new Vector2(470f, 157f));

            Image pill = CreateImage(
                "Source Pill",
                pillEdge.rectTransform,
                new Color(0.18f, 0.58f, 0.55f, 0.36f),
                WeatherCarouselSprites.Rounded);
            Stretch(pill.rectTransform, 2f);

            Text source = CreateText(
                "Source English",
                pill.rectTransform,
                dataset.SourceEnglish,
                10,
                FontStyle.Bold,
                new Color(0.76f, 1f, 0.92f));
            SetRect(source.rectTransform, new Vector2(130f, 19f), new Vector2(0f, 8f));
            source.alignment = TextAnchor.MiddleCenter;

            Text sourceChinese = CreateText(
                "Source Chinese",
                pill.rectTransform,
                dataset.SourceChinese,
                8,
                FontStyle.Normal,
                new Color(0.73f, 0.92f, 0.88f, 0.75f));
            SetRect(
                sourceChinese.rectTransform,
                new Vector2(130f, 17f),
                new Vector2(0f, -9f));
            sourceChinese.alignment = TextAnchor.MiddleCenter;
        }

        void CreateDeck(
            RectTransform parent,
            WeatherCarouselDataset dataset,
            out ScrollRect scrollRect,
            out RectTransform content,
            out RectTransform viewport,
            out WeatherCarouselController controller,
            out List<WeatherCarouselCardView> cards,
            out List<RectTransform> cardRects)
        {
            var scrollObject = new GameObject(
                "Horizontal Weather Deck",
                typeof(RectTransform),
                typeof(ScrollRect),
                typeof(WeatherCarouselController));
            scrollObject.transform.SetParent(parent, false);
            RectTransform scrollTransform = scrollObject.GetComponent<RectTransform>();
            SetRect(scrollTransform, new Vector2(ViewportWidth, 244f), new Vector2(0f, 20f));

            var viewportObject = new GameObject(
                "Viewport",
                typeof(RectTransform),
                typeof(Image),
                typeof(RectMask2D));
            viewportObject.transform.SetParent(scrollTransform, false);
            viewport = viewportObject.GetComponent<RectTransform>();
            Stretch(viewport);
            Image viewportHitSurface = viewportObject.GetComponent<Image>();
            viewportHitSurface.color = new Color(1f, 1f, 1f, 0.001f);

            var contentObject = new GameObject(
                "Cards",
                typeof(RectTransform),
                typeof(HorizontalLayoutGroup));
            contentObject.transform.SetParent(viewport, false);
            content = contentObject.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 0.5f);
            content.anchorMax = new Vector2(0f, 0.5f);
            content.pivot = new Vector2(0f, 0.5f);
            content.anchoredPosition = Vector2.zero;

            int sidePadding = Mathf.RoundToInt((ViewportWidth - CardWidth) * 0.5f);
            float contentWidth =
                sidePadding * 2f +
                dataset.Items.Length * CardWidth +
                (dataset.Items.Length - 1) * CardSpacing;
            content.sizeDelta = new Vector2(contentWidth, CardHeight + 8f);

            HorizontalLayoutGroup layout = contentObject.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(sidePadding, sidePadding, 4, 4);
            layout.spacing = CardSpacing;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            scrollRect = scrollObject.GetComponent<ScrollRect>();
            scrollRect.viewport = viewport;
            scrollRect.content = content;
            scrollRect.horizontal = true;
            scrollRect.vertical = false;
            scrollRect.inertia = true;
            scrollRect.decelerationRate = 0.12f;
            scrollRect.scrollSensitivity = 0f;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            controller = scrollObject.GetComponent<WeatherCarouselController>();
            cards = new List<WeatherCarouselCardView>();
            cardRects = new List<RectTransform>();

            for (int i = 0; i < dataset.Items.Length; i++)
            {
                WeatherCarouselCardView card =
                    CreateCard(content, controller, dataset.Items[i], i);
                cards.Add(card);
                cardRects.Add((RectTransform)card.transform);
            }
        }

        WeatherCarouselCardView CreateCard(
            RectTransform parent,
            WeatherCarouselController owner,
            WeatherCarouselItem item,
            int index)
        {
            RectTransform cardRoot = CreateRect("Forecast " + item.DayEnglish, parent);
            cardRoot.sizeDelta = new Vector2(CardWidth, CardHeight);
            LayoutElement layout = cardRoot.gameObject.AddComponent<LayoutElement>();
            layout.preferredWidth = CardWidth;
            layout.preferredHeight = CardHeight;
            cardRoot.gameObject.AddComponent<CanvasGroup>();

            Image ring = CreateImage(
                "Selection Glow",
                cardRoot,
                new Color(item.Accent.r, item.Accent.g, item.Accent.b, 0.18f),
                WeatherCarouselSprites.Rounded);
            Stretch(ring.rectTransform);
            ring.raycastTarget = false;

            Image shell = CreateImage(
                "Glass Card",
                cardRoot,
                new Color(0.08f, 0.14f, 0.24f, 0.84f),
                WeatherCarouselSprites.Rounded);
            Stretch(shell.rectTransform, 2f);
            shell.gameObject.AddComponent<Mask>().showMaskGraphic = true;

            WeatherCarouselGlassGraphic gradient =
                CreateGraphic<WeatherCarouselGlassGraphic>("Tint", shell.rectTransform);
            Stretch(gradient.rectTransform);
            gradient.raycastTarget = false;
            gradient.SetColors(
                new Color(
                    item.GlassTint.r + 0.12f,
                    item.GlassTint.g + 0.12f,
                    item.GlassTint.b + 0.12f,
                    0.68f),
                new Color(
                    item.GlassTint.r * 0.48f,
                    item.GlassTint.g * 0.48f,
                    item.GlassTint.b * 0.48f,
                    0.84f));

            Image shine = CreateImage(
                "Glass Highlight",
                shell.rectTransform,
                new Color(1f, 1f, 1f, 0.08f),
                null);
            shine.raycastTarget = false;
            shine.rectTransform.anchorMin = new Vector2(0f, 0.62f);
            shine.rectTransform.anchorMax = Vector2.one;
            shine.rectTransform.offsetMin = Vector2.zero;
            shine.rectTransform.offsetMax = Vector2.zero;

            Text day = CreateText(
                "Day English",
                shell.rectTransform,
                item.DayEnglish,
                15,
                FontStyle.Bold,
                Color.white);
            SetRect(day.rectTransform, new Vector2(92f, 24f), new Vector2(-43f, 91f));
            day.alignment = TextAnchor.MiddleLeft;

            Text dayChinese = CreateText(
                "Day Chinese",
                shell.rectTransform,
                item.DayChinese,
                10,
                FontStyle.Normal,
                new Color(0.90f, 0.94f, 1f, 0.70f));
            SetRect(
                dayChinese.rectTransform,
                new Vector2(92f, 18f),
                new Vector2(-43f, 74f));
            dayChinese.alignment = TextAnchor.MiddleLeft;

            Text date = CreateText(
                "Date",
                shell.rectTransform,
                item.DateEnglish,
                9,
                FontStyle.Bold,
                new Color(1f, 1f, 1f, 0.58f));
            SetRect(date.rectTransform, new Vector2(82f, 20f), new Vector2(52f, 88f));
            date.alignment = TextAnchor.MiddleRight;

            WeatherCarouselIconGraphic icon =
                CreateGraphic<WeatherCarouselIconGraphic>("Weather Icon", shell.rectTransform);
            SetRect(icon.rectTransform, new Vector2(115f, 90f), new Vector2(-36f, 17f));
            icon.raycastTarget = false;
            icon.SetIcon(item.Icon, item.Accent);

            Text temperature = CreateText(
                "Temperature",
                shell.rectTransform,
                item.TemperatureC + "°",
                41,
                FontStyle.Normal,
                Color.white);
            SetRect(
                temperature.rectTransform,
                new Vector2(105f, 58f),
                new Vector2(45f, 14f));
            temperature.alignment = TextAnchor.MiddleCenter;

            Text condition = CreateText(
                "Condition English",
                shell.rectTransform,
                item.ConditionEnglish,
                14,
                FontStyle.Bold,
                Color.white);
            SetRect(condition.rectTransform, new Vector2(180f, 25f), new Vector2(0f, -63f));
            condition.alignment = TextAnchor.MiddleCenter;

            Text conditionChinese = CreateText(
                "Condition Chinese",
                shell.rectTransform,
                item.ConditionChinese,
                10,
                FontStyle.Normal,
                new Color(0.89f, 0.94f, 1f, 0.72f));
            SetRect(
                conditionChinese.rectTransform,
                new Vector2(180f, 20f),
                new Vector2(0f, -83f));
            conditionChinese.alignment = TextAnchor.MiddleCenter;

            WeatherCarouselCardView view = cardRoot.gameObject.AddComponent<WeatherCarouselCardView>();
            view.Configure(owner, index, ring);
            return view;
        }

        List<Image> CreatePagination(RectTransform parent, int count)
        {
            RectTransform group = CreateRect("Pagination", parent);
            SetRect(group, new Vector2(210f, 16f), new Vector2(0f, -110f));

            HorizontalLayoutGroup layout = group.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var dots = new List<Image>();
            for (int i = 0; i < count; i++)
            {
                Image dot = CreateImage(
                    "Page " + (i + 1),
                    group,
                    new Color(1f, 1f, 1f, 0.30f),
                    WeatherCarouselSprites.Rounded);
                dot.rectTransform.sizeDelta = new Vector2(7f, 7f);
                dot.raycastTarget = false;
                dots.Add(dot);
            }
            return dots;
        }

        void CreateMetrics(
            RectTransform parent,
            out Text temperature,
            out Text rain,
            out Text humidity,
            out Text wind)
        {
            Image edge = CreateImage(
                "Metrics Edge",
                parent,
                new Color(0.72f, 0.88f, 1f, 0.30f),
                WeatherCarouselSprites.Rounded);
            SetRect(edge.rectTransform, new Vector2(1020f, 69f), new Vector2(0f, -157f));

            Image strip = CreateImage(
                "Metrics Glass",
                edge.rectTransform,
                new Color(0.09f, 0.15f, 0.24f, 0.76f),
                WeatherCarouselSprites.Rounded);
            Stretch(strip.rectTransform, 2f);

            temperature = CreateMetric(strip.rectTransform, -350f, "TEMPERATURE", "温度");
            rain = CreateMetric(strip.rectTransform, -115f, "RAIN", "降雨");
            humidity = CreateMetric(strip.rectTransform, 120f, "HUMIDITY", "湿度");
            wind = CreateMetric(strip.rectTransform, 355f, "WIND", "风速");
        }

        Text CreateMetric(
            RectTransform parent,
            float x,
            string english,
            string chinese)
        {
            RectTransform group = CreateRect(english, parent);
            SetRect(group, new Vector2(205f, 62f), new Vector2(x, 0f));

            Text label = CreateText(
                "English",
                group,
                english,
                10,
                FontStyle.Bold,
                new Color(0.76f, 0.84f, 0.96f, 0.88f));
            SetRect(label.rectTransform, new Vector2(195f, 18f), new Vector2(0f, 19f));
            label.alignment = TextAnchor.MiddleLeft;

            Text labelChinese = CreateText(
                "Chinese",
                group,
                chinese,
                8,
                FontStyle.Normal,
                new Color(0.70f, 0.80f, 0.91f, 0.65f));
            SetRect(
                labelChinese.rectTransform,
                new Vector2(195f, 15f),
                new Vector2(0f, 5f));
            labelChinese.alignment = TextAnchor.MiddleLeft;

            Text value = CreateText(
                "Value",
                group,
                "--",
                17,
                FontStyle.Bold,
                Color.white);
            SetRect(value.rectTransform, new Vector2(195f, 24f), new Vector2(0f, -17f));
            value.alignment = TextAnchor.MiddleLeft;
            return value;
        }

        void CreateNavigationButton(
            RectTransform parent,
            WeatherCarouselInput xrInput,
            Vector2 position,
            string glyph,
            UnityEngine.Events.UnityAction action,
            string objectName)
        {
            Color normal = new Color(0.34f, 0.48f, 0.66f, 0.46f);
            Color hover = new Color(0.58f, 0.76f, 0.96f, 0.70f);

            Image edge = CreateImage(
                objectName + " Edge",
                parent,
                new Color(0.78f, 0.92f, 1f, 0.56f),
                WeatherCarouselSprites.Circle);
            SetRect(edge.rectTransform, new Vector2(52f, 52f), position);

            Image surface = CreateImage(
                objectName,
                edge.rectTransform,
                normal,
                WeatherCarouselSprites.Circle);
            Stretch(surface.rectTransform, 2f);

            Button button = surface.gameObject.AddComponent<Button>();
            button.targetGraphic = surface;
            button.onClick.AddListener(action);

            Text arrow = CreateText(
                "Arrow",
                surface.rectTransform,
                glyph,
                24,
                FontStyle.Bold,
                Color.white);
            Stretch(arrow.rectTransform);
            arrow.alignment = TextAnchor.MiddleCenter;
            arrow.raycastTarget = false;

            xrInput.RegisterButton(
                edge.rectTransform,
                surface,
                () => action.Invoke(),
                normal,
                hover);
        }

        Font FindBilingualFont()
        {
            string[] candidates =
            {
                "Noto Sans CJK SC",
                "Noto Sans SC",
                "Microsoft YaHei UI",
                "Microsoft YaHei",
                "PingFang SC",
                "Arial Unicode MS"
            };

            foreach (string candidate in candidates)
            {
                Font candidateFont = Font.CreateDynamicFontFromOSFont(candidate, 32);
                if (candidateFont != null && candidateFont.HasCharacter('上'))
                    return candidateFont;
            }

            Debug.LogWarning(
                "[WeatherVR] No CJK system font was found. English will render, " +
                "but Chinese glyph availability depends on the platform fallback font.");
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        static void EnsureDesktopEventSystem()
        {
#if UNITY_EDITOR || UNITY_STANDALONE
            if (EventSystem.current != null)
                return;

            GameObject eventSystem = new GameObject(
                "Weather Carousel EventSystem",
                typeof(EventSystem),
                typeof(StandaloneInputModule));
            eventSystem.transform.position = Vector3.zero;
#endif
        }

        T CreateGraphic<T>(string objectName, Transform parent) where T : Graphic
        {
            var child = new GameObject(
                objectName,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(T));
            child.transform.SetParent(parent, false);
            return child.GetComponent<T>();
        }

        RectTransform CreateRect(string objectName, Transform parent)
        {
            var child = new GameObject(objectName, typeof(RectTransform));
            child.transform.SetParent(parent, false);
            return child.GetComponent<RectTransform>();
        }

        Image CreateImage(string objectName, Transform parent, Color color, Sprite sprite)
        {
            Image image = CreateGraphic<Image>(objectName, parent);
            image.color = color;
            image.sprite = sprite;
            if (sprite != null)
                image.type = Image.Type.Sliced;
            return image;
        }

        Text CreateText(
            string objectName,
            Transform parent,
            string value,
            int size,
            FontStyle style,
            Color color)
        {
            Text text = CreateGraphic<Text>(objectName, parent);
            text.font = font;
            text.text = value;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        static void SetRect(RectTransform rect, Vector2 size, Vector2 position)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        static void Stretch(RectTransform rect, float inset = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }
    }
}
