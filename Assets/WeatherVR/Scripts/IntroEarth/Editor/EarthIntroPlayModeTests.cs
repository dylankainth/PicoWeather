#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using WeatherVR.Core;
using WeatherVR.UI.Carousel;

namespace WeatherVR.IntroEarth.Editor
{
    /// <summary>
    /// Exercises the real launch lifecycle rather than only compiling the feature:
    /// the globe must exist while London is hidden, then clean itself up and restore
    /// both the map and carousel.
    /// </summary>
    public sealed class EarthIntroPlayModeTests
    {
        [UnityTest]
        public IEnumerator LaunchSequenceRevealsTheExistingLondonExperience()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/WeatherVR.unity");
            yield return new EnterPlayMode();
            yield return null;

            var controller = Object.FindObjectOfType<WeatherSceneController>();
            var intro = Object.FindObjectOfType<EarthIntroFeature>();

            Assert.That(controller, Is.Not.Null, "WeatherVR controller did not load.");
            Assert.That(intro, Is.Not.Null, "Earth intro did not self-install.");
            Assert.That(controller.MapRoot.gameObject.activeSelf, Is.False,
                "London was visible behind the launch globe.");
            GameObject earth = GameObject.Find("Low Detail Earth");
            Assert.That(earth, Is.Not.Null, "The low-poly Earth was not created.");

            MeshFilter[] filters = earth.GetComponentsInChildren<MeshFilter>();
            int vertexCount = 0;
            foreach (MeshFilter filter in filters)
                if (filter.sharedMesh != null)
                    vertexCount += filter.sharedMesh.vertexCount;

            Assert.That(filters.Length, Is.EqualTo(3),
                "The globe should remain three tiny mesh objects.");
            Assert.That(vertexCount, Is.LessThan(1800),
                "The globe exceeded its deliberately tiny vertex budget.");

            foreach (Renderer renderer in earth.GetComponentsInChildren<Renderer>())
            {
                Assert.That(renderer.sharedMaterial.HasProperty("_MainTex"), Is.False,
                    "The globe shader unexpectedly gained a bitmap texture slot.");
            }

            float deadline = Time.realtimeSinceStartup + 12f;
            while (Object.FindObjectOfType<EarthIntroFeature>() != null &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(Object.FindObjectOfType<EarthIntroFeature>(), Is.Null,
                "Earth intro did not finish within twelve seconds.");
            Assert.That(GameObject.Find("Low Detail Earth"), Is.Null,
                "Earth meshes were not removed after the transition.");
            Assert.That(controller.MapRoot.gameObject.activeSelf, Is.True,
                "The repaired London WeatherVR root was not restored.");

            var carousel =
                Object.FindObjectOfType<WeatherCarouselFeature>(includeInactive: true);
            Assert.That(carousel, Is.Not.Null, "Carousel feature disappeared.");
            Assert.That(carousel.enabled, Is.True,
                "Carousel was not re-enabled after the London reveal.");

            yield return new ExitPlayMode();
        }
    }
}
#endif
