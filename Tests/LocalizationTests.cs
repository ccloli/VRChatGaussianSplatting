#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

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
        public void RuntimeTextSwitchesDirectFontReferencesAndKeepsLayout()
        {
            GameObject root = new GameObject("Localization test", typeof(RectTransform), typeof(Canvas), typeof(HorizontalLayoutGroup));
            RectTransform rootRect = root.GetComponent<RectTransform>();
            rootRect.sizeDelta = new Vector2(300, 100);
            GameObject child = new GameObject("Label", typeof(RectTransform), typeof(LayoutElement));
            child.transform.SetParent(root.transform);
            try
            {
                GaussianSplatRendererUI ui = root.AddComponent<GaussianSplatRendererUI>();
                TextMeshProUGUI label = child.AddComponent<TextMeshProUGUI>();
                LayoutElement layout = child.GetComponent<LayoutElement>();
                layout.preferredWidth = 120;
                GameObject input = new GameObject("Inactive URL field", typeof(RectTransform));
                input.transform.SetParent(root.transform);
                input.SetActive(false);
                input.AddComponent<TMP_InputField>();
                GameObject inputText = new GameObject("Input text", typeof(RectTransform), typeof(TextMeshProUGUI));
                inputText.transform.SetParent(input.transform);
                TMP_FontAsset japanese = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath("22f36b798d4a5f7428f61e404f43717b"));
                TMP_FontAsset chinese = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath("f76ee1974fbcc624098cb6a8a964d2dc"));
                label.font = japanese;
                System.Type builder = System.Type.GetType("GaussianSplatting.Editor.GaussianSplatUiBuilder, GaussianSplatting.Editor");
                Assert.NotNull(builder);
                MethodInfo ensure = builder.GetMethod("EnsureChineseFontTexts", BindingFlags.Static | BindingFlags.NonPublic);
                FieldInfo language = typeof(GaussianSplatRendererUI).GetField("selectedLanguage", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo setText = typeof(GaussianSplatRendererUI).GetMethod("SetText", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo setActive = typeof(GaussianSplatRendererUI).GetMethod("SetActive", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo sync = typeof(GaussianSplatRendererUI).GetMethod("SyncLanguageFonts", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(ensure);
                Assert.NotNull(language);
                Assert.NotNull(setText);
                Assert.NotNull(setActive);
                Assert.NotNull(sync);
                Assert.IsTrue((bool)ensure.Invoke(null, new object[] { ui }));
                Assert.AreEqual(1, ui.chineseFontTexts.Length);
                TextMeshProUGUI chineseLabel = ui.chineseFontTexts[0];
                Assert.AreSame(chinese, chineseLabel.font);
                Assert.AreSame(child.transform.parent, chineseLabel.transform.parent);
                Assert.AreEqual(layout.preferredWidth, chineseLabel.GetComponent<LayoutElement>().preferredWidth);
                Assert.IsFalse(chineseLabel.gameObject.activeSelf);
                Assert.IsFalse((bool)ensure.Invoke(null, new object[] { ui }));
                LayoutRebuilder.ForceRebuildLayoutImmediate(rootRect);
                float japaneseWidth = label.rectTransform.rect.width;
                Assert.Greater(japaneseWidth, 0);

                language.SetValue(ui, 2);
                setText.Invoke(ui, new object[] { label, "画廊" });
                sync.Invoke(ui, null);
                Assert.AreEqual("画廊", label.text);
                Assert.AreEqual("画廊", chineseLabel.text);
                Assert.IsFalse(label.enabled);
                Assert.IsTrue(layout.ignoreLayout);
                Assert.IsTrue(chineseLabel.gameObject.activeSelf);
                LayoutRebuilder.ForceRebuildLayoutImmediate(rootRect);
                Assert.AreEqual(japaneseWidth, chineseLabel.rectTransform.rect.width, 0.01f);
                chineseLabel.ForceMeshUpdate(true);
                Assert.AreSame(chinese, chineseLabel.textInfo.characterInfo[0].fontAsset);

                setText.Invoke(ui, new object[] { label, "画廊 <color=red>测试</color>" });
                sync.Invoke(ui, null);
                Assert.AreEqual(label.text, chineseLabel.text);
                setActive.Invoke(ui, new object[] { label, false });
                Assert.IsFalse(chineseLabel.gameObject.activeSelf);
                setActive.Invoke(ui, new object[] { label, true });
                Assert.IsTrue(chineseLabel.gameObject.activeSelf);

                language.SetValue(ui, 1);
                setText.Invoke(ui, new object[] { label, "ギャラリー" });
                sync.Invoke(ui, null);
                Assert.AreEqual("ギャラリー", label.text);
                Assert.IsTrue(label.enabled);
                Assert.IsFalse(layout.ignoreLayout);
                Assert.IsFalse(chineseLabel.gameObject.activeSelf);
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
                sync.Invoke(ui, null);
                chineseLabel.ForceMeshUpdate(true);
                Assert.AreSame(japanese, chineseLabel.textInfo.characterInfo[0].fontAsset);

                setText.Invoke(ui, new object[] { label, "" });
                Assert.AreEqual("", label.text);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ButtonLabelsContinueToUpdateWithChineseFontActive()
        {
            GameObject root = new GameObject("Button localization test", typeof(RectTransform), typeof(Canvas));
            try
            {
                GaussianSplatRendererUI ui = root.AddComponent<GaussianSplatRendererUI>();
                GameObject buttonObject = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
                buttonObject.transform.SetParent(root.transform);
                Button button = buttonObject.GetComponent<Button>();
                GameObject labelObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
                labelObject.transform.SetParent(buttonObject.transform);
                TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
                label.font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath("22f36b798d4a5f7428f61e404f43717b"));

                System.Type builder = System.Type.GetType("GaussianSplatting.Editor.GaussianSplatUiBuilder, GaussianSplatting.Editor");
                Assert.NotNull(builder);
                MethodInfo ensure = builder.GetMethod("EnsureChineseFontTexts", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo sync = typeof(GaussianSplatRendererUI).GetMethod("SyncLanguageFonts", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo applyButton = typeof(GaussianSplatRendererUI).GetMethod("ApplyButtonVisual", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo language = typeof(GaussianSplatRendererUI).GetField("selectedLanguage", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(ensure);
                Assert.NotNull(sync);
                Assert.NotNull(applyButton);
                Assert.NotNull(language);
                ensure.Invoke(null, new object[] { ui });
                language.SetValue(ui, 2);
                sync.Invoke(ui, null);

                Assert.IsFalse(label.enabled);
                Assert.AreSame(label, button.GetComponentInChildren<TextMeshProUGUI>());
                applyButton.Invoke(ui, new object[] { button, "高画质", Color.white });
                Assert.AreEqual("高画质", label.text);
                Assert.AreEqual("高画质", ui.chineseFontTexts[0].text);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
#endif
