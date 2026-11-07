using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace GaussianSplatting.Tests
{
    public class FusedShPagesTests
    {
        [TestCase(1, 1)]
        [TestCase(33554433, 1)]
        [TestCase(8192 * 8192 - 1, 1)]
        [TestCase(8192 * 8192, 1)]
        [TestCase(8192 * 8192 + 1, 2)]
        [TestCase(16384 * 16384, 4)]
        [TestCase(16384 * 16384 + 1, 5)]
        [TestCase(int.MaxValue, 32)]
        public void PageLayoutCoversTexelsWithoutExceeding8K(int texels, int expectedPages)
        {
            var layout = GaussianSplatFuse.ChooseShPageLayout(texels);
            Assert.AreEqual(expectedPages, layout.pages);
            Assert.LessOrEqual(layout.width, 8192);
            Assert.LessOrEqual(layout.height, 8192);
            Assert.AreEqual(0, layout.width % 4);
            Assert.AreEqual(0, layout.height % 4);
            Assert.GreaterOrEqual((long)layout.PageTexels * layout.pages, texels);
            if (layout.pages > 1) Assert.Less((long)layout.PageTexels * (layout.pages - 1), texels);
        }

        [Test]
        public void GatherCrossingPageBoundaryAdjustsBothOffsets()
        {
            var source = new GaussianSplatFuse.FileRegion { fusedBase = 60, count = 12, srcOffset = 17 };
            Assert.IsTrue(GaussianSplatFuse.ClipShRegion(source, 0, 64, out var first));
            Assert.AreEqual(60, first.fusedBase);
            Assert.AreEqual(4, first.count);
            Assert.AreEqual(17, first.srcOffset);
            Assert.IsTrue(GaussianSplatFuse.ClipShRegion(source, 1, 64, out var second));
            Assert.AreEqual(0, second.fusedBase);
            Assert.AreEqual(8, second.count);
            Assert.AreEqual(21, second.srcOffset);
            Assert.IsFalse(GaussianSplatFuse.ClipShRegion(source, 2, 64, out _));
        }

        [Test]
        public void LastAddressablePageDoesNotOverflowPageEnd()
        {
            var source = new GaussianSplatFuse.FileRegion { fusedBase = int.MaxValue - 5, count = 5, srcOffset = 7 };
            Assert.IsTrue(GaussianSplatFuse.ClipShRegion(source, 31, 8192 * 8192, out var clipped));
            Assert.AreEqual(8192 * 8192 - 6, clipped.fusedBase);
            Assert.AreEqual(5, clipped.count);
            Assert.AreEqual(7, clipped.srcOffset);
        }

        [Test]
        public void PagedModeKeepsSourcesBeyondTheSingleTextureLimit()
        {
            if (!SystemInfo.supports2DArrayTextures || SystemInfo.maxTextureSize < 16384 || SystemInfo.maxTextureArraySlices < 5)
                Assert.Ignore("This test requires a 16K texture and five 8K texture-array slices.");
            var first = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
            var second = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
            var metadata = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true);
            try
            {
                metadata.Apply(false, false);
                GaussianSplatFuse.FuseLODSource Source(Texture2D texture) => new GaussianSplatFuse.FuseLODSource
                {
                    positions = new[] { texture }, fileSplatCounts = new[] { 10_000_000 },
                    sh = new[] { texture }, shCoeffCount = 15, packed = true,
                    chunkBoundsMin = metadata, chunkBoundsMax = metadata, chunkRange = metadata
                };
                var sources = new List<GaussianSplatFuse.FuseLODSource> { Source(first), Source(second) };
                var single = GaussianSplatFuse.CreateFuseLODJob(sources, "Assets", "Unused", usePagedSH: false);
                var paged = GaussianSplatFuse.CreateFuseLODJob(sources, "Assets", "Unused", usePagedSH: true);
                Assert.AreEqual(1, single.Result.shDroppedObjects);
                Assert.AreEqual(0, paged.Result.shDroppedObjects);
                Assert.IsTrue(single.Result.objShDropped[1]);
                Assert.IsFalse(paged.Result.objShDropped[1]);
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(metadata);
            }
        }

        [Test]
        public void ChangingModeReusesOnlyMatchingShRepresentation()
        {
            if (!SystemInfo.supports2DArrayTextures) Assert.Ignore("Texture arrays are unavailable.");
            var source = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
            var metadata = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true);
            var shLayout = GaussianSplatFuse.ChooseShPageLayout(30);
            var single = new Texture2D(shLayout.width, shLayout.height, TextureFormat.RGBA32, false, true);
            var array = new Texture2DArray(shLayout.width, shLayout.height, 1, TextureFormat.RGBA32, false, true);
            try
            {
                metadata.Apply(false, false);
                var sources = new List<GaussianSplatFuse.FuseLODSource>
                {
                    new GaussianSplatFuse.FuseLODSource
                    {
                        positions = new[] { source }, fileSplatCounts = new[] { 2 }, sh = new[] { source },
                        shCoeffCount = 15, packed = true, chunkBoundsMin = metadata, chunkBoundsMax = metadata, chunkRange = metadata
                    }
                };
                var cached = new GaussianSplatFuse.FuseLODResult
                {
                    fusedPositions = source, fusedColors = source, fusedRotations = source, fusedScales = source,
                    fusedSH = single, fusedSHArray = array
                };
                var singleResult = GaussianSplatFuse.CreateFuseLODJob(sources, "Assets", "Unused", cached, false).Result;
                var pagedResult = GaussianSplatFuse.CreateFuseLODJob(sources, "Assets", "Unused", cached, true).Result;
                Assert.AreSame(single, singleResult.fusedSH);
                Assert.IsNull(singleResult.fusedSHArray);
                Assert.AreSame(array, pagedResult.fusedSHArray);
                Assert.IsNull(pagedResult.fusedSH);
                cached.fusedSHArray = null;
                var switched = GaussianSplatFuse.CreateFuseLODJob(sources, "Assets", "Unused", cached, true).Result;
                Assert.IsNull(switched.fusedSHArray, "A single texture cannot be reused as an array.");
                Assert.AreSame(source, switched.fusedPositions, "Switching SH mode should reuse unchanged source textures.");
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(metadata);
                Object.DestroyImmediate(single);
                Object.DestroyImmediate(array);
            }
        }

        // Uses tiny pages to exercise the real GPU gather, BC7 compression, array serialization and reload.
        [UnityTest]
        public IEnumerator PagedBakePreservesDataAcrossPageAndAssetReload()
        {
            if (!SystemInfo.supports2DArrayTextures || !SystemInfo.supportsAsyncGPUReadback || !SystemInfo.SupportsTextureFormat(TextureFormat.BC7))
                Assert.Ignore("This test requires texture arrays, BC7 and async GPU readback.");
            if (Shader.Find("Hidden/GaussianSplatting/FuseConcat") == null)
                Assert.Ignore("Fuse concat shader is unavailable.");

            string folder = AssetDatabase.GenerateUniqueAssetPath("Assets/__GSPagedSHTests");
            const string prefix = "PageTest";
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            var source = new Texture2D(8, 8, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            try
            {
                var pixels = new Color32[64];
                for (int i = 4; i < 12; i++) pixels[GaussianSplatImporter.ComputePackedTextureIndex(i, 8)] = new Color32(255, 0, 0, 255);
                source.SetPixels32(pixels);
                source.Apply(false, false);

                var plan = new GaussianSplatFuse.FuseLODPlan
                {
                    outFolder = folder, assetPrefix = prefix, result = new GaussianSplatFuse.FuseLODResult(),
                    reuseSrc = true, usePagedSH = true,
                    shWidth = 8, shHeight = 8, shPages = 2,
                    shRegions = new List<GaussianSplatFuse.FileRegion>
                    {
                        new GaussianSplatFuse.FileRegion { fusedBase = 60, count = 8, srcOffset = 4, pos = source }
                    },
                    boundsWidth = 1, boundsHeight = 1, boundsPix = new Color[1],
                    rangeWidth = 1, rangeHeight = 1, rangePix = new Color[1],
                    fileBaseWidth = 1, fileBasePix = new Color[1],
                    shParamsHeight = 1, shParamsPix = new Color[4]
                };
                var job = new GaussianSplatFuse.FuseLODJob(plan);
                double deadline = EditorApplication.timeSinceStartup + 60;
                while (!job.IsDone && EditorApplication.timeSinceStartup < deadline) { job.Step(); yield return null; }
                Assert.IsTrue(job.IsDone, "Paged bake timed out.");
                Assert.IsFalse(job.Failed, job.Error);

                Texture2DArray array = job.Result.fusedSHArray;
                Assert.IsNotNull(array);
                Assert.AreEqual(2, array.depth);
                AssetDatabase.SaveAssets();
                job.Result.fusedSHArray = null;
                Resources.UnloadAsset(array);
                array = AssetDatabase.LoadAssetAtPath<Texture2DArray>(folder + "/" + prefix + "_LODFusedSHArray.asset");
                Assert.IsNotNull(array, "Serialized SH array did not reload.");

                var request = AsyncGPUReadback.Request(array, 0, 0, 8, 0, 8, 0, 2, TextureFormat.RGBA32);
                deadline = EditorApplication.timeSinceStartup + 60;
                while (!request.done && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(request.done, "Array readback timed out.");
                Assert.IsFalse(request.hasError, "Array readback failed.");
                var first = request.GetData<Color32>(0);
                var second = request.GetData<Color32>(1);
                for (int i = 60; i < 64; i++) Assert.Greater(first[GaussianSplatImporter.ComputePackedTextureIndex(i, 8)].r, 180);
                for (int i = 0; i < 4; i++) Assert.Greater(second[GaussianSplatImporter.ComputePackedTextureIndex(i, 8)].r, 180);
                Assert.Less(first[GaussianSplatImporter.ComputePackedTextureIndex(0, 8)].r, 50);
                Assert.Less(second[GaussianSplatImporter.ComputePackedTextureIndex(32, 8)].r, 50);

                // Rebake to the same asset path with a different slice count, then reload it again.
                plan.shPages = 1;
                plan.shRegions[0] = new GaussianSplatFuse.FileRegion { fusedBase = 0, count = 8, srcOffset = 4, pos = source };
                var rebake = new GaussianSplatFuse.FuseLODJob(plan);
                deadline = EditorApplication.timeSinceStartup + 60;
                while (!rebake.IsDone && EditorApplication.timeSinceStartup < deadline) { rebake.Step(); yield return null; }
                Assert.IsTrue(rebake.IsDone, "SH rebake timed out.");
                Assert.IsFalse(rebake.Failed, rebake.Error);
                array = rebake.Result.fusedSHArray;
                Assert.AreEqual(1, array.depth);
                AssetDatabase.SaveAssets();
                rebake.Result.fusedSHArray = null;
                Resources.UnloadAsset(array);
                array = AssetDatabase.LoadAssetAtPath<Texture2DArray>(folder + "/" + prefix + "_LODFusedSHArray.asset");
                Assert.IsNotNull(array, "Rebaked SH array did not reload.");
                Assert.AreEqual(1, array.depth);
                request = AsyncGPUReadback.Request(array, 0, 0, 8, 0, 8, 0, 1, TextureFormat.RGBA32);
                deadline = EditorApplication.timeSinceStartup + 60;
                while (!request.done && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(request.done, "Rebaked array readback timed out.");
                Assert.IsFalse(request.hasError, "Rebaked array readback failed.");
                var rebakedPage = request.GetData<Color32>();
                for (int i = 0; i < 8; i++) Assert.Greater(rebakedPage[GaussianSplatImporter.ComputePackedTextureIndex(i, 8)].r, 180);
            }
            finally
            {
                Object.DestroyImmediate(source);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
