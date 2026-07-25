using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using WeatherVR.Core;
using WeatherVR.Data;
using WeatherVR.Flood;
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
        /// <summary>Slider resolution. Half-hour steps: fine enough to land on any
        /// timeline boundary in the authored week (all of which fall on the hour), and
        /// coarse enough that a shaky controller ray does not jitter the clock.</summary>
        const float HourStep = 0.5f;

        const float DefaultHour = 12f;

        WeatherSceneController sceneController;
        BuiltWeatherCarousel built;
        WeatherCarouselDataset activeDataset;
        WeatherSceneDirector director;
        bool sceneReady;
        bool loading;
        float targetAlpha;
        bool introHidden;

        // The carousel owns (day, hour); everything rendered is derived from that pair.
        float hour = DefaultHour;
        bool hasAppliedKind;
        WeatherSceneKind appliedKind;

        // Storm-surge impact readout. Prepared once per snapshot (same lifetime as the
        // terrain/buildings it reads); re-evaluated only on a preset press, never per
        // frame — see FloodImpact's own header comment.
        readonly FloodImpact floodImpact = new FloodImpact();
        int surgeIndex;
        bool floodVisible;
        bool floodInitialized;

        /// <summary>
        /// Hides an already-built carousel while the launch globe is playing. This
        /// also works when Android resumes the existing activity and the carousel
        /// was built during a previous foreground session.
        /// </summary>
        public void SetIntroHidden(bool hidden)
        {
            introHidden = hidden;
            UpdateVisibility(immediate: true);
        }

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

            floodImpact.Prepare(snapshot);

            built = new WeatherCarouselBuilder().Build(
                dataset, head, pointer,
                OnFloodPresetSelected,
                OnHourScrubbed);
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
            // when the user actually taps a card or scrubs the clock.
            if (director != null && built.Controller != null)
                built.Controller.SelectionChanged += OnCardSelected;

            // Park the slider at noon and label it, without applying a scene — same
            // reasoning as above. The flood row still needs an explicit first pass so
            // it starts hidden by decision rather than by coincidence of the builder's
            // default-inactive buttons.
            RefreshTimeSliderLabels();
            UpdateFloodControls(SelectedItem());

            Debug.Log(
                $"[WeatherVR] Bilingual immersive weather carousel ready " +
                $"with {dataset.Items.Length} day cards ({dataset.SourceEnglish}), " +
                $"clock parked at {WeatherCarouselTimeSlider.FormatHour(hour)}.");
        }

        /// <summary>Tapping a day card keeps the clock where it is and re-resolves the
        /// weather for that day at the current hour.</summary>
        void OnCardSelected(int index) => ApplyDayAndHour();

        /// <summary>
        /// The slider's callback, called on press and on every frame of a drag, so it
        /// has to stay cheap. <paramref name="normalized"/> is 0..1 across the track.
        /// </summary>
        void OnHourScrubbed(float normalized)
        {
            float scrubbed = Mathf.Round(Mathf.Clamp01(normalized) * 24f / HourStep) * HourStep;

            // 24:00 is the same instant as 00:00, but showing "00:00" with the knob
            // pinned to the far right reads as a bug. Stop just short instead.
            if (scrubbed >= 24f)
                scrubbed = 24f - HourStep;

            // The `hasAppliedKind` half matters: the clock starts parked at noon
            // without a scene applied, so a first press that lands exactly on 12:00
            // would otherwise be swallowed and render nothing.
            if (hasAppliedKind && Mathf.Approximately(scrubbed, hour))
                return;

            hour = scrubbed;
            ApplyDayAndHour();
        }

        WeatherCarouselItem SelectedItem()
        {
            if (activeDataset?.Items == null || activeDataset.Items.Length == 0)
                return null;

            int index = built?.Controller != null ? built.Controller.SelectedIndex : 0;
            return activeDataset.Items[Mathf.Clamp(index, 0, activeDataset.Items.Length - 1)];
        }

        /// <summary>
        /// Renders whatever the selected day's timeline says is happening at the current
        /// hour.
        ///
        /// The two-tier update is deliberate and is the reason the slider is usable at
        /// all: <c>SetTimeOfDay</c> is cheap (sun angle, colour, fog, sky palette) and
        /// runs on every scrub, while <c>ApplyKind</c> rebuilds the cloud density volume
        /// and runs only when the scrub actually crosses a timeline boundary into a
        /// different case. Dragging across six hours of unchanging weather costs nothing
        /// beyond the lighting update.
        /// </summary>
        void ApplyDayAndHour()
        {
            WeatherCarouselItem item = SelectedItem();
            if (director == null || item == null)
                return;

            // Resolve from the timeline, not the card's icon — the icon set is smaller
            // than the case set, so Overcast/Fog/Snow would collapse onto Cloudy.
            WeatherSceneKind kind = item.KindAtHour(hour);

            director.SetTimeOfDay(hour);

            if (!hasAppliedKind || kind != appliedKind)
            {
                hasAppliedKind = true;
                appliedKind = kind;
                director.ApplyKind(kind);
            }

            // Reachability is keyed to the selected DAY, not the resolved hour — a
            // Thunderstorm day exposes storm-surge controls at any hour on that day,
            // not only inside its actual storm window. Called every time (not just on
            // a kind change) since it depends on `item`, not `kind`; UpdateFloodControls
            // itself no-ops unless the day's storm status actually flips.
            UpdateFloodControls(item);

            RefreshTimeSliderLabels();
        }

        void RefreshTimeSliderLabels()
        {
            WeatherCarouselTimeSlider slider = built?.TimeSlider;
            WeatherCarouselItem item = SelectedItem();
            if (slider == null || item == null)
                return;

            WeatherSceneKind kind = item.KindAtHour(hour);
            WeatherSceneProfile profile = WeatherScene.Default(kind);
            SceneKindCarouselDataProvider.Describe(
                kind, out string chinese, out _, out _, out _, out _);

            slider.SetHour(hour);
            slider.SetCondition(profile.DisplayName, chinese, item.Accent);
        }

        /// <summary>
        /// Storm surge only makes sense for a day whose timeline actually includes a
        /// storm somewhere in it — hide the row otherwise. Deliberately keyed to the
        /// whole day (<see cref="WeatherCarouselItem.HasKind"/>), not the hour currently
        /// resolved: gating on the resolved hour left the controls reachable only
        /// inside the storm's own multi-hour window, which is a blind target with
        /// nothing on screen inviting the user to land there. Guarded by
        /// <see cref="floodInitialized"/>/<see cref="floodVisible"/> so a scrub that
        /// does not change storm-day status does not re-toggle or re-zero the level on
        /// every frame of a drag.
        /// </summary>
        void UpdateFloodControls(WeatherCarouselItem item)
        {
            // Either a literal Thunderstorm segment (the authored demo week) or the
            // real forecast's designated peak-storm day (ForecastCarouselDataProvider) —
            // a real London forecast usually has no literal storm at all, so relying on
            // HasKind alone would leave the controls unreachable on every real day.
            bool hasStorm = item != null &&
                (item.HasKind(WeatherSceneKind.Thunderstorm) || item.IsPeakStormDay);

            if (floodInitialized && hasStorm == floodVisible)
                return;

            floodInitialized = true;
            floodVisible = hasStorm;

            if (built?.FloodButtons != null)
            {
                foreach (var button in built.FloodButtons)
                    if (button != null) button.SetActive(hasStorm);
            }

            if (built?.FloodTitle != null)
                built.FloodTitle.SetActive(hasStorm);

            if (built?.FloodReadout != null)
                built.FloodReadout.gameObject.SetActive(hasStorm);

            // Always start (or leave) at +0m: entering a storm day should not silently
            // resurrect whatever level a previous storm day was left at, and leaving
            // one must not let raised water linger into the next selection.
            surgeIndex = 0;
            sceneController.SetSurge(0);

            if (hasStorm)
                RefreshFloodReadout();
        }

        /// <summary>The flood row's button callback — also keeps the impact readout
        /// in step with whichever preset is currently selected.</summary>
        void OnFloodPresetSelected(int presetIndex)
        {
            surgeIndex = presetIndex;
            sceneController.SetSurge(presetIndex);
            RefreshFloodReadout();
        }

        /// <summary>
        /// Recomputes and displays the submerged-area/buildings-affected numbers for
        /// the currently selected surge preset. Evaluated against the *target* level
        /// (<see cref="FloodRenderer.LevelMeters"/>), not the mid-animation displayed
        /// one, so the readout reflects the preset the user just pressed immediately
        /// rather than crawling up together with the rise animation.
        /// </summary>
        void RefreshFloodReadout()
        {
            FloodRenderer flood = sceneController != null ? sceneController.Flood : null;
            if (built?.FloodReadout == null || flood == null)
                return;

            float[] presets = flood.SurgePresetsMeters;
            float presetMeters = presets != null && surgeIndex >= 0 && surgeIndex < presets.Length
                ? presets[surgeIndex]
                : 0f;

            // +0m has no target level (FloodRenderer.SetSurge treats it as "off"), so
            // fall back to the terrain's own floor — the correct baseline reading of
            // 0% flooded rather than showing nothing.
            float levelMeters = flood.LevelMeters ?? flood.MinElevationMeters;
            floodImpact.Evaluate(levelMeters, out float submergedFraction, out int buildingsAffected);

            built.FloodReadout.text = string.Format(
                "+{0:0}m · {1:0}% FLOODED 淹没 · {2}/{3} BUILDINGS 建筑",
                presetMeters, submergedFraction * 100f, buildingsAffected, floodImpact.BuildingCount);
        }

        void Update()
        {
            if (built == null)
                return;

            bool shouldShow = sceneReady && !introHidden;
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

            bool show = sceneReady && !introHidden;
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
