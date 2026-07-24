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

        bool _initialised;

        void LateUpdate()
        {
            if (Head == null)
            {
                if (Camera.main == null) return;
                Head = Camera.main.transform;
            }

            if (Pointer != null && Pointer.SecondaryPressedThisFrame)
                Recenter();

            ComputeAnchor(Head, Distance, VerticalOffset, out Vector3 targetPos, out Quaternion targetRot);

            if (!_initialised)
            {
                transform.position = targetPos;
                if (FaceHead) transform.rotation = targetRot;
                _initialised = true;
                return;
            }

            float blend = 1f - Mathf.Exp(-FollowSpeed * Time.unscaledDeltaTime);
            transform.position = Vector3.Lerp(transform.position, targetPos, blend);
            if (FaceHead)
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, blend);
        }

        /// <summary>Snaps the object in front of the head on the next frame.</summary>
        public void Recenter() => _initialised = false;

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
