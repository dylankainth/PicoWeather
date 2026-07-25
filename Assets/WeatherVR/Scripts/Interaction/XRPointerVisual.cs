using UnityEngine;
using WeatherVR.UI.Carousel;

namespace WeatherVR.Interaction
{
    /// <summary>
    /// Draws the controller ray at the controller, instead of nowhere.
    ///
    /// <see cref="SceneBuilder"/> has always configured a <see cref="LineRenderer"/>
    /// for this (material, gradient, width) but nothing anywhere ever called
    /// <c>SetPosition</c> on it, so it kept Unity's brand-new-component default of
    /// <c>(0,0,0) -&gt; (0,0,1)</c> in world space: a static line lying on the floor
    /// through the origin, not a ray from the hand. This is what actually moves it.
    ///
    /// A separate component rather than fields on <see cref="XRPointer"/>: that class
    /// is pure input (consumed by <c>ComfortFollow</c>, <c>WeatherCarouselInput</c>,
    /// the phone/AR paths), and must not carry a rendering dependency.
    ///
    /// Installed the same way <see cref="HeadTracking"/> is: baked in by
    /// <see cref="SceneBuilder"/> for a fresh scene, and re-asserted at runtime by
    /// <c>WeatherSceneBootstrap</c> for a scene generated before this existed, so
    /// Press Play is correct without forcing a rebuild.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class XRPointerVisual : MonoBehaviour
    {
        [Tooltip("Pointer whose ray this draws. Falls back to a sibling XRPointer.")]
        public XRPointer Pointer;

        [Tooltip("The ray line itself. Falls back to a sibling LineRenderer.")]
        public LineRenderer Line;

        [Tooltip("Ray length when it is not resting on anything, in metres.")]
        public float DefaultLength = 2.0f;
        public float MinLength = 0.10f;
        public float MaxLength = 5f;

        [Tooltip("Ray width normally, and while the trigger is held.")]
        public float Width = 0.004f;
        public float SelectWidth = 0.007f;

        [Tooltip("Reticle disc size per metre of distance, for a roughly constant angular size.")]
        public float ReticleAngularSize = 0.02f;

        LineRenderer _reticle;
        WeatherCarouselInput _input;
        float _inputProbeCooldown;

        /// <summary>Idempotent install, called from both the scene bake and the runtime bootstrap.</summary>
        public static XRPointerVisual Ensure(GameObject host, XRPointer pointer)
        {
            if (host == null) return null;

            var visual = host.GetComponent<XRPointerVisual>();
            if (visual == null) visual = host.AddComponent<XRPointerVisual>();

            if (visual.Pointer == null) visual.Pointer = pointer != null ? pointer : host.GetComponent<XRPointer>();
            if (visual.Line == null) visual.Line = host.GetComponent<LineRenderer>();
            visual.EnsureReticle();

            return visual;
        }

        void Awake()
        {
            if (Pointer == null) Pointer = GetComponent<XRPointer>();
            if (Line == null) Line = GetComponent<LineRenderer>();
            EnsureReticle();
        }

        void EnsureReticle()
        {
            if (_reticle != null) return;

            var existing = transform.Find("PointerReticle");
            GameObject go = existing != null ? existing.gameObject : new GameObject("PointerReticle");
            if (existing == null) go.transform.SetParent(transform, false);

            _reticle = go.GetComponent<LineRenderer>();
            if (_reticle == null) _reticle = go.AddComponent<LineRenderer>();

            _reticle.useWorldSpace = true;
            _reticle.positionCount = 2;
            _reticle.numCapVertices = 8;
            _reticle.alignment = LineAlignment.View;
            _reticle.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _reticle.receiveShadows = false;

            // Reuses the ray's own material (already-always-included Sprites/Default,
            // see SceneBuilder.ConfigureRayVisual) -- no new shader, no billboarding
            // maths needed, LineAlignment.View does that for us.
            if (_reticle.sharedMaterial == null && Line != null)
                _reticle.sharedMaterial = Line.sharedMaterial;
        }

        void LateUpdate()
        {
            if (Pointer == null || Line == null) return;

            if (!Pointer.IsTracked)
            {
                Line.enabled = false;
                ShowGazeReticleOnly();
                return;
            }

            ProbeInput();

            Line.enabled = true;

            float length = _input != null && _input.RayHitDistance > 0f
                ? _input.RayHitDistance
                : DefaultLength;
            length = Mathf.Clamp(length, MinLength, MaxLength);

            // Start a couple of centimetres clear of the grip, not at the device pose
            // itself.
            Vector3 origin = Pointer.Origin + Pointer.Direction * 0.02f;
            Vector3 end = Pointer.Origin + Pointer.Direction * length;

            Line.SetPosition(0, origin);
            Line.SetPosition(1, end);
            Line.widthMultiplier = Pointer.IsSelecting ? SelectWidth : Width;

            bool onTarget = _input != null && _input.RayHitDistance > 0f;
            SetReticle(end, length, onTarget ? 1f : 0.55f);
        }

        /// <summary>
        /// No tracked controller: the ray itself stays hidden (a degenerate line would
        /// still submit a draw call, which is the exact bug being fixed here), but the
        /// gaze-dwell fallback in <see cref="WeatherCarouselInput"/> otherwise has no
        /// feedback of its own -- a 1.25 s dwell with nothing on screen is
        /// indistinguishable from a dead app.
        /// </summary>
        void ShowGazeReticleOnly()
        {
            ProbeInput();

            if (_reticle == null) return;

            if (_input == null || _input.GazeProgress01 <= 0f)
            {
                _reticle.enabled = false;
                return;
            }

            _reticle.enabled = true;
            Vector3 gazePoint = _input.GazeWorldPoint;
            float distance = Camera.main != null
                ? Vector3.Distance(Camera.main.transform.position, gazePoint)
                : DefaultLength;
            SetReticle(gazePoint, distance, _input.GazeProgress01);
        }

        void SetReticle(Vector3 worldPoint, float distance, float brightness)
        {
            if (_reticle == null) return;

            _reticle.enabled = true;
            _reticle.SetPosition(0, worldPoint);
            _reticle.SetPosition(1, worldPoint + Vector3.up * 0.0005f);

            float size = Mathf.Max(brightness, 0.35f) * ReticleAngularSize * Mathf.Max(distance, 0.1f);
            _reticle.widthMultiplier = Mathf.Max(0.002f, size);

            Color color = Color.Lerp(new Color(0.55f, 0.78f, 1f, 0.55f), Color.white, brightness);
            _reticle.startColor = color;
            _reticle.endColor = color;
        }

        /// <summary>
        /// The carousel is built asynchronously (<c>WeatherCarouselFeature.LoadData</c>),
        /// so it does not exist on frame 0 -- keep probing at a low rate rather than
        /// caching a permanent null.
        /// </summary>
        void ProbeInput()
        {
            if (_input != null) return;
            _inputProbeCooldown -= Time.unscaledDeltaTime;
            if (_inputProbeCooldown > 0f) return;
            _inputProbeCooldown = 0.5f;
            _input = FindObjectOfType<WeatherCarouselInput>();
        }
    }
}
