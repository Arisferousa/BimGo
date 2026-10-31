using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Materials round: matching an IFC model's materials to images and looks (by name, category and element), the
    /// matches in a .bimgo, the material library, and the matches kept when the model is updated from a newer IFC.
    /// </summary>
    [TestClass]
    public sealed class MaterialMatchTests
    {
        private const string BRICK = "textures/brick.jpg", BUMP = "textures/brick-bump.png", MASK = "textures/mesh.png";

        /// <summary>The test scene with IFC-like materials: the wall's vertices "Concrete", the door's "Timber".</summary>
        private static SceneData Scene()
        {
            var materials = new MaterialData
            {
                Materials = new[]
                {
                    new SceneMaterial { Name = "Concrete", Schema = MaterialData.IFC_SCHEMA, Colour = new Vector3(0.6f) },
                    new SceneMaterial { Name = "Timber", Schema = MaterialData.IFC_SCHEMA, Colour = new Vector3(0.5f, 0.35f, 0.2f) }
                },
                VertexMaterial = new ushort[] { 0, 0, 0, 1, 1, 1, 1, 1, 1 },
                VertexUv = Enumerable.Range(0, 9).Select(i => new Vector2(i, i)).ToArray(),
                TextureMaxSize = MaterialData.IFC_TEXTURE_SIZE
            };
            return TestData.BuildScene(materials: materials);
        }

        private static Dictionary<string, byte[]> Images() => new(StringComparer.Ordinal)
        {
            [BRICK] = new byte[] { 0xFF, 0xD8, 1, 0xFF, 0xD9 },
            [BUMP] = new byte[] { 0x89, 0x50, 2 },
            [MASK] = new byte[] { 0x89, 0x50, 3 }
        };

        private static MaterialMatch Brick(string material) => new()
        {
            Material = material, Image = BRICK, ImageName = "Brick (built-in)", Colour = "#AA5533", SizeU = 0.6f, SizeV = 0.3f, Angle = 90f,
            Shine = 0.25f, Roughness = 0.4f, Bump = BUMP, BumpStrength = 1.5f
        };

        [TestMethod]
        public void Apply_ByName_TheLookOnTheMaterialOnly()
        {
            SceneData scene = Scene();
            var matches = new MaterialMatchDocument { Matches = { Brick("concrete") } }; // any case
            MaterialData applied = MaterialMatcher.Apply(scene.Materials, matches, Images(), scene);

            SceneMaterial concrete = applied.Materials[0];
            Assert.AreEqual(BRICK, concrete.Texture);
            Assert.AreEqual(TextureState.Embedded, concrete.TextureState);
            Assert.AreEqual(0.6f, concrete.ScaleU, 1e-6f);
            Assert.AreEqual(0.3f, concrete.ScaleV, 1e-6f);
            Assert.AreEqual(90f, concrete.Angle, 1e-6f);
            Assert.AreEqual(0xAA / 255f, concrete.Colour.X, 1e-4f);
            Assert.AreEqual(0.25f, concrete.Shine, 1e-6f);
            Assert.AreEqual(BUMP, concrete.Bump);
            Assert.IsTrue(concrete.HasBump);
            Assert.IsFalse(concrete.HasCutout);
            CollectionAssert.AreEquivalent(new[] { BRICK, BUMP }, applied.Textures.Keys.ToArray());

            // The rest untouched, the original never changed, the vertices shared (no overrides)
            Assert.IsNull(applied.Materials[1].Texture);
            Assert.IsNull(scene.Materials.Materials[0].Texture);
            Assert.AreSame(scene.Materials.VertexMaterial, applied.VertexMaterial);

            // Applying again from the original gives the same; no matches gives the original back
            Assert.AreEqual(BRICK, MaterialMatcher.Apply(scene.Materials, matches, Images(), scene).Materials[0].Texture);
            Assert.AreSame(scene.Materials, MaterialMatcher.Apply(scene.Materials, new MaterialMatchDocument(), Images(), scene));

            // A missing image is left out (the colour and the rest still apply)
            MaterialData missing = MaterialMatcher.Apply(scene.Materials, matches, new Dictionary<string, byte[]>(), scene);
            Assert.IsNull(missing.Materials[0].Texture);
            Assert.IsNull(missing.Materials[0].Bump);
            Assert.AreEqual(0.25f, missing.Materials[0].Shine, 1e-6f);
        }

        [TestMethod]
        public void Apply_WaterGrassAndCutout()
        {
            SceneData scene = Scene();
            var matches = new MaterialMatchDocument
            {
                Matches =
                {
                    new MaterialMatch { Material = "Concrete", Water = true },
                    new MaterialMatch { Material = "Timber", Image = BRICK, Grass = true, Cutout = MASK, CutoutThreshold = 0.3f, BumpFromImage = true, BumpStrength = 0.5f }
                }
            };
            MaterialData applied = MaterialMatcher.Apply(scene.Materials, matches, Images(), scene);
            Assert.IsTrue(applied.Materials[0].Water);
            Assert.AreEqual(MaterialOverrides.KEYWORD_WATER_BUMP, applied.Materials[0].WaterBump, 1e-6f);
            SceneMaterial timber = applied.Materials[1];
            Assert.IsTrue(timber.Grass);
            Assert.AreEqual(MASK, timber.Cutout);
            Assert.AreEqual(0.3f, timber.CutoutThreshold, 1e-6f);
            Assert.IsTrue(timber.HasCutout);
            Assert.IsTrue(timber.BumpFromTexture);
            Assert.IsTrue(timber.HasBump);

            // MaterialData.With keeps bump and cutout images (a later TEXTURES change mustn't drop them)
            MaterialData with = applied.With(applied.Materials.Select(m => m.Clean()).ToArray());
            CollectionAssert.Contains(with.Textures.Keys.ToArray(), MASK);
        }

        [TestMethod]
        public void Apply_CategoryAndElementOverrides()
        {
            SceneData scene = Scene();
            var matches = new MaterialMatchDocument
            {
                Matches =
                {
                    new MaterialMatch { Category = "walls", Colour = "#FF0000" },
                    new MaterialMatch { Element = "DOOR-UID", Label = "Front door", Image = BRICK }, // any case
                    new MaterialMatch { Category = CategoryCatalog.KEY_DOORS, Colour = "#00FF00" }
                }
            };
            MaterialData applied = MaterialMatcher.Apply(scene.Materials, matches, Images(), scene);

            // Two original materials, then one per override that covers something: walls, doors, then the door itself
            Assert.AreEqual(5, applied.Materials.Length);
            Assert.AreEqual("Walls", applied.Materials[2].Name);
            Assert.AreEqual(1f, applied.Materials[2].Colour.X, 1e-6f);
            Assert.AreEqual("Front door", applied.Materials[4].Name);
            Assert.AreEqual(BRICK, applied.Materials[4].Texture);
            Assert.AreEqual(0.5f, applied.Materials[4].Colour.X, 1e-6f, "an element override starts from its own material");

            // The wall's vertices point at the wall override, the door's at its own (the element wins over the category)
            CollectionAssert.AreEqual(new ushort[] { 2, 2, 2, 4, 4, 4, 4, 4, 4 }, applied.VertexMaterial);
            CollectionAssert.AreEqual(new ushort[] { 0, 0, 0, 1, 1, 1, 1, 1, 1 }, scene.Materials.VertexMaterial, "the original indices are untouched");
        }

        [TestMethod]
        public void Matches_RoundTripThroughABimgo_AndTheOriginalComesBack()
        {
            using var folder = new TempFolder();
            SceneData scene = Scene();
            var matches = new MaterialMatchDocument
            {
                Matches = { Brick("Concrete"), new MaterialMatch { Element = "wall-uid", Colour = "#112233", Cutout = MASK } },
                BaseMaterials = scene.Materials.Materials.ToList()
            };
            Dictionary<string, byte[]> images = Images();
            images["textures/unused-in-the-model.jpg"] = new byte[] { 1 };
            matches.Matches.Add(new MaterialMatch { Material = "Not in this model", Image = "textures/unused-in-the-model.jpg" });
            MaterialData applied = MaterialMatcher.Apply(scene.Materials, matches, images, scene);

            BimGoDocument document = TestData.BuildDocument();
            var withScene = new BimGoDocument
            {
                Scene = scene, Materials = applied, MaterialMatches = matches, MatchImages = images,
                MaterialBaseVertices = scene.Materials.VertexMaterial, CreatedUtc = document.CreatedUtc
            };
            string path = folder.File("matched.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, withScene, TestData.Writer, FileKinds.SAVE, out string error), error);
            CollectionAssert.Contains(TestData.EntryNames(path), "material-matches.json");
            CollectionAssert.Contains(TestData.EntryNames(path), "material-base.bin");

            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            Assert.AreEqual(3, read.MaterialMatches.Matches.Count);
            Assert.AreEqual(2, read.MaterialMatches.BaseMaterials.Count);
            Assert.IsTrue(read.MatchImages.ContainsKey("textures/unused-in-the-model.jpg"), "a match's image is kept even when nothing uses it yet");
            Assert.AreEqual(BRICK, read.Scene.Materials.Materials[0].Texture, "the file holds the look applied");
            Assert.AreEqual(BUMP, read.Scene.Materials.Materials[0].Bump);
            Assert.AreEqual(MASK, read.Scene.Materials.Materials[2].Cutout);

            MaterialData original = MaterialMatcher.OriginalOf(read.Scene.Materials, read.MaterialMatches, read.MaterialBaseVertices);
            Assert.AreEqual(2, original.Materials.Length);
            Assert.IsNull(original.Materials[0].Texture);
            CollectionAssert.AreEqual(scene.Materials.VertexMaterial, original.VertexMaterial);

            // A file without matches reads without them
            Assert.IsTrue(BimGoWriter.Write(path, new BimGoDocument { Scene = scene, CreatedUtc = document.CreatedUtc }, TestData.Writer, FileKinds.SAVE, out error), error);
            read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNull(read.MaterialMatches, error);
            Assert.AreSame(read.Scene.Materials, MaterialMatcher.OriginalOf(read.Scene.Materials, null, null));
        }

        [TestMethod]
        public void Library_PutApplyWriteReadMerge()
        {
            using var folder = new TempFolder();
            var library = new MaterialLibrary();
            library.Put(Brick("Concrete - Cast-in-Place"), Images());
            library.Put(new MaterialMatch { Category = "walls" }, Images()); // overrides aren't library material
            Assert.AreEqual(1, library.Matches.Count);
            CollectionAssert.AreEquivalent(new[] { BRICK, BUMP }, library.Images.Keys.ToArray());

            string path = folder.File("office" + MaterialLibrary.EXTENSION);
            Assert.IsTrue(library.Write(path, out string error), error);
            MaterialLibrary read = MaterialLibrary.Read(path, out error);
            Assert.IsNotNull(read, error);
            Assert.AreEqual(1, read.Matches.Count);
            Assert.AreEqual(BRICK, read.Find("CONCRETE - CAST-IN-PLACE").Image);
            CollectionAssert.AreEqual(Images()[BRICK], read.Images[BRICK]);

            // A model's unmatched materials with the same name start matched; matched ones are left alone
            var matches = new MaterialMatchDocument { Matches = { new MaterialMatch { Material = "Glass", Shine = 0.9f } } };
            var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            int n = read.ApplyTo(new[] { "concrete - cast-in-place", "Glass", "Timber" }, matches, images);
            Assert.AreEqual(1, n);
            Assert.AreEqual("concrete - cast-in-place", matches.ForMaterial("Concrete - cast-in-place").Material);
            Assert.IsTrue(images.ContainsKey(BRICK));

            // Merging: the other's win; removing drops images nothing uses
            var other = new MaterialLibrary();
            other.Put(new MaterialMatch { Material = "concrete - cast-in-place", Colour = "#808080" }, null);
            Assert.AreEqual(1, read.Merge(other));
            Assert.AreEqual("#808080", read.Find("Concrete - Cast-in-Place").Colour);
            Assert.AreEqual(0, read.Images.Count);

            // Not a library, or missing
            Assert.AreEqual(0, MaterialLibrary.Read(folder.File("none" + MaterialLibrary.EXTENSION), out error).Matches.Count);
            System.IO.File.WriteAllText(folder.File("bad" + MaterialLibrary.EXTENSION), "not a zip");
            Assert.IsNull(MaterialLibrary.Read(folder.File("bad" + MaterialLibrary.EXTENSION), out error));
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void Clean_DropsBadMatchesAndRepeats()
        {
            var matches = new MaterialMatchDocument
            {
                Matches =
                {
                    null,
                    new MaterialMatch(), // matches nothing
                    new MaterialMatch { Material = " Brick ", Colour = "red", Image = "../etc/passwd", SizeU = -1f, Shine = 5f },
                    new MaterialMatch { Material = "brick", Colour = "#00ff00" } // same key: the last wins
                }
            }.Clean();
            Assert.AreEqual(1, matches.Matches.Count);
            MaterialMatch brick = matches.Matches[0];
            Assert.AreEqual("brick", brick.Material);
            Assert.AreEqual("#00FF00", brick.Colour);

            MaterialMatch odd = new MaterialMatch { Material = " Brick ", Colour = "red", Image = "../etc/passwd", SizeU = -1f, Shine = 5f }.Clean();
            Assert.AreEqual("Brick", odd.Material);
            Assert.IsNull(odd.Colour);
            Assert.IsNull(odd.Image);
            Assert.AreEqual(1f, odd.SizeU);
            Assert.AreEqual(1f, odd.Shine);
            Assert.IsTrue(MaterialMatch.TryParseColour(MaterialMatch.FormatColour(new Vector3(1f, 0.5f, 0f)), out Vector3 colour));
            Assert.AreEqual(0.5f, colour.Y, 0.01f);
        }

        [TestMethod]
        public void UpdateFromIfc_KeepsTheMatchesOnTheNewModel()
        {
            SceneData scene = Scene();
            var matches = new MaterialMatchDocument { Matches = { Brick("Concrete"), new MaterialMatch { Element = "door-uid", Colour = "#0000FF" } } };
            var reviewed = new BimGoDocument { Scene = scene, MaterialMatches = matches, MatchImages = Images() };

            // The newer export: the same names (another order) and element GlobalIds
            SceneData newer = TestData.BuildScene(materials: new MaterialData
            {
                Materials = new[]
                {
                    new SceneMaterial { Name = "Timber", Schema = MaterialData.IFC_SCHEMA },
                    new SceneMaterial { Name = "Concrete", Schema = MaterialData.IFC_SCHEMA }
                },
                VertexMaterial = new ushort[] { 1, 1, 1, 0, 0, 0, 0, 0, 0 },
                VertexUv = new Vector2[9]
            });
            BimGoDocument updated = ModelUpdate.WithNewModel(reviewed, newer);
            Assert.AreEqual(BRICK, updated.Materials.Materials[1].Texture);
            Assert.AreEqual(3, updated.Materials.Materials.Length, "the door's override");
            CollectionAssert.AreEqual(new ushort[] { 1, 1, 1, 2, 2, 2, 2, 2, 2 }, updated.Materials.VertexMaterial);
            Assert.AreSame(newer.Materials.VertexMaterial, updated.MaterialBaseVertices);
            Assert.AreEqual("Timber", updated.MaterialMatches.BaseMaterials[0].Name);
            Assert.AreEqual(2, updated.MaterialMatches.Matches.Count);
            Assert.IsTrue(updated.MatchImages.ContainsKey(BRICK));
        }
    }
}
