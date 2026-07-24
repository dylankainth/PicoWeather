using UnityEngine;
using UnityEngine.SceneManagement;
using WeatherVR.Core;
using WeatherVR.Interaction;

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
            EnvironmentController environment = InstallEnvironment();
            WeatherVisuals visuals = InstallVisuals(controller);
            WeatherSceneDirector director = InstallDirector(controller, visuals, environment);

            if (controller.IsReady)
                Begin(director);
            else
                controller.Ready += _ => Begin(director);
        }

        void InstallFollow(WeatherSceneController controller, Transform cam, XRPointer pointer)
        {
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
