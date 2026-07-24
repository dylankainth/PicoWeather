using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using WeatherVR.Core;
using WeatherVR.Interaction;
using WeatherVR.UI.Carousel;

namespace WeatherVR.IntroEarth
{
    /// <summary>
    /// Runs one lightweight launch sequence:
    /// low-poly Earth -> rotate to London -> zoom through -> reveal WeatherVR.
    ///
    /// It self-installs and temporarily hides the existing map and carousel. No scene
    /// YAML or repaired WeatherVR systems are modified, so removing this one folder
    /// removes the feature completely.
    /// </summary>
    [DefaultExecutionOrder(-300)]
    public sealed class EarthIntroFeature : MonoBehaviour
    {
        const float AppearSeconds = 0.55f;
        const float InspectSeconds = 1.35f;
        const float FocusSeconds = 1.05f;
        const float ZoomSeconds = 1.30f;
        const float MaximumDataWaitSeconds = 6f;

        WeatherSceneController _weather;
        WeatherCarouselFeature _carousel;
        Transform _mapRoot;
        Transform _head;
        LowPolyEarthBuilder.Visual _earth;
        Material _earthMaterial;
        Material _grid;
        Material _marker;

        bool _mapWasActive;
        bool _carouselWasEnabled;
        bool _contentHidden;
        bool _revealed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (SceneManager.GetActiveScene().name != "WeatherVR")
                return;

            if (FindObjectOfType<EarthIntroFeature>() == null)
                new GameObject("Earth to London Intro").AddComponent<EarthIntroFeature>();
        }

        IEnumerator Start()
        {
            _weather = FindObjectOfType<WeatherSceneController>();
            _head = Camera.main != null ? Camera.main.transform : null;

            if (_weather == null || _weather.MapRoot == null || _head == null)
            {
                Debug.LogWarning(
                    "[WeatherVR] Earth intro could not find the WeatherVR controller, " +
                    "map root and main camera. The normal London scene will continue.");
                Destroy(gameObject);
                yield break;
            }

            _mapRoot = _weather.MapRoot;
            _mapWasActive = _mapRoot.gameObject.activeSelf;
            _mapRoot.gameObject.SetActive(false);

            // STOP THE CAROUSEL BEFORE ITS START METHOD RUNS.
            // IT WILL INITIALISE NORMALLY AS SOON AS LONDON IS REVEALED.
            _carousel = FindObjectOfType<WeatherCarouselFeature>(includeInactive: true);
            if (_carousel != null)
            {
                _carouselWasEnabled = _carousel.enabled;
                _carousel.enabled = false;
            }

            _contentHidden = true;

            if (!BuildEarth())
            {
                RevealLondon();
                Destroy(gameObject);
                yield break;
            }

            _earth.Root.transform.SetParent(_head, worldPositionStays: false);
            _earth.Root.transform.localPosition = new Vector3(0f, -0.035f, 1.25f);

            Vector3 londonDirection =
                LowPolyEarthBuilder.DirectionFromLatitudeLongitude(
                    LowPolyEarthBuilder.LondonLatitude,
                    LowPolyEarthBuilder.LondonLongitude);
            Quaternion londonFront =
                Quaternion.FromToRotation(londonDirection, Vector3.back);
            Quaternion openingRotation =
                Quaternion.Euler(-7f, -68f, 4f) * londonFront;

            _earth.Root.transform.localRotation = openingRotation;
            _earth.Root.transform.localScale = Vector3.zero;
            SetAlpha(0f);

            // 1. EARTH APPEARS.
            yield return Animate(AppearSeconds, t =>
            {
                float eased = Smooth(t);
                _earth.Root.transform.localScale = Vector3.one * eased;
                _earth.Root.transform.localRotation =
                    Quaternion.Slerp(openingRotation, londonFront, eased * 0.18f);
                SetAlpha(eased);
                PulseMarker(t);
            });

            // 2. THE GLOBE TURNS UNTIL LONDON FACES THE USER.
            yield return Animate(InspectSeconds, t =>
            {
                float eased = Smooth(t);
                _earth.Root.transform.localRotation =
                    Quaternion.Slerp(openingRotation, londonFront, eased);
                PulseMarker(t);
            });

            // Give the existing WeatherVR data pipeline time to finish behind the
            // hidden root. Never trap the user in an intro if loading has failed.
            float wait = 0f;
            while (!_weather.IsReady && wait < MaximumDataWaitSeconds)
            {
                wait += Time.unscaledDeltaTime;
                _earth.Root.transform.Rotate(0f, 4f * Time.unscaledDeltaTime, 0f, Space.Self);
                PulseMarker(wait);
                yield return null;
            }

            // 3. LOCK ONTO LONDON AND MOVE CLOSER.
            Vector3 focusStartPosition = _earth.Root.transform.localPosition;
            yield return Animate(FocusSeconds, t =>
            {
                float eased = Smooth(t);
                _earth.Root.transform.localRotation =
                    Quaternion.Slerp(_earth.Root.transform.localRotation, londonFront, eased);
                _earth.Root.transform.localPosition =
                    Vector3.Lerp(focusStartPosition, new Vector3(0f, -0.02f, 1.13f), eased);
                _earth.Root.transform.localScale =
                    Vector3.one * Mathf.Lerp(1f, 1.16f, eased);
                PulseMarker(t * 2f);
            });

            // 4. ZOOM THROUGH THE LONDON POINT, THEN REVEAL THE REAL LONDON MAP.
            yield return Animate(ZoomSeconds, t =>
            {
                float eased = t * t * (3f - 2f * t);
                float scale = Mathf.Lerp(1.16f, 4.35f, eased);
                _earth.Root.transform.localScale = Vector3.one * scale;
                _earth.Root.transform.localPosition =
                    Vector3.Lerp(new Vector3(0f, -0.02f, 1.13f),
                                 new Vector3(0f, 0f, 0.92f),
                                 eased);

                float fade = 1f - Smooth(Mathf.InverseLerp(0.42f, 0.94f, t));
                SetAlpha(fade);
                PulseMarker(t * 3f);
            });

            RevealLondon();
            DestroyEarth();
            Debug.Log(
                "[WeatherVR] Earth intro complete — London weather scene revealed.");
            Destroy(gameObject);
        }

