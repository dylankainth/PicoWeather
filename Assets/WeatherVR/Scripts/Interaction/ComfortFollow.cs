using UnityEngine;

namespace WeatherVR.Interaction
{
    /// <summary>
    /// Keeps an object comfortably in front of the user: it eases toward a point a
    /// fixed distance ahead of the head at a fixed height, following yaw and position
    /// but staying world-upright so the object never rolls or pitches with the head.
    ///
    /// This is the "lazy follow" the weather map uses so the tabletop travels with
    /// the user instead of being world-locked. The carousel's own follower runs the
    /// identical maths (see <c>ComputeAnchor</c>), so the map and the carousel move
    /// together off the same head — they stay in the same relative layout as the user
    /// turns and walks.
    ///
    /// The smoothing settles the object when the head is still, so you can still lean
    /// in to inspect. The secondary controller button snaps it back in front, which is
    /// the only "placement" gesture left now that the scene follows the head.
    /// </summary>
    public sealed class ComfortFollow : MonoBehaviour
    {
        [Tooltip("Head/camera to follow. Falls back to the main camera.")]
        public Transform Head;

        [Tooltip("Optional pointer; its secondary button re-centres the object in front of the user.")]
        public XRPointer Pointer;

        [Tooltip("Distance in front of the head, in metres.")]
        public float Distance = 0.9f;

        [Tooltip("Height offset relative to the head, in metres. Negative sits it below eye level.")]
        public float VerticalOffset = -0.35f;

        [Tooltip("Easing rate. Higher snaps in faster; 8 is the comfortable default the UI uses.")]
        public float FollowSpeed = 8f;

        [Tooltip("If true the object yaws to face the user. The map wants this so its front edge faces you.")]
        public bool FaceHead = true;

        [Tooltip("Head yaw change, in degrees, before the object re-anchors. 0 = follow " +
                 "continuously (the original, always-recomputing behaviour).")]
        public float YawDeadzoneDegrees = 0f;

        [Tooltip("Head translation, in metres, before the object re-anchors. 0 = follow " +
                 "continuously (the original, always-recomputing behaviour).")]
        public float PositionDeadzone = 0f;

        bool _initialised;
        Vector3 _anchorPosition;
        float _anchorYaw;
        Vector3 _targetPosition;
        Quaternion _targetRotation;

        void LateUpdate()
        {
            if (Head == null)
            {
                if (Camera.main == null) return;
                Head = Camera.main.transform;
            }

            if (Pointer != null && Pointer.SecondaryPressedThisFrame)
                Recenter();

            // Both deadzones at 0 (the field default) means "recompute every frame",
            // which is bit-identical to this component's original behaviour before
            // deadzones existed -- every other consumer of ComputeAnchor (the
            // carousel's own head-relative fallback) leaves both at 0 and is
            // unaffected. A non-zero deadzone holds the last committed anchor until
            // the head departs by more than it, then re-targets and eases there over
            // the usual exponential blend below. Without this, a follow distance long
            // enough to clear the carousel panel (see SceneBuilder) slides the whole
            // world sideways every time the user so much as glances around.
            bool continuous = YawDeadzoneDegrees <= 0f && PositionDeadzone <= 0f;
            bool recompute = !_initialised || continuous;

            if (!recompute)
            {
                float yawDelta = Mathf.Abs(Mathf.DeltaAngle(_anchorYaw, Head.eulerAngles.y));
                Vector3 flatHeadPos = Head.position;
                flatHeadPos.y = 0f;
                float posDelta = Vector3.Distance(flatHeadPos, _anchorPosition);

                recompute = (YawDeadzoneDegrees > 0f && yawDelta > YawDeadzoneDegrees) ||
                            (PositionDeadzone > 0f && posDelta > PositionDeadzone);
            }

            if (recompute)
            {
                _anchorYaw = Head.eulerAngles.y;
                _anchorPosition = Head.position;
                _anchorPosition.y = 0f;
                ComputeAnchor(Head, Distance, VerticalOffset, out _targetPosition, out _targetRotation);
            }

            if (!_initialised)
            {
                transform.position = _targetPosition;
                if (FaceHead) transform.rotation = _targetRotation;
                _initialised = true;
                return;
            }

            float blend = 1f - Mathf.Exp(-FollowSpeed * Time.unscaledDeltaTime);
            transform.position = Vector3.Lerp(transform.position, _targetPosition, blend);
            if (FaceHead)
                transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, blend);
        }

        /// <summary>Snaps the object in front of the head on the next frame.</summary>
        public void Recenter() => _initialised = false;

        /// <summary>
        /// Keeps the current world-space target after the XR rig was moved by
        /// controller locomotion. Without this, a snap turn can cross the yaw
        /// deadzone and make the map follow the rig instead of letting the user move
        /// around it.
        /// </summary>
        public void PreserveWorldPoseAfterRigMove()
        {
            if (!_initialised) return;

            if (Head == null && Camera.main != null)
                Head = Camera.main.transform;
            if (Head == null) return;

            _anchorYaw = Head.eulerAngles.y;
            _anchorPosition = Head.position;
            _anchorPosition.y = 0f;
        }

        /// <summary>
        /// The shared anchor maths. The pose sits <paramref name="distance"/> ahead of
        /// the head along its flattened forward, <paramref name="verticalOffset"/> up,
        /// facing back toward the head and kept world-upright.
        /// </summary>
        public static void ComputeAnchor(
            Transform head, float distance, float verticalOffset,
            out Vector3 position, out Quaternion rotation)
        {
            Vector3 forward = head.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();

            position = head.position + forward * distance + Vector3.up * verticalOffset;
            rotation = Quaternion.LookRotation(forward, Vector3.up);
        }
    }
}
