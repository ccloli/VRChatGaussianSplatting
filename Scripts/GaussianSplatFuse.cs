#if UNITY_EDITOR && !COMPILER_UDONSHARP
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

namespace GaussianSplatting
{
    // Editor bake that concatenates every combined splat's packed source data into ONE fused texture set +
    // chunk metadata, so the runtime combine binds a single source set + a per-object table instead of
    // per-object source textures. Every object is a (1..N level) source with per-chunk selection metadata;
    // its chunks feed the scene-global selection. The GPU gather runs via GSFuseConcat.shader.
    public static class GaussianSplatFuse
    {
        // Source data for one object (one entry per source "file") fed to the unified fuse bake.
        public sealed class FuseLODSource
        {
            public Texture2D[] positions;   // RGBA32 packed, one per file
            public Texture2D[] colors;      // RGBA32
            public Texture2D[] rotations;   // RGBA32
            public Texture2D[] scales;      // RGB9e5Float
            public int[] fileSplatCounts;
            public Texture2D chunkBoundsMin; // RGBAFloat row0: xyz min, w = chunk splat count
            public Texture2D chunkBoundsMax; // RGBAFloat row0: xyz max, w = LOCAL file index
            public Texture2D chunkRange;     // RGBAFloat row0: offsetHi, offsetLo, lod0Count, w (free)
            public int chunkCount;
            public int chunkSize;
            public bool packed;
            // Spherical harmonics (optional). One SH texture per file, coeff-major within the file
            // (coeff*fileSplatCount + local). coeffCount/min/range/band are uniform across the object.
            public Texture2D[] sh;
            public int shCoeffCount;
            public Vector4 shMin;
            public Vector4 shRange;
            public float shBand;
        }

        public sealed class FuseLODResult
        {
            public Texture2D fusedPositions;  // unified source: all combined object files concatenated
            public Texture2D fusedColors;
            public Texture2D fusedRotations;
            public Texture2D fusedScales;
            public Texture2D globalBounds;    // chunks, 2D stacked: [0,metaH) min (xyz,w=splatCount), [metaH,2metaH) max (xyz,w=globalFileId)
            public Texture2D globalRange;     // chunks (offsetHi, offsetLo, lod0Count, w = GLOBAL objectId)
            public Texture2D fileBaseTable;   // per file: (fusedBase hi/lo, fileSplatCount, 0)
            // One of these SH representations is populated, according to the editor bake setting.
            public Texture2D fusedSH;
            public Texture2DArray fusedSHArray;
            public Texture2D shParams;
            public int fusedShCoordShift;
            public int fusedShCoordMask;
            public int totalSourceCount;
            public int totalChunkCount;
            public int fileCount;
            public int objectCount;           // total = nonLodObjectCount + LOD objects
            public int metaWidth;             // global metadata 2D row width: chunk c -> uint2(c%metaWidth, c/metaWidth)
            public int shDroppedObjects;      // unique sources whose SH exceeded the addressable limit
            // Per fused object (objId order) debug stats for the inspector table.
            public int[] objSplatCount;       // source splat count (n)
            public int[] objFileCount;        // number of source files
            public int[] objChunkCount;       // chunk count
            public int[] objShCoeff;          // SH coeff count its source carries (0 = no SH)
            public bool[] objShDropped;       // had SH but it exceeded the addressable limit
        }

        // Each chunk's in-file source offset (offsetHi*GSLOD_OFFSET_BASE + offsetLo) is unchanged; the
        // global source index is fileBaseTable[globalFileId] + offset + localSourceIndex. Decode bounds
        // stay per-(global)chunk. objectId (range.w) lets selection/combine pick the object's params.
        public const float GSLOD_OFFSET_BASE = 4096.0f;

        // Use 8K pages for large sets. Global indices and the existing concat-region fields are signed ints;
        // the practical limit may be lower on devices with fewer array slices or insufficient memory.
        public const int FusedShPageDimension = 8192;
        public const long SingleFusedShTexels = 16384L * 16384L;
        public const long MaxFusedShTexels = int.MaxValue;

        internal readonly struct ShPageLayout
        {
            public readonly int width, height, pages;
            public int PageTexels => width * height;

            public ShPageLayout(int width, int height, int pages)
            {
                this.width = width;
                this.height = height;
                this.pages = pages;
            }
        }