        bool BuildEarth()
        {
            Shader shader = Resources.Load<Shader>("IntroEarth");
            if (shader == null)
            {
                Debug.LogError(
                    "[WeatherVR] IntroEarth shader is missing; skipping the globe intro.");
                return false;
            }

            try
            {
                _earthMaterial = NewMaterial(shader, "Intro Earth Surface", Color.white);
                _grid = NewMaterial(shader, "Intro Earth Grid", new Color(0.52f, 0.82f, 0.94f, 0.28f));
                _marker = NewMaterial(shader, "Intro Earth London", new Color(1.0f, 0.48f, 0.27f, 1f));
                _earth = LowPolyEarthBuilder.Build(
                    transform, _earthMaterial, _grid, _marker);
                return _earth != null;
            }
            catch (System.Exception exception)
            {
                Debug.LogError(
                    "[WeatherVR] Earth intro mesh creation failed. " +
                    "The normal London scene will continue.\n" + exception);
                DestroyEarth();
                return false;
            }
        }

        static Material NewMaterial(Shader shader, string name, Color colour)
        {
            var material = new Material(shader) { name = name };
            material.SetColor("_Color", colour);
            return material;
        }

        IEnumerator Animate(float seconds, System.Action<float> update)
        {
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                update(Mathf.Clamp01(elapsed / seconds));
                yield return null;
            }
            update(1f);
        }

        void PulseMarker(float time)
        {
            if (_earth?.LondonMarker == null)
                return;

            float pulse = 0.018f * (1f + 0.16f * Mathf.Sin(time * Mathf.PI * 4f));
            _earth.LondonMarker.localScale = Vector3.one * pulse;
        }

        void SetAlpha(float alpha)
        {
            SetMaterialAlpha(_earthMaterial, alpha);
            SetMaterialAlpha(_grid, alpha * 0.28f);
            SetMaterialAlpha(_marker, alpha);
        }

        static void SetMaterialAlpha(Material material, float alpha)
        {
            if (material == null)
                return;
            Color colour = material.GetColor("_Color");
            colour.a = Mathf.Clamp01(alpha);
            material.SetColor("_Color", colour);
        }

        static float Smooth(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        void RevealLondon()
        {
            if (_revealed)
                return;

            _revealed = true;
            if (_mapRoot != null && _mapWasActive)
            {
                _mapRoot.gameObject.SetActive(true);

                // DesktopPreview could not see a follow component while the map was
                // hidden. Match its normal behaviour when testing without a headset.
                if (!Application.isMobilePlatform)
                {
                    var follow = _mapRoot.GetComponent<ComfortFollow>();
                    if (follow != null)
                        follow.enabled = false;
                }
            }

            if (_carousel != null && _carouselWasEnabled)
                _carousel.enabled = true;

            _contentHidden = false;
        }

        void DestroyEarth()
        {
            if (_earth != null)
            {
                if (_earth.Root != null)
                    Destroy(_earth.Root);
                if (_earth.Meshes != null)
                {
                    foreach (Mesh mesh in _earth.Meshes)
                        if (mesh != null)
                            Destroy(mesh);
                }
                _earth = null;
            }

            if (_earthMaterial != null) Destroy(_earthMaterial);
            if (_grid != null) Destroy(_grid);
            if (_marker != null) Destroy(_marker);
            _earthMaterial = null;
            _grid = null;
            _marker = null;
        }

        void OnDestroy()
        {
            if (_contentHidden)
                RevealLondon();
            DestroyEarth();
        }
    }
}
