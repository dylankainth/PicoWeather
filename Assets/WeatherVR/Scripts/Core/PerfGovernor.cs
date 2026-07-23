using UnityEngine;
using WeatherVR.Clouds;

namespace WeatherVR.Core
{
    /// <summary>
    /// Keeps the frame inside its budget by spending cloud quality instead of
    /// framerate.
    ///
    /// On a standalone headset a dropped frame is not a cosmetic problem — it is
    /// felt. So the rule here is that the raymarcher is the adjustable load and the
    /// refresh rate is not: when frame time creeps up we take steps away from the
    /// cloud march, and only give them back once there is comfortable headroom.
    ///
    /// The cloud shader integrates each segment analytically rather than as a plain
    /// Riemann sum, which is what makes this safe to do live: halving the step count
    /// softens the clouds slightly but barely changes their brightness, so quality
    /// changes are not visible as a flash.
    /// </summary>
    public class PerfGovernor : MonoBehaviour
    {
        [Tooltip("Cloud renderer to throttle. Found automatically if unset.")]
        public CloudRenderer Clouds;

        [Tooltip("Seconds of frame-time history averaged before acting. Reacting to a " +
                 "single slow frame would make quality oscillate.")]
        public float EvaluationWindowSeconds = 1.0f;

        [Tooltip("Fraction of the frame budget above which quality is reduced.")]
        [Range(0.6f, 1.2f)] public float DowngradeThreshold = 0.92f;

        [Tooltip("Fraction of the frame budget below which quality is restored. The gap " +
                 "between this and the downgrade threshold is the hysteresis band.")]
        [Range(0.4f, 1.0f)] public float UpgradeThreshold = 0.68f;

        [Tooltip("Seconds to wait after a change before considering another.")]
        public float SettleSeconds = 2.0f;

        [Tooltip("Log every quality change. Useful on device, noisy in the editor.")]
        public bool LogChanges = true;

        /// <summary>Quality tiers, richest first.</summary>
        struct Tier
        {
            public int MarchSteps;
            public int LightSteps;
            public string Name;
        }

        Tier[] _tiers;
        int _currentTier;
        float _accumulatedTime;
        int _accumulatedFrames;
        float _lastChangeTime;
        float _frameBudget;

        /// <summary>Smoothed frame time in milliseconds, for the HUD.</summary>
        public float AverageFrameMs { get; private set; }

        /// <summary>Name of the tier currently in force.</summary>
        public string CurrentTierName => _tiers != null && _tiers.Length > 0 ? _tiers[_currentTier].Name : "n/a";

        void Start()
        {
            var config = AppConfig.Instance;

            if (Clouds == null) Clouds = FindObjectOfType<CloudRenderer>();

            // Ask the platform for the target rate. On PICO/Quest this is the display
            // refresh rate; in the editor it is whatever the user's monitor does.
            int target = config.TargetFrameRate;
            _frameBudget = 1000f / Mathf.Max(target, 30);

            int high = config.CloudMarchSteps;
            int low = config.CloudMarchStepsMin;
            int mid = Mathf.RoundToInt(Mathf.Lerp(low, high, 0.5f));

            _tiers = new[]
            {
                new Tier { MarchSteps = high, LightSteps = 3, Name = "high" },
                new Tier { MarchSteps = mid,  LightSteps = 2, Name = "medium" },
                new Tier { MarchSteps = low,  LightSteps = 1, Name = "low" },
                new Tier { MarchSteps = Mathf.Max(8, low / 2), LightSteps = 0, Name = "minimum" }
            };

            _currentTier = 0;
            ApplyTier();

            Application.targetFrameRate = target;
            // Vsync is meaningless in VR — the compositor owns presentation — and
            // leaving it on makes targetFrameRate a no-op.
            QualitySettings.vSyncCount = 0;
        }

        void Update()
        {
            if (_tiers == null || Clouds == null) return;

            _accumulatedTime += Time.unscaledDeltaTime;
            _accumulatedFrames++;

            if (_accumulatedTime < EvaluationWindowSeconds) return;

            AverageFrameMs = _accumulatedTime / _accumulatedFrames * 1000f;
            _accumulatedTime = 0f;
            _accumulatedFrames = 0;

            if (Time.unscaledTime - _lastChangeTime < SettleSeconds) return;

            float load = AverageFrameMs / _frameBudget;

            if (load > DowngradeThreshold && _currentTier < _tiers.Length - 1)
            {
                _currentTier++;
                ApplyTier();
                if (LogChanges)
                    Debug.Log($"[WeatherVR] Frame time {AverageFrameMs:F1} ms " +
                              $"({load:P0} of budget) — dropping to {CurrentTierName}.");
            }
            else if (load < UpgradeThreshold && _currentTier > 0)
            {
                _currentTier--;
                ApplyTier();
                if (LogChanges)
                    Debug.Log($"[WeatherVR] Frame time {AverageFrameMs:F1} ms " +
                              $"({load:P0} of budget) — restoring {CurrentTierName}.");
            }
        }

        void ApplyTier()
        {
            _lastChangeTime = Time.unscaledTime;
            var tier = _tiers[_currentTier];
            Clouds?.SetQuality(tier.MarchSteps, tier.LightSteps);
        }

        /// <summary>Pins quality to a tier index and stops automatic adjustment.</summary>
        public void ForceTier(int index)
        {
            if (_tiers == null) return;
            _currentTier = Mathf.Clamp(index, 0, _tiers.Length - 1);
            ApplyTier();
            enabled = false;
        }
    }
}