        // A single small page is sized to the data, avoiding a 64 MiB allocation for every small scene.
        // Array slices must have equal dimensions; size their shared height to the total data instead of
        // allocating a full 8K square for a mostly empty last slice.
        internal static ShPageLayout ChooseShPageLayout(int texelCount, int maxDimension = FusedShPageDimension)
        {
            if (maxDimension < 4 || (maxDimension & (maxDimension - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(maxDimension));
            int count = Mathf.Max(1, texelCount);
            long pageCapacity = (long)maxDimension * maxDimension;
            if (count > pageCapacity)
            {
                int pages = (int)(((long)count + pageCapacity - 1) / pageCapacity);
                long rowCapacity = (long)maxDimension * pages;
                int height = AlignToBCBlock((int)(((long)count + rowCapacity - 1) / rowCapacity));
                return new ShPageLayout(maxDimension, height, pages);
            }

            GaussianSplatImporter.TextureLayout layout = GaussianSplatImporter.ChoosePotTextureLayout(count);
            // The general layout chooser uses float division, which can round down for large counts.
            // Recompute height with integers so the final texel always has a valid destination.
            int exactHeight = AlignToBCBlock((int)(((long)count + layout.Width - 1) / layout.Width));
            layout = new GaussianSplatImporter.TextureLayout(layout.Width, exactHeight);
            if (layout.Width > maxDimension || layout.Height > maxDimension)
            {
                int minWidth = Mathf.Max(4, (int)(((long)count + maxDimension - 1) / maxDimension));
                int width = Mathf.NextPowerOfTwo(minWidth);
                int height = AlignToBCBlock((int)(((long)count + width - 1) / width));
                return new ShPageLayout(width, height, 1);
            }
            return new ShPageLayout(layout.Width, layout.Height, 1);
        }

        // Builds the bake plan (all CPU metadata + region lists), then returns a job that streams the heavy GPU
        // concat + readback across editor frames via Step(). Returns a job whose Result is null when there is no
        // source to fuse.
        //
        // reuseFrom: when the UNIQUE source set is unchanged (only instances/transforms/chunk metadata changed),
        // pass the previously baked fused source textures to SKIP the heavy GPU source concat + readback. The
        // (small) per-instance metadata textures are always rebuilt. The deduped source size is stable across
        // duplicate add/remove, so the cached textures stay dimension-compatible.
        public static FuseLODJob CreateFuseLODJob(List<FuseLODSource> sources, string outFolder, string assetPrefix, FuseLODResult reuseFrom = null, bool usePagedSH = false)
        {
            return new FuseLODJob(BuildFuseLODPlan(sources, outFolder, assetPrefix, reuseFrom, usePagedSH));
        }

        static FuseLODPlan BuildFuseLODPlan(List<FuseLODSource> sources, string outFolder, string assetPrefix, FuseLODResult reuseFrom, bool usePagedSH)
        {
            sources = sources ?? new List<FuseLODSource>();

            // Every combined splat is a (1..N level) LOD source flowing through the unified selection + combine.
            // (Standalone "presorted" splats render via their own mesh/material, not through this path.)
            var lod = new List<FuseLODSource>();
            for (int i = 0; i < sources.Count; i++)
            {
                FuseLODSource s = sources[i];
                if (s != null && s.packed && s.positions != null) lod.Add(s);
            }

            // Instance dedup: objects sharing a source asset bake the fused LOD source ONCE (see sharedSources
            // below), so size the texture by the UNIQUE source splat total - not by placement count - both to
            // realize the VRAM saving and so the texture dimensions stay stable when a duplicate is added/removed.
            int totalLod = 0;
            var countedSources = new HashSet<Texture2D>();
            for (int k = 0; k < lod.Count; k++)
            {
                Texture2D key = lod[k].positions != null && lod[k].positions.Length > 0 ? lod[k].positions[0] : null;
                if (key != null && !countedSources.Add(key)) continue; // duplicate placement -> source already counted
                for (int f = 0; f < lod[k].positions.Length; f++)
                    totalLod += lod[k].fileSplatCounts != null && f < lod[k].fileSplatCounts.Length ? Mathf.Max(0, lod[k].fileSplatCounts[f]) : 0;
            }
            if (totalLod <= 0) return null;

            GaussianSplatImporter.TextureLayout layout = GaussianSplatImporter.ChoosePotTextureLayout(totalLod);
            int fw = layout.Width;

            // Source concat runs on the GPU (block-swizzle gather, GSFuseConcat). Only small metadata is
            // read back on the CPU. One regions list spans all files; one SH set spans all objects.
            var regions = new List<FileRegion>();
            var shRegions = new List<FileRegion>();   // .pos = SH source; base/count are SH-texel-space
            var shParams = new List<Color>();          // 4 rows per object, appended in objId order
            int shCursor = 0;
            long shAddressLimit = usePagedSH ? MaxFusedShTexels : SingleFusedShTexels;
            int shDroppedObjects = 0;                  // unique sources whose SH exceeded the index limit
            long shRequestedTexels = 0;                // SH texels the scene asked for (placed + dropped)
            var objSplatCount = new int[lod.Count];    // per object debug stats for the inspector table
            var objFileCount = new int[lod.Count];
            var objChunkCount = new int[lod.Count];
            var objShCoeff = new int[lod.Count];
            var objShDropped = new bool[lod.Count];
            var fileBase = new List<Vector4>();
            var gMin = new List<Color>();
            var gMax = new List<Color>();
            var gRange = new List<Color>();
            var gRangeStats = new List<Color>();
            int fusedCursor = 0;
            int globalFileId = 0;
            int totalChunks = 0;
            // Instance dedup: objects sharing a source asset (prefab instances -> same source textures) bake the
            // fused source + SH region ONCE; each instance still gets its own chunk metadata (gMax.w -> shared
            // global files, gRange.w -> own objId) + transform. (fileOffset, srcBase, totalCount, shBase, shCoeff).
            var sharedSources = new Dictionary<Texture2D, (int fileOffset, int srcBase, int totalCount, int shBase, int shCoeff)>();
            for (int k = 0; k < lod.Count; k++)
            {
                FuseLODSource s = lod[k];
                int objId = k;
                Texture2D sourceKey = s.positions != null && s.positions.Length > 0 ? s.positions[0] : null;
                (int fileOffset, int srcBase, int totalCount, int shBase, int shCoeff) entry = default;
                bool reuse = sourceKey != null && sharedSources.TryGetValue(sourceKey, out entry);
                if (!reuse)
                {
                    int objectFileOffset = globalFileId;
                    int objSrcBase = fusedCursor;
                    var objFileCounts = new List<int>(s.positions.Length);
                    for (int f = 0; f < s.positions.Length; f++)
                    {
                        int fc = s.fileSplatCounts != null && f < s.fileSplatCounts.Length ? Mathf.Max(0, s.fileSplatCounts[f]) : 0;
                        objFileCounts.Add(fc);
                        Texture2D pTex = s.positions[f];
                        Texture2D cTex = s.colors != null && f < s.colors.Length ? s.colors[f] : null;
                        Texture2D rTex = s.rotations != null && f < s.rotations.Length ? s.rotations[f] : null;
                        Texture2D scTex = s.scales != null && f < s.scales.Length ? s.scales[f] : null;
                        // fusedBase split hi/lo (can exceed the float32 2^24 exact-integer limit).
                        fileBase.Add(new Vector4(fusedCursor / 4096, fusedCursor % 4096, fc, 0));
                        if (fc > 0 && pTex != null)
                            regions.Add(new FileRegion { fusedBase = fusedCursor, count = fc, pos = pTex, col = cTex, rot = rTex, scale = scTex });
                        fusedCursor += fc;
                        globalFileId++;
                    }
                    int objTotalCount = fusedCursor - objSrcBase;
                    // SH: concat each file's coeff-major SH into the object's coeff-major region (n = objTotalCount).
                    // Per (file, coeff) gather is contiguous: dst = objShBase + coeff*objTotalCount + fileOffset,
                    // src = file SH coeff block at coeff*fileCount. Decode: id = shBase + coeff*n + (gsi - objSrcBase).
                    int shCoeffNew = s.sh != null && s.shCoeffCount > 0 ? s.shCoeffCount : 0;
                    shRequestedTexels += (long)shCoeffNew * objTotalCount;
                    // Keep the existing DC-only fallback at the signed-index limit instead of overflowing.
                    if (shCoeffNew > 0 && (long)shCursor + (long)shCoeffNew * objTotalCount > shAddressLimit) { shCoeffNew = 0; shDroppedObjects++; }
                    int objShBase = shCursor;
                    if (shCoeffNew > 0)
                    {
                        int fileOff = 0;
                        for (int f = 0; f < objFileCounts.Count; f++)
                        {
                            int fc = objFileCounts[f];
                            Texture2D shTex = s.sh != null && f < s.sh.Length ? s.sh[f] : null;
                            if (fc > 0 && shTex != null)
                                for (int c = 0; c < shCoeffNew; c++)
                                    shRegions.Add(new FileRegion { fusedBase = objShBase + c * objTotalCount + fileOff, count = fc, srcOffset = c * fc, pos = shTex });
                            fileOff += fc;
                        }
                        shCursor += shCoeffNew * objTotalCount;
                    }
                    entry = (objectFileOffset, objSrcBase, objTotalCount, objShBase, shCoeffNew);
                    if (sourceKey != null) sharedSources[sourceKey] = entry;
                }

                int cc = Mathf.Max(0, s.chunkCount);
                Color[] bMin = ReadbackF(s.chunkBoundsMin);
                Color[] bMax = ReadbackF(s.chunkBoundsMax);
                Color[] rng = ReadbackF(s.chunkRange);
                int rangeWidth = s.chunkRange != null ? Mathf.Max(1, s.chunkRange.width) : 1;
                int rangeMetaHeight = (Mathf.Max(1, cc) + rangeWidth - 1) / rangeWidth;
                int rangeStatsOffset = rangeWidth * rangeMetaHeight;
                bool hasRangeStats = s.chunkRange != null && s.chunkRange.height >= rangeMetaHeight * 2 && rng != null && rng.Length >= rangeStatsOffset + cc;
                for (int c = 0; c < cc; c++)
                {
                    Color cmin = bMin != null && c < bMin.Length ? bMin[c] : new Color(0, 0, 0, 0);
                    Color cmax = bMax != null && c < bMax.Length ? bMax[c] : new Color(0, 0, 0, 0);
                    Color crng = rng != null && c < rng.Length ? rng[c] : new Color(0, 0, 0, 0);
                    Color cstat = hasRangeStats ? rng[rangeStatsOffset + c] : new Color(1, 0, 0, 0);
                    if (cstat.r <= 0.0f || float.IsNaN(cstat.r) || float.IsInfinity(cstat.r)) cstat.r = 1.0f;
                    int localFile = Mathf.RoundToInt(cmax.a);
                    gMin.Add(cmin);
                    gMax.Add(new Color(cmax.r, cmax.g, cmax.b, entry.fileOffset + localFile)); // shared global file id
                    gRange.Add(new Color(crng.r, crng.g, crng.b, objId));                      // w = GLOBAL objectId
                    gRangeStats.Add(cstat);
                }
                totalChunks += cc;
                shParams.Add(new Color(entry.shCoeff, entry.totalCount, entry.shBase / 4096, entry.shBase % 4096)); // row0
                shParams.Add(new Color(s.shMin.x, s.shMin.y, s.shMin.z, s.shBand));                                  // row1
                shParams.Add(new Color(s.shRange.x, s.shRange.y, s.shRange.z, 0));                                   // row2
                // Store the count as hi/lo too: a float cannot represent every integer above 2^24.
                shParams.Add(new Color(entry.srcBase / 4096, entry.srcBase % 4096, entry.totalCount / 4096, entry.totalCount % 4096)); // row3

                objSplatCount[k] = entry.totalCount;
                objFileCount[k] = s.positions != null ? s.positions.Length : 0;
                objChunkCount[k] = cc;
                objShCoeff[k] = s.sh != null && s.shCoeffCount > 0 ? s.shCoeffCount : 0;
                objShDropped[k] = objShCoeff[k] > 0 && entry.shCoeff == 0; // source carried SH but it didn't fit
            }

            if (shDroppedObjects > 0)
            {
                string limitType = usePagedSH ? GSEditorText.T("paged", "ページ分割", "分页") : GSEditorText.T("single-texture", "単一テクスチャ", "单纹理");
                string remedy = usePagedSH
                    ? GSEditorText.T("Lower the SH band, reduce splat counts, or split the scene.", "SH バンドを下げる、スプラット数を減らす、またはシーンを分割してください。", "请降低 SH 阶数、减少 Splat 数量或拆分场景。")
                    : GSEditorText.T("Enable Paged Fused SH in the renderer, lower the SH band, reduce splat counts, or split the scene.", "レンダラーでページ分割 SH を有効にする、SH バンドを下げる、スプラット数を減らす、またはシーンを分割してください。", "请在渲染器中启用分页融合 SH、降低 SH 阶数、减少 Splat 数量或拆分场景。");
                Debug.LogWarning(GSEditorText.T(
                    $"[GaussianSplatFuse] Scene spherical harmonics ({shRequestedTexels:N0} texels) exceed the {limitType} fused SH limit ({shAddressLimit:N0}); SH was dropped for {shDroppedObjects} source(s), which now render without view-dependent color. {remedy}",
                    $"[GaussianSplatFuse] シーンの球面調和 ({shRequestedTexels:N0} テクセル) が {limitType} 統合 SH の上限 ({shAddressLimit:N0}) を超えたため、{shDroppedObjects} 個のソースの SH が破棄され、視点依存色なしで描画されます。{remedy}",
                    $"[GaussianSplatFuse] 场景球谐数据（{shRequestedTexels:N0} 个纹素）超过{limitType}融合 SH 上限（{shAddressLimit:N0}），已丢弃 {shDroppedObjects} 个源的 SH 数据；这些源将不带视角相关颜色渲染。{remedy}"));
            }

            FuseLODResult res = new FuseLODResult { totalSourceCount = fusedCursor, totalChunkCount = totalChunks, fileCount = globalFileId, objectCount = lod.Count, shDroppedObjects = shDroppedObjects,
                objSplatCount = objSplatCount, objFileCount = objFileCount, objChunkCount = objChunkCount, objShCoeff = objShCoeff, objShDropped = objShDropped };
            // Source set unchanged -> the GPU concat output is byte-identical; reuse the cached source textures
            // (saved on disk already) and skip the ~GB gather + readback. Guard on matching dimensions in case the
            // deduped layout shifted; fall back to a full concat if it does.
            bool reuseSrc = reuseFrom != null && reuseFrom.fusedPositions != null && reuseFrom.fusedColors != null
                && reuseFrom.fusedRotations != null && reuseFrom.fusedScales != null
                && reuseFrom.fusedPositions.width == fw && reuseFrom.fusedPositions.height == layout.Height;
            if (reuseSrc)
            {
                res.fusedPositions = reuseFrom.fusedPositions;
                res.fusedColors = reuseFrom.fusedColors;
                res.fusedRotations = reuseFrom.fusedRotations;
                res.fusedScales = reuseFrom.fusedScales;
            }

            // LOD chunk metadata (2D row-major; a unified scene can exceed the 16384 1-row limit). Bounds
            // are stacked into one texture: rows [0,metaH) hold min, [metaH,2metaH) hold max.
            int metaW = Mathf.Min(4096, Mathf.NextPowerOfTwo(Mathf.Max(1, totalChunks)));
            int metaH = (Mathf.Max(1, totalChunks) + metaW - 1) / metaW;
            res.metaWidth = metaW;
            Color[] boundsPix = new Color[metaW * metaH * 2];
            for (int c = 0; c < totalChunks && c < gMin.Count; c++) { boundsPix[c] = gMin[c]; boundsPix[metaW * metaH + c] = gMax[c]; }
            int metaPixels = metaW * metaH;
            Color[] rangePix = new Color[metaPixels * 2];
            for (int c = 0; c < totalChunks; c++)
            {
                rangePix[c] = c < gRange.Count ? gRange[c] : new Color(0, 0, 0, 0);
                rangePix[metaPixels + c] = c < gRangeStats.Count ? gRangeStats[c] : new Color(1, 0, 0, 0);
            }
            int fbw = Mathf.NextPowerOfTwo(Mathf.Max(1, fileBase.Count));
            Color[] fbPix = new Color[fbw];
            for (int i = 0; i < fileBase.Count; i++) fbPix[i] = new Color(fileBase[i].x, fileBase[i].y, fileBase[i].z, fileBase[i].w);

            // Each slice uses the same 4x4 block swizzle as the old single texture.
            ShPageLayout shLayout = ChooseShPageLayout(shCursor, usePagedSH ? FusedShPageDimension : 16384);
            if (shLayout.width > SystemInfo.maxTextureSize || shLayout.height > SystemInfo.maxTextureSize
                || (usePagedSH && (!SystemInfo.supports2DArrayTextures || shLayout.pages > SystemInfo.maxTextureArraySlices)))
            {
                string textureType = usePagedSH
                    ? GSEditorText.T("texture array with " + shLayout.pages + " slice(s)", shLayout.pages + " スライスのテクスチャ配列", shLayout.pages + " 层的纹理数组")
                    : GSEditorText.T("texture", "テクスチャ", "纹理");
                throw new InvalidOperationException(GSEditorText.T(
                    $"Fused SH requires a {shLayout.width}x{shLayout.height} {textureType}, which this editor GPU does not support.",
                    $"統合 SH には {shLayout.width}x{shLayout.height} の{textureType}が必要ですが、このエディタの GPU は対応していません。",
                    $"融合 SH 需要 {shLayout.width}×{shLayout.height} 的{textureType}，当前编辑器使用的 GPU 不支持该资源。"));
            }
            bool reuseSH = reuseSrc && (usePagedSH
                ? reuseFrom.fusedSHArray != null && reuseFrom.fusedSHArray.width == shLayout.width && reuseFrom.fusedSHArray.height == shLayout.height && reuseFrom.fusedSHArray.depth == shLayout.pages
                : reuseFrom.fusedSH != null && reuseFrom.fusedSH.width == shLayout.width && reuseFrom.fusedSH.height == shLayout.height);
            if (reuseSH)
            {
                if (usePagedSH) res.fusedSHArray = reuseFrom.fusedSHArray;
                else res.fusedSH = reuseFrom.fusedSH;
            }
            int shBpr = Mathf.Max(1, shLayout.width >> 2);
            res.fusedShCoordShift = GaussianSplatImporter.ComputeTextureCoordShift(shBpr);
            res.fusedShCoordMask = shBpr - 1;
            Color[] spPix = new Color[4 * Mathf.Max(1, lod.Count)];
            for (int i = 0; i < shParams.Count && i < spPix.Length; i++) spPix[i] = shParams[i];

            return new FuseLODPlan
            {
                outFolder = outFolder,
                assetPrefix = assetPrefix,
                result = res,
                regions = regions,
                shRegions = shRegions,
                reuseSrc = reuseSrc,
                reuseSH = reuseSH,
                usePagedSH = usePagedSH,
                sourceWidth = fw,
                sourceHeight = layout.Height,
                shWidth = shLayout.width,
                shHeight = shLayout.height,
                shPages = shLayout.pages,
                boundsPix = boundsPix,
                boundsWidth = metaW,
                boundsHeight = metaH * 2,
                rangePix = rangePix,
                rangeWidth = metaW,
                rangeHeight = metaH * 2,
                fileBasePix = fbPix,
                fileBaseWidth = fbw,
                shParamsPix = spPix,
                shParamsHeight = Mathf.Max(1, lod.Count)
            };
        }

        // Fully-resolved bake: CPU metadata pixel arrays + region lists for the GPU concat passes. The heavy
        // source/SH textures are produced by FuseLODJob streaming AsyncConcatReadback across frames.
        internal sealed class FuseLODPlan
        {
            public string outFolder;
            public string assetPrefix;
            public FuseLODResult result;
            public List<FileRegion> regions;
            public List<FileRegion> shRegions;
            public bool reuseSrc;
            public bool reuseSH;
            public bool usePagedSH;
            public int sourceWidth;
            public int sourceHeight;
            public int shWidth;
            public int shHeight;
            public int shPages;
            public Color[] boundsPix;
            public int boundsWidth;
            public int boundsHeight;
            public Color[] rangePix;
            public int rangeWidth;
            public int rangeHeight;
            public Color[] fileBasePix;
            public int fileBaseWidth;
            public Color[] shParamsPix;
            public int shParamsHeight;
        }

        // Runs the fuse one step at a time so the editor stays responsive: each source/SH stage kicks a GPU
        // concat + async readback and yields until it lands; metadata stages write their (small) texture inline.
        public sealed class FuseLODJob
        {
            enum Stage { Positions, Colors, Rotations, Scales, Bounds, Range, FileBase, SH, SHParams, Done, Failed }

            readonly FuseLODPlan _plan;
            Stage _stage;
            AsyncConcatReadback _readback;
            Texture2DArray _shArray;
            int _shPage;
            public FuseLODResult Result { get; private set; }
            public string Error { get; private set; }
            public bool IsDone => _stage == Stage.Done || _stage == Stage.Failed;
            public bool Failed => _stage == Stage.Failed;
            public float Progress => Mathf.Clamp01(((int)_stage + (_stage == Stage.SH && _plan.usePagedSH && !_plan.reuseSH ? _shPage / (float)_plan.shPages : 0f)) / (float)Stage.Done);
            public string StageName
            {
                get
                {
                    switch (_stage)
                    {
                        case Stage.Positions: return GSEditorText.T("Positions", "位置", "位置");
                        case Stage.Colors: return GSEditorText.T("Colors", "色", "颜色");
                        case Stage.Rotations: return GSEditorText.T("Rotations", "回転", "旋转");
                        case Stage.Scales: return GSEditorText.T("Scales", "スケール", "缩放");
                        case Stage.Bounds: return GSEditorText.T("Bounds", "境界", "边界");
                        case Stage.Range: return GSEditorText.T("Range", "範囲", "范围");
                        case Stage.FileBase: return GSEditorText.T("File Base", "ファイルベース", "文件基址");
                        case Stage.SH:
                            return _plan.usePagedSH && !_plan.reuseSH
                                ? GSEditorText.T($"SH page {_shPage + 1}/{_plan.shPages}", $"SH ページ {_shPage + 1}/{_plan.shPages}", $"SH 页面 {_shPage + 1}/{_plan.shPages}")
                                : "SH";
                        case Stage.SHParams: return GSEditorText.T("SH Parameters", "SH パラメーター", "SH 参数");
                        case Stage.Done: return GSEditorText.T("Done", "完了", "完成");
                        case Stage.Failed: return GSEditorText.T("Failed", "失敗", "失败");
                        default: return _stage.ToString();
                    }
                }
            }

            internal FuseLODJob(FuseLODPlan plan)
            {
                _plan = plan;
                Result = plan?.result;
                _stage = plan == null ? Stage.Done : Stage.Positions;
            }

            // Advances at most one stage per call. Returns true once the whole fused set is written (or failed).
            public bool Step()
            {
                if (IsDone) return true;
                try
                {
                    switch (_stage)
                    {
                        case Stage.Positions:
                            if (_plan.reuseSrc) _stage = Stage.Colors;
                            else if (StepSource(FileAttr.Pos, "_LODFusedPositions", out Texture2D pos)) { Result.fusedPositions = pos; _stage = Stage.Colors; }
                            return false;
                        case Stage.Colors:
                            if (_plan.reuseSrc) _stage = Stage.Rotations;
                            else if (StepSource(FileAttr.Col, "_LODFusedColors", out Texture2D col)) { Result.fusedColors = col; _stage = Stage.Rotations; }
                            return false;
                        case Stage.Rotations:
                            if (_plan.reuseSrc) _stage = Stage.Scales;
                            else if (StepSource(FileAttr.Rot, "_LODFusedRotations", out Texture2D rot)) { Result.fusedRotations = rot; _stage = Stage.Scales; }
                            return false;
                        case Stage.Scales:
                            if (_plan.reuseSrc) _stage = Stage.Bounds;
                            else if (StepSource(FileAttr.Scale, "_LODFusedScales", out Texture2D scl)) { Result.fusedScales = scl; _stage = Stage.Bounds; }
                            return false;
                        case Stage.Bounds:
                            Result.globalBounds = MakeTexF(_plan.boundsWidth, _plan.boundsHeight, _plan.boundsPix, TextureFormat.RGBAFloat, _plan.assetPrefix + "_LODGlobalBounds", _plan.outFolder);
                            _stage = Stage.Range;
                            return false;
                        case Stage.Range:
                            Result.globalRange = MakeTexF(_plan.rangeWidth, _plan.rangeHeight, _plan.rangePix, TextureFormat.RGBAFloat, _plan.assetPrefix + "_LODGlobalRange", _plan.outFolder);
                            _stage = Stage.FileBase;
                            return false;
                        case Stage.FileBase:
                            Result.fileBaseTable = MakeTexF(_plan.fileBaseWidth, 1, _plan.fileBasePix, TextureFormat.RGBAFloat, _plan.assetPrefix + "_LODFileBase", _plan.outFolder);
                            _stage = Stage.SH;
                            return false;
                        case Stage.SH:
                            if (_plan.reuseSH) _stage = Stage.SHParams;
                            else if (_plan.usePagedSH)
                            {
                                if (StepSH()) _stage = Stage.SHParams;
                            }
                            else if (StepReadback(_plan.shWidth, _plan.shHeight, _plan.shRegions, FileAttr.SH, "_LODFusedSH", out Texture2D sh))
                            {
                                try
                                {
                                    sh.Apply(false, true);
                                    Result.fusedSH = Save(sh, _plan.outFolder + "/" + sh.name + ".asset");
                                }
                                catch
                                {
                                    if (sh != null) UnityEngine.Object.DestroyImmediate(sh);
                                    throw;
                                }
                                _stage = Stage.SHParams;
                            }
                            return false;
                        case Stage.SHParams:
                            Result.shParams = MakeTexF(4, _plan.shParamsHeight, _plan.shParamsPix, TextureFormat.RGBAFloat, _plan.assetPrefix + "_LODShParams", _plan.outFolder);
                            _stage = Stage.Done;
                            return true;
                    }
                }
                catch (Exception e)
                {
                    Error = e.Message;
                    Debug.LogException(e);
                    _readback?.Dispose();
                    _readback = null;
                    if (_shArray != null) UnityEngine.Object.DestroyImmediate(_shArray);
                    _shArray = null;
                    _stage = Stage.Failed;
                    return true;
                }
                return false;
            }

            bool StepSource(FileAttr attr, string suffix, out Texture2D texture)
                => StepReadback(_plan.sourceWidth, _plan.sourceHeight, _plan.regions, attr, suffix, out texture);

            bool StepSH()
            {
                if (_shArray == null)
                {
                    _shArray = new Texture2DArray(_plan.shWidth, _plan.shHeight, _plan.shPages, TextureFormat.BC7, false, true)
                    {
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Point,
                        name = _plan.assetPrefix + "_LODFusedSHArray"
                    };
                }

                if (_readback == null)
                {
                    int pageTexels = _plan.shWidth * _plan.shHeight;
                    var pageRegions = new List<FileRegion>();
                    foreach (FileRegion region in _plan.shRegions)
                        if (ClipShRegion(region, _shPage, pageTexels, out FileRegion clipped)) pageRegions.Add(clipped);
                    _readback = AsyncConcatReadback.Start(_plan.shWidth, _plan.shHeight, pageRegions, FileAttr.SH, _shArray.name, _plan.outFolder);
                    return false;
                }

                if (!_readback.Step(out Texture2D page)) return false;
                _readback = null;
                try
                {
                    // Copy compressed CPU data into the array. A GPU-only CopyTexture would not reliably
                    // populate the serialized asset, and Apply could overwrite the copied GPU contents.
                    var bytes = page.GetRawTextureData<byte>();
                    if (bytes.Length != _plan.shWidth * _plan.shHeight)
                        throw new InvalidOperationException(GSEditorText.T("Unexpected BC7 page size while baking fused SH.", "統合 SH のベイク中に BC7 ページサイズが想定と異なりました。", "烘焙融合 SH 时，BC7 页面的大小不符合预期。"));
                    _shArray.SetPixelData(bytes, 0, _shPage);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(page);
                }
                _shPage++;
                if (_shPage < _plan.shPages) return false;

                _shArray.Apply(false, true);
                Result.fusedSHArray = SaveShArray(_shArray, _plan.outFolder + "/" + _shArray.name + ".asset");
                _shArray = null;
                return true;
            }

            // Kicks the concat on the first call, then completes once the async readback lands.
            bool StepReadback(int width, int height, List<FileRegion> regions, FileAttr attr, string suffix, out Texture2D texture)
            {
                texture = null;
                if (_readback == null)
                {
                    _readback = AsyncConcatReadback.Start(width, height, regions, attr, _plan.assetPrefix + suffix, _plan.outFolder);
                    return false;
                }
                if (!_readback.Step(out texture)) return false;
                _readback = null;
                return true;
            }
        }

        // One GPU concat pass into a temp RT + an async readback that persists the result as an asset. Step()
        // polls the readback so the (potentially ~GB) GPU stall doesn't block the editor.
        sealed class AsyncConcatReadback
        {
            readonly RenderTexture _rt;
            readonly AsyncGPUReadbackRequest _request;
            readonly int _width;
            readonly int _height;
            readonly string _name;
            readonly string _folder;
            readonly FileAttr _attr;

            AsyncConcatReadback(RenderTexture rt, AsyncGPUReadbackRequest request, int width, int height, string name, string folder, FileAttr attr)
            {
                _rt = rt;
                _request = request;
                _width = width;
                _height = height;
                _name = name;
                _folder = folder;
                _attr = attr;
            }

            public static AsyncConcatReadback Start(int width, int height, List<FileRegion> regions, FileAttr attr, string name, string folder)
            {
                bool floatData = attr == FileAttr.Scale;
                RenderTextureFormat rtFormat = floatData ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGB32;
                TextureFormat readbackFormat = floatData ? TextureFormat.RGBAFloat : TextureFormat.RGBA32;
                Material mat = ConcatMat();
                int dBlocks = Mathf.Max(1, width >> 2);
                mat.SetInt("_DstShift", Log2Pot(dBlocks));
                mat.SetInt("_DstMask", dBlocks - 1);

                RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, rtFormat, RenderTextureReadWrite.Linear);
                rt.filterMode = FilterMode.Point;
                RenderTexture prev = RenderTexture.active;
                try
                {
                    RenderTexture.active = rt;
                    GL.Clear(false, true, new Color(0, 0, 0, 0));
                    for (int i = 0; regions != null && i < regions.Count; i++)
                    {
                        Texture2D src = attr == FileAttr.Scale ? regions[i].scale : (attr == FileAttr.SH ? regions[i].pos : PickAttr(regions[i], attr));
                        if (src == null || regions[i].count <= 0) continue;
                        BlitRegion(mat, rt, src, regions[i].fusedBase, regions[i].count, regions[i].srcOffset);
                    }
                    return new AsyncConcatReadback(rt, AsyncGPUReadback.Request(rt, 0, readbackFormat), width, height, name, folder, attr);
                }
                catch
                {
                    RenderTexture.ReleaseTemporary(rt);
                    throw;
                }
                finally
                {
                    RenderTexture.active = prev;
                }
            }

            public bool Step(out Texture2D texture)
            {
                texture = null;
                if (!_request.done) return false;
                if (_request.hasError)
                {
                    throw new InvalidOperationException("Async GPU readback failed while fusing gaussian splat textures.");
                }
                if (_attr == FileAttr.Scale)
                {
                    // Re-encode to RGB9e5 (lossless: values originated from an RGB9e5 source) to keep the fused
                    // scale texture at 4 bytes/texel instead of 16.
                    texture = MakeTexF(_width, _height, _request.GetData<Color>().ToArray(), TextureFormat.RGB9e5Float, _name, _folder);
                }
                else if (_attr == FileAttr.SH)
                {
                    // Best-quality BC7 reduces SH color errors; keep the bytes readable for array pages.
                    var sh = new Texture2D(_width, _height, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = _name };
                    try
                    {
                        sh.SetPixels32(_request.GetData<Color32>().ToArray());
                        sh.Apply(false, false);
                        EditorUtility.CompressTexture(sh, TextureFormat.BC7, TextureCompressionQuality.Best);
                        if (sh.format != TextureFormat.BC7)
                            throw new InvalidOperationException(GSEditorText.T("BC7 compression failed while baking fused SH.", "統合 SH のベイク中に BC7 圧縮に失敗しました。", "烘焙融合 SH 时，BC7 压缩失败。"));
                        texture = sh;
                    }
                    catch
                    {
                        UnityEngine.Object.DestroyImmediate(sh);
                        throw;
                    }
                }
                else
                {
                    texture = MakeTex32(_width, _height, _request.GetData<Color32>().ToArray(), _name, _folder);
                }
                Dispose();
                return true;
            }

            public void Dispose()
            {
                if (_rt != null) RenderTexture.ReleaseTemporary(_rt);
            }
        }

        static int AlignToBCBlock(int value)
        {
            return Mathf.Max(4, ((Mathf.Max(1, value) + 3) / 4) * 4);
        }

        // ---- GPU source concatenation (GSFuseConcat.shader) --------------------------------------
        // A fused output texel's global index = block-swizzle(pixel); each file/object blits one
        // full-screen gather pass that writes only its [base, base+count) region (discard elsewhere),
        // so disjoint regions accumulate into one RT. One readback per attribute persists the asset.
        enum FileAttr { Pos, Col, Rot, Scale, SH }

        internal struct FileRegion
        {
            public int fusedBase;
            public int count;
            public int srcOffset;   // source-texel offset (interleaved SH gathers); 0 for whole-texture regions
            public Texture2D pos, col, rot, scale;
        }

        // Intersect a global SH gather with one page; both destination and source offsets must be adjusted.
        internal static bool ClipShRegion(FileRegion region, int pageIndex, int pageTexels, out FileRegion clipped)
        {
            if (pageIndex < 0 || pageTexels <= 0) throw new ArgumentOutOfRangeException();
            long pageStart = (long)pageIndex * pageTexels;
            long start = Math.Max((long)region.fusedBase, pageStart);
            long end = Math.Min((long)region.fusedBase + region.count, pageStart + pageTexels);
            clipped = region;
            if (end <= start) return false;
            clipped.fusedBase = checked((int)(start - pageStart));
            clipped.count = checked((int)(end - start));
            clipped.srcOffset = checked(region.srcOffset + (int)(start - region.fusedBase));
            return true;
        }

        static Material s_concatMat;

        static Material ConcatMat()
        {
            if (s_concatMat == null)
            {
                Shader sh = Shader.Find("Hidden/GaussianSplatting/FuseConcat");
                if (sh != null) s_concatMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
            return s_concatMat;
        }

        static int Log2Pot(int v)
        {
            int s = 0;
            v = Mathf.Max(1, v);
            while (v > 1) { v >>= 1; s++; }
            return s;
        }

        static void BlitRegion(Material mat, RenderTexture dst, Texture2D src, int fusedBase, int count, int srcOffset)
        {
            int sBlocks = Mathf.Max(1, src.width >> 2);
            mat.SetInt("_SrcShift", Log2Pot(sBlocks));
            mat.SetInt("_SrcMask", sBlocks - 1);
            mat.SetTexture("_SrcTex", src);
            // base and count both split hi/lo (each can exceed the float32 exact-integer limit 2^24).
            mat.SetVector("_ConcatParams", new Vector4(fusedBase / 4096, fusedBase % 4096, count / 4096, count % 4096));
            mat.SetVector("_SrcBaseParam", new Vector4(srcOffset / 4096, srcOffset % 4096, 0, 0));
            Graphics.Blit(src, dst, mat, 0);
        }

        static Texture2D PickAttr(FileRegion fr, FileAttr a)
        {
            return a == FileAttr.Pos ? fr.pos : (a == FileAttr.Col ? fr.col : fr.rot);
        }

        static Color[] ReadbackF(Texture2D tex)
        {
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var rd = new Texture2D(tex.width, tex.height, TextureFormat.RGBAFloat, false, true);
            rd.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0, false);
            rd.Apply(false, false);
            var px = rd.GetPixels();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(rd);
            return px;
        }

        static Texture2D MakeTex32(int w, int h, Color32[] px, string name, string folder)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = name };
            t.SetPixels32(px); t.Apply(false, true);
            return Save(t, folder + "/" + name + ".asset");
        }

        static Texture2D MakeTexF(int w, int h, Color[] px, TextureFormat fmt, string name, string folder)
        {
            var t = new Texture2D(w, h, fmt, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = name };
            t.SetPixels(px); t.Apply(false, true);
            return Save(t, folder + "/" + name + ".asset");
        }

        static Texture2D Save(Texture2D t, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) { EditorUtility.CopySerialized(t, existing); UnityEngine.Object.DestroyImmediate(t); return existing; }
            AssetDatabase.CreateAsset(t, path);
            return t;
        }

        static Texture2DArray SaveShArray(Texture2DArray t, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
            if (existing != null) { EditorUtility.CopySerialized(t, existing); UnityEngine.Object.DestroyImmediate(t); return existing; }
            AssetDatabase.CreateAsset(t, path);
            return t;
        }
    }
}
#endif
