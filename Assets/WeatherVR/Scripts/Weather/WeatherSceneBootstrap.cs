using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using WeatherVR.Core;
using WeatherVR.Flood;
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

            ComfortFollow mapFollow = EnsureMapFollow(controller);
            EnsureControllerLocomotion(mapFollow);
            EnsurePointerVisual();
            EnvironmentController environment = InstallEnvironment(controller);
            WeatherVisuals visuals = InstallVisuals(controller);
            InstallFlood(controller);
            WeatherSceneDirector director = InstallDirector(controller, visuals, environment);

            if (controller.IsReady)
                Begin(director);
            else
                controller.Ready += _ => Begin(director);

            // XR runtimes report the persisted scene camera pose for the first few
            // frames. Wait for tracking to settle before snapping the head-follow map
            // in front of the user -- ComfortFollow._initialised snapping on frame 1
            // would latch onto that stale pose instead.
            for (int frame = 0; frame < 4; frame++)
                yield return null;
            if (mapFollow != null) mapFollow.Recenter();
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

        // The map follows the head instead of being world-locked (see ComfortFollow,
        // added by SceneBuilder.Populate). A scene generated before this existed, or a
        // world-locked scene from before 2026-07-25, may have no ComfortFollow on the
        // map root at all -- self-install one, idempotent, so Press Play is correct
        // without a scene rebuild. Values mirror SceneBuilder.Populate's; see its
        // comment for why 2.95 / -0.55 rather than this component's original 0.95 /
        // -0.40 (the latter puts the carousel panel behind the user's head) and why the
        // 2.95 tracks MapSizeMeters.
        ComfortFollow EnsureMapFollow(WeatherSceneController controller)
        {
            Transform mapRoot = controller.MapRoot;
            if (mapRoot == null) return null;

            var follow = mapRoot.GetComponent<ComfortFollow>();
            if (follow == null)
            {
                follow = mapRoot.gameObject.AddComponent<ComfortFollow>();
                follow.Distance = 2.95f;
                follow.VerticalOffset = -0.55f;
                follow.FollowSpeed = 3.0f;
                follow.FaceHead = true;
                follow.YawDeadzoneDegrees = 25f;
                Debug.LogWarning(
                    "[WeatherVR] The map had no ComfortFollow, so the exhibit would " +
                    "have stayed world-locked. Added one at runtime — rebuild the " +
                    "scene with Tools > WeatherVR > Build Scene to bake it in properly.");
            }

            if (follow.Head == null && Camera.main != null)
                follow.Head = Camera.main.transform;
            if (follow.Pointer == null)
                follow.Pointer = FindObjectOfType<XRPointer>();

            return follow;
        }

        // Scenes generated before controller locomotion existed have no component on
        // XRRig. Repair them at runtime so pulling this code is enough to make an
        // existing baked WeatherVR scene work without a manual scene rebuild.
        void EnsureControllerLocomotion(ComfortFollow mapFollow)
        {
            Camera camera = Camera.main;
            if (camera == null) return;

            GameObject rig = camera.transform.root.gameObject;
            ControllerLocomotion.Ensure(rig, camera.transform, mapFollow);
        }

        // A scene generated before the ray visual existed will have an XRPointer with
        // no XRPointerVisual alongside it -- self-install one on the same GameObject,
        // idempotent, so the ray is visible without a scene rebuild. See
        // XRPointerVisual's own header for why this was needed at all: the baked
        // LineRenderer had a configured material and gradient but nothing ever moved
        // its two points, so it sat at the world origin instead of at the controller.
        void EnsurePointerVisual()
        {
            var pointer = FindObjectOfType<XRPointer>();
            if (pointer == null) return;

            XRPointerVisual.Ensure(pointer.gameObject, pointer);
        }

        EnvironmentController InstallEnvironment(WeatherSceneController controller)
        {
            var environment = FindObjectOfType<EnvironmentController>();
            if (environment == null)
                environment = new GameObject("Environment").AddComponent<EnvironmentController>();

            // A scene generated before the glass floor existed will have no MapRoot
            // wired up on its Environment object; back-fill it so the floor aligns
            // under the table without a scene rebuild, same as the flood/visuals
            // installers above.
            if (environment.MapRoot == null)
                environment.MapRoot = controller.MapRoot;

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

        // A scene generated before the flood layer existed will have no FloodRenderer
        // wired up; self-install one under the map root so Press Play works without a
        // scene rebuild, same as InstallVisuals above.
        FloodRenderer InstallFlood(WeatherSceneController controller)
        {
            if (controller.Flood != null) return controller.Flood;

            var flood = FindObjectOfType<FloodRenderer>();
            if (flood == null)
            {
                var parent = controller.MapRoot != null ? controller.MapRoot : controller.transform;
                var go = new GameObject("WaterSurface");
                go.transform.SetParent(parent, false);
                flood = go.AddComponent<FloodRenderer>();
            }

            controller.Flood = flood;
            return flood;
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
