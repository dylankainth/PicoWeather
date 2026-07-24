using UnityEngine;

namespace WeatherVR.Audio
{
    /// <summary>
    /// Synthesises the entire soundscape at runtime.
    ///
    /// The project ships no audio assets, and a hackathon build should not depend on
    /// sourcing licensed thunder samples. Everything here is generated from noise
    /// and filters, which has a genuine advantage over samples for this app: thunder
    /// can be rendered *for a specific distance*, so a strike 40 km away really is a
    /// different sound from one overhead rather than the same sample turned down.
    /// </summary>
    public static class ProceduralAudio
    {
        public const int SampleRate = 44100;

        /// <summary>
        /// Thunder for a strike at <paramref name="distanceKm"/>.
        ///
        /// The physics that matters: high frequencies are absorbed by air far faster
        /// than low ones, and the sound from different parts of a several-kilometre
        /// channel arrives at different times. So a near strike is a sharp crack with
        /// a short tail, and a distant one is a low rumble with no crack at all and a
        /// tail lasting many seconds.
        /// </summary>
        public static AudioClip CreateThunder(float distanceKm, int seed)
        {
            distanceKm = Mathf.Clamp(distanceKm, 0.2f, 60f);
            float proximity = Mathf.Clamp01(1f - distanceKm / 25f);

            // Distant thunder rumbles for longer: the channel's path-length spread
            // smears arrival times.
            float duration = Mathf.Lerp(6.5f, 1.8f, proximity);
            int sampleCount = Mathf.CeilToInt(duration * SampleRate);
            var samples = new float[sampleCount];

            var random = new System.Random(seed);
            float NextNoise() => (float)(random.NextDouble() * 2.0 - 1.0);

            // Air absorption: the cutoff of the one-pole low-pass, in Hz.
            float cutoff = Mathf.Lerp(180f, 4200f, proximity * proximity);
            float lowpassCoeff = Mathf.Clamp01(1f - Mathf.Exp(-2f * Mathf.PI * cutoff / SampleRate));

            // The crack only exists nearby; beyond ~10 km there is nothing left of it.
            float crackAmount = Mathf.Pow(proximity, 2.2f);
            float crackDecay = Mathf.Lerp(60f, 14f, proximity);

            float lowpassState = 0f;
            float rumbleState = 0f;
            float dcState = 0f;

            // A handful of discrete arrivals: the tortuous channel radiates from many
            // points at many ranges, which is what turns one bang into a rolling peal.
            const int arrivalCount = 5;
            var arrivalTime = new float[arrivalCount];
            var arrivalGain = new float[arrivalCount];
            for (int i = 0; i < arrivalCount; i++)
            {
                arrivalTime[i] = (float)random.NextDouble() * duration * 0.55f;
                arrivalGain[i] = Mathf.Lerp(0.35f, 1f, (float)random.NextDouble()) / (1f + i * 0.6f);
            }

            for (int n = 0; n < sampleCount; n++)
            {
                float t = n / (float)SampleRate;
                float noise = NextNoise();

                // --- rumble bed: heavily low-passed noise, slowly modulated --------
                lowpassState += lowpassCoeff * (noise - lowpassState);
                // A second pole steepens the roll-off so distant thunder has no hiss.
                rumbleState += lowpassCoeff * 0.55f * (lowpassState - rumbleState);

                float envelope = 0f;
                for (int i = 0; i < arrivalCount; i++)
                {
                    float dt = t - arrivalTime[i];
                    if (dt < 0f) continue;
                    // Fast attack, exponential decay.
                    envelope += arrivalGain[i] * Mathf.Exp(-dt * Mathf.Lerp(0.7f, 2.2f, proximity))
                                               * (1f - Mathf.Exp(-dt * 45f));
                }
                envelope = Mathf.Min(envelope, 1.6f);

                // Slow sub-audio wobble gives the rolling quality.
                float wobble = 0.72f + 0.28f * Mathf.Sin(t * 2.1f + Mathf.Sin(t * 0.7f) * 2f);

                float sample = rumbleState * envelope * wobble * 3.2f;

                // --- initial crack --------------------------------------------------
                if (crackAmount > 0.001f && t < 0.6f)
                {
                    float crackEnvelope = Mathf.Exp(-t * crackDecay) * (1f - Mathf.Exp(-t * 900f));
                    sample += noise * crackEnvelope * crackAmount * 0.9f;
                }

                samples[n] = sample;
            }

            RemoveDc(samples, ref dcState);
            Normalize(samples, 0.92f);
            FadeOut(samples, Mathf.Min(0.35f, duration * 0.2f));

            var clip = AudioClip.Create($"Thunder_{distanceKm:F0}km", sampleCount, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Seamlessly looping wind bed: pink-ish noise with slow gusting.
        /// </summary>
        public static AudioClip CreateWindLoop(float durationSeconds, int seed)
        {
            int loopSamples = Mathf.CeilToInt(Mathf.Max(2f, durationSeconds) * SampleRate);
            int fade = Mathf.Min(loopSamples / 4, Mathf.CeilToInt(0.25f * SampleRate));

            // Generate `fade` extra samples past the loop point: that surplus is what
            // gets blended into the head, and is then discarded.
            var samples = new float[loopSamples + fade];
            var random = new System.Random(seed);

            // Three one-pole stages approximate a pink spectrum well enough for a bed.
            float s1 = 0f, s2 = 0f, s3 = 0f;
            float c1 = Coefficient(1200f), c2 = Coefficient(320f), c3 = Coefficient(70f);

            // Gust periods are whole multiples of the *final* loop length, so the
            // modulation meets itself exactly at the seam.
            float period = loopSamples / (float)SampleRate;

            for (int n = 0; n < samples.Length; n++)
            {
                float t = n / (float)SampleRate;
                float noise = (float)(random.NextDouble() * 2.0 - 1.0);

                s1 += c1 * (noise - s1);
                s2 += c2 * (s1 - s2);
                s3 += c3 * (s2 - s3);

                float gust = 0.55f
                           + 0.28f * Mathf.Sin(2f * Mathf.PI * t / period * 3f)
                           + 0.17f * Mathf.Sin(2f * Mathf.PI * t / period * 7f + 1.3f);

                samples[n] = (s1 * 0.25f + s2 * 0.5f + s3 * 1.6f) * gust;
            }

            CrossfadeLoop(samples, loopSamples, fade);
            System.Array.Resize(ref samples, loopSamples);
            Normalize(samples, 0.55f);

            var clip = AudioClip.Create("WindLoop", loopSamples, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Seamlessly looping rain bed: bright filtered noise with a hint of splatter.
        /// </summary>
        public static AudioClip CreateRainLoop(float durationSeconds, int seed)
        {
            int loopSamples = Mathf.CeilToInt(Mathf.Max(2f, durationSeconds) * SampleRate);
            int fade = Mathf.Min(loopSamples / 4, Mathf.CeilToInt(0.2f * SampleRate));

            var samples = new float[loopSamples + fade];
            var random = new System.Random(seed);

            float highState = 0f, bandState = 0f;
            float cHigh = Coefficient(7000f), cBand = Coefficient(2200f);

            for (int n = 0; n < samples.Length; n++)
            {
                float noise = (float)(random.NextDouble() * 2.0 - 1.0);

                highState += cHigh * (noise - highState);
                bandState += cBand * (highState - bandState);

                // Band-pass = low-passed minus more-low-passed.
                float band = highState - bandState;

                // Sparse louder droplets stop it sounding like plain hiss.
                float droplet = random.NextDouble() < 0.0016 ? noise * 2.4f : 0f;

                samples[n] = band * 1.5f + bandState * 0.35f + droplet;
            }

            CrossfadeLoop(samples, loopSamples, fade);
            System.Array.Resize(ref samples, loopSamples);
            Normalize(samples, 0.5f);

            var clip = AudioClip.Create("RainLoop", loopSamples, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        // ------------------------------------------------------------- utilities

        static float Coefficient(float cutoffHz)
            => Mathf.Clamp01(1f - Mathf.Exp(-2f * Mathf.PI * cutoffHz / SampleRate));

        static void Normalize(float[] samples, float peak)
        {
            float max = 0f;
            foreach (float s in samples) max = Mathf.Max(max, Mathf.Abs(s));
            if (max < 1e-6f) return;

            float gain = peak / max;
            for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
        }

        /// <summary>One-pole DC blocker; a rumble with an offset clicks on playback.</summary>
        static void RemoveDc(float[] samples, ref float state)
        {
            const float coefficient = 0.9995f;
            float previousInput = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float input = samples[i];
                state = input - previousInput + coefficient * state;
                previousInput = input;
                samples[i] = state;
            }
        }

        static void FadeOut(float[] samples, float seconds)
        {
            int fadeSamples = Mathf.Min(samples.Length, Mathf.CeilToInt(seconds * SampleRate));
            if (fadeSamples <= 1) return;

            int start = samples.Length - fadeSamples;
            for (int i = 0; i < fadeSamples; i++)
            {
                float t = 1f - i / (float)(fadeSamples - 1);
                samples[start + i] *= t * t;
            }
        }

        /// <summary>
        /// Blends the surplus material at <c>[loopSamples, loopSamples + fade)</c> into
        /// the head so that <c>[0, loopSamples)</c> loops without a seam. The caller
        /// must then truncate the buffer to <paramref name="loopSamples"/>.
        ///
        /// Truncating is the part that matters, and getting it wrong is audible. The
        /// first version of this faded the tail to silence and *kept* it, so every
        /// wrap played down to digital zero and then jumped to a full-scale sample —
        /// a one-sample step that is a broadband impulse, i.e. a click every time the
        /// bed looped. Discarding the surplus instead leaves sample
        /// <c>loopSamples - 1</c> flowing into sample <c>loopSamples</c>, which were
        /// consecutive in the generated signal, so the seam is continuous.
        /// </summary>
        static void CrossfadeLoop(float[] samples, int loopSamples, int fade)
        {
            if (fade <= 1 || loopSamples + fade > samples.Length) return;

            for (int i = 0; i < fade; i++)
            {
                float t = i / (float)fade;   // 0 at the seam, 1 by the end of the fade
                samples[i] = samples[i] * t + samples[loopSamples + i] * (1f - t);
            }
        }
    }
}
