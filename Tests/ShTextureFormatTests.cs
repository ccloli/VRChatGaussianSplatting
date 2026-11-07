using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Tests
{
    public class ShTextureFormatTests
    {
        [TestCase(0, GaussianSplatImporter.SHCompression.RGB565)]
        [TestCase(1, GaussianSplatImporter.SHCompression.BC1)]
        [TestCase(2, GaussianSplatImporter.SHCompression.BC7)]
        [TestCase(3, GaussianSplatImporter.SHCompression.RGBA32)]
        public void ImportMetadata_PreservesFormatValues(int savedValue, GaussianSplatImporter.SHCompression expected)
        {
            var metadata = GaussianSplatImporter.ImportMetadata.FromJson("{\"options\":{\"shCompression\":" + savedValue + "}}");
            Assert.AreEqual(expected, metadata.options.shCompression);
        }

        [TestCase(GaussianSplatImporter.SHCompression.RGBA32, TextureFormat.RGBA32)]
        [TestCase(GaussianSplatImporter.SHCompression.RGB565, TextureFormat.RGB565)]
        [TestCase(GaussianSplatImporter.SHCompression.BC7, TextureFormat.BC7)]
        [TestCase(GaussianSplatImporter.SHCompression.BC1, TextureFormat.DXT1)]
        public void ShTexture_PreservesRgbAndUsesSelectedFormat(GaussianSplatImporter.SHCompression mode, TextureFormat expectedFormat)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !SystemInfo.supportsRenderTextures || !SystemInfo.SupportsTextureFormat(expectedFormat))
                Assert.Ignore("The editor graphics device cannot test this texture format.");

            // SH inputs have zero alpha; BC1 must preserve RGB instead of treating the block as transparent.
            Color expected = new Color(0.21f, 0.43f, 0.77f, 0.0f);
            var pixels = new Color[16];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = expected;

            Texture2D texture = null;
            Texture2D readback = null;
            RenderTexture target = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                texture = GaussianSplatImporter.CreateShTexture(4, 4, "SH Test", pixels, mode);
                Assert.AreEqual(expectedFormat, texture.format);
                target = RenderTexture.GetTemporary(4, 4, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                readback = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
                readback.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
                readback.Apply(false, false);
                Color actual = readback.GetPixel(0, 0);
                float tolerance = mode == GaussianSplatImporter.SHCompression.RGBA32 ? 0.005f : 0.05f;
                Assert.That(actual.r, Is.EqualTo(expected.r).Within(tolerance));
                Assert.That(actual.g, Is.EqualTo(expected.g).Within(tolerance));
                Assert.That(actual.b, Is.EqualTo(expected.b).Within(tolerance));
            }
            finally
            {
                RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (readback != null) Object.DestroyImmediate(readback);
                if (texture != null) Object.DestroyImmediate(texture);
            }
        }
    }
}
