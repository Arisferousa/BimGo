using System;
using System.Collections.Generic;
using System.IO;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Updating a reviewed model from a newer IFC: the review is kept, the source IFC is recognised and its changes seen.
    /// </summary>
    [TestClass]
    public sealed class ModelUpdateTests
    {
        private static void MadeFromIfc(SceneData scene, string ifcPath, DateTime importedUtc)
        {
            scene.Provenance.ModelPath = ifcPath;
            scene.Provenance.ModelKey = "ifc:project-guid";
            scene.Provenance.ExtractedUtc = importedUtc;
        }

        [TestMethod]
        public void WithNewModel_KeepsTheReviewAndDropsTheEdits()
        {
            using var folder = new TempFolder();
            BimGoDocument reviewed = TestData.BuildDocument();
            reviewed.Photos = new PhotoDocument { Photos = new List<SitePhoto> { new() { Name = "a.jpg", Group = "Block A", Width = 10, Height = 10 } } };
            reviewed.PhotoImages = new Dictionary<string, byte[]> { [reviewed.Photos.Photos[0].Image] = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 } };
            reviewed.Viewer = new ViewerDto { Start = ViewerStart.ORBIT };
            reviewed.Path = folder.File("site.bimgo");

            SceneData newer = TestData.BuildScene();
            BimGoDocument updated = ModelUpdate.WithNewModel(reviewed, newer);

            Assert.AreSame(newer, updated.Scene);
            Assert.AreSame(reviewed.Comments, updated.Comments);
            Assert.AreSame(reviewed.Bookmarks, updated.Bookmarks);
            Assert.AreSame(reviewed.Photos, updated.Photos);
            Assert.AreSame(reviewed.PhotoImages, updated.PhotoImages);
            Assert.AreSame(reviewed.Sun, updated.Sun);
            Assert.AreSame(reviewed.Visibility, updated.Visibility);
            Assert.AreSame(reviewed.Viewer, updated.Viewer);
            Assert.AreEqual(reviewed.Path, updated.Path);
            Assert.AreEqual(0, updated.Journal.Count);
            Assert.IsNull(updated.Materials);
            Assert.AreEqual(3, reviewed.Journal.Count); // the input is untouched

            // Written and read back: the review is there with the new model
            Assert.IsTrue(BimGoWriter.Write(updated.Path, updated, TestData.Writer, FileKinds.SAVE, out string error), error);
            BimGoDocument read = BimGoReader.Read(updated.Path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            Assert.AreEqual("c1", read.Comments.Comments[0].Id);
            Assert.AreEqual("Entry", read.Bookmarks.Bookmarks[0].Name);
            Assert.AreEqual("Block A", read.Photos.Photos[0].Group);
            Assert.IsNotNull(read.Viewer);
            Assert.AreEqual(0, read.Journal.Count);
        }

        [TestMethod]
        public void SourceIfc_OnlyForIfcImports()
        {
            Assert.IsNull(ModelUpdate.SourceIfc(null));
            Assert.IsNull(ModelUpdate.SourceIfc(new ModelProvenance { ModelPath = @"C:\models\site.rvt", ModelKey = "revit-uid" }));
            Assert.IsNull(ModelUpdate.SourceIfc(new ModelProvenance { ModelPath = "site.ifc", ModelKey = "revit-uid" }));
            Assert.AreEqual("site.ifc", ModelUpdate.SourceIfc(new ModelProvenance { ModelPath = "site.ifc", ModelKey = "ifc:guid" }));
            Assert.AreEqual("site.ifczip", ModelUpdate.SourceIfc(new ModelProvenance { ModelPath = "site.ifczip", ModelKey = "ifc:guid" }));
        }

        [TestMethod]
        public void SourceChanged_WhenTheIfcIsSavedAfterTheImport()
        {
            using var folder = new TempFolder();
            string ifc = folder.File("site.ifc");
            File.WriteAllText(ifc, "ISO-10303-21;");
            DateTime imported = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
            var provenance = new ModelProvenance { ModelPath = ifc, ModelKey = "ifc:guid", ExtractedUtc = imported };

            File.SetLastWriteTimeUtc(ifc, imported.AddMinutes(-5)); // exported, then imported
            Assert.IsFalse(ModelUpdate.SourceChanged(provenance, out _, out _));

            File.SetLastWriteTimeUtc(ifc, imported.AddDays(3)); // exported again
            Assert.IsTrue(ModelUpdate.SourceChanged(provenance, out string path, out DateTime saved));
            Assert.AreEqual(ifc, path);
            Assert.AreEqual(imported.AddDays(3), saved);

            File.Delete(ifc); // moved away (e.g. the file was sent on): nothing to offer
            Assert.IsFalse(ModelUpdate.SourceChanged(provenance, out _, out _));
        }

        [TestMethod]
        public void FindModelFor_FindsTheBimgoMadeFromTheIfc()
        {
            using var folder = new TempFolder();
            using var elsewhere = new TempFolder();
            string ifc = folder.File("site.ifc");
            File.WriteAllText(ifc, "ISO-10303-21;");

            // Beside the IFC: one made from another IFC, one from this
            BimGoDocument other = TestData.BuildDocument();
            MadeFromIfc(other.Scene, folder.File("other.ifc"), DateTime.UtcNow);
            Assert.IsTrue(BimGoWriter.Write(folder.File("other.bimgo"), other, TestData.Writer, FileKinds.SAVE, out string error), error);
            Assert.IsNull(ModelUpdate.FindModelFor(ifc, null));

            BimGoDocument mine = TestData.BuildDocument();
            MadeFromIfc(mine.Scene, ifc, DateTime.UtcNow);
            string beside = folder.File("review.bimgo");
            Assert.IsTrue(BimGoWriter.Write(beside, mine, TestData.Writer, FileKinds.SAVE, out error), error);
            Assert.AreEqual(Path.GetFullPath(beside), ModelUpdate.FindModelFor(ifc, null));

            // Saved elsewhere and in the recent list: found too, and the most recently saved wins
            System.Threading.Thread.Sleep(20);
            string away = elsewhere.File("copy.bimgo");
            Assert.IsTrue(BimGoWriter.Write(away, mine, TestData.Writer, FileKinds.SAVE, out error), error);
            Assert.AreEqual(Path.GetFullPath(away), ModelUpdate.FindModelFor(ifc, new[] { away, folder.File("missing.bimgo"), "notes.txt" }));
        }
    }
}
