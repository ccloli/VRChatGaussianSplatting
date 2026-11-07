using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GaussianSplatting.Tests
{
    public class GaussianSplatBudgetTests
    {
        const int M = 1024 * 1024;
        Scene scene;
        GameObject rendererObject;
        GaussianSplatRenderer renderer;

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            rendererObject = new GameObject("LOD budget test");
            EditorSceneManager.MoveGameObjectToScene(rendererObject, scene);
            renderer = rendererObject.AddComponent<GaussianSplatRenderer>();
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(scene);
        }

        void SetField(string name, object value)
        {
            FieldInfo field = typeof(GaussianSplatRenderer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name);
            field.SetValue(renderer, value);
        }

        int RequestedBudget()
        {
            return (int)typeof(GaussianSplatRenderer).GetField("requestedCombinedLodSplatBudget", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(renderer);
        }

        [Test]
        public void LegacySerializedCaps_DefaultToInheritedGlobalCaps()
        {
            EditorJsonUtility.FromJsonOverwrite("{\"combinedLodSplatBudgetPC\":3000000,\"combinedLodSplatBudgetAndroid\":200000}", renderer);
            Assert.AreEqual(3000000, renderer.GetCombinedLodSplatBudgetSliderMaxPC());
            Assert.AreEqual(200000, renderer.GetCombinedLodSplatBudgetSliderMaxAndroid());
            renderer.SetQualityVeryLow();
            Assert.AreEqual(750000, RequestedBudget());
            renderer.SetQualityHigh();
            Assert.AreEqual(3000000, RequestedBudget());

            EditorJsonUtility.FromJsonOverwrite("{\"combinedLodSplatBudgetPC\":0}", renderer);
            Assert.AreEqual(0, renderer.GetCombinedLodSplatBudgetSliderMaxPC());
            renderer.SetQualityVeryLow();
            Assert.AreEqual(0, RequestedBudget());
            Assert.AreEqual(0, renderer.GetEffectiveCombinedLodSplatBudget());
        }

        [Test]
        public void GlobalCapExtendsSliderWithoutChangingPresetsOrStartup()
        {
            SetField("combinedLodGlobalSplatBudgetPC", 8 * M);
            SetField("combinedLodGlobalSplatBudgetAndroid", 1 * M);
            Assert.AreEqual(8 * M, renderer.GetCombinedLodSplatBudgetSliderMax());
            Assert.AreEqual(1 * M, renderer.GetCombinedLodSplatBudgetSliderMaxAndroid());

            renderer.SetQualityVeryLow();
            Assert.AreEqual(1 * M, RequestedBudget());
            Assert.AreEqual(0.15f, renderer.alphaCull);
            renderer.SetQualityLow();
            Assert.AreEqual(2 * M, RequestedBudget());
            Assert.AreEqual(0.1f, renderer.alphaCutoff);
            renderer.SetQualityMedium();
            Assert.AreEqual(3 * M, RequestedBudget());
            renderer.SetQualityHigh();
            Assert.AreEqual(4 * M, RequestedBudget());

            renderer.SetEffectiveCombinedLodSplatBudget(10 * M);
            Assert.AreEqual(8 * M, RequestedBudget());
            renderer.SetQualityLow();
            Assert.AreEqual(2 * M, RequestedBudget());

            SetField("startupQualityPreset", 2);
            Assert.AreEqual(3 * M, renderer.GetEffectiveCombinedLodSplatBudget());
            SetField("startupQualityPreset", -1);
            SetField("startupLodCapacity", 0.5f);
            Assert.AreEqual(2 * M, renderer.GetEffectiveCombinedLodSplatBudget());
            typeof(GaussianSplatRenderer).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(renderer, null);
            Assert.AreEqual(2 * M, RequestedBudget());
        }

        [Test]
        public void SmallOrZeroPresetCapPreservesLegacyBehavior()
        {
            SetField("combinedLodGlobalSplatBudgetPC", 8 * M);
            renderer.SetCombinedLodSplatBudgetPC(32000);
            Assert.AreEqual(32000, renderer.GetCombinedLodSplatBudgetSliderMin());
            renderer.SetQualityVeryLow();
            Assert.AreEqual(32000, RequestedBudget());

            renderer.SetCombinedLodSplatBudgetPC(0);
            Assert.AreEqual(0, renderer.GetCombinedLodSplatBudgetSliderMin());
            Assert.AreEqual(8 * M, renderer.GetCombinedLodSplatBudgetSliderMax());
            renderer.SetQualityVeryLow();
            Assert.AreEqual(0, RequestedBudget());
            renderer.SetQualityHigh();
            Assert.AreEqual(0, RequestedBudget());
            Assert.AreEqual(0, renderer.GetEffectiveCombinedLodSplatBudget());
            renderer.SetEffectiveCombinedLodSplatBudget(6 * M);
            Assert.AreEqual(6 * M, RequestedBudget());
            renderer.SetEffectiveCombinedLodSplatBudget(0);
            Assert.AreEqual(0, RequestedBudget());
        }

        [Test]
        public void GlobalCapDoesNotLowerPresetAndIsLimitedToRendererCapacity()
        {
            SetField("combinedLodGlobalSplatBudgetPC", 2 * M);
            Assert.AreEqual(4 * M, renderer.GetCombinedLodSplatBudgetSliderMaxPC());
            SetField("combinedLodGlobalSplatBudgetPC", 20 * M);
            Assert.AreEqual(16 * M, renderer.GetCombinedLodSplatBudgetSliderMaxPC());
        }

        [Test]
        public void ResourceBudgetCoversBothPlatformsAndUnlimitedMode()
        {
            SetField("combinedLodGlobalSplatBudgetAndroid", 8 * M);
            Assert.AreEqual(8 * M, renderer.GetMaximumReachableCombinedLodSplatBudget());
            int reachable = GaussianSplatRenderer.ComputeCombinedTierCount(12 * M, renderer.GetMaximumReachableCombinedLodSplatBudget());
            Assert.AreEqual(8 * M, reachable);
            Assert.AreEqual(5, GaussianSplatRTPool.PassesToCover(reachable));

            renderer.SetCombinedLodSplatBudgetPC(0);
            Assert.AreEqual(0, renderer.GetMaximumReachableCombinedLodSplatBudget());
            Assert.AreEqual(12 * M, GaussianSplatRenderer.ComputeCombinedTierCount(12 * M, renderer.GetMaximumReachableCombinedLodSplatBudget()));
            renderer.SetCombinedLodSplatBudgetPC(4 * M);
            renderer.SetCombinedLodSplatBudgetAndroid(0);
            Assert.AreEqual(0, renderer.GetMaximumReachableCombinedLodSplatBudget());
        }

        [Test]
        public void UnlimitedAndPositiveCapsHaveDistinctSliderPositions()
        {
            renderer.SetCombinedLodSplatBudgetPC(0);
            SetField("combinedLodGlobalSplatBudgetPC", 8 * M);
            GameObject uiObject = new GameObject("LOD slider test");
            EditorSceneManager.MoveGameObjectToScene(uiObject, scene);
            GaussianSplatRendererUI ui = uiObject.AddComponent<GaussianSplatRendererUI>();
            ui.gaussianSplatRenderer = renderer;
            MethodInfo toSlider = typeof(GaussianSplatRendererUI).GetMethod("ToLodSplatCapSliderValue", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo fromSlider = typeof(GaussianSplatRendererUI).GetMethod("FromLodSplatCapSliderValue", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.AreEqual(0.0f, (float)toSlider.Invoke(ui, new object[] { 0.0f }));
            float positivePosition = (float)toSlider.Invoke(ui, new object[] { 10000.0f });
            Assert.Greater(positivePosition, 0.0f);
            Assert.AreEqual(10000.0f, (float)fromSlider.Invoke(ui, new object[] { positivePosition }), 0.1f);
            Assert.AreEqual(0.0f, (float)fromSlider.Invoke(ui, new object[] { 0.0f }));
            Assert.AreEqual(1.0f, (float)toSlider.Invoke(ui, new object[] { 8.0f * M }), 0.00001f);
        }
    }
}
