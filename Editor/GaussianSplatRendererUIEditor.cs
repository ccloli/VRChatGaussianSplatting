#if UNITY_EDITOR
using System.Collections.Generic;
using GaussianSplatting;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // Minimal inspector for the splat UI: hides the dozens of internal UI references and shows only the bits a
    // world author touches - the additional description text and the gallery list.
    [CustomEditor(typeof(GaussianSplatRendererUI))]
    class GaussianSplatRendererUIEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target))
            {
                return;
            }

            serializedObject.Update();

            EditorGUILayout.LabelField(GSEditorText.T("Gallery", "ギャラリー", "画廊"), EditorStyles.boldLabel);
            SerializedProperty galleryObjects = serializedObject.FindProperty("galleryObjects");
            SerializedProperty gallerySelectedIndex = serializedObject.FindProperty("_gallerySelectedIndex");
            EditorGUILayout.PropertyField(galleryObjects, new GUIContent(GSEditorText.T("Splat Objects", "Splat オブジェクト", "Splat 对象")), true);
            DrawActiveSplatPopup(galleryObjects, gallerySelectedIndex);
            EditorGUILayout.HelpBox(
                GSEditorText.T("Add splat objects here to turn the area into a gallery: in-game only the selected one renders, and " +
                "the list builds itself. Splat objects not listed here are left untouched.",
                "Splat オブジェクトを追加するとギャラリーになります。ゲーム内では選択したものだけが描画され、リストは自動生成されます。リスト外のオブジェクトには影響しません。",
                "在此添加 Splat 对象即可启用画廊：游戏中只渲染选中的对象，列表会自动生成。列表之外的 Splat 对象不受影响。"),
                MessageType.None);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("galleryEnabled"), new GUIContent(GSEditorText.T("Gallery Enabled", "ギャラリーを有効化", "启用画廊")));
            EditorGUILayout.HelpBox(GSEditorText.T("Turn this off to temporarily disable gallery UI and selection enforcement without clearing the list or changing listed splat active states.", "無効にするとリストを保持したままギャラリー UI と選択制御を一時停止し、Splat の有効状態は変更されません。", "关闭后会暂时禁用画廊界面和选中状态控制，保留列表且不改变列表中 Splat 的启用状态。"), MessageType.None);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("galleryMasterLock"), new GUIContent(GSEditorText.T("Master Lock", "マスターロック", "实例主控锁定")));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(GSEditorText.T("Additional Description", "追加説明", "附加描述"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("customSubtitleEnglish"), new GUIContent(GSEditorText.T("English", "英語", "英语")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("customSubtitleJapanese"), new GUIContent(GSEditorText.T("Japanese", "日本語", "日语")));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("customSubtitleChinese"), new GUIContent(GSEditorText.T("Simplified Chinese", "簡体字中国語", "简体中文")));

            if (serializedObject.ApplyModifiedProperties())
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    if (targets[i] is GaussianSplatRendererUI ui)
                    {
                        GaussianSplatUiBuilder.EnsureAuthorTextCharacters(ui);
                        ui.ApplyGalleryInspectorState();
                        EditorUtility.SetDirty(ui);
                    }
                }
            }
        }

        static void DrawActiveSplatPopup(SerializedProperty galleryObjects, SerializedProperty selectedIndex)
        {
            if (galleryObjects == null || selectedIndex == null || !galleryObjects.isArray)
            {
                EditorGUILayout.HelpBox(GSEditorText.T("Active splat selection is unavailable until the UI script metadata refreshes.", "UI スクリプトのメタデータ更新後に Splat を選択できます。", "UI 脚本元数据刷新后才能选择当前 Splat。"), MessageType.Info);
                return;
            }

            List<int> indices = new List<int>();
            List<string> labels = new List<string>();
            for (int i = 0; i < galleryObjects.arraySize; i++)
            {
                GaussianSplatObject splat = galleryObjects.GetArrayElementAtIndex(i).objectReferenceValue as GaussianSplatObject;
                if (splat == null)
                {
                    continue;
                }
                indices.Add(i);
                labels.Add(i + ": " + DisplayName(splat));
            }

            if (indices.Count == 0)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.Popup(GSEditorText.T("Active Splat", "アクティブな Splat", "默认启用的 Splat"), 0, new[] { GSEditorText.T("No listed splats", "Splat がありません", "列表中没有 Splat") });
                EditorGUI.EndDisabledGroup();
                return;
            }

            int selected = selectedIndex.intValue;
            int popupIndex = indices.IndexOf(selected);
            if (popupIndex < 0)
            {
                popupIndex = 0;
                selectedIndex.intValue = indices[0];
            }

            EditorGUI.BeginChangeCheck();
            popupIndex = EditorGUILayout.Popup(GSEditorText.T("Active Splat", "アクティブな Splat", "默认启用的 Splat"), popupIndex, labels.ToArray());
            if (EditorGUI.EndChangeCheck())
            {
                selectedIndex.intValue = indices[Mathf.Clamp(popupIndex, 0, indices.Count - 1)];
            }
        }

        static string DisplayName(GaussianSplatObject splat)
        {
            if (splat == null)
            {
                return "";
            }
            if (!string.IsNullOrEmpty(splat.splatName))
            {
                return splat.splatName;
            }
            return splat.gameObject != null ? splat.gameObject.name : splat.name;
        }
    }
}
#endif
