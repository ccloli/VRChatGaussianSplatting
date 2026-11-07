#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using GaussianSplatting.Editor.Importers;

namespace GaussianSplatting
{
    // Re-import a splat from the import metadata stamped on it at import time: either exactly (re-run with the
    // stored settings) or with edits (open the import window prefilled). Metadata lives in importMetadataJson.
    public static class GaussianSplatReimport
    {
        [MenuItem("CONTEXT/GaussianSplatObject/Reimport (exact)")]
        static void ReimportObjectExact(MenuCommand cmd) => ReimportExact((cmd.context as GaussianSplatObject)?.importMetadataJson);

        [MenuItem("CONTEXT/GaussianSplatObject/Reimport (edit settings)…")]
        static void ReimportObjectEdit(MenuCommand cmd) => ReimportEdit((cmd.context as GaussianSplatObject)?.importMetadataJson);

        static GaussianSplatImporter.ImportMetadata Parse(string json)
        {
            GaussianSplatImporter.ImportMetadata md = GaussianSplatImporter.ImportMetadata.FromJson(json);
            if (md == null || string.IsNullOrEmpty(md.sourcePath))
            {
                EditorUtility.DisplayDialog(GSEditorText.T("Reimport", "再インポート", "重新导入"), GSEditorText.T("This splat has no stored import metadata (it predates the metadata feature, or was imported another way). Re-import it once via Gaussian Splatting -> Import Splats... to stamp it.", "この Splat にインポートメタデータがありません。Gaussian Splatting -> Import Splats... から再インポートしてください。", "此 Splat 没有保存导入元数据（可能创建于该功能推出之前，或通过其他方式导入）。请通过 Gaussian Splatting -> Import Splats... 重新导入一次，以保存元数据。"), "OK");
                return null;
            }
            if (!File.Exists(md.sourcePath))
            {
                EditorUtility.DisplayDialog(GSEditorText.T("Reimport", "再インポート", "重新导入"), GSEditorText.T($"Source file not found:\n{md.sourcePath}\n\nUse “Reimport (edit settings)…” and pick the file again.", $"ソースファイルが見つかりません:\n{md.sourcePath}\n\n“Reimport (edit settings)…” でファイルを再選択してください。", $"找不到源文件：\n{md.sourcePath}\n\n请使用 “Reimport (edit settings)…” 重新选择文件。"), "OK");
                return null;
            }
            return md;
        }

        static void ReimportExact(string json)
        {
            GaussianSplatImporter.ImportMetadata md = Parse(json);
            if (md == null) return;
            if (md.importAsLOD)
            {
                if (!TryImportLOD(md.sourcePath, md.prefabPath, md.lodChunkSize, md.options))
                {
                    return;
                }
            }
            else
            {
                GaussianSplatImporter.Import(md.sourcePath, md.prefabPath, md.options);
            }
            Debug.Log($"[GaussianSplatting] Re-imported {Path.GetFileName(md.sourcePath)} -> {md.prefabPath}");
        }

        static bool TryImportLOD(string sourcePath, string prefabPath, int chunkSize, GaussianSplatImporter.ImportOptions options)
        {
            Type importerType = Type.GetType("GaussianSplatting.GaussianSplatLODImporter, GaussianSplatting.Editor");
            if (importerType == null)
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    importerType = assembly.GetType("GaussianSplatting.GaussianSplatLODImporter");
                    if (importerType != null)
                    {
                        break;
                    }
                }
            }

            MethodInfo importMethod = importerType?.GetMethod(
                "ImportLODToPrefab",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(string), typeof(int), typeof(GaussianSplatImporter.ImportOptions) },
                null);
            if (importMethod == null)
            {
                EditorUtility.DisplayDialog(GSEditorText.T("Reimport", "再インポート", "重新导入"), GSEditorText.T("LOD importer entry point was not found in this package.", "このパッケージに LOD インポーターのエントリーポイントがありません。", "此工具包中找不到 LOD 导入器的入口。"), "OK");
                return false;
            }

            importMethod.Invoke(null, new object[] { sourcePath, prefabPath, chunkSize, options });
            return true;
        }

        static void ReimportEdit(string json)
        {
            GaussianSplatImporter.ImportMetadata md = GaussianSplatImporter.ImportMetadata.FromJson(json);
            if (md == null || string.IsNullOrEmpty(md.sourcePath))
            {
                EditorUtility.DisplayDialog(GSEditorText.T("Reimport", "再インポート", "重新导入"), GSEditorText.T("This splat has no stored import metadata. Re-import it once via Gaussian Splatting -> Import Splats... to stamp it.", "この Splat にインポートメタデータがありません。Gaussian Splatting -> Import Splats... から再インポートしてください。", "此 Splat 没有保存导入元数据。请通过 Gaussian Splatting -> Import Splats... 重新导入一次，以保存元数据。"), "OK");
                return;
            }
            GaussianSplatImportWizard.OpenForReimport(md);
        }
    }
}
#endif
