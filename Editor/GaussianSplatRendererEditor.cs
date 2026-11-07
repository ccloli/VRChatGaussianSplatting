#if UNITY_EDITOR
using GaussianSplatting;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatRenderer))]
    [CanEditMultipleObjects]
    class GaussianSplatRendererEditor : UnityEditor.Editor
    {
        const long VRC_WORLD_SIZE_WARNING_BYTES = 1200L * 1024L * 1024L;
        const float SPLAT_DATA_DOWNLOAD_COMPRESSION_ESTIMATE = 0.85f;

        SerializedProperty _cameraPositionQuantization;
        SerializedProperty _combinedLodSplatBudgetPC;
        SerializedProperty _combinedLodSplatBudgetAndroid;
        SerializedProperty _combinedLodGlobalSplatBudgetPC;
        SerializedProperty _combinedLodGlobalSplatBudgetAndroid;
        SerializedProperty _combinedLodTargetScale;
        SerializedProperty _combinedLodDirectionalBias;
        SerializedProperty _lodMaxSplatsPerPixel;
        SerializedProperty _startupQualityPreset;
        SerializedProperty _startupLodCapacity;
        SerializedProperty _debugDrawLodGrid;
        SerializedProperty _debugRenderOpaqueEllipsoids;
        SerializedProperty _debugDrawChunkBounds;
        SerializedProperty _debugDrawChunkCenterArea;

        SerializedProperty _overrideMaterialProperties;
        SerializedProperty _overrideRenderQueue;
        SerializedProperty _startRenderQueue;
        SerializedProperty _requestedSHBand;
        SerializedProperty _usePagedFusedSH;
        bool _showFusedObjectTable;
        SerializedProperty _gaussianScale;
        SerializedProperty _thinThreshold;
        SerializedProperty _antiAliasing;
        SerializedProperty _log2MinScale;
        SerializedProperty _alphaCutoff;
        SerializedProperty _alphaCull;
        SerializedProperty _lodCull;
        SerializedProperty _scaleCutoff;
        SerializedProperty _exposure;
        SerializedProperty _opacity;
        SerializedProperty _oklchShift;
        SerializedProperty _gamma;
        SerializedProperty _useVrcLightVolumes;
        SerializedProperty _lightVolumeIntensity;

        void OnEnable()
        {
            _cameraPositionQuantization = serializedObject.FindProperty("cameraPositionQuantization");
            _combinedLodSplatBudgetPC = serializedObject.FindProperty("combinedLodSplatBudgetPC");
            _combinedLodSplatBudgetAndroid = serializedObject.FindProperty("combinedLodSplatBudgetAndroid");
            _combinedLodGlobalSplatBudgetPC = serializedObject.FindProperty("combinedLodGlobalSplatBudgetPC");
            _combinedLodGlobalSplatBudgetAndroid = serializedObject.FindProperty("combinedLodGlobalSplatBudgetAndroid");
            _combinedLodTargetScale = serializedObject.FindProperty("combinedLodTargetScale");
            _combinedLodDirectionalBias = serializedObject.FindProperty("combinedLodDirectionalBias");
            _lodMaxSplatsPerPixel = serializedObject.FindProperty("lodMaxSplatsPerPixel");
            _startupQualityPreset = serializedObject.FindProperty("startupQualityPreset");
            _startupLodCapacity = serializedObject.FindProperty("startupLodCapacity");
            _debugDrawLodGrid = serializedObject.FindProperty("debugDrawLodGrid");
            _debugRenderOpaqueEllipsoids = serializedObject.FindProperty("debugRenderOpaqueEllipsoids");
            _debugDrawChunkBounds = serializedObject.FindProperty("debugDrawChunkBounds");
            _debugDrawChunkCenterArea = serializedObject.FindProperty("debugDrawChunkCenterArea");

            _overrideMaterialProperties = serializedObject.FindProperty("overrideMaterialProperties");
            _overrideRenderQueue = serializedObject.FindProperty("overrideRenderQueue");
            _startRenderQueue = serializedObject.FindProperty("startRenderQueue");
            _requestedSHBand = serializedObject.FindProperty("requestedSHBand");
            _usePagedFusedSH = serializedObject.FindProperty("usePagedFusedSH");
            _gaussianScale = serializedObject.FindProperty("gaussianScale");
            _thinThreshold = serializedObject.FindProperty("thinThreshold");
            _antiAliasing = serializedObject.FindProperty("antiAliasing");
            _log2MinScale = serializedObject.FindProperty("log2MinScale");
            _alphaCutoff = serializedObject.FindProperty("alphaCutoff");
            _alphaCull = serializedObject.FindProperty("alphaCull");
            _lodCull = serializedObject.FindProperty("lodCull");
            _scaleCutoff = serializedObject.FindProperty("scaleCutoff");
            _exposure = serializedObject.FindProperty("exposure");
            _opacity = serializedObject.FindProperty("opacity");
            _oklchShift = serializedObject.FindProperty("oklchShift");
            _gamma = serializedObject.FindProperty("gamma");
            _useVrcLightVolumes = serializedObject.FindProperty("useVrcLightVolumes");
            _lightVolumeIntensity = serializedObject.FindProperty("lightVolumeIntensity");
        }

        public override void OnInspectorGUI()
        {
            DrawUdonSharpHeader();

            serializedObject.Update();

            DrawSettingsGroup(GSEditorText.T("Rendering Settings", "表示設定", "渲染设置"), DrawRenderingSettings);
            EditorGUILayout.Space();
            DrawSettingsGroup(GSEditorText.T("Sorting Settings", "ソート設定", "排序设置"), DrawSortingSettings);
            EditorGUILayout.Space();
            DrawSettingsGroup(GSEditorText.T("Material Settings", "マテリアル設定", "材质设置"), DrawMaterialSettings);

            bool changed = serializedObject.ApplyModifiedProperties();

            if (!serializedObject.isEditingMultipleObjects && target is GaussianSplatRenderer sceneRenderer)
            {
                if (changed)
                {
                    sceneRenderer.RefreshEditorResourcesAndVisibility();
                    sceneRenderer.ApplyEditorDebugRenderingModeNow();
                }
                else
                {
                    GaussianSplatCombinedHierarchyBuilder.EnsureChunkHierarchy(sceneRenderer);
                    sceneRenderer.ApplyEditorDebugRenderingModeNow();
                }
            }

            EditorGUILayout.Space();
            DrawUdonSharpUtilities();
        }

        void DrawRenderingSettings()
        {
            bool hasActiveLodObjects = !serializedObject.isEditingMultipleObjects && target is GaussianSplatRenderer sceneRendererWithLod && CountActiveSceneLODObjects(sceneRendererWithLod) > 0;
            if (hasActiveLodObjects)
            {
                DrawCombinedLodBudgetField();
                EditorGUILayout.PropertyField(_debugDrawLodGrid, GSEditorText.C("Debug LOD", "LOD をデバッグ表示", "调试 LOD"));
            }
            EditorGUILayout.PropertyField(_debugRenderOpaqueEllipsoids, GSEditorText.C("Debug Opaque Ellipsoids", "不透明楕円体をデバッグ表示", "调试不透明椭球体"));
            EditorGUILayout.PropertyField(_debugDrawChunkBounds, GSEditorText.C("Debug Chunk Bounds", "チャンク境界をデバッグ表示", "调试分块边界"));
            EditorGUILayout.PropertyField(_debugDrawChunkCenterArea, GSEditorText.C("Debug Chunk Center+Area", "チャンク重心+面積をデバッグ表示", "调试分块中心和面积"));
            if (!serializedObject.isEditingMultipleObjects && target is GaussianSplatRenderer sceneRenderer)
            {
                EditorGUILayout.LabelField(GSEditorText.T("Rendered Splat Count", "描画スプラット数", "已渲染 Splat 数量"), sceneRenderer.GetCurrentRenderedSplatCount().ToString());
                EditorGUILayout.LabelField(GSEditorText.T("Baked Splat Count", "ベイク済みスプラット数", "已烘焙 Splat 数量"), sceneRenderer.GetTotalBakedSplatCount().ToString("N0"));
                long splatDataBytes = sceneRenderer.GetBakedSplatDataBytes();
                if (splatDataBytes > 0)
                {
                    // Build/download is an LZ4 asset bundle; the high-entropy RGBA32 splat textures compress to
                    // ~0.85x (measured on a 4.5M-splat build: 81.7 MB uncompressed source -> ~70 MB in the .vrcw).
                    long compressedBytes = (long)(splatDataBytes * SPLAT_DATA_DOWNLOAD_COMPRESSION_ESTIMATE);
                    EditorGUILayout.LabelField(GSEditorText.T("Splat Data (download est.)", "スプラットデータ (DL推定)", "Splat 数据（预计下载量）"),
                        EditorUtility.FormatBytes(compressedBytes) + " (~" + EditorUtility.FormatBytes(splatDataBytes) + " raw)");
                    if (compressedBytes > VRC_WORLD_SIZE_WARNING_BYTES)
                    {
                        EditorGUILayout.HelpBox(
                            GSEditorText.T("Estimated splat data alone exceeds the 1.2 GB VRChat world size limit. Reduce baked splat data before uploading.", "Splat データの推定サイズだけで VRChat ワールドの上限 1.2 GB を超えています。アップロード前にデータを減らしてください。", "当前预估的 Splat 数据大小已超过 VRChat 世界的 1.2 GB 大小限制。请在上传前减少需要烘焙的 Splat 数据。"),
                            MessageType.Warning);
                    }
                }
                int readbackCount = sceneRenderer.GetEditorReadbackRenderedSplatCount();
                int reservedCount = sceneRenderer.GetEditorReadbackReservedSplatCount();
                if (reservedCount > 0)
                {
                    EditorGUILayout.LabelField(GSEditorText.T("Editor Readback Splat Count", "エディタ読み戻しスプラット数", "编辑器回读 Splat 数量"), readbackCount + " / " + reservedCount);
                    EditorGUILayout.LabelField(GSEditorText.T("Editor Readback Log2 Alpha", "エディタ読み戻し Log2 アルファ", "编辑器回读 Log2 Alpha"), sceneRenderer.GetEditorReadbackAlpha().ToString("0.###"));
                }
            }
        }

        void DrawCombinedLodBudgetField()
        {
            if (_combinedLodSplatBudgetPC != null)
            {
                EditorGUI.showMixedValue = _combinedLodSplatBudgetPC.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                int nextPcBudget = EditorGUILayout.IntField(GSEditorText.T("LOD PC Preset Cap", "LOD PC プリセット上限", "LOD PC 预设上限"), _combinedLodSplatBudgetPC.intValue);
                if (EditorGUI.EndChangeCheck())
                {
                    _combinedLodSplatBudgetPC.intValue = Mathf.Max(0, nextPcBudget);
                }
                EditorGUI.showMixedValue = false;
            }
            if (_combinedLodSplatBudgetAndroid != null)
            {
                EditorGUI.showMixedValue = _combinedLodSplatBudgetAndroid.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                int nextAndroidBudget = EditorGUILayout.IntField(GSEditorText.T("LOD Android Preset Cap", "LOD Android プリセット上限", "LOD Android 预设上限"), _combinedLodSplatBudgetAndroid.intValue);
                if (EditorGUI.EndChangeCheck())
                {
                    _combinedLodSplatBudgetAndroid.intValue = Mathf.Max(0, nextAndroidBudget);
                }
                EditorGUI.showMixedValue = false;
            }
            DrawGlobalLodCap(_combinedLodGlobalSplatBudgetPC, GSEditorText.T("LOD PC Global Cap", "LOD PC 全体上限", "LOD PC 全局上限"));
            DrawGlobalLodCap(_combinedLodGlobalSplatBudgetAndroid, GSEditorText.T("LOD Android Global Cap", "LOD Android 全体上限", "LOD Android 全局上限"));
            EditorGUILayout.HelpBox(GSEditorText.T(
                "Preset caps set the quality tiers and startup capacity; 0 means no cap, even when a global cap is set. Global caps set the advanced slider maximum; 0 inherits the preset cap, and a smaller value cannot lower it. The combined renderer supports up to 16,777,216 splats.",
                "プリセット上限は品質と起動時容量を設定し、0 は全体上限の設定に関わらず上限なしです。全体上限は詳細設定スライダーの最大値で、0 はプリセット上限を継承し、小さい値では上限を下げません。統合レンダラーは最大 16,777,216 スプラットに対応します。",
                "预设上限决定画质档位和启动容量；即使设置了全局上限，0 仍表示无上限。全局上限决定高级设置中滑块的最大值；0 表示沿用预设上限，小于预设上限的值不会降低上限。合并渲染器最多支持 16,777,216 个 Splat。"), MessageType.None);
            if (_combinedLodTargetScale != null)
            {
                EditorGUI.showMixedValue = _combinedLodTargetScale.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                float nextTargetScale = EditorGUILayout.Slider(GSEditorText.T("Combined LOD Target Scale", "統合 LOD 目標倍率", "合并 LOD 目标缩放"), _combinedLodTargetScale.floatValue > 0.0f ? _combinedLodTargetScale.floatValue : 0.95f, 0.1f, 1.0f);
                if (EditorGUI.EndChangeCheck())
                {
                    _combinedLodTargetScale.floatValue = Mathf.Clamp(nextTargetScale, 0.1f, 1.0f);
                }
                EditorGUI.showMixedValue = false;
            }
            if (_combinedLodDirectionalBias != null)
            {
                EditorGUI.showMixedValue = _combinedLodDirectionalBias.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                float nextDirectionalBias = EditorGUILayout.Slider(GSEditorText.T("Combined LOD Directional Bias", "統合 LOD 方向バイアス", "合并 LOD 方向偏置"), _combinedLodDirectionalBias.floatValue > 0.0f ? _combinedLodDirectionalBias.floatValue : 2.0f, 1.0f, 16.0f);
                if (EditorGUI.EndChangeCheck())
                {
                    _combinedLodDirectionalBias.floatValue = Mathf.Clamp(nextDirectionalBias, 1.0f, 16.0f);
                }
                EditorGUI.showMixedValue = false;
            }
            if (_lodMaxSplatsPerPixel != null)
            {
                EditorGUI.showMixedValue = _lodMaxSplatsPerPixel.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                float nextMaxPerPixel = EditorGUILayout.Slider(GSEditorText.T("LOD Max Splats / Pixel", "LOD 最大スプラット/ピクセル", "LOD 每像素最大 Splat 数"), Mathf.Max(0.0f, _lodMaxSplatsPerPixel.floatValue), 0.0f, 4.0f);
                if (EditorGUI.EndChangeCheck())
                {
                    _lodMaxSplatsPerPixel.floatValue = Mathf.Max(0.0f, nextMaxPerPixel);
                }
                EditorGUI.showMixedValue = false;
            }
            if (_startupQualityPreset != null)
            {
                // Index 0 = "Keep Inspector Settings" maps to the stored value -1; indices 1..4 map to 0..3.
                string[] startupLabels = { GSEditorText.T("Keep Inspector Settings", "インスペクター設定を維持", "沿用 Inspector 设置"), GSEditorText.T("Very Low", "最低", "极低"), GSEditorText.T("Low", "低", "低"), GSEditorText.T("Medium", "中", "中"), GSEditorText.T("High", "高", "高") };
                EditorGUI.showMixedValue = _startupQualityPreset.hasMultipleDifferentValues;
                EditorGUI.BeginChangeCheck();
                int nextStartup = EditorGUILayout.Popup(GSEditorText.T("Startup Quality", "起動時の品質", "启动画质"), Mathf.Clamp(_startupQualityPreset.intValue + 1, 0, startupLabels.Length - 1), startupLabels);
                if (EditorGUI.EndChangeCheck())
                {
                    _startupQualityPreset.intValue = nextStartup - 1;
                }
                EditorGUI.showMixedValue = false;
            }
            if (!serializedObject.isEditingMultipleObjects && target is GaussianSplatRenderer sceneRenderer)
            {
                if (_startupLodCapacity != null)
                {
                    // Startup LOD capacity as a fraction of THIS platform's cap (scales per-platform). Used when
                    // Startup Quality = "Keep Inspector Settings". Shows the resolved count for the editor's
                    // platform (PC cap) for reference.
                    EditorGUI.showMixedValue = _startupLodCapacity.hasMultipleDifferentValues;
                    EditorGUI.BeginChangeCheck();
                    float nextCapacity = EditorGUILayout.Slider(GSEditorText.T("Startup LOD Capacity", "起動時 LOD 容量", "启动时 LOD 容量"), Mathf.Clamp01(_startupLodCapacity.floatValue), 0.0f, 1.0f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _startupLodCapacity.floatValue = Mathf.Clamp01(nextCapacity);
                    }
                    EditorGUI.showMixedValue = false;
                    int sliderMin = sceneRenderer.GetCombinedLodSplatBudgetSliderMin();
                    int presetCap = sceneRenderer.GetCombinedLodSplatBudgetPC();
                    int resolved = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(_startupLodCapacity.floatValue) * presetCap), sliderMin, presetCap);
                    string resolvedText = resolved == 0 ? GSEditorText.T("No cap", "上限なし", "无上限") : resolved.ToString("N0");
                    EditorGUILayout.LabelField(" ", GSEditorText.T("= " + resolvedText + " (PC preset cap)", "= " + resolvedText + " (PC プリセット上限)", "= " + resolvedText + "（PC 预设上限）"));
                }
                int effectiveBudget = sceneRenderer.GetEffectiveCombinedLodSplatBudget();
                int targetBudget = effectiveBudget > 0 ? Mathf.FloorToInt(effectiveBudget * sceneRenderer.GetEffectiveCombinedLodTargetScale()) : 0;
                string effective = effectiveBudget == 0 ? GSEditorText.T("No cap", "上限なし", "无上限") : effectiveBudget.ToString();
                string target = effectiveBudget == 0 ? GSEditorText.T("No cap", "上限なし", "无上限") : targetBudget.ToString();
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "Editor previews use the PC preset cap. Android builds use the Android preset cap. Effective cap: " + effective + ". LOD selection target: " + target + ".",
                    "エディタプレビューでは PC プリセット上限を使用します。Android ビルドでは Android プリセット上限を使用します。現在の上限: " + effective + "。LOD 選択目標: " + target + "。",
                    "编辑器预览使用 PC 预设上限，Android 构建使用 Android 预设上限。当前上限：" + effective + "。LOD 选择目标：" + target + "。"), MessageType.None);
            }
        }

        static void DrawGlobalLodCap(SerializedProperty property, string label)
        {
            if (property == null) return;
            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            int value = EditorGUILayout.IntField(label, property.intValue);
            if (EditorGUI.EndChangeCheck()) property.intValue = Mathf.Clamp(value, 0, 1 << 24);
            EditorGUI.showMixedValue = false;
        }

        static int CountActiveSceneLODObjects(GaussianSplatRenderer renderer)
        {
            int count = 0;
            foreach (GaussianSplatObject lodObject in Resources.FindObjectsOfTypeAll<GaussianSplatObject>())
            {
                if (lodObject != null && lodObject.gameObject.scene == renderer.gameObject.scene && !EditorUtility.IsPersistent(lodObject) && lodObject.gameObject.activeInHierarchy)
                {
                    count++;
                }
            }
            return count;
        }

        void DrawSortingSettings()
        {
            EditorGUILayout.PropertyField(_cameraPositionQuantization, GSEditorText.C("Camera Position Quantization", "カメラ位置量子化", "相机位置量化"));
        }

        void DrawMaterialSettings()
        {
            EditorGUILayout.IntSlider(_requestedSHBand, 0, 3, GSEditorText.C("Requested SH Band", "要求 SH バンド", "请求的 SH 阶数"));
            GUIContent pagedLabel = GSEditorText.C("Paged Fused SH (Experimental)", "ページ分割 SH (実験的)", "分页融合 SH（实验性）");
            pagedLabel.tooltip = GSEditorText.T("May use more memory and requires texture-array support on the target device. Changing this rebakes SH.",
                "メモリ使用量が増える可能性があり、端末がテクスチャ配列に対応している必要があります。変更すると SH を再ベイクします。",
                "可能增加内存占用，且目标设备必须支持纹理数组。更改此设置会重新烘焙 SH。");
            using (new EditorGUI.DisabledScope(Application.isPlaying))
                EditorGUILayout.PropertyField(_usePagedFusedSH, pagedLabel);
            if (_usePagedFusedSH.boolValue)
                EditorGUILayout.HelpBox(GSEditorText.T(
                    "Editor bake setting: SH is stored in an array of up to 8K pages. Changing this rebakes SH. Array support and memory use depend on the target device; the last page may be partly unused.",
                    "エディタのベイク設定: SH を最大 8K のページ配列に保存します。変更すると SH を再ベイクします。配列の対応状況とメモリ使用量は端末に依存し、最後のページに未使用領域が生じる場合があります。",
                    "编辑器烘焙设置：将 SH 存储在由最大 8K × 8K 页面组成的纹理数组中。更改此设置会重新烘焙 SH。纹理数组支持情况和内存占用取决于目标设备；最后一页可能有未使用空间。"), MessageType.Warning);
            if (!serializedObject.isEditingMultipleObjects && target is GaussianSplatRenderer shRenderer)
            {
                GaussianSplatCombiner shCombiner = shRenderer.GetCombiner();
                int droppedSh = shCombiner != null ? shCombiner.GetFusedShDroppedObjectCount() : 0;
                if (droppedSh > 0)
                {
                    if (shCombiner.GetFusedShAddressLimit() == GaussianSplatFuse.SingleFusedShTexels)
                    {
                        EditorGUILayout.HelpBox(GSEditorText.T(
                            $"Spherical harmonics were dropped for {droppedSh} object(s): the scene's total SH exceeds the single fused SH texture cap (16384² texels). Those splats render without view-dependent color regardless of this setting. Lower the SH band on some splats, reduce splat counts, or split the scene.",
                            $"{droppedSh} 個のオブジェクトの球面調和が破棄されました: シーン全体の SH が単一の統合 SH テクスチャ上限 (16384² テクセル) を超えています。該当スプラットはこの設定に関わらず視点依存色なしで描画されます。一部スプラットの SH バンドを下げる、スプラット数を減らす、またはシーンを分割してください。",
                            $"{droppedSh} 个对象的球谐数据已被丢弃：场景 SH 总量超过单张融合 SH 纹理的上限（16384² 纹素）。这些 Splat 无论此设置为何值，都将不带视角相关颜色渲染。请降低部分 Splat 的 SH 阶数、减少 Splat 数量或拆分场景。")
                            + (_usePagedFusedSH.boolValue ? "" : GSEditorText.T("\nEnable Paged Fused SH to increase the limit.", "\nページ分割 SH を有効にすると上限を増やせます。", "\n启用分页融合 SH 可提高上限。")),
                            MessageType.Warning);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(GSEditorText.T(
                            $"Spherical harmonics were dropped for {droppedSh} source(s): the fused SH address limit was exceeded. Those splats render without view-dependent color regardless of this setting. Lower the SH band, reduce splat counts, or split the scene.",
                            $"{droppedSh} 個のソースの球面調和が破棄されました: 統合 SH のアドレス上限を超えています。該当スプラットは視点依存色なしで描画されます。SH バンドを下げる、スプラット数を減らす、またはシーンを分割してください。",
                            $"因超过融合 SH 的寻址上限，已丢弃 {droppedSh} 个源的球谐数据。这些 Splat 不受此设置影响，将不带视角相关颜色渲染。请降低 SH 阶数、减少 Splat 数量或拆分场景。"),
                            MessageType.Warning);
                    }
                }
                DrawFusedObjectTable(shCombiner);
            }
            EditorGUILayout.PropertyField(_useVrcLightVolumes, GSEditorText.C("Use VRC Light Volumes", "VRC Light Volumes を使用", "使用 VRC Light Volumes"));
            using (new EditorGUI.DisabledScope(!_useVrcLightVolumes.boolValue))
            {
                EditorGUILayout.Slider(_lightVolumeIntensity, 0.0f, 10.0f, GSEditorText.C("Light Volume Intensity", "ライトボリューム強度", "光照体积强度"));
            }

            EditorGUILayout.Space();
            DrawQualityPresetButtons();
            EditorGUILayout.PropertyField(_overrideRenderQueue, GSEditorText.C("Override Render Queue", "レンダーキューを上書き", "覆盖渲染队列"));
            using (new EditorGUI.DisabledScope(!_overrideRenderQueue.boolValue))
            {
                EditorGUILayout.IntSlider(_startRenderQueue, 2000, 5000, GSEditorText.C("Start Render Queue", "開始レンダーキュー", "起始渲染队列"));
            }
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_overrideMaterialProperties, GSEditorText.C("Override Material Properties", "マテリアル設定を上書き", "覆盖材质属性"));
            using (new EditorGUI.DisabledScope(!_overrideMaterialProperties.boolValue))
            {
                EditorGUILayout.Slider(_gaussianScale, 0.0f, 2.0f, GSEditorText.C("Gaussian Scale", "ガウススケール", "高斯缩放"));
                EditorGUILayout.Slider(_thinThreshold, 0.0f, 1.0f, GSEditorText.C("Thinness Threshold", "薄さしきい値", "薄度阈值"));
                EditorGUILayout.Slider(_antiAliasing, 0.0f, 5.0f, GSEditorText.C("Anti Aliasing", "アンチエイリアス", "抗锯齿"));
                EditorGUILayout.Slider(_log2MinScale, -20.0f, 10.0f, GSEditorText.C("Log2 Minimum Scale", "Log2 最小スケール", "Log2 最小缩放"));
                EditorGUILayout.Slider(_alphaCutoff, 0.0f, 1.0f, GSEditorText.C("Alpha Cutoff", "アルファカットオフ", "Alpha 截断阈值"));
                EditorGUILayout.Slider(_alphaCull, 0.0f, 1.0f, GSEditorText.C("Alpha Cull", "アルファカリング", "Alpha 剔除阈值"));
                EditorGUILayout.Slider(_lodCull, 0.0f, 0.1f, GSEditorText.C("LOD Cull", "LOD カリング", "LOD 剔除"));
                EditorGUILayout.Slider(_scaleCutoff, 0.0f, 100.0f, GSEditorText.C("Scale Cutoff", "スケールカットオフ", "缩放截断阈值"));
                EditorGUILayout.Slider(_exposure, 0.0f, 5.0f, GSEditorText.C("Exposure", "露出", "曝光"));
                EditorGUILayout.Slider(_opacity, 0.0f, 5.0f, GSEditorText.C("Opacity", "不透明度", "不透明度"));
                EditorGUILayout.PropertyField(_oklchShift, GSEditorText.C("OKLCH Color Shift", "OKLCH 色シフト", "OKLCH 颜色偏移"));
                DrawMinFloatField(_gamma, GSEditorText.C("Gamma", "ガンマ", "伽马"), 0.001f);
            }
        }

        static readonly GUILayoutOption[] _colNum = { GUILayout.Width(64) };
        static readonly GUILayoutOption[] _colShTex = { GUILayout.Width(110) };
        static readonly GUILayoutOption[] _colFlag = { GUILayout.Width(72) };

        static void DrawFusedTableRow(string c0, string active, string splats, string files, string chunks, string band, string shTex, string flag, GUIStyle style)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(c0, style, GUILayout.MinWidth(120));
                EditorGUILayout.LabelField(active, style, _colFlag);
                EditorGUILayout.LabelField(splats, style, _colNum);
                EditorGUILayout.LabelField(files, style, _colNum);
                EditorGUILayout.LabelField(chunks, style, _colNum);
                EditorGUILayout.LabelField(band, style, _colNum);
                EditorGUILayout.LabelField(shTex, style, _colShTex);
                EditorGUILayout.LabelField(flag, style, _colFlag);
            }
        }

        // Debug table of every fused object's params; shared sources count only once toward SH storage.
        void DrawFusedObjectTable(GaussianSplatCombiner combiner)
        {
            if (combiner == null)
            {
                return;
            }
            var rows = combiner.GetFusedObjectDebugRows();
            if (rows == null || rows.Count == 0)
            {
                return;
            }
            _showFusedObjectTable = EditorGUILayout.Foldout(_showFusedObjectTable, GSEditorText.T("Fused Objects (debug)", "統合オブジェクト (デバッグ)", "融合对象（调试）"), true);
            if (!_showFusedObjectTable)
            {
                return;
            }
            long cap = combiner.GetFusedShAddressLimit();
            long shUsed = 0, shRequested = 0, splatTotal = 0;
            foreach (var r in rows)
            {
                if (!r.sharedSource)
                {
                    shRequested += r.shTexels;
                    if (!r.shDropped) shUsed += r.shTexels;
                }
                splatTotal += r.splats;
            }
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField(GSEditorText.T("Objects / Splats", "オブジェクト / スプラット", "对象 / Splat"), $"{rows.Count}  /  {splatTotal:N0}");
                EditorGUILayout.LabelField(GSEditorText.T("Fused SH used / cap", "統合 SH 使用 / 上限", "融合 SH 用量 / 上限"), $"{shUsed:N0} / {cap:N0}  ({(cap > 0 ? 100.0 * shUsed / cap : 0):F1}%)");
                if (shRequested != shUsed)
                {
                    EditorGUILayout.LabelField(GSEditorText.T("SH requested (incl. dropped)", "SH 要求 (破棄含む)", "请求的 SH（含已丢弃）"), $"{shRequested:N0}  ({(cap > 0 ? 100.0 * shRequested / cap : 0):F1}%)");
                }
                EditorGUILayout.Space(2);
                var head = EditorStyles.miniBoldLabel;
                var cell = EditorStyles.miniLabel;
                DrawFusedTableRow(GSEditorText.T("Object", "オブジェクト", "对象"), GSEditorText.T("Active", "有効", "启用"),
                    GSEditorText.T("Splats", "Splat 数", "Splat 数"), GSEditorText.T("Files", "ファイル", "文件"),
                    GSEditorText.T("Chunks", "チャンク", "分块"), GSEditorText.T("Band", "バンド", "阶数"),
                    GSEditorText.T("SH texels", "SH テクセル", "SH 纹素"), GSEditorText.T("Dropped", "破棄", "丢弃"), head);
                foreach (var r in rows)
                {
                    int band = r.shCoeff >= 15 ? 3 : (r.shCoeff >= 8 ? 2 : (r.shCoeff >= 3 ? 1 : 0));
                    string shTex = r.shCoeff > 0 ? $"{r.shTexels:N0} ({(cap > 0 ? 100.0 * r.shTexels / cap : 0):F1}%)" : "-";
                    string flag = r.shDropped ? GSEditorText.T("DROPPED", "破棄", "已丢弃") : (r.sharedSource ? GSEditorText.T("shared", "共有", "共享") : (r.shCoeff > 0 ? "" : GSEditorText.T("no SH", "SH なし", "无 SH")));
                    DrawFusedTableRow(r.name, r.active ? GSEditorText.T("yes", "はい", "是") : GSEditorText.T("no", "いいえ", "否"), $"{r.splats:N0}", r.files.ToString(), r.chunks.ToString(), band.ToString(), shTex, flag, cell);
                }
            }
        }

        void DrawQualityPresetButtons()
        {
            EditorGUILayout.LabelField(GSEditorText.T("Quality Preset", "品質プリセット", "画质预设"), EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(GSEditorText.T("Very Low", "最低", "极低"))) ApplyQualityPreset(0.15f, 0.15f);
                if (GUILayout.Button(GSEditorText.T("Low", "低", "低"))) ApplyQualityPreset(0.07f, 0.1f);
                if (GUILayout.Button(GSEditorText.T("Medium", "中", "中"))) ApplyQualityPreset(0.04f, 0.04f);
                if (GUILayout.Button(GSEditorText.T("High", "高", "高"))) ApplyQualityPreset(0.01f, 0.01f);
            }
        }

        void ApplyQualityPreset(float cull, float cutoff)
        {
            _overrideMaterialProperties.boolValue = true;
            _alphaCull.floatValue = cull;
            _alphaCutoff.floatValue = cutoff;
        }

        static void DrawMinFloatField(SerializedProperty property, GUIContent label, float minValue)
        {
            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            float nextValue = EditorGUILayout.FloatField(label, property.floatValue);
            if (EditorGUI.EndChangeCheck())
            {
                property.floatValue = Mathf.Max(minValue, nextValue);
            }
            EditorGUI.showMixedValue = false;
        }

        static void DrawSettingsGroup(string title, System.Action drawContents)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                drawContents();
            }
        }

        void DrawUdonSharpHeader()
        {
            if (targets != null && targets.Length > 1)
            {
                UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(targets);
            }
            else
            {
                UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target);
            }
        }

        void DrawUdonSharpUtilities()
        {
            if (targets != null && targets.Length > 1)
            {
                UdonSharpGUI.DrawUtilities(targets);
            }
            else
            {
                UdonSharpGUI.DrawUtilities(target);
            }
        }
    }
}
#endif
