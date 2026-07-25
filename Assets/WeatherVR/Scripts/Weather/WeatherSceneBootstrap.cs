using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using WeatherVR.Core;
using WeatherVR.Interaction;
using WeatherVR.IntroEarth;

namespace WeatherVR.Weather
{
    /// <summary>
    /// Self-installs the head-follow, the glass sky and the new weather system into
    /// the running scene, the same way the carousel self-installs. Pressing Play is
    /// enough — no scene rebuild required — and everything here is idempotent, so a
    /// scene that SceneBuilder already populated is left as-is.
    ///
    /// It deliberately installs only the new <see cref="WeatherVisuals"/>; the old
    /// volumetric cloud / particle rain / lightning-bolt renderers are not created or
    /// referenced anywhere in the app any more.
    /// </summary>
    [DefaultExecutionOrder(-140)]
    public sealed class WeatherSceneBootstrap : MonoBehaviour
    {
        const float MinimumBackgroundSeconds = 0.5f;
        bool _started;
        bool _replayQueued;
        float _backgroundedAt = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (SceneManager.GetActiveScene().name != "WeatherVR")
                return;

            EarthIntroFeature.EnsureInstalled();

            if (FindObjectOfType<WeatherSceneBootstrap>() == null)
                new GameObject("Weather Scene Bootstrap").AddComponent<WeatherSceneBootstrap>();
        }

        IEnumerator Start()
        {
            var controller = FindObjectOfType<WeatherSceneController>();
            if (controller == null)
            {
                Debug.LogWarning("[WeatherVR] Scene bootstrap found no WeatherSceneController.");
                Destroy(gameObject);
                yield break;
            }

            WorldLockMap(controller);
            EnvironmentController environment = InstallEnvironment();
            WeatherVisuals visuals = InstallVisuals(controller);
            WeatherSceneDirector director = InstallDirector(controller, visuals, environment);

            if (controller.IsReady)
                Begin(director);
            else
                controller.Ready += _ => Begin(director);

            // XR runtimes report the persisted scene camera pose for the first few
            // frames. Wait for tracking to settle, then place the world-locked
            // exhibit in front of the user instead of leaving the user at its centre.
            for (int frame = 0; frame < 4; frame++)
                yield return null;
            PlaceExhibitInFront(controller);
            _started = true;
        }

        void OnApplicationPause(bool paused)
        {
            HandleForegroundState(!paused);
        }

        void OnApplicationFocus(bool focused)
        {
            HandleForegroundState(focused);
        }

        void HandleForegroundState(bool foreground)
        {
            if (!_started)
                return;

            if (!foreground)
            {
                _backgroundedAt = Time.realtimeSinceStartup;
                return;
            }

            if (_backgroundedAt < 0f ||
                Time.realtimeSinceStartup - _backgroundedAt < MinimumBackgroundSeconds ||
                _replayQueued)
                return;

            _backgroundedAt = -1f;
            QueueIntroReplay();
        }

        /// <summary>
        /// Called by WeatherVRActivity when PICO brings an already-resumed spatial
        /// activity to the foreground through a launcher intent.
        /// </summary>
        public void OnAndroidForegroundLaunch(string unused)
        {
            if (!_started)
                return;

            QueueIntroReplay();
        }

        void QueueIntroReplay()
        {
            if (_replayQueued)
                return;

            _replayQueued = true;
            StartCoroutine(ReplayIntroAfterResume());
        }

        IEnumerator ReplayIntroAfterResume()
        {
            // Allow PICO to restore the tracked camera pose before parenting the
            // globe to the head again.
            yield return null;
            yield return null;
            EarthIntroFeature.EnsureInstalled();
            _replayQueued = false;
            Debug.Log("[WeatherVR] Foreground launch detected — replaying Earth intro.");
        }

        // The map is a world-locked exhibit the user walks around. A scene generated
        // before this change may still carry a head-follow on the map root, so strip
        // any ComfortFollow off it — idempotent, and makes Press Play correct without a
        // scene rebuild.
        void WorldLockMap(WeatherSceneController controller)
        {
            Transform mapRoot = controller.MapRoot;
            if (mapRoot == null) return;

            foreach (var follow in mapRoot.GetComponents<ComfortFollow>())
                Destroy(follow);
        }

        static void PlaceExhibitInFront(WeatherSceneController controller)
        {
            Transform mapRoot = controller.MapRoot;
            Transform head = Camera.main != null ? Camera.main.transform : null;
            if (mapRoot == null || head == null)
                return;

            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f)
                forward = Vector3.forward;
            forward.Normalize();

            mapRoot.SetPositionAndRotation(
                head.position + forward * 2.45f + Vector3.down * 0.55f,
                Quaternion.LookRotation(forward, Vector3.up));

            Debug.Log("[WeatherVR] Table exhibit placed in front of the starting view.");
        }

        EnvironmentController InstallEnvironment()
        {
            var environment = FindObjectOfType<EnvironmentController>();
            if (environment == null)
                environment = new GameObject("Environment").AddComponent<EnvironmentController>();
            return environment;
        }

        WeatherVisuals InstallVisuals(WeatherSceneController controller)
        {
            var visuals = FindObjectOfType<WeatherVisuals>();
            if (visuals != null) return visuals;

            // Lives under the map root so it is in map-local units and travels with the table.
            var parent = controller.MapRoot != null ? controller.MapRoot : controller.transform;
            var go = new GameObject("WeatherVisuals");
            go.transform.SetParent(parent, false);
            return go.AddComponent<WeatherVisuals>();
        }

        WeatherSceneDirector InstallDirector(
            WeatherSceneController controller, WeatherVisuals visuals, EnvironmentController environment)
        {
            var director = controller.SceneDirector != null
                ? controller.SceneDirector
                : FindObjectOfType<WeatherSceneDirector>();

            if (director == null)
                director = controller.gameObject.AddComponent<WeatherSceneDirector>();

            if (director.Visuals == null) director.Visuals = visuals;
            if (director.Sun == null) director.Sun = controller.SunLight;
            if (director.Environment == null) director.Environment = environment;

            controller.SceneDirector = director;
            return director;
        }

        static void Begin(WeatherSceneDirector director)
        {
            if (director == null) return;
            director.Initialize(AppConfig.Instance);
            director.ApplyKind(WeatherSceneKind.Clear);
        }
    }
}
