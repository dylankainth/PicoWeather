using NUnit.Framework;
using UnityEngine;
using WeatherVR.Core;
using WeatherVR.Interaction;
using WeatherVR.UI.Carousel;
using WeatherVR.Weather;

namespace WeatherVR.EditorTools.Tests
{
    public sealed class VisualComfortTests
    {
        [Test]
        public void SkyShaderExposesCalmPrismControls()
        {
            Shader shader = Shader.Find("WeatherVR/StudioSky");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            try
            {
                Assert.That(material.HasProperty("_PrismColor"), Is.True);
                Assert.That(material.HasProperty("_PrismStrength"), Is.True);
                Assert.That(material.HasProperty("_LatticeStrength"), Is.True);
                Assert.That(material.GetFloat("_PrismStrength"), Is.InRange(0.08f, 0.25f));
                Assert.That(material.GetFloat("_LatticeStrength"), Is.InRange(0.02f, 0.07f));
                Assert.That(material.GetFloat("_SunGlow"), Is.LessThanOrEqualTo(1f));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void CarouselStillMapsEveryWeatherCase()
        {
            WeatherCarouselDataset dataset = null;
            var provider = new SceneKindCarouselDataProvider();
            var load = provider.Load(null, value => dataset = value, Assert.Fail);
            while (load.MoveNext()) { }

            Assert.That(dataset, Is.Not.Null);
            Assert.That(dataset.LocationEnglish, Is.EqualTo("LONDON"));
            Assert.That(dataset.LocationChinese, Is.EqualTo("伦敦"));
            Assert.That(dataset.Items, Has.Length.EqualTo(WeatherScene.AllKinds.Length));

            for (int i = 0; i < dataset.Items.Length; i++)
            {
                Assert.That(dataset.Items[i].SceneKind, Is.EqualTo((int)WeatherScene.AllKinds[i]));
                Assert.That(dataset.Items[i].DayEnglish, Is.Not.Empty);
                Assert.That(dataset.Items[i].DayChinese, Is.Not.Empty);
            }
        }

        [Test]
        public void WeatherProfilesRemainWithinComfortableRuntimeRanges()
        {
            foreach (WeatherSceneKind kind in WeatherScene.AllKinds)
            {
                WeatherSceneProfile profile = WeatherScene.Default(kind);
                Assert.That(profile.CloudAmount, Is.InRange(0f, 1f), kind.ToString());
                Assert.That(profile.CloudDarkness, Is.InRange(0f, 1f), kind.ToString());
                Assert.That(profile.PrecipIntensity, Is.InRange(0f, 1f), kind.ToString());
                Assert.That(profile.SunIntensity, Is.InRange(0.35f, 1.65f), kind.ToString());
                Assert.That(profile.FogDensity, Is.InRange(0f, 0.32f), kind.ToString());
            }
        }

        [Test]
        public void PrecipitationStylesAreLargeAndCoverEverySliderWeatherCase()
        {
            // Derived from the live config and the shipped cloud altitude rather than
            // hardcoded: this was 0.056, which was only right for a 900 m cloud base on a
            // 2 m map and silently stopped describing the app when either moved.
            float cloudBaseMap = AppConfig.Instance.AltitudeToMapUnits(
                WeatherVisuals.CloudBaseMeters);
            Assert.That(cloudBaseMap, Is.GreaterThan(0f), "cloud base must sit above the map plane");

            foreach (WeatherSceneKind kind in WeatherScene.AllKinds)
            {
                WeatherSceneProfile profile = WeatherScene.Default(kind);
                WeatherVisuals.PrecipitationStyle style =
                    WeatherVisuals.ResolvePrecipitationStyle(
                        profile.Precip,
                        profile.PrecipIntensity,
                        cloudBaseMap,
                        profile.WindMs);

                if (profile.Precip == PrecipKind.None)
                {
                    Assert.That(style.Enabled, Is.False, kind.ToString());
                    continue;
                }

                Assert.That(style.Enabled, Is.True, kind.ToString());
                Assert.That(style.MaxSize, Is.GreaterThan(style.MinSize), kind.ToString());
                Assert.That(style.EmissionRate, Is.GreaterThan(0f), kind.ToString());
                Assert.That(
                    style.MinSize,
                    Is.GreaterThanOrEqualTo(style.Snow ? 0.020f : 0.0065f),
                    kind.ToString());
            }
        }

        [Test]
        public void ControllerLocomotionUsesRadialDeadzoneAndHeadRelativeAxes()
        {
            Assert.That(
                ControllerLocomotion.ApplyRadialDeadzone(new Vector2(0.1f, 0.1f), 0.18f),
                Is.EqualTo(Vector2.zero));

            Vector2 fullStick =
                ControllerLocomotion.ApplyRadialDeadzone(Vector2.up, 0.18f);
            Assert.That(Vector2.Distance(fullStick, Vector2.up), Is.LessThan(1e-5f));

            Vector3 pitchedForward = new Vector3(0f, 0.8f, 0.6f);
            Vector3 strafe =
                ControllerLocomotion.ComputeMoveDirection(Vector2.right, pitchedForward);
            Vector3 forward =
                ControllerLocomotion.ComputeMoveDirection(Vector2.up, pitchedForward);

            Assert.That(Vector3.Distance(strafe, Vector3.right), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(forward, Vector3.forward), Is.LessThan(1e-5f));

            float turn = ControllerLocomotion.ComputeSmoothTurnDegrees(0.5f, 80f, 0.25f);
            Assert.That(turn, Is.EqualTo(10f).Within(1e-5f));

            Vector3 zoomIn = ControllerLocomotion.ComputeZoomDirection(
                1f, new Vector3(2f, 1.6f, 0f), Vector3.zero, Vector3.forward);
            Vector3 zoomOut = ControllerLocomotion.ComputeZoomDirection(
                -1f, new Vector3(2f, 1.6f, 0f), Vector3.zero, Vector3.forward);
            Assert.That(Vector3.Distance(zoomIn, Vector3.left), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(zoomOut, Vector3.right), Is.LessThan(1e-5f));
        }
    }
}
