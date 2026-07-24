using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WeatherVR.Audio
{
    /// <summary>
    /// Spatialised thunder playback.
    ///
    /// Clips are synthesised once per distance bucket rather than per strike:
    /// generating a six-second 44.1 kHz buffer costs a few milliseconds, which is
    /// fine at load but not fine in the middle of a storm. Buckets mean a strike on
    /// the far side of the map genuinely sounds different from one at your elbow —
    /// low, long and crackless — instead of being the same sample at lower volume.
    /// </summary>
    public class ThunderAudio : MonoBehaviour
    {
        [Tooltip("Concurrent thunder voices. Distant thunder runs 6.5 s and an active " +
                 "storm strikes every ~3 s, so roughly 2-3 overlap on average; 6 leaves " +
                 "headroom for a Poisson burst before any voice has to be stolen.")]
        [Range(1, 8)] public int VoiceCount = 6;

        [Tooltip("Distance buckets in real kilometres. One clip is synthesised per bucket.")]
        public float[] DistanceBucketsKm = { 1f, 5f, 12f, 25f, 45f };

        [Tooltip("Overall thunder level.")]
        [Range(0f, 1f)] public float Volume = 0.85f;

        [Tooltip("World-space distance at which a thunder voice reaches full attenuation.")]
        public float MaxAudibleWorldDistance = 12f;

        [Tooltip("Seed for the noise the clips are built from.")]
        public int Seed = 20260723;

        readonly List<AudioSource> _voices = new List<AudioSource>();
        AudioClip[] _clips;
        int _nextVoice;

        void Awake()
        {
            BuildClips();
            BuildVoices();
        }

        void BuildClips()
        {
            if (DistanceBucketsKm == null || DistanceBucketsKm.Length == 0)
                DistanceBucketsKm = new[] { 1f, 5f, 12f, 25f, 45f };

            _clips = new AudioClip[DistanceBucketsKm.Length];
            for (int i = 0; i < DistanceBucketsKm.Length; i++)
                _clips[i] = ProceduralAudio.CreateThunder(DistanceBucketsKm[i], Seed + i * 7919);
        }

        void BuildVoices()
        {
            for (int i = 0; i < VoiceCount; i++)
            {
                var voiceObject = new GameObject($"ThunderVoice{i}");
                voiceObject.transform.SetParent(transform, worldPositionStays: false);

                var source = voiceObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 1f;                    // fully 3D
                source.rolloffMode = AudioRolloffMode.Linear; // predictable at tabletop scale
                source.minDistance = 0.4f;
                source.maxDistance = MaxAudibleWorldDistance;
                source.dopplerLevel = 0f;                    // a stationary sound source
                source.spread = 60f;                         // thunder is not a point source

                _voices.Add(source);
            }
        }

        /// <summary>
        /// Queues thunder for a strike.
        /// </summary>
        /// <param name="worldPosition">Where the strike terminated, in world space.</param>
        /// <param name="realDistanceKm">
        /// Distance in real-world kilometres, used to choose the timbre. This is the
        /// distance the viewer *would* be from the strike if they were standing in
        /// the scene at 1:1, not the tabletop distance.
        /// </param>
        /// <param name="delaySeconds">Time-of-flight delay, already compressed.</param>
        public void PlayThunder(Vector3 worldPosition, float realDistanceKm, float delaySeconds)
        {
            if (_voices.Count == 0 || _clips == null || _clips.Length == 0) return;

            StartCoroutine(PlayDelayed(worldPosition, realDistanceKm, Mathf.Max(0f, delaySeconds)));
        }

        /// <summary>
        /// Picks a voice that is not currently sounding, falling back to round-robin.
        ///
        /// This matters more than it looks. Distant thunder runs 6.5 s and strikes
        /// arrive every ~3 s, so a naive round-robin reuses a voice that is still
        /// mid-rumble; calling Play() on it restarts the clip instantly and the
        /// waveform jumps from the middle of one rumble to the start of another — an
        /// audible click. Choosing a free voice avoids the discontinuity, and the
        /// choice is made *after* the time-of-flight delay, when it is actually known
        /// which voices are busy.
        /// </summary>
        AudioSource AcquireVoice()
        {
            foreach (var voice in _voices)
                if (voice != null && !voice.isPlaying) return voice;

            var stolen = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % _voices.Count;
            return stolen;
        }

        IEnumerator PlayDelayed(Vector3 worldPosition, float realDistanceKm, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);

            var source = AcquireVoice();
            if (source == null) yield break;

            source.transform.position = worldPosition;
            source.clip = SelectClip(realDistanceKm);

            // Loudness falls off with distance on top of the spatial rolloff, because
            // the *source* is genuinely quieter by the time it has travelled 40 km.
            float attenuation = Mathf.Clamp01(1f - realDistanceKm / 60f);
            source.volume = Volume * Mathf.Lerp(0.25f, 1f, attenuation * attenuation);

            // Slight pitch variation so repeated strikes from one bucket do not sound
            // like the same recording played twice.
            source.pitch = Random.Range(0.93f, 1.07f);

            source.Play();
        }

        AudioClip SelectClip(float realDistanceKm)
        {
            int best = 0;
            float bestDelta = float.MaxValue;
            for (int i = 0; i < DistanceBucketsKm.Length; i++)
            {
                float delta = Mathf.Abs(DistanceBucketsKm[i] - realDistanceKm);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = i;
                }
            }
            return _clips[best];
        }

        void OnDestroy()
        {
            if (_clips == null) return;
            foreach (var clip in _clips)
                if (clip != null) Destroy(clip);
        }
    }
}
