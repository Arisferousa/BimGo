using System.IO;
using System.IO.Compression;
using System.Linq;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Viewer round: the manifest's <c>viewer</c> flag (a file made for the simple viewer) and the viewer's keys.
    /// </summary>
    [TestClass]
    public sealed class ViewerTests
    {
        private static string ManifestText(string path)
        {
            using ZipArchive zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("manifest.json").Open());
            return reader.ReadToEnd();
        }

        [TestMethod]
        public void Viewer_RoundTripsThroughTheManifest()
        {
            using var folder = new TempFolder();
            BimGoDocument document = TestData.BuildDocument();
            document.Viewer = new ViewerDto { Start = ViewerStart.WALK };
            string path = folder.File("viewer.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);

            ManifestDto manifest = BimGoReader.ReadManifest(path, out error);
            Assert.IsNotNull(manifest, error);
            Assert.IsNotNull(manifest.Viewer);
            Assert.IsTrue(manifest.Viewer.Open);

            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            Assert.IsNotNull(read.Viewer);
            Assert.IsTrue(read.Viewer.Open);
            Assert.AreEqual(ViewerStart.WALK, read.Viewer.Start);
            Assert.AreNotSame(document.Viewer, read.Viewer);
        }

        [TestMethod]
        public void Viewer_IsLeftOutOfOrdinaryFiles()
        {
            using var folder = new TempFolder();
            BimGoDocument document = TestData.BuildDocument();
            string path = folder.File("full.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);
            StringAssert.DoesNotMatch(ManifestText(path), new System.Text.RegularExpressions.Regex("\"viewer\""));
            Assert.IsNull(BimGoReader.Read(path, new LaunchSettings(), out error).Viewer, error);

            // Switched off: written as an ordinary file
            document.Viewer = new ViewerDto { Open = false };
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out error), error);
            StringAssert.DoesNotMatch(ManifestText(path), new System.Text.RegularExpressions.Regex("\"viewer\""));
            Assert.IsNull(BimGoReader.Read(path, new LaunchSettings(), out error).Viewer, error);
        }

        [TestMethod]
        public void Viewer_UnknownStartReadsAsOrbit()
        {
            Assert.AreEqual(ViewerStart.ORBIT, new ViewerDto { Start = "sideways" }.Copy().Start);
            Assert.AreEqual(ViewerStart.ORBIT, new ViewerDto { Start = null }.Copy().Start);
            Assert.AreEqual(ViewerStart.WALK, new ViewerDto { Start = ViewerStart.WALK }.Copy().Start);
        }

        [TestMethod]
        public void Viewer_SituationWalksAndSavesButHasNoGunsOrEdits()
        {
            InputSituation viewer = InputCatalog.Situation(InputCatalog.SIT_VIEWER);
            Assert.IsNotNull(viewer);
            foreach (InputAction action in new[] { InputAction.MoveForward, InputAction.Run, InputAction.GoHome, InputAction.Save, InputAction.Fullscreen, InputAction.Fly,
                InputAction.ViewerMeasure, InputAction.ViewerSection, InputAction.ViewerComment, InputAction.ViewerViews, InputAction.ViewerSun, InputAction.ViewerGhost,
                InputAction.ViewerFaster, InputAction.ViewerSlower })
            {
                Assert.IsTrue(viewer.Contains(action), $"{action} should be live in the viewer");
            }
            foreach (InputAction action in new[] { InputAction.Gun1, InputAction.GunWheel, InputAction.Undo, InputAction.ClashMode, InputAction.MediaPanel,
                InputAction.KioskExit, InputAction.SunPanel })
            {
                Assert.IsFalse(viewer.Contains(action), $"{action} should not be live in the viewer");
            }
            Assert.IsTrue(viewer.Help.Any(h => h.Label.StartsWith("Orbit")));

            // The viewer's own keys are live there only (the walkthrough keeps M, X, C, B, O, G for its own)
            foreach (InputSituation other in InputCatalog.Situations)
            {
                if (ReferenceEquals(other, viewer)) { continue; }
                Assert.IsFalse(other.Contains(InputAction.ViewerMeasure), $"ViewerMeasure is live in {other.Name}");
                Assert.IsFalse(other.Contains(InputAction.ViewerGhost), $"ViewerGhost is live in {other.Name}");
            }
            Assert.AreEqual("M", new KeyMap().Primary(InputAction.ViewerMeasure).ToString());
        }
    }
}
