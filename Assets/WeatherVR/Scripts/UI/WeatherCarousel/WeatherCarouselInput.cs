using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
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

        RectTransform sliderTrack;
        Action<float> sliderChanged;
        bool draggingSlider;
        HitTarget gazeTarget;
        float gazeSeconds;
        const float GazeDwellSeconds = 1.25f;

        /// <summary>
        /// Distance along the XR ray to the point it hit the carousel panel this
        /// frame, or -1 when the ray is not on the panel at all. Read by
        /// <see cref="WeatherVR.Interaction.XRPointerVisual"/> so the drawn ray
        /// terminates at the glass instead of punching a fixed length through it, or
        /// hanging short of it in empty air. Reset every frame, including on the
        /// screen-pointer and gaze branches below, so neither can leave a stale value
        /// for the XR visual to pick up.
        /// </summary>
        public float RayHitDistance { get; private set; } = -1f;

        /// <summary>
        /// 0..1 progress of the controller-free gaze dwell (see <see cref="UpdateGaze"/>),
        /// 0 when nothing is currently being gazed at. The dwell has no other feedback
        /// of its own, so without this a working gaze selection is indistinguishable
        /// from a dead app.
        /// </summary>
        public float GazeProgress01 { get; private set; }

        /// <summary>World point the gaze ray is currently resting on, valid only while
        /// <see cref="GazeProgress01"/> is greater than zero.</summary>
        public Vector3 GazeWorldPoint { get; private set; }

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

        /// <summary>
        /// The one continuously-dragged control on the panel (the time slider), as
        /// opposed to the discrete cards and buttons above.
        ///
        /// <paramref name="normalizedChanged"/> receives 0..1 along the track and is
        /// called on press and on every frame of the drag, so it must be cheap — see
        /// WeatherCarouselFeature, which separates the per-frame sun update from the
        /// scene rebuild that only runs when the resolved weather case changes.
        /// </summary>
        public void RegisterSlider(RectTransform track, Action<float> normalizedChanged)
        {
            sliderTrack = track;
            sliderChanged = normalizedChanged;
        }

        void Update()
        {
            // Reset every frame regardless of which branch below runs, so a stale hit
            // from a previous XR-ray frame can never survive into a screen-pointer or
            // gaze frame.
            RayHitDistance = -1f;

            if (canvasRect == null || controller == null)
                return;

            if (Keyboard.current != null)
            {
                if (Keyboard.current.leftArrowKey.wasPressedThisFrame)
                    controller.Previous();
                if (Keyboard.current.rightArrowKey.wasPressedThisFrame)
                    controller.Next();
            }

            bool hasScreenPointer = TryGetScreenPointer(
                out Vector2 screenPosition,
                out bool screenPressed,
                out bool screenHeld,
                out bool screenReleased);

            // The PICO emulator advertises a tracked controller even while the user
            // is clicking its desktop window. A real mouse/touch press must therefore
            // take priority over the idle XR ray or emulator tile clicks are ignored.
            if (hasScreenPointer &&
                (screenPressed || screenHeld || screenReleased) &&
                Camera.main != null)
            {
                ProcessScreenPointer(
                    screenPosition,
                    screenPressed,
                    screenHeld,
                    screenReleased,
                    Camera.main);
                return;
            }

            if (pointer != null && pointer.IsTracked)
            {
                ResetGaze();
                ProcessRay(
                    pointer.Ray,
                    pointer.SelectPressedThisFrame,
                    pointer.IsSelecting,
                    pointer.SelectReleasedThisFrame);
                return;
            }

            if (hasScreenPointer && Camera.main != null && !Application.isMobilePlatform)
            {
                ResetGaze();
                SetHoveredButton(
                    FindScreenTarget(buttons, screenPosition, Camera.main));
                return;
            }

            // Controller-free PICO gaze mode. A steady head-centre gaze works on
            // devices and emulators even when hardware eye tracking is unavailable.
            if (Camera.main != null)
            {
                UpdateGaze(Camera.main.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)));
                return;
            }

            ResetGaze();
            if (dragging)
                FinishDrag();
        }

        void UpdateGaze(Ray gazeRay)
        {
            if (!TryGetCanvasHit(gazeRay, out Vector3 worldPoint, out _))
            {
                ResetGaze();
                return;
            }

            HitTarget target = FindTarget(buttons, worldPoint) ??
                               FindTarget(cards, worldPoint);
            SetHoveredButton(buttons.Contains(target) ? target : null);

            if (target == null)
            {
                ResetGaze();
                return;
            }

            if (gazeTarget != target)
            {
                gazeTarget = target;
                gazeSeconds = 0f;
                GazeWorldPoint = worldPoint;
                GazeProgress01 = 0f;
                return;
            }

            gazeSeconds += Time.unscaledDeltaTime;
            GazeWorldPoint = worldPoint;
            GazeProgress01 = Mathf.Clamp01(gazeSeconds / GazeDwellSeconds);
            if (gazeSeconds < GazeDwellSeconds)
                return;

            target.Click?.Invoke();
            Debug.Log("[WeatherVR] Gaze dwell selected a weather carousel target.");
            gazeSeconds = -0.45f;
            GazeProgress01 = 0f;
        }

        void ResetGaze()
        {
            gazeTarget = null;
            gazeSeconds = 0f;
            GazeProgress01 = 0f;
            SetHoveredButton(null);
        }

        void ProcessScreenPointer(
            Vector2 screenPosition,
            bool pressed,
            bool held,
            bool released,
            Camera camera)
        {
            HitTarget button = FindScreenTarget(buttons, screenPosition, camera);
            SetHoveredButton(button);

            // The slider needs a *position along* the track rather than a yes/no hit,
            // so it goes through the same canvas-plane intersection the XR ray uses
            // instead of the screen-rect containment test the discrete targets use.
            if ((pressed || (held && draggingSlider)) &&
                TryGetCanvasHit(camera.ScreenPointToRay(screenPosition),
                                out Vector3 sliderPoint, out _))
            {
                if (pressed && Contains(sliderTrack, sliderPoint))
                    draggingSlider = true;

                if (draggingSlider)
                {
                    ReportSlider(sliderPoint);
                    return;
                }
            }

            if (released || !held)
                draggingSlider = false;

            if (!pressed)
                return;

            if (button != null)
            {
                button.Click?.Invoke();
                return;
            }

            HitTarget card = FindScreenTarget(cards, screenPosition, camera);
            if (card != null)
            {
                // Select on press. Emulator touch-up can be consumed by the spatial
                // window host, so waiting for release made otherwise valid clicks
                // unreliable.
                card.Click?.Invoke();
                Debug.Log("[WeatherVR] Screen pointer selected a weather card.");
            }
        }

        void ProcessRay(Ray ray, bool pressed, bool held, bool released)
        {
            if (!TryGetCanvasHit(ray, out Vector3 worldPoint, out float canvasX))
            {
                SetHoveredButton(null);
                if (dragging && released)
                    FinishDrag();
                if (released)
                    draggingSlider = false;
                return;
            }

            // Only when the ray is actually on the panel, not merely on the infinite
            // plane TryGetCanvasHit tests against -- otherwise the drawn ray would
            // terminate at the panel's plane even while aimed well off its edges.
            if (Contains(canvasRect, worldPoint))
                RayHitDistance = Vector3.Distance(ray.origin, worldPoint);

            HitTarget button = FindTarget(buttons, worldPoint);
            SetHoveredButton(button);

            // The slider claims the drag before the deck does, and keeps it until
            // release even if the ray wanders off the track — that is what makes a
            // slider feel like a slider rather than something that drops the value
            // the moment the hand drifts a couple of centimetres.
            if (pressed && button == null && Contains(sliderTrack, worldPoint))
                draggingSlider = true;

            if (draggingSlider)
            {
                bool down = pressed || held;
                if (down)
                    ReportSlider(worldPoint);

                // Released, or the button came up without a release event ever being
                // seen (tracking loss mid-drag). Without the second condition a stuck
                // flag would keep scrubbing the clock from bare ray movement, with no
                // press involved at all.
                if (released || !down)
                    draggingSlider = false;
                return;
            }

            if (pressed)
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

            if (dragging && held)
            {
                float delta = canvasX - previousCanvasX;
                previousCanvasX = canvasX;
                totalDrag += Mathf.Abs(delta);
                controller.DragXR(delta);
            }

            if (dragging && released)
                FinishDrag();
        }

        static bool TryGetScreenPointer(
            out Vector2 position,
            out bool pressed,
            out bool held,
            out bool released)
        {
            if (Touchscreen.current != null)
            {
                var touch = Touchscreen.current.primaryTouch;
                position = touch.position.ReadValue();
                pressed = touch.press.wasPressedThisFrame;
                held = touch.press.isPressed;
                released = touch.press.wasReleasedThisFrame;
                if (pressed || held || released)
                    return true;
            }

            if (Mouse.current != null)
            {
                position = Mouse.current.position.ReadValue();
                pressed = Mouse.current.leftButton.wasPressedThisFrame;
                held = Mouse.current.leftButton.isPressed;
                released = Mouse.current.leftButton.wasReleasedThisFrame;
                return true;
            }

            position = default;
            pressed = held = released = false;
            return false;
        }

        /// <summary>
        /// Turns a point on the canvas into 0..1 along the slider track. Measured from
        /// the rect's own xMin rather than assuming a centred pivot, so the track can be
        /// laid out however the builder likes.
        /// </summary>
        void ReportSlider(Vector3 worldPoint)
        {
            if (sliderTrack == null || sliderChanged == null)
                return;

            Rect rect = sliderTrack.rect;
            if (rect.width <= 0f)
                return;

            float local = sliderTrack.InverseTransformPoint(worldPoint).x;
            sliderChanged(Mathf.Clamp01((local - rect.xMin) / rect.width));
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

        bool TryGetCanvasHit(Ray ray, out Vector3 worldPoint, out float canvasX)
        {
            var plane = new Plane(canvasRect.forward, canvasRect.position);
            if (!plane.Raycast(ray, out float distance) || distance < 0f || distance > 6f)
            {
                worldPoint = default;
                canvasX = 0f;
                return false;
            }

            worldPoint = ray.GetPoint(distance);
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

        static HitTarget FindScreenTarget(
            List<HitTarget> targets,
            Vector2 screenPoint,
            Camera camera)
        {
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                RectTransform rect = targets[i].Rect;
                if (rect == null || !rect.gameObject.activeInHierarchy)
                    continue;

                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                Vector2 min = camera.WorldToScreenPoint(corners[0]);
                Vector2 max = min;
                for (int corner = 1; corner < corners.Length; corner++)
                {
                    Vector2 projected = camera.WorldToScreenPoint(corners[corner]);
                    min = Vector2.Min(min, projected);
                    max = Vector2.Max(max, projected);
                }

                if (new Rect(min, max - min).Contains(screenPoint))
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
    /// When <see cref="Anchor"/> is set (the map root), the panel is *mounted on the
    /// pedestal wall*, not on the camera: it snaps flush to whichever of the four
    /// walls currently faces the user and rides with the table. Walk around the table
    /// and it hops to the wall you are now looking at. Without an anchor it falls back
    /// to a head-relative dock.
    /// </summary>
    public sealed class WeatherCarouselFollower : MonoBehaviour
    {
        public Transform Head;

        [Tooltip("Map root to mount against. When set, the panel mounts on the pedestal wall facing the user.")]
        public Transform Anchor;

        [Tooltip("How far out from the map centre the panel sits, in map-local units. " +
                 "Pedestal rim is ~0.57 and terrain edge 0.5, so >0.7 clears the block and floats it proud.")]
        public float WallHalfExtent = 0.68f;

        [Tooltip("Vertical centre of the panel, in map-local units. 0 = tabletop/rim level; positive floats it higher.")]
        public float WallHeight = 0.02f;

        [Tooltip("Upward pitch of the wall-mounted panel, degrees. 0 = vertical, facing straight out.")]
        public float TiltDegrees = 0f;

        [Tooltip("Extra margin (map-local units) the user must round a corner by before the " +
                 "panel hops to the next wall. Stops it flip-flopping at the 45° diagonal.")]
        public float WallSwitchMargin = 0.12f;

        public float Distance = 1.20f;
        public float VerticalOffset = -0.30f;
        public float FollowSpeed = 8f;

        bool initialised;
        Vector3 currentNormal;

        void LateUpdate()
        {
            if (Head == null)
                return;

            Vector3 targetPosition;
            Quaternion targetRotation;
            bool snap = !initialised;

            if (Anchor != null)
            {
                // Which of the four pedestal walls faces the user right now, in the
                // map's own local frame: dominant horizontal axis of the head offset.
                Vector3 localToHead = Anchor.InverseTransformPoint(Head.position);
                localToHead.y = 0f;

                Vector3 candidate = Mathf.Abs(localToHead.x) >= Mathf.Abs(localToHead.z)
                    ? new Vector3(Mathf.Sign(localToHead.x == 0f ? 1f : localToHead.x), 0f, 0f)
                    : new Vector3(0f, 0f, Mathf.Sign(localToHead.z == 0f ? 1f : localToHead.z));

                // Hysteresis: stay on the current wall until the user has clearly rounded
                // the corner (its projection beats the current wall's by a margin). Then
                // hop — snapping, not sliding, so the panel never sweeps through the body.
                if (!initialised)
                {
                    currentNormal = candidate;
                }
                else if (candidate != currentNormal)
                {
                    float cur = Vector3.Dot(localToHead, currentNormal);
                    float cand = Vector3.Dot(localToHead, candidate);
                    if (cand > cur + WallSwitchMargin)
                    {
                        currentNormal = candidate;
                        snap = true;
                    }
                }

                // Flush against that wall, at mid pedestal height. TransformPoint applies
                // the map's scale + rotation, so the panel rides with the table.
                Vector3 localPos = currentNormal * WallHalfExtent + Vector3.up * WallHeight;
                targetPosition = Anchor.TransformPoint(localPos);

                // Canvas faces outward from the pedestal toward the user. A world-space
                // UGUI canvas is readable from its -Z side, so its +Z must point AWAY
                // from the viewer — i.e. back into the wall, the opposite of the outward
                // normal. LookRotation with -worldNormal does that; otherwise the panel
                // shows its mirrored back (which is what "it's inside" was).
                Vector3 worldNormal = Anchor.TransformDirection(currentNormal).normalized;
                targetRotation = Quaternion.LookRotation(-worldNormal, Vector3.up)
                                 * Quaternion.Euler(-TiltDegrees, 0f, 0f);
            }
            else
            {
                // Identical anchor maths to the map's ComfortFollow, off the same head.
                ComfortFollow.ComputeAnchor(
                    Head, Distance, VerticalOffset,
                    out targetPosition, out targetRotation);
            }

            if (snap)
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
