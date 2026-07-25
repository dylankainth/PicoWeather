using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using WeatherVR.Core;
using WeatherVR.Data;
using WeatherVR.Interaction;
using WeatherVR.Weather;

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
        BuiltWeatherCarousel built;
        WeatherCarouselDataset activeDataset;
        WeatherSceneDirector director;
        bool sceneReady;
        bool loading;
        float targetAlpha;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            // Only attach to the Pico immersive weather scene.
            string sceneName = SceneManager.GetActiveScene().name;
            if (sceneName != "WeatherVR")
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

            sceneController.Ready += OnSceneReady;

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
            if (head == null)
            {
                Debug.LogError(
                    "[WeatherVR] The carousel needs the main camera.");
                yield break;
            }

            built = new WeatherCarouselBuilder().Build(dataset, head, pointer);
            built.Root.transform.SetParent(transform, true);
            activeDataset = dataset;

            // Anchor the carousel to the map so it always sits a fixed gap beneath the
            // terrain and can never intersect it — the two move together as one.
            var follower = built.Root.GetComponent<WeatherCarouselFollower>();
            if (follower != null && sceneController.MapRoot != null)
                follower.Anchor = sceneController.MapRoot;

            UpdateVisibility(immediate: true);

            // Clicking a card switches the rendered weather scene, so terrain and UI
            // change together. Subscribe first, then apply the current card once.
            director = sceneController.SceneDirector != null
                ? sceneController.SceneDirector
                : FindObjectOfType<WeatherSceneDirector>();

            // Subscribe only — do not apply a scene on load. The bootstrap has already
            // shown a bright default so the terrain is visible; the weather changes
            // when the user actually taps a card.
            if (director != null && built.Controller != null)
                built.Controller.SelectionChanged += OnCardSelected;

            Debug.Log(
                $"[WeatherVR] Bilingual immersive weather carousel ready " +
                $"with {dataset.Items.Length} cards ({dataset.SourceEnglish}).");
        }

        void OnCardSelected(int index)
        {
            if (director == null || activeDataset?.Items == null || activeDataset.Items.Length == 0)
                return;

            index = Mathf.Clamp(index, 0, activeDataset.Items.Length - 1);
            WeatherCarouselItem item = activeDataset.Items[index];
            // Use the card's own scene, not its icon — the icon set is smaller than the
            // case set, so Overcast/Fog/Snow would otherwise collapse onto Cloudy.
            director.ApplyKind((WeatherSceneKind)item.SceneKind);
        }

        void Update()
        {
            if (built == null)
                return;

            bool shouldShow = sceneReady;
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

            bool show = sceneReady;
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

        void OnDestroy()
        {
            if (built?.Controller != null)
                built.Controller.SelectionChanged -= OnCardSelected;

            if (sceneController != null)
                sceneController.Ready -= OnSceneReady;
        }
    }
}
