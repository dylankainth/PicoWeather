using UnityEngine;
using WeatherVR.Core;

namespace WeatherVR.Interaction
{
    /// <summary>
    /// Places the weather map on a surface and then gets out of the way.
    ///
    /// Flow: the map follows the pointer as a translucent preview, snapped to a
    /// horizontal surface; a trigger pull or a pinch commits it; the map is then
    /// world-locked and the user walks around it. Holding the secondary button
    /// returns to preview so a bad placement is one gesture to fix rather than an
    /// app restart.
    ///
    /// Surface detection tries a physics raycast first — if the app is running with
    /// PICO's spatial meshing the room's real furniture has colliders and the map
    /// lands on the actual table. Failing that it falls back to a horizontal plane a
    /// comfortable table's height below the user's head, which is the case that
    /// matters in a bare demo scene.
    /// </summary>
    public class MapPlacementController : MonoBehaviour
    {
        public enum State
        {
            Previewing,
            Placed
        }

        [Header("References")]
        [Tooltip("The transform that gets moved. Usually the map root.")]
        public Transform MapRoot;

        [Tooltip("Primary pointer. Falls back to the first XRPointer in the scene.")]
        public XRPointer Pointer;

        [Tooltip("The head/camera transform, used for the fallback placement plane and " +
                 "to face the map towards the user.")]
        public Transform Head;

        [Tooltip("Optional ray visual shown during preview.")]
        public LineRenderer RayVisual;

        [Header("Placement")]
        [Tooltip("Height below the head at which the fallback plane sits. 0.75 m is " +
                 "about the difference between standing eye height and a table.")]
        public float FallbackDropBelowHead = 0.75f;

        [Tooltip("Distance in front of the user the fallback plane is probed at, when " +
                 "the pointer is aimed above the horizon.")]
        public float FallbackForwardDistance = 1.2f;

        [Tooltip("Layers considered valid placement surfaces.")]
        public LayerMask SurfaceLayers = ~0;

        [Tooltip("Maximum ray length when probing for a surface.")]
        public float MaxRayDistance = 8f;

        [Tooltip("Surfaces tilted more than this from horizontal are rejected.")]
        [Range(0f, 60f)] public float MaxSurfaceTilt = 25f;

        [Tooltip("Seconds the secondary button must be held to unlock a placed map.")]
        public float ReplaceHoldSeconds = 0.8f;

        [Header("Preview feel")]
        [Tooltip("Smoothing applied to the preview position. Raw pointer motion is jittery.")]
        [Range(0f, 30f)] public float PreviewSmoothing = 14f;

        public State CurrentState { get; private set; } = State.Previewing;

        /// <summary>Raised when the map is committed to a position.</summary>
        public event System.Action<Pose> Placed;

        /// <summary>Raised when the map returns to preview.</summary>
        public event System.Action Unplaced;

        float _secondaryHeldFor;
        Vector3 _smoothedPosition;
        Quaternion _smoothedRotation = Quaternion.identity;
        bool _hasPreviewTarget;

        void Awake()
        {
            if (Pointer == null) Pointer = FindObjectOfType<XRPointer>();
            if (Head == null && Camera.main != null) Head = Camera.main.transform;
            if (MapRoot == null) MapRoot = transform;
        }

        void Start()
        {
            // Start in preview so the very first thing the user does is place the map,
            // which is also the clearest possible tutorial for the interaction.
            EnterPreview();
        }

        void Update()
        {
            if (Pointer == null || MapRoot == null) return;

            switch (CurrentState)
            {
                case State.Previewing:
                    UpdatePreview();
                    break;
                case State.Placed:
                    UpdatePlaced();
                    break;
            }
        }

        // ------------------------------------------------------------ preview

