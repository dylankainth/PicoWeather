using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using WeatherVR.Interaction;

namespace WeatherVR.UI.Carousel
{
    /// <summary>
    /// Reuses the app's existing PICO controller/hand ray instead of installing a
    /// second XR interaction stack. That keeps placement and carousel input simple.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class WeatherCarouselInput : MonoBehaviour
    {
        sealed class HitTarget
        {
            public RectTransform Rect;
            public Action Click;
            public Image Surface;
            public Color Normal;
            public Color Hover;
        }

        readonly List<HitTarget> buttons = new List<HitTarget>();
        readonly List<HitTarget> cards = new List<HitTarget>();

        RectTransform canvasRect;
        RectTransform viewport;
        WeatherCarouselController controller;
        XRPointer pointer;
        HitTarget hoveredButton;
        HitTarget pressedCard;
        bool dragging;
        float previousCanvasX;
        float totalDrag;

        public void Configure(
            RectTransform canvas,
            RectTransform carouselViewport,
            WeatherCarouselController carousel,
            XRPointer xrPointer)
        {
            canvasRect = canvas;
            viewport = carouselViewport;
            controller = carousel;
            pointer = xrPointer;
        }

        public void RegisterButton(
            RectTransform rect,
            Image surface,
            Action clicked,
            Color normal,
            Color hover)
        {
            buttons.Add(new HitTarget
            {
                Rect = rect,
                Surface = surface,
                Click = clicked,
                Normal = normal,
                Hover = hover
            });
            surface.color = normal;
        }

        public void RegisterCard(RectTransform rect, Action clicked)
        {
            cards.Add(new HitTarget { Rect = rect, Click = clicked });
        }

        void Update()
        {
            if (pointer == null || canvasRect == null || !pointer.IsTracked)
            {
                SetHoveredButton(null);
                if (dragging)
                    FinishDrag();
                return;
            }

            if (!TryGetCanvasHit(out Vector3 worldPoint, out float canvasX))
            {
                SetHoveredButton(null);
                if (dragging && pointer.SelectReleasedThisFrame)
                    FinishDrag();
                return;
            }

            HitTarget button = FindTarget(buttons, worldPoint);
            SetHoveredButton(button);

            if (pointer.SelectPressedThisFrame)
            {
                if (button != null)
                {
                    button.Click?.Invoke();
                    return;
                }

                if (Contains(viewport, worldPoint))
                {
                    pressedCard = FindTarget(cards, worldPoint);
                    dragging = true;
                    totalDrag = 0f;
                    previousCanvasX = canvasX;
                    controller.BeginXRDrag();
                }
            }

            if (dragging && pointer.IsSelecting)
            {
                float delta = canvasX - previousCanvasX;
                previousCanvasX = canvasX;
                totalDrag += Mathf.Abs(delta);
                controller.DragXR(delta);
            }

            if (dragging && pointer.SelectReleasedThisFrame)
                FinishDrag();
        }

        void FinishDrag()
        {
            controller.EndXRDrag();
            if (totalDrag < 9f)
                pressedCard?.Click?.Invoke();

            dragging = false;
            pressedCard = null;
            totalDrag = 0f;
        }

        bool TryGetCanvasHit(out Vector3 worldPoint, out float canvasX)
        {
            var plane = new Plane(canvasRect.forward, canvasRect.position);
            if (!plane.Raycast(pointer.Ray, out float distance) || distance < 0f || distance > 6f)
            {
                worldPoint = default;
                canvasX = 0f;
                return false;
            }

            worldPoint = pointer.Ray.GetPoint(distance);
            canvasX = canvasRect.InverseTransformPoint(worldPoint).x;
            return true;
        }

        static HitTarget FindTarget(List<HitTarget> targets, Vector3 worldPoint)
        {
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                if (Contains(targets[i].Rect, worldPoint))
                    return targets[i];
            }
            return null;
        }

        static bool Contains(RectTransform rect, Vector3 worldPoint)
        {
            if (rect == null || !rect.gameObject.activeInHierarchy)
                return false;

            Vector3 local = rect.InverseTransformPoint(worldPoint);
            return rect.rect.Contains(new Vector2(local.x, local.y));
        }

        void SetHoveredButton(HitTarget target)
        {
            if (hoveredButton == target)
                return;

            if (hoveredButton?.Surface != null)
                hoveredButton.Surface.color = hoveredButton.Normal;

            hoveredButton = target;
            if (hoveredButton?.Surface != null)
                hoveredButton.Surface.color = hoveredButton.Hover;
        }
    }

    /// <summary>
    /// Keeps the carousel in the lower field of view with gentle smoothing, world
    /// upright so it never rolls or pitches with the head.
    ///
    /// When <see cref="Anchor"/> is set (the map root), the dock rides a fixed gap
    /// *directly beneath the terrain* and pulled a little toward the user, so it moves
    /// as one with the table and is guaranteed never to intersect it. Without an
    /// anchor it falls back to a head-relative dock.
    /// </summary>
    public sealed class WeatherCarouselFollower : MonoBehaviour
    {
        public Transform Head;

        [Tooltip("Map root to hang beneath. When set, the dock tracks the map, not the head directly.")]
        public Transform Anchor;

        [Tooltip("Gap below the map, in metres. Large enough that the panel clears the tabletop.")]
        public float DropBelow = 0.5f;

        [Tooltip("How far toward the user the dock is pulled from under the map, in metres.")]
        public float NearOffset = 0.22f;

        public float Distance = 1.20f;
        public float VerticalOffset = -0.30f;
        public float FollowSpeed = 8f;

        bool initialised;

        void LateUpdate()
        {
            if (Head == null)
                return;

            Vector3 targetPosition;
            Quaternion targetRotation;

            if (Anchor != null)
            {
                // Flattened facing so the panel stays upright and turns to face the user.
                Vector3 forward = Head.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
                forward.Normalize();

                // Straight down from the map, then a little back toward the user so the
                // panel sits at the near-lower edge rather than hidden under the table.
                targetPosition = Anchor.position + Vector3.down * DropBelow - forward * NearOffset;
                targetRotation = Quaternion.LookRotation(forward, Vector3.up);
            }
            else
            {
                // Identical anchor maths to the map's ComfortFollow, off the same head.
                ComfortFollow.ComputeAnchor(
                    Head, Distance, VerticalOffset,
                    out targetPosition, out targetRotation);
            }

            if (!initialised)
            {
                transform.SetPositionAndRotation(targetPosition, targetRotation);
                initialised = true;
                return;
            }

            float blend = 1f - Mathf.Exp(-FollowSpeed * Time.unscaledDeltaTime);
            transform.position = Vector3.Lerp(transform.position, targetPosition, blend);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, blend);
        }
    }
}
