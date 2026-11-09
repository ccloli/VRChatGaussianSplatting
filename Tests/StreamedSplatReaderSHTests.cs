using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using GaussianSplatting;
using GaussianSplatting.Editor.Importers;
using UnityEngine;

namespace GaussianSplatting.Tests
{
    // f_rest layout detection. Two packings exist in the wild and are told apart only by which property
    // names are present: INRIA separates the SH channels by 15, others pack them at the exported count.
    public class StreamedSplatReaderSHTests
    {
        static Dictionary<string, int> Inria(int coeffCount)
        {
            Dictionary<string, int> offsets = new Dictionary<string, int>();
            for (int coeff = 0; coeff < coeffCount; coeff++)
            {
                for (int channel = 0; channel < 3; channel++) offsets[$"f_rest_{coeff + channel * 15}"] = offsets.Count * 4;
            }
            return offsets;
        }

        static Dictionary<string, int> Consecutive(int coeffCount)
        {
            Dictionary<string, int> offsets = new Dictionary<string, int>();
            for (int i = 0; i < coeffCount * 3; i++) offsets[$"f_rest_{i}"] = i * 4;
            return offsets;
        }

        [TestCase(15)]
        [TestCase(8)]
        [TestCase(3)]
        public void Inria_DetectsBandAtStride15(int coeffCount)
        {
            Assert.AreEqual(coeffCount, StreamedSplatReader.DetectSHCoeffCount(Inria(coeffCount), out int stride));
            Assert.AreEqual(15, stride);
        }

        [TestCase(8)]
        [TestCase(3)]
        public void Consecutive_DetectsBandAtItsOwnStride(int coeffCount)
        {
            Assert.AreEqual(coeffCount, StreamedSplatReader.DetectSHCoeffCount(Consecutive(coeffCount), out int stride));
            Assert.AreEqual(coeffCount, stride);
        }

        [Test]
        public void FullSH3_IsIdenticalUnderBothPackings()
        {
            Assert.AreEqual(15, StreamedSplatReader.DetectSHCoeffCount(Consecutive(15), out int stride));
            Assert.AreEqual(15, stride);
        }

        // A consecutive file carrying more than SH3 must not be mistaken for stride 15 just because
        // f_rest_0..44 happen to be present; the extra coefficients are dropped, the stride is not.
        [Test]
        public void ConsecutiveBeyondSH3_KeepsItsStrideAndCapsAtSH3()
        {
            Assert.AreEqual(15, StreamedSplatReader.DetectSHCoeffCount(Consecutive(24), out int stride));
            Assert.AreEqual(24, stride);
        }

        [Test]
        public void NoRestProperties_IsDcOnly()
        {
            Assert.AreEqual(0, StreamedSplatReader.DetectSHCoeffCount(new Dictionary<string, int>(), out int stride));
            Assert.AreEqual(15, stride);
        }

        // A truncated INRIA write must step down to the largest complete band, not read past the gap.
        [Test]
        public void IncompleteInriaSH3_StepsDownToSH2()
        {
            Dictionary<string, int> offsets = Inria(15);
            offsets.Remove("f_rest_44");
            Assert.AreEqual(8, StreamedSplatReader.DetectSHCoeffCount(offsets, out int stride));
            Assert.AreEqual(15, stride);
        }

        [TestCase(15, 8_947_848, false, SHBand.SH3)]
        [TestCase(15, 8_947_849, false, SHBand.SH2)]
        [TestCase(15, 15_000_000, false, SHBand.SH2)]
        [TestCase(15, 15_000_000, true, SHBand.SH3)]
        [TestCase(15, 17_895_697, true, SHBand.SH3)]
        [TestCase(15, 17_895_698, true, SHBand.SH2)]
        [TestCase(15, 33_554_432, true, SHBand.SH2)]
        [TestCase(15, 33_554_433, true, SHBand.SH1)]
        [TestCase(8, 10_000_000, true, SHBand.SH2)]
        [TestCase(15, int.MaxValue, true, SHBand.SH0)]
        public void ImportBudget_SelectsOnlyAvailableWholeBands(int sourceCoefficients, int storedSplats, bool extended, SHBand expected)
        {
            Assert.AreEqual(expected, GaussianSplatImporter.ResolveLODImportSHBand(sourceCoefficients, storedSplats, extended));
        }

        [Test]
        public void LegacyImportBudgetApiKeepsOriginalLimit()
        {
            Assert.AreEqual(SHBand.SH2, GaussianSplatImporter.ResolveLODImportSHBand(15, 15_000_000));
        }

        [Test]
        public void ImportMetadata_DefaultsToOriginalLimitAndPreservesOptIn()
        {
            var legacy = GaussianSplatImporter.ImportMetadata.FromJson("{\"options\":{\"lodReusePercent\":50}}");
            Assert.IsFalse(legacy.options.lodExtendedSHImport);
            var metadata = new GaussianSplatImporter.ImportMetadata
            {
                options = new GaussianSplatImporter.ImportOptions { lodReusePercent = 50, lodExtendedSHImport = true }
            };
            var restored = GaussianSplatImporter.ImportMetadata.FromJson(GaussianSplatImporter.ImportMetadata.ToJson(metadata));
            Assert.IsTrue(restored.options.lodExtendedSHImport);
            Assert.AreEqual(50, restored.options.lodReusePercent);
        }

        [Test]
        public void ImportWizard_PropagatesOptInAndCopiesItToOtherEntries()
        {
            var wizard = ScriptableObject.CreateInstance<GaussianSplatImportWizard>();
            try
            {
                var wizardType = typeof(GaussianSplatImportWizard);
                var entryType = wizardType.GetNestedType("ImportEntry", BindingFlags.NonPublic);
                var source = System.Activator.CreateInstance(entryType, true);
                var target = System.Activator.CreateInstance(entryType, true);
                entryType.GetField("lodExtendedSHImport").SetValue(source, true);

                var optionsMethod = wizardType.GetMethod("GetEntryOptions", BindingFlags.Instance | BindingFlags.NonPublic);
                var options = (GaussianSplatImporter.ImportOptions)optionsMethod.Invoke(wizard, new[] { source });
                Assert.IsTrue(options.lodExtendedSHImport);

                var entries = (IList)wizardType.GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(wizard);
                entries.Add(source);
                entries.Add(target);
                wizardType.GetMethod("CopyEntrySettingsToAll", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(wizard, new[] { source });
                Assert.IsTrue((bool)entryType.GetField("lodExtendedSHImport").GetValue(target));

                entryType.GetField("importMode").SetValue(source, GaussianSplatImportWizard.ImportMode.Standalone);
                options = (GaussianSplatImporter.ImportOptions)optionsMethod.Invoke(wizard, new[] { source });
                Assert.IsFalse(options.lodExtendedSHImport);
            }
            finally
            {
                Object.DestroyImmediate(wizard);
            }
        }
    }
}
