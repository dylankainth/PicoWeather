using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WeatherVR.UI.Carousel
{
    public sealed class WeatherCarouselController : MonoBehaviour,
        IBeginDragHandler,
        IEndDragHandler,
        IScrollHandler
    {
        const float VelocityThreshold = 360f;

        readonly List<WeatherCarouselCardView> cards = new List<WeatherCarouselCardView>();
        readonly List<Image> dots = new List<Image>();

        ScrollRect scrollRect;
        RectTransform content;
        WeatherCarouselDataset dataset;
        Text temperatureValue;
        Text rainValue;
        Text humidityValue;
        Text windValue;
        float stride;
        float targetX;
        float smoothVelocity;
        bool dragging;
        int selectedIndex;

        static readonly Color IdleDot = new Color(1f, 1f, 1f, 0.30f);

        public int SelectedIndex => selectedIndex;

        public void Configure(
            ScrollRect targetScrollRect,
            RectTransform targetContent,
            float cardStride,
            WeatherCarouselDataset weather,
            IList<WeatherCarouselCardView> cardViews,
            IList<Image> paginationDots,
            Text temperature,
            Text rain,
            Text humidity,
            Text wind)
        {
            scrollRect = targetScrollRect;
            content = targetContent;
            stride = cardStride;
            dataset = weather;
            cards.AddRange(cardViews);
            dots.AddRange(paginationDots);
            temperatureValue = temperature;
            rainValue = rain;
            humidityValue = humidity;
            windValue = wind;
            Select(0, true);
        }

        public void Previous() => Select(selectedIndex - 1);

        public void Next() => Select(selectedIndex + 1);

        public void Select(int index) => Select(index, false);

        public void BeginXRDrag()
        {
            dragging = true;
            smoothVelocity = 0f;
            scrollRect?.StopMovement();
        }

        public void DragXR(float deltaCanvasX)
        {
            if (!dragging || content == null)
                return;

            Vector2 position = content.anchoredPosition;
            float minimum = -(dataset.Items.Length - 1) * stride - stride * 0.18f;
            float maximum = stride * 0.18f;
            position.x = Mathf.Clamp(position.x + deltaCanvasX, minimum, maximum);
            content.anchoredPosition = position;
        }

        public void EndXRDrag()
        {
            if (!dragging || content == null)
                return;

            dragging = false;
            Select(Mathf.RoundToInt(-content.anchoredPosition.x / stride));
        }

        void Select(int index, bool immediate)
        {
            if (dataset?.Items == null || dataset.Items.Length == 0)
                return;

            selectedIndex = Mathf.Clamp(index, 0, dataset.Items.Length - 1);
            targetX = -selectedIndex * stride;
            WeatherCarouselItem item = dataset.Items[selectedIndex];

            for (int i = 0; i < cards.Count; i++)
                cards[i].SetSelected(i == selectedIndex);

            for (int i = 0; i < dots.Count; i++)
            {
                bool active = i == selectedIndex;
                dots[i].color = active ? item.Accent : IdleDot;
                dots[i].rectTransform.sizeDelta =
                    active ? new Vector2(24f, 7f) : new Vector2(7f, 7f);
            }

            temperatureValue.text = item.TemperatureC + "°C";
            rainValue.text = item.RainChance + "%";
            humidityValue.text = item.Humidity + "%";
            windValue.text = item.WindKmh + " km/h";

            if (immediate)
            {
                Vector2 position = content.anchoredPosition;
                position.x = targetX;
                content.anchoredPosition = position;
            }
        }

        void Update()
        {
            if (content == null)
                return;

            if (!dragging)
            {
                Vector2 position = content.anchoredPosition;
                position.x = Mathf.SmoothDamp(
                    position.x,
                    targetX,
                    ref smoothVelocity,
                    0.14f,
                    Mathf.Infinity,
                    Time.unscaledDeltaTime);
                content.anchoredPosition = position;
            }

#if UNITY_EDITOR || UNITY_STANDALONE
            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A))
                Previous();
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D))
                Next();
#endif
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            dragging = true;
            smoothVelocity = 0f;
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            dragging = false;
            if (content == null)
                return;
            int nearest = Mathf.RoundToInt(-content.anchoredPosition.x / stride);
            if (scrollRect != null && Mathf.Abs(scrollRect.velocity.x) > VelocityThreshold)
                nearest = selectedIndex + (scrollRect.velocity.x < 0f ? 1 : -1);

            scrollRect?.StopMovement();
            Select(nearest);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (Mathf.Abs(eventData.scrollDelta.y) < 0.01f)
                return;

            Select(selectedIndex + (eventData.scrollDelta.y < 0f ? 1 : -1));
        }
    }

    public sealed class WeatherCarouselCardView : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerClickHandler
    {
        RectTransform rect;
        CanvasGroup canvasGroup;
        Image selectionRing;
        WeatherCarouselController owner;
        int index;
        float targetEmphasis;
        float emphasis;
        bool hovered;

        public void Configure(
            WeatherCarouselController carousel,
            int cardIndex,
            Image ring)
        {
            owner = carousel;
            index = cardIndex;
            selectionRing = ring;
            rect = (RectTransform)transform;
            canvasGroup = GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
        }

        public void SetSelected(bool selected)
        {
            targetEmphasis = selected ? 1f : (hovered ? 0.40f : 0f);
        }

        void Update()
        {
            emphasis = Mathf.Lerp(
                emphasis,
                targetEmphasis,
                1f - Mathf.Exp(-Time.unscaledDeltaTime * 12f));

            float scale = Mathf.Lerp(0.88f, 1f, emphasis);
            rect.localScale = new Vector3(scale, scale, 1f);
            canvasGroup.alpha = Mathf.Lerp(0.55f, 1f, emphasis);
            if (selectionRing != null)
            {
                Color color = selectionRing.color;
                color.a = Mathf.Lerp(0.12f, 0.88f, emphasis);
                selectionRing.color = color;
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            hovered = true;
            if (owner != null && owner.SelectedIndex != index)
                targetEmphasis = 0.40f;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            hovered = false;
            if (owner != null && owner.SelectedIndex != index)
                targetEmphasis = 0f;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!eventData.dragging)
                owner?.Select(index);
        }
    }
}
