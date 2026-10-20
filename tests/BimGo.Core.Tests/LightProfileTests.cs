using System;
using System.Linq;
using System.Numerics;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Round X-D1: the IES parser, profile tables, built-ins, the per-light code, and their place in the .bimgo
    /// (lighting.json + ies/*.ies, light-profiles.json). The sample files are written here by hand (CC0).
    /// </summary>
    [TestClass]
    public sealed class LightProfileTests
    {
        #region Sample files

        /// <summary>A 30° spot, bilateral (C 0–180), 0–90° gamma, 1000 lm lamp.</summary>
        private const string SPOT = @"IESNA:LM-63-2002
[TEST] BimGo test file (hand-written, CC0)
[MANUFAC] BimGo
[LUMINAIRE] Test spot, 30 deg, bilateral 0-180
TILT=NONE
1 1000 1 7 3 1 2 0.1 0.1 0
1 1 10
0 10 20 30 45 60 90
0 90 180
2000 1800 900 300 80 20 0
2000 1600 700 200 60 15 0
2000 1400 500 150 40 10 0
";

        /// <summary>An even 100 cd source over the whole sphere (one C plane): 400π ≈ 1257 lm.</summary>
        private const string EVEN = "IESNA91\n[TEST] even\nTILT=NONE\n1,-1,1,3,1,1,2,0,0,0\n1 1 5\n0 90 180\n0\n100 100 100\n";

        /// <summary>Quadrant symmetric (C 0–90): brighter along C 0 than C 90.</summary>
        private const string QUADRANT = "TILT=NONE\n1 500 2 2 2 1 2 0 0 0\n1 1 5\n0 90\n0 90\n100 50\n40 20\n";

        /// <summary>Bilateral about the 90–270 plane: C 90 strong, C 270 weak.</summary>
        private const string PLANE_90 = "TILT=NONE\n1 500 1 2 3 1 2 0 0 0\n1 1 5\n0 90\n90 180 270\n10 10\n50 50\n200 200\n";

        /// <summary>Full 0–270 (no 360): C 315 is half way between C 270 and C 0 (wrapping).</summary>
        private const string WRAP = "TILT=NONE\n1 500 1 2 4 1 2 0 0 0\n1 1 5\n0 90\n0 90 180 270\n100 100\n0 0\n0 0\n300 300\n";

        /// <summary>TILT=INCLUDE: the tilt block (geometry, 3 angles, 3 factors) is skipped.</summary>
        private const string TILTED = "IESNA:LM-63-1995\nTILT=INCLUDE\n1\n3\n0 45 90\n1 0.9 0.8\n1 1000 1 2 1 1 2 0 0 0\n1 1 10\n0 90\n0\n500 100\n";

        #endregion

        #region Parsing

        [TestMethod]
        public void Ies_ParsesHeaderAnglesAndKeywords()
        {
            IesData data = IesData.Parse(SPOT, out string error);
            Assert.IsNotNull(data, error);
            Assert.AreEqual(1, data.Lamps);
            Assert.AreEqual(1000f, data.LumensPerLamp);
            Assert.AreEqual(1, data.PhotometricType);
            CollectionAssert.AreEqual(new[] { 0f, 10f, 20f, 30f, 45f, 60f, 90f }, data.Vertical);
            CollectionAssert.AreEqual(new[] { 0f, 90f, 180f }, data.Horizontal);
            Assert.AreEqual("BimGo", data.Keywords["MANUFAC"]);
            Assert.AreEqual(2000f, data.Candela(0f, 0f), 1e-3f);
            Assert.AreEqual(1900f, data.Candela(5f, 0f), 1e-3f, "half way between 0° and 10°");
        }

        [TestMethod]
        public void Ies_ExpandsSymmetry()
        {
            IesData spot = IesData.Parse(SPOT, out _);
            Assert.AreEqual(spot.Candela(20f, 90f), spot.Candela(20f, 270f), 1e-3f, "bilateral 0–180: C 270 mirrors C 90");
            Assert.AreEqual(spot.Candela(20f, 45f), spot.Candela(20f, 315f), 1e-3f);
            Assert.AreEqual(0f, spot.Candela(120f, 0f), "nothing above the file's gamma range");

            IesData even = IesData.Parse(EVEN, out string error);
            Assert.IsNotNull(even, error);
            Assert.AreEqual(100f, even.Candela(130f, 77f), 1e-3f, "one C plane: the same all round");

            IesData quadrant = IesData.Parse(QUADRANT, out error);
            Assert.IsNotNull(quadrant, error);
            Assert.AreEqual(200f, quadrant.Candela(0f, 0f), 1e-3f, "multiplier 2 applied");
            Assert.AreEqual(quadrant.Candela(45f, 30f), quadrant.Candela(45f, 150f), 1e-3f);
            Assert.AreEqual(quadrant.Candela(45f, 30f), quadrant.Candela(45f, 330f), 1e-3f);

            IesData plane90 = IesData.Parse(PLANE_90, out error);
            Assert.IsNotNull(plane90, error);
            Assert.AreEqual(50f, plane90.Candela(30f, 0f), 1e-3f, "C 0 mirrors C 180 about the 90–270 plane");
            Assert.AreEqual(plane90.Candela(30f, 60f), plane90.Candela(30f, 120f), 1e-3f);
            Assert.AreEqual(plane90.Candela(30f, 300f), plane90.Candela(30f, 240f), 1e-3f);

            IesData wrap = IesData.Parse(WRAP, out error);
            Assert.IsNotNull(wrap, error);
            Assert.AreEqual(200f, wrap.Candela(30f, 315f), 1e-3f, "wraps from 270 back to 0");
        }

        [TestMethod]
        public void Ies_SkipsIncludedTilt()
        {
            IesData data = IesData.Parse(TILTED, out string error);
            Assert.IsNotNull(data, error);
            Assert.AreEqual(500f, data.Candela(0f, 0f), 1e-3f);
            Assert.AreEqual(300f, data.Candela(45f, 200f), 1e-3f);
        }

        [TestMethod]
        public void Ies_RefusesDamagedOrUnsupported()
        {
            Assert.IsNull(IesData.Parse("", out string error));
            Assert.IsNotNull(error);
            Assert.IsNull(IesData.Parse("IESNA:LM-63-2002\n[TEST] x\n1 2 3\n", out error), "no TILT line");
            Assert.IsNull(IesData.Parse(EVEN.Replace("1,-1,1,3,1,1,", "1,-1,1,3,1,2,"), out error), "type B");
            StringAssert.Contains(error, "type C");
            Assert.IsNull(IesData.Parse(SPOT[..^20], out error), "truncated");
            Assert.IsNull(IesData.Parse(SPOT.Replace("0 10 20 30 45 60 90", "0 20 10 30 45 60 90"), out error), "angles out of order");
            Assert.IsNull(IesData.Parse(SPOT.Replace("1 1 10\n", "1 1 x10\n"), out error), "not a number");
        }

        [TestMethod]
        public void Ies_TotalLumensIntegratesTheCandela()
        {
            IesData even = IesData.Parse(EVEN, out _);
            Assert.AreEqual(400f * MathF.PI, even.TotalLumens(), 400f * MathF.PI * 0.01f);
            IesData spot = IesData.Parse(SPOT, out _);
            Assert.AreEqual(884f, spot.TotalLumens(), 20f, "the luminaire's output, not the lamp's 1000 lm");
        }

        #endregion

        #region Tables and built-ins

        [TestMethod]
        public void Profile_EvenLightHasScaleOne()
        {
            LightProfile even = LightProfile.FromFunction("t:even", "Even", (g, c) => 3f);
            Assert.IsTrue(even.Table.All(b => b == 255));
            Assert.AreEqual(1f, even.Scale, 0.01f);
            Assert.AreEqual(1f, even.Sample(123f, 321f), 1e-6f);

            LightProfile fromIes = LightProfile.FromIes(LightProfiles.KeyOf(EVEN), "even.ies", EVEN, out string error);
            Assert.IsNotNull(fromIes, error);
            Assert.AreEqual(1f, fromIes.Scale, 0.01f);
            Assert.AreEqual(400f * MathF.PI, fromIes.Lumens, 15f);
            Assert.IsTrue(fromIes.IsFile);
        }

        [TestMethod]
        public void Profile_NarrowBeamsConcentrate()
        {
            LightProfile spot = LightProfile.FromIes(LightProfiles.KeyOf(SPOT), "spot.ies", SPOT, out string error);
            Assert.IsNotNull(spot, error);
            Assert.AreEqual(1f, spot.Sample(0f, 0f), 1e-6f, "peak 1 straight out");
            Assert.AreEqual(0f, spot.Sample(150f, 0f), 1e-6f);
            Assert.IsTrue(spot.Scale > 15f && spot.Scale < 40f, $"scale {spot.Scale}");
            Assert.IsTrue(spot.BeamAngle > 25f && spot.BeamAngle < 45f, $"beam {spot.BeamAngle}");

            // Energy: the table × scale integrates to 4π (an even light of the same output)
            Assert.AreEqual(4f * MathF.PI, LightProfile.Integrate(spot.Table) * spot.Scale, 0.01f);
        }

        [TestMethod]
        public void BuiltIns_AreDistinctAndShaped()
        {
            LightProfile[] all = LightProfiles.BuiltIns;
            Assert.AreEqual(5, all.Length);
            Assert.AreEqual(all.Length, all.Select(p => p.Key).Distinct().Count());
            Assert.IsTrue(all.All(p => p.Key.StartsWith(LightProfiles.BUILT_IN_PREFIX, StringComparison.Ordinal) && !p.IsFile && p.Lumens == 0f));
            Assert.AreSame(all[0], LightProfiles.FindBuiltIn(LightProfiles.DOWNLIGHT_NARROW));
            Assert.IsNull(LightProfiles.FindBuiltIn("builtin:nope"));

            LightProfile narrow = LightProfiles.FindBuiltIn(LightProfiles.DOWNLIGHT_NARROW);
            LightProfile medium = LightProfiles.FindBuiltIn(LightProfiles.DOWNLIGHT_MEDIUM);
            LightProfile flood = LightProfiles.FindBuiltIn(LightProfiles.FLOOD);
            Assert.IsTrue(narrow.Scale > medium.Scale && medium.Scale > flood.Scale && flood.Scale > 1f);
            Assert.AreEqual(25f, narrow.BeamAngle, 4f);
            Assert.AreEqual(40f, medium.BeamAngle, 5f);

            LightProfile washer = LightProfiles.FindBuiltIn(LightProfiles.WALL_WASHER);
            Assert.IsTrue(washer.Sample(35f, 0f) > 3f * washer.Sample(35f, 180f), "throws towards C 0");

            LightProfile pendant = LightProfiles.FindBuiltIn(LightProfiles.PENDANT);
            Assert.IsTrue(pendant.Sample(180f, 0f) > 0.3f && pendant.Sample(0f, 0f) > pendant.Sample(180f, 0f), "some up, more down");
        }

        #endregion

        #region Keys and codes

        [TestMethod]
        public void Keys_HashTheContent()
        {
            string key = LightProfiles.KeyOf(SPOT);
            Assert.AreEqual(key, LightProfiles.KeyOf(SPOT.Replace("\n", "\r\n")), "line endings don't matter");
            Assert.AreNotEqual(key, LightProfiles.KeyOf(EVEN));
            string hash = LightProfiles.HashOf(key);
            Assert.IsTrue(LightProfiles.IsSafeHash(hash));
            Assert.AreEqual(LightProfiles.FILE_PREFIX + hash, key);
            Assert.IsNull(LightProfiles.HashOf("ies:../../x"));
            Assert.IsNull(LightProfiles.HashOf(LightProfiles.DOWNLIGHT_NARROW));
            Assert.IsFalse(LightProfiles.IsSafeHash("ABCDEF0123456789"));
        }

        [TestMethod]
        public void Code_RoundTripsSlotAndOrientation()
        {
            // Straight down along X: exact
            float down = LightProfiles.Encode(5, -Vector3.UnitZ, Vector3.UnitX);
            Assert.IsTrue(down < 0f && down == MathF.Round(down));
            (int slot, Vector3 aim, Vector3 across) = LightProfiles.Decode(down);
            Assert.AreEqual(5, slot);
            Assert.IsTrue(Vector3.Distance(aim, -Vector3.UnitZ) < 1e-5f);
            Assert.IsTrue(Vector3.Distance(across, Vector3.UnitX) < 1e-5f);

            var random = new Random(7);
            for (int i = 0; i < 200; i++)
            {
                Vector3 a = Vector3.Normalize(new Vector3((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1));
                Vector3 c = Vector3.Normalize(Vector3.Cross(a, new Vector3(0.3f, -0.7f, 0.2f)));
                int s = random.Next(LightProfiles.MAX_GPU_PROFILES);
                (int gotSlot, Vector3 gotAim, Vector3 gotAcross) = LightProfiles.Decode(LightProfiles.Encode(s, a, c));
                Assert.AreEqual(s, gotSlot);
                Assert.IsTrue(Vector3.Dot(gotAim, a) > MathF.Cos(4.5f * MathF.PI / 180f), $"aim {a} → {gotAim}"); // 6-bit octahedral: under 4° worst case
                Assert.IsTrue(Vector3.Dot(gotAcross, c) > MathF.Cos(6f * MathF.PI / 180f), $"across {c} → {gotAcross}");
                Assert.AreEqual(0f, Vector3.Dot(gotAcross, gotAim), 1e-4f);
            }

            // The angles the shader works out
            (float gamma, float cAngle) = LightProfiles.AnglesOf(new Vector3(0f, 1f, -1f), -Vector3.UnitZ, Vector3.UnitX);
            Assert.AreEqual(45f, gamma, 1e-3f);
            Assert.AreEqual(90f, cAngle, 1e-3f, "C 90 is a quarter turn anticlockwise from above");
        }

        #endregion

        #region Files

        [TestMethod]
        public void Lighting_IesFilesAndOrientationRoundTrip()
        {
            using var folder = new TempFolder();
            string hash = LightProfiles.HashOf(LightProfiles.KeyOf(SPOT));
            var lighting = new LightingData
            {
                Lights = new[]
                {
                    new LightSource { Element = 0, Position = new Vector3(1, 2, 2.6f), Ies = hash, Aim = Vector3.Normalize(new Vector3(0, 1, -1)), Across = Vector3.UnitX },
                    new LightSource { Element = 1, Position = new Vector3(4, 5, 2.4f) }
                },
                IesFiles = new[] { new LightIesFile(hash, "spot.ies", SPOT) }
            };
            BimGoDocument document = TestData.BuildDocument(lighting);
            document.LightProfiles = new LightProfileDocument();
            document.LightProfiles.SetChoice("Downlight : 600", LightProfiles.WALL_WASHER);
            document.LightProfiles.Imports.Add(new LightProfileImport { Hash = LightProfiles.HashOf(LightProfiles.KeyOf(EVEN)), Name = "even.ies", Text = EVEN });

            string path = folder.File("ies.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
            {
                Assert.IsNotNull(zip.GetEntry("ies/" + hash + ".ies"));
                Assert.IsNotNull(zip.GetEntry("light-profiles.json"));
            }
            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);

            LightingData got = read.Scene.Lighting;
            Assert.AreEqual(1, got.IesFiles.Length);
            Assert.AreEqual("spot.ies", got.IesFiles[0].Name);
            Assert.AreEqual(SPOT, got.IesFiles[0].Text);
            Assert.AreEqual(hash, got.Lights[0].Ies);
            Assert.IsTrue(Vector3.Distance(lighting.Lights[0].Aim, got.Lights[0].Aim) < 1e-5f);
            Assert.IsNull(got.Lights[1].Ies);
            Assert.AreEqual(-Vector3.UnitZ, got.Lights[1].Aim, "defaults when not written");
            Assert.AreEqual(Vector3.UnitX, got.Lights[1].Across);

            Assert.IsNotNull(read.LightProfiles);
            Assert.AreEqual(LightProfiles.WALL_WASHER, read.LightProfiles.ChoiceFor("Downlight : 600"));
            Assert.AreEqual(1, read.LightProfiles.Imports.Count);
            Assert.AreEqual(EVEN, read.LightProfiles.Imports[0].Text);
        }

        [TestMethod]
        public void Lighting_MissingIesEntryFallsBackToTheLobe()
        {
            using var folder = new TempFolder();
            string hash = LightProfiles.HashOf(LightProfiles.KeyOf(SPOT));
            var lighting = new LightingData
            {
                Lights = new[] { new LightSource { Element = 0, Position = Vector3.One, Ies = hash } },
                IesFiles = new[] { new LightIesFile(hash, "spot.ies", SPOT) }
            };
            string path = folder.File("missing.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, TestData.BuildDocument(lighting), TestData.Writer, FileKinds.SAVE, out string error), error);
            using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
            {
                zip.GetEntry("ies/" + hash + ".ies").Delete();
            }
            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            Assert.AreEqual(0, read.Scene.Lighting.IesFiles.Length);
            Assert.IsNull(read.Scene.Lighting.Lights[0].Ies);
        }

        [TestMethod]
        public void ProfileDocument_CleanDropsDamage()
        {
            var document = new LightProfileDocument();
            document.Types.Add(new LightProfileChoice { FamilyType = "A : 1", Profile = LightProfiles.LOBE });
            document.Types.Add(new LightProfileChoice { FamilyType = "A : 1", Profile = LightProfiles.REVIT });
            document.Types.Add(new LightProfileChoice { FamilyType = "B : 1", Profile = "something else" });
            document.Types.Add(new LightProfileChoice { FamilyType = "", Profile = LightProfiles.LOBE });
            document.Types.Add(null);
            document.Imports.Add(new LightProfileImport { Hash = "../evil", Name = "x", Text = SPOT });
            document.Imports.Add(new LightProfileImport { Hash = LightProfiles.HashOf(LightProfiles.KeyOf(SPOT)), Name = "spot.ies", Text = SPOT });
            document.Imports.Add(new LightProfileImport { Hash = LightProfiles.HashOf(LightProfiles.KeyOf(SPOT)), Name = "again.ies", Text = SPOT });
            document.Clean();

            Assert.AreEqual(1, document.Types.Count);
            Assert.AreEqual(LightProfiles.REVIT, document.ChoiceFor("A : 1"), "the last choice per type wins");
            Assert.IsNull(document.ChoiceFor("B : 1"));
            Assert.AreEqual(1, document.Imports.Count);
            Assert.AreEqual("spot.ies", document.Imports[0].Name);

            document.SetChoice("A : 1", null);
            Assert.IsNull(document.ChoiceFor("A : 1"));
            Assert.IsFalse(document.IsEmpty, "the import is still there");
        }

        [TestMethod]
        public void ProfileDocument_LivesInTheModelFolder()
        {
            Assert.AreEqual(ModelFolders.LIGHT_PROFILES_FILE, ModelFolders.FileForSuffix(BimGoFormat.LIGHT_PROFILES_SIDECAR_SUFFIX));

            using var folder = new TempFolder();
            string path = folder.File("light-profiles.json");
            var document = new LightProfileDocument();
            document.SetChoice("Washer : 1200", LightProfiles.WALL_WASHER);
            Assert.IsTrue(LightProfileFiles.Write(path, document, out string error), error);
            LightProfileDocument read = LightProfileFiles.Read(path, out error);
            Assert.IsNotNull(read, error);
            Assert.AreEqual(LightProfiles.WALL_WASHER, read.ChoiceFor("Washer : 1200"));
        }

        #endregion
    }
}
