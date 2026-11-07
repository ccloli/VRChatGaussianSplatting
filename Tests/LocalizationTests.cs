#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Tests
{
    public class LocalizationTests
    {
        [TestCase(SystemLanguage.English, "English")]
        [TestCase(SystemLanguage.Japanese, "日本語")]
        [TestCase(SystemLanguage.ChineseSimplified, "简体中文")]
        [TestCase(SystemLanguage.Chinese, "简体中文")]
        [TestCase(SystemLanguage.ChineseTraditional, "English")]
        public void EditorLanguageUsesMatchingTranslation(SystemLanguage language, string expected)
        {
            Assert.AreEqual(expected, GSEditorText.Select(language, "English", "日本語", "简体中文"));
        }

        [Test]
        public void FontsReferenceEachOtherAsFirstFallback()
        {
            TMP_FontAsset japanese = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath("22f36b798d4a5f7428f61e404f43717b"));
            TMP_FontAsset chinese = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath("f76ee1974fbcc624098cb6a8a964d2dc"));
            Assert.NotNull(japanese);
            Assert.NotNull(chinese);
            Assert.AreEqual(AtlasPopulationMode.Dynamic, japanese.atlasPopulationMode);
            Assert.AreEqual(AtlasPopulationMode.Dynamic, chinese.atlasPopulationMode);
            Assert.IsTrue(japanese.isMultiAtlasTexturesEnabled);
            Assert.IsTrue(chinese.isMultiAtlasTexturesEnabled);
            Assert.AreSame(chinese, japanese.fallbackFontAssetTable[0]);
            Assert.AreSame(japanese, chinese.fallbackFontAssetTable[0]);
            Assert.AreEqual("NotoSansSC-VF SDF", chinese.name);
        }

        [Test]
        public void EditorGlyphCollectionPreservesSupplementaryCodePoints()
        {
            System.Type builder = System.Type.GetType("GaussianSplatting.Editor.GaussianSplatUiBuilder, GaussianSplatting.Editor");
            Assert.NotNull(builder);
            MethodInfo getCodePoints = builder.GetMethod("GetCodePoints", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(getCodePoints);
            uint[] result = (uint[])getCodePoints.Invoke(null, new object[] { "A\U00020BB7\nA" });
            CollectionAssert.AreEqual(new uint[] { 65, 0x20BB7 }, result);
        }

        [Test]
        public void RuntimeTextSwitchesFontAndRestoresPlainText()
        {
            GameObject root = new GameObject("Localization test", typeof(RectTransform), typeof(Canvas));
            GameObject child = new GameObject("Label", typeof(RectTransform));
            child.transform.SetParent(root.transform);
            try
            {
                GaussianSplatRendererUI ui = root.AddComponent<GaussianSplatRendererUI>();
                TextMeshProUGUI label = child.AddComponent<TextMeshProUGUI>();
                TMP_FontAsset japanese = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath("22f36b798d4a5f7428f61e404f43717b"));
                TMP_FontAsset chinese = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath("f76ee1974fbcc624098cb6a8a964d2dc"));
                Assert.AreSame(chinese, Resources.Load<TMP_FontAsset>(TMP_Settings.defaultFontAssetPath + "NotoSansSC-VF SDF"));
                label.font = japanese;
                FieldInfo language = typeof(GaussianSplatRendererUI).GetField("selectedLanguage", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo setText = typeof(GaussianSplatRendererUI).GetMethod("SetText", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(language);
                Assert.NotNull(setText);

                language.SetValue(ui, 2);
                setText.Invoke(ui, new object[] { label, "画廊" });
                Assert.AreEqual("<font=\"NotoSansSC-VF SDF\">画廊</font>", label.text);
                label.ForceMeshUpdate(true);
                Assert.AreSame(chinese, label.textInfo.characterInfo[0].fontAsset);
                setText.Invoke(ui, new object[] { label, "画廊" });
                Assert.AreEqual("<font=\"NotoSansSC-VF SDF\">画廊</font>", label.text);
                language.SetValue(ui, 1);
                setText.Invoke(ui, new object[] { label, "ギャラリー" });
                Assert.AreEqual("ギャラリー", label.text);
                label.ForceMeshUpdate(true);
                Assert.AreSame(japanese, label.textInfo.characterInfo[0].fontAsset);

                // These code points exist in only one of the supplied source fonts.
                Assert.IsFalse(japanese.HasCharacter('专', false, true));
                Assert.IsTrue(chinese.HasCharacter('专', false, true));
                setText.Invoke(ui, new object[] { label, "专" });
                label.ForceMeshUpdate(true);
                Assert.AreSame(chinese, label.textInfo.characterInfo[0].fontAsset);
                Assert.IsFalse(chinese.HasCharacter('\uF904', false, true));
                Assert.IsTrue(japanese.HasCharacter('\uF904', false, true));
                language.SetValue(ui, 2);
                setText.Invoke(ui, new object[] { label, "\uF904" });
                label.ForceMeshUpdate(true);
                Assert.AreSame(japanese, label.textInfo.characterInfo[0].fontAsset);

                setText.Invoke(ui, new object[] { label, "" });
                Assert.AreEqual("", label.text);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
#endif
