using NUnit.Framework;
using UnityEngine;
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
    }
}
