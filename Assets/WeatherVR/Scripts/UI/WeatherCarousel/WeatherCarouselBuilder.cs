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

        /// <summary>
        /// The four storm-surge preset buttons (+0/+2/+5/+10 m), hidden by default.
        /// Only shown while the selected day's timeline includes Thunderstorm — see
        /// <see cref="WeatherCarouselFeature"/>.
        /// </summary>
        public List<GameObject> FloodButtons;

        /// <summary>"STORM SURGE 风暴潮" label above the preset buttons, hidden/shown
        /// together with <see cref="FloodButtons"/> so the row is identifiable rather
        /// than four unlabeled chips.</summary>
        public GameObject FloodTitle;

        /// <summary>One-line impact readout ("+5m · 23% FLOODED · 412/1860 BUILDINGS"),
        /// hidden/shown together with <see cref="FloodButtons"/>.</summary>
        public Text FloodReadout;

        /// <summary>The time-of-day scrubber under the metrics strip.</summary>
        public WeatherCarouselTimeSlider TimeSlider;
    }

    /// <summary>
    /// The time-of-day scrubber's view: track, fill, knob and the two labels.
    ///
    /// Deliberately knows nothing about weather. <see cref="WeatherCarouselFeature"/>
    /// owns the hour, resolves it against the selected day's timeline, and hands back
    /// text to display — so the hour has exactly one owner and this stays a dumb view.
    /// </summary>
    public sealed class WeatherCarouselTimeSlider : MonoBehaviour
    {
        public RectTransform Track;
        public RectTransform Fill;
        public RectTransform Knob;
        public Text TimeLabel;
        public Text ConditionLabel;
        public Image FillImage;
        public Image KnobImage;

        /// <summary>Moves the knob/fill and re-labels the clock. <paramref name="hour"/> is 0..24.</summary>
        public void SetHour(float hour)
        {
            float value = Mathf.Clamp01(hour / 24f);
            float width = Track != null ? Track.rect.width : 0f;

            if (Fill != null)
                Fill.sizeDelta = new Vector2(width * value, Fill.sizeDelta.y);
            if (Knob != null)
                Knob.anchoredPosition = new Vector2(width * value, 0f);
            if (TimeLabel != null)
                TimeLabel.text = FormatHour(hour);
        }

        /// <summary>Names the case currently resolved for this hour, in the card's accent.</summary>
        public void SetCondition(string english, string chinese, Color accent)
        {
            if (ConditionLabel != null)
            {
                ConditionLabel.text = string.IsNullOrEmpty(chinese)
                    ? english.ToUpperInvariant()
                    : english.ToUpperInvariant() + "  " + chinese;
            }

            if (FillImage != null)
                FillImage.color = new Color(accent.r, accent.g, accent.b, 0.85f);
            if (KnobImage != null)
                KnobImage.color = new Color(accent.r, accent.g, accent.b, 1f);
        }

        public static string FormatHour(float hour)
        {
            hour = Mathf.Clamp(hour, 0f, 24f);
            int wholeHours = Mathf.Clamp(Mathf.FloorToInt(hour), 0, 23);
            int minutes = Mathf.Clamp(Mathf.RoundToInt((hour - wholeHours) * 60f), 0, 59);
            return wholeHours.ToString("00") + ":" + minutes.ToString("00");
        }
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

        static readonly (string Label, int Preset)[] FloodPresets =
        {
            ("+0m", 0), ("+2m", 1), ("+5m", 2), ("+10m", 3)
        };

        public BuiltWeatherCarousel Build(
            WeatherCarouselDataset dataset,
            Transform head,
            XRPointer pointer,
            Action<int> onFloodPresetSelected,
            Action<float> onHourNormalized = null)
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
            // Grown from 440 to fit the time scrubber. The extra height is added
            // BELOW the old content rather than around it (see CreateGlassPanel's
            // off-centre panel), so every tuned y position above still means what it
            // meant before and the wall-mount does not need retuning.
            canvasRect.sizeDelta = new Vector2(1280f, 540f);
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

            // Sits in the header's free gap between the location text and the source
            // pill, hidden until the selected day's timeline includes Thunderstorm —
            // see WeatherCarouselFeature.UpdateFloodControls. On the same canvas and
            // xrInput as the cards/arrows above, so it needs no second XR ray-hit setup.
            GameObject floodTitle = CreateFloodTitle(canvasRect);
            List<GameObject> floodButtons = CreateFloodRow(canvasRect, xrInput, onFloodPresetSelected);
            Text floodReadout = CreateFloodReadout(canvasRect);

            WeatherCarouselTimeSlider timeSlider =
                CreateTimeRow(canvasRect, xrInput, onHourNormalized);

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
                Controller = controller,
                FloodButtons = floodButtons,
                FloodTitle = floodTitle,
                FloodReadout = floodReadout,
                TimeSlider = timeSlider
            };
        }

        // The panel is centred at y = -28, not 0: it grew downward to take the time
        // scrubber, so its top edge stays at the +195 it has always been while the
        // bottom drops to -251. Centring the growth instead would have pushed every
        // element in the panel up by 50 units and left a dead band above the header.
        const float PanelCentreY = -28f;

        void CreateGlassPanel(RectTransform parent)
        {
            Image shadow = CreateImage(
                "Soft Shadow",
                parent,
                new Color(0f, 0f, 0f, 0.20f),
                WeatherCarouselSprites.Rounded);
            SetRect(shadow.rectTransform, new Vector2(1170f, 458f), new Vector2(0f, PanelCentreY - 10f));

            Image edge = CreateImage(
                "Glass Edge",
                parent,
                new Color(0.58f, 0.42f, 0.66f, 0.34f),
                WeatherCarouselSprites.Rounded);
            SetRect(edge.rectTransform, new Vector2(1164f, 452f), new Vector2(0f, PanelCentreY));

            Image surface = CreateImage(
                "Glass Surface",
                parent,
                new Color(0.075f, 0.052f, 0.105f, 0.62f),
                WeatherCarouselSprites.Rounded);
            SetRect(surface.rectTransform, new Vector2(1158f, 446f), new Vector2(0f, PanelCentreY));
            surface.gameObject.AddComponent<Mask>().showMaskGraphic = true;

            WeatherCarouselGlassGraphic gradient =
                CreateGraphic<WeatherCarouselGlassGraphic>("Glass Gradient", surface.rectTransform);
            Stretch(gradient.rectTransform);
            gradient.raycastTarget = false;
            gradient.SetColors(
                new Color(0.34f, 0.22f, 0.42f, 0.27f),
                new Color(0.045f, 0.030f, 0.070f, 0.70f));

            Image topReflection = CreateImage(
                "Top Reflection",
                surface.rectTransform,
                new Color(1f, 1f, 1f, 0.055f),
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
                new Color(0.94f, 0.97f, 0.98f));
            SetRect(location.rectTransform, new Vector2(360f, 32f), new Vector2(-385f, 168f));
            location.alignment = TextAnchor.MiddleLeft;

            Text locationChinese = CreateText(
                "Location Chinese",
                parent,
                dataset.LocationChinese,
                15,
                FontStyle.Normal,
                new Color(0.84f, 0.91f, 0.92f, 0.86f));
            SetRect(
                locationChinese.rectTransform,
                new Vector2(360f, 22f),
                new Vector2(-385f, 143f));
            locationChinese.alignment = TextAnchor.MiddleLeft;

            Image pillEdge = CreateImage(
                "Source Pill Edge",
                parent,
                new Color(0.62f, 0.43f, 0.66f, 0.30f),
                WeatherCarouselSprites.Rounded);
            SetRect(pillEdge.rectTransform, new Vector2(142f, 50f), new Vector2(470f, 157f));

            Image pill = CreateImage(
                "Source Pill",
                pillEdge.rectTransform,
                new Color(0.30f, 0.18f, 0.38f, 0.34f),
                WeatherCarouselSprites.Rounded);
            Stretch(pill.rectTransform, 2f);

            Text source = CreateText(
                "Source English",
                pill.rectTransform,
                dataset.SourceEnglish,
                10,
                FontStyle.Bold,
                new Color(0.94f, 0.88f, 0.97f));
            SetRect(source.rectTransform, new Vector2(130f, 19f), new Vector2(0f, 8f));
            source.alignment = TextAnchor.MiddleCenter;

            Text sourceChinese = CreateText(
                "Source Chinese",
                pill.rectTransform,
                dataset.SourceChinese,
                8,
                FontStyle.Normal,
                new Color(0.88f, 0.82f, 0.92f, 0.84f));
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
                new Color(item.Accent.r, item.Accent.g, item.Accent.b, 0.10f),
                WeatherCarouselSprites.Rounded);
            Stretch(ring.rectTransform);
            ring.raycastTarget = false;

            Image shell = CreateImage(
                "Glass Card",
                cardRoot,
                new Color(0.075f, 0.052f, 0.105f, 0.66f),
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
                    0.50f),
                new Color(
                    item.GlassTint.r * 0.48f,
                    item.GlassTint.g * 0.48f,
                    item.GlassTint.b * 0.48f,
                    0.72f));

            Image shine = CreateImage(
                "Glass Highlight",
                shell.rectTransform,
                new Color(1f, 1f, 1f, 0.045f),
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
                new Color(0.95f, 0.97f, 0.98f));
            SetRect(day.rectTransform, new Vector2(92f, 24f), new Vector2(-43f, 91f));
            day.alignment = TextAnchor.MiddleLeft;

            Text dayChinese = CreateText(
                "Day Chinese",
                shell.rectTransform,
                item.DayChinese,
                12,
                FontStyle.Normal,
                new Color(0.86f, 0.92f, 0.94f, 0.84f));
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
                new Color(0.96f, 0.98f, 0.98f));
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
                new Color(0.94f, 0.97f, 0.98f));
            SetRect(condition.rectTransform, new Vector2(180f, 25f), new Vector2(0f, -63f));
            condition.alignment = TextAnchor.MiddleCenter;

            Text conditionChinese = CreateText(
                "Condition Chinese",
                shell.rectTransform,
                item.ConditionChinese,
                11,
                FontStyle.Normal,
                new Color(0.85f, 0.91f, 0.93f, 0.84f));
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
                new Color(0.57f, 0.41f, 0.64f, 0.25f),
                WeatherCarouselSprites.Rounded);
            SetRect(edge.rectTransform, new Vector2(1020f, 69f), new Vector2(0f, -157f));

            Image strip = CreateImage(
                "Metrics Glass",
                edge.rectTransform,
                new Color(0.065f, 0.045f, 0.095f, 0.74f),
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
                new Color(0.78f, 0.86f, 0.88f, 0.92f));
            SetRect(label.rectTransform, new Vector2(195f, 18f), new Vector2(0f, 19f));
            label.alignment = TextAnchor.MiddleLeft;

            Text labelChinese = CreateText(
                "Chinese",
                group,
                chinese,
                9,
                FontStyle.Normal,
                new Color(0.74f, 0.83f, 0.84f, 0.80f));
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
                new Color(0.95f, 0.97f, 0.98f));
            SetRect(value.rectTransform, new Vector2(195f, 24f), new Vector2(0f, -17f));
            value.alignment = TextAnchor.MiddleLeft;
            return value;
        }

        // Bottom band of the grown panel: y in roughly [-251, -195], below the metrics
        // strip (which ends at -192). A clock block on the left, the track filling the
        // rest, hour ticks beneath it.
        WeatherCarouselTimeSlider CreateTimeRow(
            RectTransform parent,
            WeatherCarouselInput xrInput,
            Action<float> onNormalized)
        {
            var slider = parent.gameObject.AddComponent<WeatherCarouselTimeSlider>();

            Text time = CreateText(
                "Time Value",
                parent,
                "12:00",
                30,
                FontStyle.Bold,
                new Color(0.95f, 0.97f, 0.98f));
            SetRect(time.rectTransform, new Vector2(210f, 36f), new Vector2(-460f, -200f));
            time.alignment = TextAnchor.MiddleLeft;

            Text condition = CreateText(
                "Time Condition",
                parent,
                "",
                12,
                FontStyle.Bold,
                new Color(0.86f, 0.92f, 0.94f, 0.88f));
            SetRect(condition.rectTransform, new Vector2(230f, 20f), new Vector2(-450f, -230f));
            condition.alignment = TextAnchor.MiddleLeft;

            // The hit rect is 40 tall while the bar it draws is 8: a controller ray at
            // arm's length is nowhere near pixel-accurate, and this is the only control
            // on the panel that has to be *dragged* rather than just hit.
            RectTransform track = CreateRect("Time Track", parent);
            SetRect(track, new Vector2(860f, 40f), new Vector2(105f, -214f));

            Image bar = CreateImage(
                "Track Bar",
                track,
                new Color(1f, 1f, 1f, 0.14f),
                WeatherCarouselSprites.Rounded);
            bar.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            bar.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            bar.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            bar.rectTransform.offsetMin = new Vector2(0f, -4f);
            bar.rectTransform.offsetMax = new Vector2(0f, 4f);
            bar.raycastTarget = false;

            Image fill = CreateImage(
                "Track Fill",
                track,
                new Color(0.55f, 0.72f, 0.95f, 0.85f),
                WeatherCarouselSprites.Rounded);
            fill.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            fill.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            fill.rectTransform.pivot = new Vector2(0f, 0.5f);
            fill.rectTransform.sizeDelta = new Vector2(0f, 8f);
            fill.rectTransform.anchoredPosition = Vector2.zero;
            fill.raycastTarget = false;

            Image knob = CreateImage(
                "Track Knob",
                track,
                Color.white,
                WeatherCarouselSprites.Circle);
            knob.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            knob.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            knob.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            knob.rectTransform.sizeDelta = new Vector2(24f, 24f);
            knob.raycastTarget = false;

            CreateHourTicks(track);

            slider.Track = track;
            slider.Fill = fill.rectTransform;
            slider.Knob = knob.rectTransform;
            slider.FillImage = fill;
            slider.KnobImage = knob;
            slider.TimeLabel = time;
            slider.ConditionLabel = condition;

            if (onNormalized != null)
                xrInput.RegisterSlider(track, onNormalized);

            return slider;
        }

        // 00 / 06 / 12 / 18 / 24 under the bar. Anchored fractionally rather than at
        // computed pixel offsets so they stay aligned with the fill if the track is
        // ever resized.
        void CreateHourTicks(RectTransform track)
        {
            for (int hour = 0; hour <= 24; hour += 6)
            {
                float fraction = hour / 24f;

                Image tick = CreateImage(
                    "Tick " + hour,
                    track,
                    new Color(1f, 1f, 1f, 0.22f),
                    null);
                tick.raycastTarget = false;
                tick.rectTransform.anchorMin = new Vector2(fraction, 0.5f);
                tick.rectTransform.anchorMax = new Vector2(fraction, 0.5f);
                tick.rectTransform.pivot = new Vector2(0.5f, 1f);
                tick.rectTransform.sizeDelta = new Vector2(2f, 7f);
                tick.rectTransform.anchoredPosition = new Vector2(0f, -7f);

                Text label = CreateText(
                    "Tick Label " + hour,
                    track,
                    hour.ToString("00"),
                    9,
                    FontStyle.Normal,
                    new Color(0.80f, 0.87f, 0.90f, 0.62f));
                label.alignment = TextAnchor.MiddleCenter;
                label.rectTransform.anchorMin = new Vector2(fraction, 0.5f);
                label.rectTransform.anchorMax = new Vector2(fraction, 0.5f);
                label.rectTransform.pivot = new Vector2(0.5f, 1f);
                label.rectTransform.sizeDelta = new Vector2(40f, 14f);
                label.rectTransform.anchoredPosition = new Vector2(0f, -15f);
            }
        }

        // Free header gap is x in [-205, 399] (location text ends ~-205, source pill
        // starts ~399), same y row as both (157). The four 90-wide buttons, spaced 100
        // apart and centred in the gap (see CreateFloodRow), span [-98, 292] — that
        // leaves [-205, -98], 107 units, for the title on the left and [292, 399], 107
        // units, unused on the right.
        GameObject CreateFloodTitle(RectTransform parent)
        {
            RectTransform group = CreateRect("Flood Title", parent);
            SetRect(group, new Vector2(100f, 40f), new Vector2(-151f, 157f));

            Text english = CreateText(
                "Storm Surge English",
                group,
                "STORM SURGE",
                10,
                FontStyle.Bold,
                new Color(0.80f, 0.87f, 0.95f, 0.92f));
            SetRect(english.rectTransform, new Vector2(100f, 16f), new Vector2(0f, 8f));
            english.alignment = TextAnchor.MiddleCenter;

            Text chinese = CreateText(
                "Storm Surge Chinese",
                group,
                "风暴潮",
                9,
                FontStyle.Normal,
                new Color(0.74f, 0.83f, 0.90f, 0.82f));
            SetRect(chinese.rectTransform, new Vector2(100f, 14f), new Vector2(0f, -8f));
            chinese.alignment = TextAnchor.MiddleCenter;

            group.gameObject.SetActive(false);
            return group.gameObject;
        }

        // Pagination row: the dots only span x in [-105, 105] of the 210-wide group
        // centred at (0, -110), leaving the panel's right side free right up to its
        // edge (~579). One line, no Chinese — this is a number readout, not a label.
        Text CreateFloodReadout(RectTransform parent)
        {
            Text readout = CreateText(
                "Flood Readout",
                parent,
                "",
                11,
                FontStyle.Bold,
                new Color(0.82f, 0.90f, 0.96f, 0.92f));
            SetRect(readout.rectTransform, new Vector2(400f, 16f), new Vector2(320f, -110f));
            readout.alignment = TextAnchor.MiddleLeft;
            readout.gameObject.SetActive(false);
            return readout;
        }

        List<GameObject> CreateFloodRow(
            RectTransform parent,
            WeatherCarouselInput xrInput,
            Action<int> onSelect)
        {
            var buttons = new List<GameObject>();
            const float centerX = 97f;
            const float spacing = 100f;
            float startX = centerX - (FloodPresets.Length - 1) * spacing * 0.5f;

            for (int i = 0; i < FloodPresets.Length; i++)
            {
                (string label, int preset) = FloodPresets[i];
                Vector2 position = new Vector2(startX + i * spacing, 157f);
                GameObject button = CreateFloodButton(parent, xrInput, position, label,
                    () => onSelect?.Invoke(preset));
                button.SetActive(false);
                buttons.Add(button);
            }

            return buttons;
        }

        GameObject CreateFloodButton(
            RectTransform parent,
            WeatherCarouselInput xrInput,
            Vector2 position,
            string label,
            Action clicked)
        {
            Color normal = new Color(0.20f, 0.38f, 0.56f, 0.70f);
            Color hover = new Color(0.36f, 0.62f, 0.90f, 0.85f);

            Image surface = CreateImage(
                "Flood " + label,
                parent,
                normal,
                WeatherCarouselSprites.Rounded);
            SetRect(surface.rectTransform, new Vector2(90f, 40f), position);

            Text text = CreateText(
                "Label",
                surface.rectTransform,
                label,
                16,
                FontStyle.Bold,
                Color.white);
            Stretch(text.rectTransform);
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;

            xrInput.RegisterButton(surface.rectTransform, surface, clicked, normal, hover);

            return surface.gameObject;
        }

        void CreateNavigationButton(
            RectTransform parent,
            WeatherCarouselInput xrInput,
            Vector2 position,
            string glyph,
            UnityEngine.Events.UnityAction action,
            string objectName)
        {
            Color normal = new Color(0.30f, 0.20f, 0.38f, 0.46f);
            Color hover = new Color(0.56f, 0.38f, 0.64f, 0.62f);

            Image edge = CreateImage(
                objectName + " Edge",
                parent,
                new Color(0.66f, 0.47f, 0.71f, 0.42f),
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