        void UpdatePreview()
        {
            bool haveTarget = ResolveTarget(out Vector3 position, out Quaternion rotation);

            if (haveTarget)
            {
                if (!_hasPreviewTarget)
                {
                    // Snap on the first valid frame rather than sliding in from wherever
                    // the map happened to be.
                    _smoothedPosition = position;
                    _smoothedRotation = rotation;
                    _hasPreviewTarget = true;
                }
                else
                {
                    float t = PreviewSmoothing <= 0f ? 1f : 1f - Mathf.Exp(-PreviewSmoothing * Time.deltaTime);
                    _smoothedPosition = Vector3.Lerp(_smoothedPosition, position, t);
                    _smoothedRotation = Quaternion.Slerp(_smoothedRotation, rotation, t);
                }

                MapRoot.SetPositionAndRotation(_smoothedPosition, _smoothedRotation);
            }

            UpdateRayVisual(haveTarget, _smoothedPosition);

            if (haveTarget && Pointer.SelectPressedThisFrame)
                Commit();
        }

        bool ResolveTarget(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!Pointer.IsTracked) return false;

            Ray ray = Pointer.Ray;
            bool found = false;

            if (Physics.Raycast(ray, out RaycastHit hit, MaxRayDistance, SurfaceLayers,
                                QueryTriggerInteraction.Ignore))
            {
                float tilt = Vector3.Angle(hit.normal, Vector3.up);
                if (tilt <= MaxSurfaceTilt)
                {
                    position = hit.point;
                    found = true;
                }
            }

            if (!found && !ResolveFallbackPlane(ray, out position)) return false;

            // Keep the map level and turn its "north" edge away from the user, so the
            // first thing they see is the map the right way up rather than upside down.
            Vector3 toUser = Head != null ? Head.position - position : Vector3.back;
            toUser.y = 0f;
            rotation = toUser.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(-toUser.normalized, Vector3.up)
                : Quaternion.identity;

            return true;
        }

        /// <summary>
        /// Intersects the pointer ray with a horizontal plane at table height. If the
        /// ray is aimed at or above the horizon there is no intersection, so we place
        /// the map a fixed distance ahead instead of refusing to place it at all.
        /// </summary>
        bool ResolveFallbackPlane(Ray ray, out Vector3 position)
        {
            float planeY = Head != null ? Head.position.y - FallbackDropBelowHead : 0f;
            var plane = new Plane(Vector3.up, new Vector3(0f, planeY, 0f));

            if (plane.Raycast(ray, out float distance) && distance <= MaxRayDistance)
            {
                position = ray.GetPoint(distance);
                return true;
            }

            if (Head == null)
            {
                position = ray.GetPoint(Mathf.Min(FallbackForwardDistance, MaxRayDistance));
                return true;
            }

            Vector3 forward = Head.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            position = Head.position + forward.normalized * FallbackForwardDistance;
            position.y = planeY;
            return true;
        }

        // ------------------------------------------------------------- placed

        void UpdatePlaced()
        {
            if (Pointer.IsSecondaryHeld)
            {
                _secondaryHeldFor += Time.deltaTime;
                if (_secondaryHeldFor >= ReplaceHoldSeconds)
                {
                    EnterPreview();
                    _secondaryHeldFor = 0f;
                }
            }
            else
            {
                _secondaryHeldFor = 0f;
            }
        }

        // ---------------------------------------------------------- transitions

        public void EnterPreview()
        {
            CurrentState = State.Previewing;
            _hasPreviewTarget = false;
            _secondaryHeldFor = 0f;
            if (RayVisual != null) RayVisual.enabled = true;
            Unplaced?.Invoke();
        }

        public void Commit()
        {
            CurrentState = State.Placed;
            if (RayVisual != null) RayVisual.enabled = false;
            Placed?.Invoke(new Pose(MapRoot.position, MapRoot.rotation));
        }

        /// <summary>Places the map at an explicit pose, skipping the preview.</summary>
        public void PlaceAt(Pose pose)
        {
            MapRoot.SetPositionAndRotation(pose.position, pose.rotation);
            _smoothedPosition = pose.position;
            _smoothedRotation = pose.rotation;
            _hasPreviewTarget = true;
            Commit();
        }

        void UpdateRayVisual(bool haveTarget, Vector3 endPoint)
        {
            if (RayVisual == null) return;

            RayVisual.enabled = Pointer.IsTracked;
            if (!Pointer.IsTracked) return;

            RayVisual.positionCount = 2;
            RayVisual.SetPosition(0, Pointer.Origin);
            RayVisual.SetPosition(1, haveTarget ? endPoint : Pointer.Origin + Pointer.Direction * MaxRayDistance);
        }
    }
}
