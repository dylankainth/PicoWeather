using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using WeatherVR.Core;
using WeatherVR.Data;
using WeatherVR.Interaction;

namespace WeatherVR.UI.Carousel
{
    /// <summary>
    /// Automatic entry point for the immersive carousel.
    ///
    /// The feature creates itself after WeatherVR loads. It intentionally does not
    /// require a prefab or a generated-scene edit, which keeps this branch easy to
    /// merge with ongoing work on the weather scene.
    /// </summary>
    [DefaultExecutionOrder(-150)]
    public sealed class WeatherCarouselFeature : MonoBehaviour
    {
        WeatherSceneController sceneController;
        MapPlacementController placement;
        BuiltWeatherCarousel built;
        bool sceneReady;
        bool loading;
        float targetAlpha;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            // ONLY ATTACH TO THE PICO IMMERSIVE SCENE.
            // PHONE AND AR SCENES KEEP THEIR OWN SCREEN-SPACE INTERFACES.
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName != "WeatherVR" && sceneName != "WeatherVR_MR")
                return;

            if (FindObjectOfType<WeatherCarouselFeature>() == null)
            {
                new GameObject("Weather Carousel Feature")
                    .AddComponent<WeatherCarouselFeature>();
            }
        }

        void Start()
        {
            sceneController = FindObjectOfType<WeatherSceneController>();
            if (sceneController == null)
            {
                Debug.LogWarning(
                    "[WeatherVR] The carousel found no WeatherSceneController and will not start.");
                Destroy(gameObject);
                return;
            }

            placement = sceneController.Placement != null
                ? sceneController.Placement
                : FindObjectOfType<MapPlacementController>();

            // REPLACE THE OLD MAP-SIDE TEXT PANEL WITH THE IMMERSIVE CAROUSEL.
            // THE OBJECT REMAINS IN THE GENERATED SCENE, BUT IT NEVER RENDERS.
            // THIS AVOIDS EDITING THE GENERATED SCENE AND PREVENTS MERGE CONFLICTS.
            if (sceneController.Provenance != null)
                sceneController.Provenance.gameObject.SetActive(false);

            sceneController.Ready += OnSceneReady;
            if (placement != null)
            {
                placement.Placed += OnPlaced;
                placement.Unplaced += OnUnplaced;
            }

            if (sceneController.IsReady)
                OnSceneReady(sceneController.Snapshot);
        }

        void OnSceneReady(WeatherSnapshot snapshot)
        {
            if (loading || built != null)
                return;

            sceneReady = true;
            loading = true;
            IWeatherCarouselDataProvider provider =
                WeatherCarouselDataProvider.CreateDefault();
            StartCoroutine(LoadData(provider, snapshot));
        }

        IEnumerator LoadData(
            IWeatherCarouselDataProvider provider,
            WeatherSnapshot snapshot)
        {
            WeatherCarouselDataset dataset = null;
            string error = null;

            yield return provider.Load(
                snapshot,
                value => dataset = value,
                message => error = message);

            loading = false;
            if (dataset?.Items == null || dataset.Items.Length == 0)
            {
                Debug.LogError(
                    "[WeatherVR] Weather carousel data failed: " +
                    (string.IsNullOrEmpty(error) ? "no items returned" : error));
                yield break;
            }

            Transform head = Camera.main != null ? Camera.main.transform : null;
            XRPointer pointer = FindObjectOfType<XRPointer>();
            if (head == null || pointer == null)
            {
                Debug.LogError(
                    "[WeatherVR] The carousel needs the main camera and the existing XRPointer.");
                yield break;
            }

            built = new WeatherCarouselBuilder().Build(dataset, head, pointer);
            built.Root.transform.SetParent(transform, true);
            UpdateVisibility(immediate: true);

            Debug.Log(
                $"[WeatherVR] Bilingual immersive weather carousel ready " +
                $"with {dataset.Items.Length} cards ({dataset.SourceEnglish}).");
        }

        void OnPlaced(Pose pose) => UpdateVisibility(immediate: false);

        void OnUnplaced() => UpdateVisibility(immediate: false);

        void Update()
        {
            if (built == null)
                return;

            bool shouldShow = sceneReady && IsMapPlaced();
            targetAlpha = shouldShow ? 1f : 0f;

            if (shouldShow && !built.Root.activeSelf)
                built.Root.SetActive(true);

            float blend = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 10f);
            built.Visibility.alpha = Mathf.Lerp(
                built.Visibility.alpha,
                targetAlpha,
                blend);

            bool interactive = built.Visibility.alpha > 0.96f && shouldShow;
            built.Visibility.interactable = interactive;
            built.Visibility.blocksRaycasts = interactive;

            if (!shouldShow && built.Visibility.alpha < 0.01f && built.Root.activeSelf)
                built.Root.SetActive(false);
        }

        void UpdateVisibility(bool immediate)
        {
            if (built == null)
                return;

            bool show = sceneReady && IsMapPlaced();
            targetAlpha = show ? 1f : 0f;

            if (show)
                built.Root.SetActive(true);

            if (immediate)
            {
                built.Visibility.alpha = targetAlpha;
                built.Visibility.interactable = show;
                built.Visibility.blocksRaycasts = show;
                if (!show)
                    built.Root.SetActive(false);
            }
        }

        bool IsMapPlaced()
        {
            if (placement == null)
                return true;

            if (placement.CurrentState == MapPlacementController.State.Placed)
                return true;

            // PICO MR automatically seats the map on detected furniture, then
            // disables manual placement. That path does not call Commit(), so the
            // disabled placement component is the reliable completion signal.
            return !placement.enabled &&
                   sceneController != null &&
                   sceneController.MapRoot != null &&
                   sceneController.MapRoot.position.y > -100f;
        }

        void OnDestroy()
        {
            if (sceneController != null)
                sceneController.Ready -= OnSceneReady;

            if (placement != null)
            {
                placement.Placed -= OnPlaced;
                placement.Unplaced -= OnUnplaced;
            }
        }
    }
}
