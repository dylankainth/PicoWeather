using UnityEngine;
using UnityEngine.SceneManagement;
using WeatherVR.Core;
using WeatherVR.Interaction;

namespace WeatherVR.Weather
{
    /// <summary>
    /// Self-installs the head-follow, the glass environment and the weather-scene
    /// director into the running scene, exactly the way the carousel self-installs.
    ///
    /// This exists because those three used to be added only by the editor's
    /// SceneBuilder, so a scene (or APK) that had not been regenerated ran with none
    /// of them — the map stayed world-locked, the surround stayed the old sky, and
    /// tapping a card did nothing, i.e. it looked identical to before the feature
    /// existed. Building them at runtime removes that "did you rebuild the scene?"
    /// trap: pressing Play is enough.
    ///
    /// Everything here is idempotent. If SceneBuilder already put these components in
    /// the scene, the bootstrap finds them and wires nothing twice.
    /// </summary>
    [DefaultExecutionOrder(-140)]
    public sealed class WeatherSceneBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (SceneManager.GetActiveScene().name != "WeatherVR")
                return;

            if (FindObjectOfType<WeatherSceneBootstrap>() == null)
                new GameObject("Weather Scene Bootstrap").AddComponent<WeatherSceneBootstrap>();
        }

        void Start()
        {
            var controller = FindObjectOfType<WeatherSceneController>();
            if (controller == null)
            {
                Debug.LogWarning("[WeatherVR] Scene bootstrap found no WeatherSceneController.");
                Destroy(gameObject);
                return;
            }

            Transform cam = Camera.main != null ? Camera.main.transform : null;
            XRPointer pointer = FindObjectOfType<XRPointer>();

            InstallFollow(controller, cam, pointer);
            EnvironmentController environment = InstallEnvironment(cam);
            WeatherSceneDirector director = InstallDirector(controller, environment);

            // Initialise the director and show a bright default so the terrain is
            // visible immediately, whatever the underlying data says. The carousel
            // then switches scenes on tap.
            if (controller.IsReady)
                Begin(director, controller.Snapshot);
            else
                controller.Ready += snapshot => Begin(director, snapshot);
        }

        void InstallFollow(WeatherSceneController controller, Transform cam, XRPointer pointer)
        {
            // The map rides the head instead of being placed on a surface. Disable the
            // old placement controller so it does not fight the follow by world-locking.
            if (controller.Placement != null)
                controller.Placement.enabled = false;

            Transform mapRoot = controller.MapRoot;
            if (mapRoot == null) return;

            if (mapRoot.GetComponent<ComfortFollow>() == null)
            {
                var follow = mapRoot.gameObject.AddComponent<ComfortFollow>();
                follow.Head = cam;
                follow.Pointer = pointer;
                follow.Distance = 0.95f;
                follow.VerticalOffset = -0.40f;
                follow.FollowSpeed = 8f;
                follow.FaceHead = true;
            }
        }

        EnvironmentController InstallEnvironment(Transform cam)
        {
            var environment = FindObjectOfType<EnvironmentController>();
            if (environment == null)
            {
                var go = new GameObject("Environment");
                environment = go.AddComponent<EnvironmentController>();
            }
            if (environment.Head == null) environment.Head = cam;
            return environment;
        }

        WeatherSceneDirector InstallDirector(
            WeatherSceneController controller, EnvironmentController environment)
        {
            var director = controller.SceneDirector != null
                ? controller.SceneDirector
                : FindObjectOfType<WeatherSceneDirector>();

            if (director == null)
            {
                director = controller.gameObject.AddComponent<WeatherSceneDirector>();
                director.Clouds = controller.Clouds;
                director.Rain = controller.Rain;
                director.Lightning = controller.Lightning;
                director.Sun = controller.SunLight;
                director.MapRoot = controller.MapRoot;
            }

            if (director.Environment == null) director.Environment = environment;
            controller.SceneDirector = director;
            return director;
        }

        static void Begin(WeatherSceneDirector director, Data.WeatherSnapshot snapshot)
        {
            if (director == null || snapshot == null) return;
            director.Initialize(snapshot, AppConfig.Instance);
            director.ApplyKind(WeatherSceneKind.Clear);
        }
    }
}
