using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Site photos round: the EXIF reader, GPS → model fitting, the field of view, and photos in .bimgo files and model
    /// folders.
    /// </summary>
    [TestClass]
    public sealed class SitePhotoTests
    {
        #region A JPEG with EXIF, written here

        /// <summary>Writes a TIFF block (IFD0 → Exif IFD, GPS IFD) in either byte order.</summary>
        private sealed class TiffWriter
        {
            private readonly bool _motorola;
            private readonly List<byte> _bytes = new();

            public TiffWriter(bool motorola) { _motorola = motorola; }

            public int Position => _bytes.Count;

            public void U8(int v) => _bytes.Add((byte)v);

            public void U16(int v)
            {
                if (_motorola) { _bytes.Add((byte)(v >> 8)); _bytes.Add((byte)v); }
                else { _bytes.Add((byte)v); _bytes.Add((byte)(v >> 8)); }
            }

            public void U32(long v)
            {
                if (_motorola) { for (int s = 24; s >= 0; s -= 8) { _bytes.Add((byte)(v >> s)); } }
                else { for (int s = 0; s <= 24; s += 8) { _bytes.Add((byte)(v >> s)); } }
            }

            public void PatchU32(int at, long v)
            {
                for (int i = 0; i < 4; i++)
                {
                    int shift = _motorola ? 24 - i * 8 : i * 8;
                    _bytes[at + i] = (byte)(v >> shift);
                }
            }

            public byte[] ToArray() => _bytes.ToArray();
        }

        /// <summary>One IFD entry: inline value (≤ 4 bytes) or the bytes to put after the IFD.</summary>
        private sealed record Tag(ushort Id, ushort Type, uint Count, Action<TiffWriter> Write, int Size);

        private static Tag Short(ushort id, int v) => new(id, 3, 1, w => { w.U16(v); w.U16(0); }, 2);
        private static Tag Long(ushort id, long v) => new(id, 4, 1, w => w.U32(v), 4);
        private static Tag AsciiChar(ushort id, char c) => new(id, 2, 2, w => { w.U8(c); w.U8(0); w.U16(0); }, 2);
        private static Tag Byte(ushort id, int v) => new(id, 1, 1, w => { w.U8(v); w.U8(0); w.U16(0); }, 1);

        private static Tag Rationals(ushort id, params (long Num, long Den)[] values) =>
            new(id, 5, (uint)values.Length, w => { foreach ((long n, long d) in values) { w.U32(n); w.U32(d); } }, values.Length * 8);

        private static Tag Ascii(ushort id, string text) =>
            new(id, 2, (uint)(text.Length + 1), w => { foreach (char c in text) { w.U8(c); } w.U8(0); }, text.Length + 1);

        /// <summary>Writes an IFD at the current position (its out-of-line values after it); returns where it starts.</summary>
        private static int WriteIfd(TiffWriter w, IReadOnlyList<Tag> tags, Dictionary<ushort, int> pointerSlots = null)
        {
            int start = w.Position;
            w.U16(tags.Count);
            int dataAt = start + 2 + tags.Count * 12 + 4;
            var later = new List<Tag>();
            foreach (Tag tag in tags)
            {
                w.U16(tag.Id);
                w.U16(tag.Type);
                w.U32(tag.Count);
                if (tag.Size <= 4)
                {
                    if (pointerSlots != null && tag.Type == 4) { pointerSlots[tag.Id] = w.Position; }
                    tag.Write(w);
                }
                else
                {
                    w.U32(dataAt);
                    dataAt += tag.Size + (tag.Size & 1);
                    later.Add(tag);
                }
            }
            w.U32(0); // no next IFD
            foreach (Tag tag in later)
            {
                tag.Write(w);
                if ((tag.Size & 1) == 1) { w.U8(0); }
            }
            return start;
        }

        /// <summary>A tiny "JPEG": SOI, APP1 Exif, then EOI (enough for the reader, which stops before image data).</summary>
        private static byte[] JpegWithExif(bool motorola)
        {
            var w = new TiffWriter(motorola);
            w.U8(motorola ? 'M' : 'I');
            w.U8(motorola ? 'M' : 'I');
            w.U16(42);
            w.U32(8);

            var slots = new Dictionary<ushort, int>();
            WriteIfd(w, new[] { Short(0x0112, 6), Long(0x8769, 0), Long(0x8825, 0) }, slots);
            int exif = WriteIfd(w, new[] { Ascii(0x9003, "2026:10:01 14:05:33"), Short(0xA405, 26) });
            int gps = WriteIfd(w, new[]
            {
                AsciiChar(1, 'N'), Rationals(2, (51, 1), (30, 1), (264, 10)),
                AsciiChar(3, 'W'), Rationals(4, (0, 1), (7, 1), (39, 1)),
                Byte(5, 0), Rationals(6, (255, 10)),
                AsciiChar(16, 'T'), Rationals(17, (1234, 10))
            });
            w.PatchU32(slots[0x8769], exif);
            w.PatchU32(slots[0x8825], gps);

            byte[] tiff = w.ToArray();
            var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1 };
            int length = tiff.Length + 8;
            jpeg.Add((byte)(length >> 8));
            jpeg.Add((byte)length);
            jpeg.AddRange("Exif"u8.ToArray());
            jpeg.Add(0);
            jpeg.Add(0);
            jpeg.AddRange(tiff);
            jpeg.Add(0xFF);
            jpeg.Add(0xD9);
            return jpeg.ToArray();
        }

        #endregion

        #region EXIF

        [TestMethod]
        public void Exif_ReadsGpsHeadingLensDateAndOrientation_InBothByteOrders()
        {
            foreach (bool motorola in new[] { false, true })
            {
                ExifInfo info = ExifReader.Read(JpegWithExif(motorola));
                Assert.AreEqual(6, info.Orientation, $"motorola {motorola}");
                Assert.IsTrue(info.SwapsSides);
                Assert.IsTrue(info.HasLocation);
                Assert.AreEqual(51 + 30 / 60.0 + 26.4 / 3600.0, info.Latitude.Value, 1e-9);
                Assert.AreEqual(-(7 / 60.0 + 39 / 3600.0), info.Longitude.Value, 1e-9);
                Assert.AreEqual(25.5, info.Altitude.Value, 1e-9);
                Assert.AreEqual(123.4, info.Heading.Value, 1e-9);
                Assert.IsFalse(info.HeadingMagnetic);
                Assert.AreEqual(26.0, info.FocalLength35.Value, 1e-9);
                Assert.AreEqual("2026-10-01 14:05", info.Taken);
            }
        }

        [TestMethod]
        public void Exif_NothingOrDamaged_ReadsEmptyWithoutThrowing()
        {
            Assert.IsFalse(ExifReader.Read(null).HasLocation);
            Assert.IsFalse(ExifReader.Read(new byte[] { 1, 2, 3 }).HasLocation);
            Assert.AreEqual(1, ExifReader.Read(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }).Orientation);

            // Every truncation of a real block: never throws, never invents a position
            byte[] full = JpegWithExif(motorola: false);
            for (int length = 4; length < full.Length; length += 3)
            {
                ExifInfo info = ExifReader.Read(full.Take(length).ToArray());
                if (info.HasLocation) { Assert.AreEqual(51.5073, info.Latitude.Value, 1e-3); }
            }

            // Garbage inside the TIFF block
            byte[] broken = (byte[])full.Clone();
            for (int i = 20; i < broken.Length - 2; i += 7) { broken[i] ^= 0x5A; }
            ExifReader.Read(broken);
        }

        #endregion

        #region Geo

        private static PhotoAnchor AnchorAt(double lat, double lon, double angle, double tx, double ty, double refLat, double refLon)
        {
            PhotoGeo.ToLocal(lat, lon, refLat, refLon, out double e, out double n);
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return new PhotoAnchor(lat, lon, c * e - s * n + tx, s * e + c * n + ty);
        }

        [TestMethod]
        public void Geo_LocalMetres()
        {
            PhotoGeo.ToLocal(51.501, 0.0, 51.5, 0.0, out double east, out double north);
            Assert.AreEqual(0.0, east, 1e-9);
            Assert.AreEqual(111.19, north, 0.05);
            PhotoGeo.ToLocal(0.0, 0.001, 0.0, 0.0, out east, out north);
            Assert.AreEqual(111.19, east, 0.05);
            PhotoGeo.ToLocal(51.5, 0.001, 51.5, 0.0, out east, out _);
            Assert.AreEqual(111.19 * Math.Cos(51.5 * Math.PI / 180), east, 0.05);
        }

        [TestMethod]
        public void Geo_FitFromSeveralAnchors_RecoversTheTurnAndShift()
        {
            const double angle = 0.52, tx = 1200.0, ty = -340.0, refLat = 51.5, refLon = -0.12;
            var anchors = new List<PhotoAnchor>
            {
                AnchorAt(51.5002, -0.1203, angle, tx, ty, refLat, refLon),
                AnchorAt(51.5011, -0.1188, angle, tx, ty, refLat, refLon),
                AnchorAt(51.4995, -0.1179, angle, tx, ty, refLat, refLon)
            };
            Assert.IsTrue(PhotoGeo.TryFit(anchors, northAngle: 2.0, out PhotoGeoFit fit)); // the true north is ignored here
            Assert.AreEqual(3, fit.Anchors);
            Assert.IsFalse(fit.FromNorth);
            Assert.AreEqual(angle, fit.Angle, 1e-6);
            Assert.AreEqual(0.0, fit.Rms, 1e-3);

            PhotoAnchor check = AnchorAt(51.5006, -0.1195, angle, tx, ty, refLat, refLon);
            fit.Apply(check.Latitude, check.Longitude, out double x, out double y);
            Assert.AreEqual(check.X, x, 0.01);
            Assert.AreEqual(check.Y, y, 0.01);

            // Headings turn with it: north in true-north axes, then by the fit's angle
            Assert.AreEqual(Math.PI / 2 + angle, fit.YawOf(0), 1e-5);
            Assert.AreEqual(angle, fit.YawOf(90), 1e-5);
        }

        [TestMethod]
        public void Geo_OneAnchor_TurnsByTheTrueNorth()
        {
            // SolarPosition.ToModel turns true north by −(internal → shared angle): a fit from one anchor does the same
            const double northAngle = -0.3;
            var anchor = new PhotoAnchor(51.5, -0.12, 10.0, 20.0);
            Assert.IsTrue(PhotoGeo.TryFit(new[] { anchor }, northAngle, out PhotoGeoFit fit));
            Assert.IsTrue(fit.FromNorth);
            Assert.AreEqual(0.3, fit.Angle, 1e-9);
            fit.Apply(anchor.Latitude, anchor.Longitude, out double x, out double y);
            Assert.AreEqual(10.0, x, 1e-6);
            Assert.AreEqual(20.0, y, 1e-6);

            System.Numerics.Vector3 north = SolarPosition.ToModel(new System.Numerics.Vector3(0, 1, 0), northAngle);
            Assert.AreEqual(Math.Atan2(north.Y, north.X), fit.YawOf(0), 1e-5);

            // Two anchors almost on top of each other: GPS noise, so the true north is kept
            var close = new[] { anchor, new PhotoAnchor(51.5000001, -0.12, 10.2, 20.1) };
            Assert.IsTrue(PhotoGeo.TryFit(close, northAngle, out fit));
            Assert.IsTrue(fit.FromNorth);
            Assert.IsFalse(PhotoGeo.TryFit(Array.Empty<PhotoAnchor>(), 0, out _));
        }

        [TestMethod]
        public void Geo_HeadingCorrection_IsTheCircularMeanOfThePinsOffsets()
        {
            Assert.AreEqual(0f, PhotoGeo.HeadingCorrection(null));
            Assert.AreEqual(0f, PhotoGeo.HeadingCorrection(Array.Empty<(float, float)>()));
            // A 12° magnetic declination seen by three pins, with a little noise
            float d = 12f * MathF.PI / 180f;
            var pairs = new[] { (1.0f + d + 0.01f, 1.0f), (-2.0f + d - 0.01f, -2.0f), (3.0f + d, 3.0f) };
            Assert.AreEqual(d, PhotoGeo.HeadingCorrection(pairs), 1e-3f);
            // Across ±π: −179° and +179° offsets average to 180°, not 0°
            float a = 179f * MathF.PI / 180f;
            float mean = PhotoGeo.HeadingCorrection(new[] { (a, 0f), (-a, 0f) });
            Assert.AreEqual(MathF.PI, MathF.Abs(mean), 1e-3f);
        }

        [TestMethod]
        public void SitePhoto_SituationFadesFliesAndHasNoGuns()
        {
            InputSituation photo = InputCatalog.Situation(InputCatalog.SIT_SITE_PHOTO);
            Assert.IsNotNull(photo);
            foreach (InputAction action in new[] { InputAction.MoveForward, InputAction.Jump, InputAction.Crouch, InputAction.DragLook, InputAction.Fullscreen })
            {
                Assert.IsTrue(photo.Contains(action), $"{action} should be live in the photo view");
            }
            foreach (InputAction action in new[] { InputAction.Gun1, InputAction.GunPrimary, InputAction.Save, InputAction.MediaPanel })
            {
                Assert.IsFalse(photo.Contains(action), $"{action} should not be live in the photo view");
            }
        }

        [TestMethod]
        public void Geo_FieldOfViewFromTheEquivalentFocalLength()
        {
            // A phone's main camera (26 mm equivalent, 4:3): about 67° across in landscape, 53° in portrait
            Assert.AreEqual(67.3f, PhotoGeo.FieldOfView(26, 4032, 3024).Value, 0.5f);
            Assert.AreEqual(53.1f, PhotoGeo.FieldOfView(26, 3024, 4032).Value, 0.5f);
            // A 3:2 frame at 50 mm: the classic 39.6°
            Assert.AreEqual(39.6f, PhotoGeo.FieldOfView(50, 6000, 4000).Value, 0.2f);
            Assert.IsNull(PhotoGeo.FieldOfView(null, 100, 100));
            Assert.IsNull(PhotoGeo.FieldOfView(26, 0, 100));
        }

        #endregion

        #region Files

        private static PhotoDocument SampleDocument(out Dictionary<string, byte[]> images)
        {
            var placed = new SitePhoto
            {
                Name = "IMG_0001.jpg", Group = "Block A", Taken = "2026-10-01 14:05", Latitude = 51.5, Longitude = -0.12, Heading = 123.4,
                Width = 2048, Height = 1536, Fov = 67.3f, Placement = PhotoPlacements.PINNED, X = 12.5, Y = -4.25, Z = 101.7, Yaw = 1.2f, Pitch = -0.1f
            };
            var loose = new SitePhoto { Name = "scan.jpg", Width = 800, Height = 600 };
            images = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [placed.Image] = new byte[] { 0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9 },
                [placed.Thumb] = new byte[] { 0xFF, 0xD8, 4, 0xFF, 0xD9 },
                [loose.Image] = new byte[] { 0xFF, 0xD8, 5, 0xFF, 0xD9 }
            };
            return new PhotoDocument { Model = "Test Model", ShowPins = false, ThumbScale = 1.75f, ThumbDistance = 320f, Photos = new List<SitePhoto> { placed, loose } };
        }

        [TestMethod]
        public void Photos_RoundTripThroughABimgo()
        {
            using var folder = new TempFolder();
            BimGoDocument document = TestData.BuildDocument();
            document.Photos = SampleDocument(out Dictionary<string, byte[]> images);
            document.PhotoImages = images;
            string path = folder.File("photos.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);

            List<string> names = TestData.EntryNames(path);
            CollectionAssert.Contains(names, "photos.json");
            CollectionAssert.Contains(names, "photos/" + document.Photos.Photos[0].Image);
            CollectionAssert.Contains(names, "photos/" + document.Photos.Photos[0].Thumb);

            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            Assert.AreEqual(2, read.Photos.Photos.Count);
            Assert.IsFalse(read.Photos.ShowPins);
            Assert.AreEqual(1.75f, read.Photos.ThumbScale, 1e-6f);
            Assert.AreEqual(320f, read.Photos.ThumbDistance, 1e-6f);
            SitePhoto a = read.Photos.Photos[0], b = read.Photos.Photos[1];
            Assert.AreEqual("Block A", a.Group);
            Assert.AreEqual(PhotoPlacements.PINNED, a.Placement);
            Assert.AreEqual(12.5, a.X, 1e-9);
            Assert.AreEqual(101.7, a.Z, 1e-9);
            Assert.AreEqual(1.2f, a.Yaw, 1e-6f);
            Assert.AreEqual(123.4, a.Heading.Value, 1e-9);
            Assert.AreEqual(67.3f, a.Fov, 1e-4f);
            Assert.IsFalse(b.HasLocation);
            Assert.AreEqual(PhotoPlacements.NONE, b.Placement);
            CollectionAssert.AreEqual(images[a.Image], read.PhotoImages[a.Image]);
            CollectionAssert.AreEqual(images[a.Thumb], read.PhotoImages[a.Thumb]);
            Assert.IsFalse(read.PhotoImages.ContainsKey(b.Thumb)); // missing pictures stay missing

            // Without photos nothing is written
            var bare = new BimGoDocument { Scene = document.Scene, CreatedUtc = document.CreatedUtc };
            Assert.IsTrue(BimGoWriter.Write(path, bare, TestData.Writer, FileKinds.SAVE, out error), error);
            CollectionAssert.DoesNotContain(TestData.EntryNames(path), "photos.json");
            Assert.IsNull(BimGoReader.Read(path, null, out error).Photos, error);
        }

        [TestMethod]
        public void Photos_CleanDropsBadAndRepeatedPhotosAndClamps()
        {
            var twin = new SitePhoto { Name = "a" };
            var document = new PhotoDocument
            {
                ThumbScale = 40f,
                ThumbDistance = 5f,
                Photos = new List<SitePhoto>
                {
                    twin,
                    new() { Id = twin.Id, Name = "copy" },
                    new() { Id = "../evil", Name = "bad id" },
                    null,
                    new() { Name = "  ", Fov = 400f, Latitude = 95, Longitude = 10, Placement = PhotoPlacements.GPS, Heading = -90, Pitch = 3f },
                    new() { Placement = "sideways", X = double.NaN }
                }
            }.Clean();

            Assert.AreEqual(3, document.Photos.Count);
            Assert.AreEqual(PhotoDocument.MAX_THUMB_SCALE, document.ThumbScale);
            Assert.AreEqual(PhotoDocument.MIN_THUMB_DISTANCE, document.ThumbDistance);
            Assert.AreEqual(150f, new PhotoDocument().ThumbDistance);
            Assert.AreEqual(1f, new PhotoDocument { ThumbScale = float.NaN }.Clean().ThumbScale);
            Assert.AreEqual("a", document.Photos[0].Name);
            SitePhoto clamped = document.Photos[1];
            Assert.AreEqual("Photo", clamped.Name);
            Assert.AreEqual(SitePhoto.MAX_FOV, clamped.Fov);
            Assert.IsFalse(clamped.HasLocation); // a latitude past 90 drops the pair
            Assert.AreEqual(PhotoPlacements.NONE, clamped.Placement); // GPS placement needs GPS
            Assert.AreEqual(270.0, clamped.Heading.Value, 1e-9);
            Assert.AreEqual(1.5f, clamped.Pitch);
            Assert.AreEqual(PhotoPlacements.NONE, document.Photos[2].Placement);
            Assert.IsTrue(PhotoDocument.IsSafeImageName(document.Photos[0].Thumb));
            Assert.IsFalse(PhotoDocument.IsSafeImageName("../x.jpg"));
            Assert.IsFalse(PhotoDocument.IsSafeImageName("x.png"));
        }

        [TestMethod]
        public void Photos_ModelFolderFileAndPictures()
        {
            using var folder = new TempFolder();
            string comments = Path.Combine(folder.Path, ModelFolders.COMMENTS_FILE);
            string sidecar = PhotoFiles.SidecarFor(comments);
            Assert.AreEqual(Path.Combine(folder.Path, PhotoFiles.FOLDER_FILE), sidecar);
            StringAssert.EndsWith(PhotoFiles.SidecarFor(folder.File("Tower" + BimGoFormat.SIDECAR_SUFFIX)), "Tower" + BimGoFormat.PHOTOS_SIDECAR_SUFFIX);

            PhotoDocument document = SampleDocument(out Dictionary<string, byte[]> images);
            Assert.IsTrue(PhotoFiles.Write(sidecar, document, out string error), error);
            string pictures = PhotoFiles.ImagesFolderFor(sidecar);
            foreach (KeyValuePair<string, byte[]> pair in images) { Assert.IsTrue(PhotoFiles.WriteImage(pictures, pair.Key, pair.Value, out error), error); }
            Assert.IsFalse(PhotoFiles.WriteImage(pictures, "../escape.jpg", new byte[] { 1 }, out error));

            PhotoDocument read = PhotoFiles.Read(sidecar, out error);
            Assert.IsNotNull(read, error);
            Assert.AreEqual(2, read.Photos.Count);
            Dictionary<string, byte[]> readImages = PhotoFiles.ReadImages(pictures, read);
            Assert.AreEqual(3, readImages.Count);
            PhotoFiles.DeleteImage(pictures, read.Photos[0].Thumb);
            Assert.AreEqual(2, PhotoFiles.ReadImages(pictures, read).Count);
        }

        #endregion
    }
}
