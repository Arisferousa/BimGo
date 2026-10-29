using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;
using Gl = BimGo.Native.Gl;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Site photos (site photos round): photographs of the site linked to the model, kept inside the .bimgo (or the live
    /// model folder) so whoever opens the file sees them without the originals.
    /// <list type="bullet">
    /// <item><b>Import</b> (pause menu → SITE PHOTOS → IMPORT PHOTOS… / IMPORT FOLDER…): JPEGs, turned upright from their
    /// EXIF orientation, kept at up to 2048 px with a 320 px thumbnail; GPS, compass heading, the 35 mm focal length (the
    /// field of view) and the time are read from EXIF. A folder's sub-folders become the photos' groups (one per
    /// building).</item>
    /// <item><b>Alignment</b>: open a photo, fly until the model lines up with it (adjust: the photo over the model,
    /// see-through, the lens on the wheel) and PIN HERE. A pinned photo with GPS is an anchor: one anchor and the model's
    /// true north, or two or more by least squares (<see cref="PhotoGeo.TryFit"/>), place every other photo with GPS (on
    /// the first floor below it, 1.6 m up, facing its compass heading). A photo without GPS is pinned by hand.</item>
    /// <item><b>Pins</b>: a dot at each photo's eye with a heading tick, and the nearest photos' thumbnails floating above
    /// them (faint behind buildings). A click on one (the viewer's cursor, the walkthrough's crosshair with no gun) opens
    /// it.</item>
    /// <item><b>Photo view</b>: the camera at the photo's eye with its lens, the photo over the model (the wheel or the
    /// slider fades between them), previous / next (within a group when browsing one), and in the full walkthrough
    /// ADJUST and PIN HERE.</item>
    /// </list>
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private const int SITE_PHOTO_MAX_SIDE = 2048, SITE_PHOTO_THUMB_SIDE = 320;
        private const long SITE_PHOTO_QUALITY = 85L, SITE_PHOTO_THUMB_QUALITY = 78L;

        /// <summary>A photo placed from GPS stands this far above the floor under it (m).</summary>
        private const float SITE_PHOTO_EYE = 1.6f;

        /// <summary>Floating thumbnails drawn at once (the nearest), how far they show (m), uploads and sight lines per frame.</summary>
        private const int SITE_THUMBS_MAX = 40, SITE_THUMB_UPLOADS = 3, SITE_SIGHT_CHECKS = 10;
        private const float SITE_THUMB_FAR = 900f;

        /// <summary>The largest photo file read (bytes).</summary>
        private const long SITE_PHOTO_MAX_FILE = 64L * 1024 * 1024;

        private const string SITE_PHOTO_FILTER = "JPEG photos (*.jpg;*.jpeg)|*.jpg;*.jpeg|All files (*.*)|*.*";
        private const int SITE_SLIDER_OPACITY = 9201, SITE_SLIDER_FOV = 9202;

        /// <summary>The model's site photos (never null after setup).</summary>
        public PhotoStore SitePhotos { get; private set; }

        private PhotoGeoFit _siteFit;
        private bool _siteFitted;
        private float _siteHeadingCorrection; // added to compass yaws (radians): learnt from the pins
        private int _savedSitePhotoRevision;

        // Thumbnails by photo id (0 = unreadable)
        private readonly Dictionary<string, uint> _siteThumbTextures = new(StringComparer.Ordinal);

        // The floating thumbnails: candidates sorted by distance, sight lines (blocked = faint), last frame's rectangles
        private int[] _siteOrder = Array.Empty<int>();
        private float[] _siteDistance = Array.Empty<float>();
        private bool[] _siteBlocked = Array.Empty<bool>();
        private int _siteSightNext, _siteCacheRevision = -1, _siteCacheCount = -1;
        private readonly Vector4[] _siteThumbRects = new Vector4[SITE_THUMBS_MAX];
        private readonly int[] _siteThumbPhoto = new int[SITE_THUMBS_MAX];
        private int _siteThumbCount;
        private int _siteHover = -1;

        // The photo view
        private SitePhoto _sitePhotoView;
        private bool _sitePhotoAdjust, _siteWasWalking;
        private float _sitePhotoOpacity = 1f, _siteAdjustOpacity = 0.55f, _siteAdjustFov = SitePhoto.DEFAULT_FOV;
        private uint _sitePhotoTexture;
        private string _sitePhotoTextureFor;
        private string _siteGroupFilter;
        private Vector4 _sitePhotoPanelRect;

        // The SITE PHOTOS panel
        private bool _sitePhotosOpen;
        private readonly ScrollRegion _sitePhotoScroll = new();
        private SitePhoto _siteDeleteArmed;
        private float _siteDeleteArmedUntil;
        private string _sitePhotosNotice;
        private string _sitePhotosMenuLabel;
        private int _sitePhotosMenuLabelFor = -1;

        // The groups (the viewer's popover), rebuilt when the photos change
        private readonly List<string> _siteGroups = new();
        private int _siteGroupsRevision = -1;

        #endregion

        #region State

        /// <summary>True while the photo view is open.</summary>
        private bool SitePhotoViewOpen => _sitePhotoView != null;

        /// <summary>True while the SITE PHOTOS panel replaces the pause menu.</summary>
        private bool IsSitePhotosPanelOpen => _sitePhotosOpen;

        /// <summary>True when there is a photo standing in the model.</summary>
        private bool HasPlacedSitePhotos
        {
            get
            {
                if (SitePhotos == null) { return false; }
                foreach (SitePhoto photo in SitePhotos.Photos)
                {
                    if (photo.IsPlaced) { return true; }
                }
                return false;
            }
        }

        /// <summary>True when the photos changed since the file was saved.</summary>
        private bool SitePhotosDirty => SitePhotos != null && SitePhotos.Revision != _savedSitePhotoRevision;

        /// <summary>"SITE PHOTOS (n)" (rebuilt only when the count changes).</summary>
        private string SitePhotosMenuLabel()
        {
            int count = SitePhotos?.Photos.Count ?? 0;
            if (count != _sitePhotosMenuLabelFor || _sitePhotosMenuLabel == null)
            {
                _sitePhotosMenuLabelFor = count;
                _sitePhotosMenuLabel = count == 0 ? "SITE PHOTOS" : $"SITE PHOTOS ({count})";
            }
            return _sitePhotosMenuLabel;
        }

        #endregion

        #region Setup

        /// <summary>Loads the photos: inside the file, else the live model folder's.</summary>
        private void InitialiseSitePhotos()
        {
            if (IsFileMode)
            {
                SitePhotos = new PhotoStore(null, Scene.ModelTitle, Scene.OriginOffset);
                SitePhotos.LoadFrom(_options.Document?.Photos, _options.Document?.PhotoImages);
            }
            else
            {
                SitePhotos = new PhotoStore(PhotoFiles.SidecarFor(Scene.CommentsPath), Scene.ModelTitle, Scene.OriginOffset);
                SitePhotos.Load();
            }
            SitePhotos.Changed += UpdateTitle;
            RefreshSiteFit();
        }

        /// <summary>Frees the photos' textures (session end).</summary>
        private void ReleaseSitePhotoTextures()
        {
            foreach (uint texture in _siteThumbTextures.Values) { if (texture != 0) { Gl.DeleteTexture(texture); } }
            _siteThumbTextures.Clear();
            ReleaseSitePhotoPicture();
        }

        private void ReleaseSitePhotoPicture()
        {
            if (_sitePhotoTexture != 0) { Gl.DeleteTexture(_sitePhotoTexture); }
            _sitePhotoTexture = 0;
            _sitePhotoTextureFor = null;
        }

        #endregion

        #region Import

        /// <summary>One photo read on the worker: the record and its two JPEGs.</summary>
        private sealed record ImportedSitePhoto(SitePhoto Photo, byte[] Picture, byte[] Thumbnail);

        /// <summary>
        /// IMPORT PHOTOS… / IMPORT FOLDER…: reads the JPEGs on a worker with a progress bar (Esc stops; what was read so far
        /// is kept), skips ones already here, adds them and places those with GPS when the photos are aligned.
        /// </summary>
        private void ImportSitePhotos(bool folder)
        {
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            string start = !string.IsNullOrEmpty(DocumentPath) ? Path.GetDirectoryName(DocumentPath) : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string root = null;
            string[] files;
            if (folder)
            {
                root = FileDialogs.ShowFolder(_window.Handle, "Import site photos: pick a folder (its sub-folders become groups, such as one per building)", start);
                if (root == null) { return; }
                try
                {
                    files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                        .Where(IsJpegPath)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Site photo folder not read ({root}): {ex.Message}");
                    _sitePhotosNotice = $"The folder could not be read: {ex.Message}";
                    Sound.Play(SoundId.Error);
                    return;
                }
            }
            else
            {
                files = FileDialogs.ShowOpenMany(_window.Handle, "Import site photos", SITE_PHOTO_FILTER, start);
                if (files == null) { return; }
            }
            if (files.Length == 0)
            {
                _sitePhotosNotice = "No JPEG photos there (.jpg or .jpeg; convert HEIC photos to JPEG first)";
                Sound.Play(SoundId.Error);
                return;
            }

            var progress = new Utilities.OperationProgress();
            progress.Begin("Reading the photos", 0.0, 1.0);
            int failed = 0;
            List<ImportedSitePhoto> read = ProgressScreen.Run(_window, _ui, $"Importing {files.Length} photo{(files.Length == 1 ? string.Empty : "s")}", progress,
                () => ReadSitePhotos(files, root, progress, out failed));
            _window.Input.ReleaseAll();

            // Skip what is already here (same file name, time and size)
            var known = new HashSet<string>(SitePhotos.Photos.Select(SitePhotoKey), StringComparer.OrdinalIgnoreCase);
            int added = 0, repeated = 0, full = 0, located = 0;
            foreach (ImportedSitePhoto photo in read)
            {
                if (!known.Add(SitePhotoKey(photo.Photo)))
                {
                    repeated++;
                    continue;
                }
                if (!SitePhotos.Add(photo.Photo, photo.Picture, photo.Thumbnail))
                {
                    full++;
                    continue;
                }
                added++;
                if (photo.Photo.HasLocation) { located++; }
            }
            int placed = ApplySiteAlignment();
            if (added > 0) { SitePhotos.Save(); }
            Sound.Play(added > 0 ? SoundId.Commit : SoundId.Error);

            var notice = new System.Text.StringBuilder();
            notice.Append($"Imported {added} photo{(added == 1 ? string.Empty : "s")}");
            if (located > 0) { notice.Append(_siteFitted ? $", {placed} placed from GPS" : $" ({located} with GPS: pin one to place them)"); }
            if (repeated > 0) { notice.Append($" · {repeated} already here"); }
            if (failed > 0) { notice.Append($" · {failed} unreadable"); }
            if (full > 0) { notice.Append($" · {full} over the {PhotoDocument.MAX_PHOTOS} a model keeps"); }
            if (progress.CancelRequested) { notice.Append(" · stopped early"); }
            _sitePhotosNotice = SitePhotos.LastError ?? notice.ToString();
            _sitePhotoScroll.ScrollToEnd();
            Utilities.Log_Utils.Write($"Site photos: {_sitePhotosNotice}");
        }

        private static bool IsJpegPath(string path)
        {
            string extension = Path.GetExtension(path);
            return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        private static string SitePhotoKey(SitePhoto photo) => $"{photo.Name}|{photo.Taken}|{photo.Width}x{photo.Height}";

        /// <summary>The worker: reads, turns, scales and encodes each photo (unreadable ones are counted, not thrown).</summary>
        private static List<ImportedSitePhoto> ReadSitePhotos(string[] files, string root, Utilities.OperationProgress progress, out int failed)
        {
            failed = 0;
            var photos = new List<ImportedSitePhoto>(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                if (progress.CancelRequested) { break; }
                progress.Detail(Path.GetFileName(files[i]));
                ImportedSitePhoto photo = ReadSitePhoto(files[i], root);
                if (photo != null) { photos.Add(photo); }
                else { failed++; }
                progress.Step((i + 1) / (double)files.Length);
            }
            return photos;
        }

        /// <summary>One photo: EXIF, then upright, at most 2048 px, and a 320 px thumbnail (null when unreadable).</summary>
        private static ImportedSitePhoto ReadSitePhoto(string path, string root)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0 || info.Length > SITE_PHOTO_MAX_FILE) { return null; }
                byte[] bytes = File.ReadAllBytes(path);
                ExifInfo exif = ExifReader.Read(bytes);

                using var stream = new MemoryStream(bytes);
                using var source = new System.Drawing.Bitmap(stream);
                System.Drawing.RotateFlipType turn = exif.Orientation switch
                {
                    2 => System.Drawing.RotateFlipType.RotateNoneFlipX,
                    3 => System.Drawing.RotateFlipType.Rotate180FlipNone,
                    4 => System.Drawing.RotateFlipType.Rotate180FlipX,
                    5 => System.Drawing.RotateFlipType.Rotate90FlipX,
                    6 => System.Drawing.RotateFlipType.Rotate90FlipNone,
                    7 => System.Drawing.RotateFlipType.Rotate270FlipX,
                    8 => System.Drawing.RotateFlipType.Rotate270FlipNone,
                    _ => System.Drawing.RotateFlipType.RotateNoneFlipNone
                };
                if (turn != System.Drawing.RotateFlipType.RotateNoneFlipNone) { source.RotateFlip(turn); }

                byte[] picture = EncodeScaledJpeg(source, SITE_PHOTO_MAX_SIDE, SITE_PHOTO_QUALITY, out int width, out int height);
                byte[] thumbnail = EncodeScaledJpeg(source, SITE_PHOTO_THUMB_SIDE, SITE_PHOTO_THUMB_QUALITY, out _, out _);
                if (picture == null) { return null; }

                var photo = new SitePhoto
                {
                    Name = Path.GetFileName(path),
                    Group = SitePhotoGroup(path, root),
                    Taken = exif.Taken,
                    Width = width,
                    Height = height,
                    Fov = PhotoGeo.FieldOfView(exif.FocalLength35, width, height) ?? DefaultSitePhotoFov(width, height)
                };
                if (exif.HasLocation)
                {
                    photo.Latitude = exif.Latitude;
                    photo.Longitude = exif.Longitude;
                    photo.Altitude = exif.Altitude;
                    photo.Heading = exif.Heading;
                }
                return new ImportedSitePhoto(photo, picture, thumbnail);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Site photo not read ({path}): {ex.Message}");
                return null;
            }
        }

        /// <summary>The first sub-folder under the imported folder (the group), or empty.</summary>
        private static string SitePhotoGroup(string path, string root)
        {
            if (string.IsNullOrEmpty(root)) { return string.Empty; }
            try
            {
                string relative = Path.GetRelativePath(root, Path.GetDirectoryName(path) ?? root);
                if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal)) { return string.Empty; }
                int slash = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
                return slash > 0 ? relative[..slash] : relative;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>A phone's main camera when the photo doesn't say (the default across the long side).</summary>
        private static float DefaultSitePhotoFov(int width, int height)
        {
            if (width >= height || height == 0) { return SitePhoto.DEFAULT_FOV; }
            double half = Math.Tan(SitePhoto.DEFAULT_FOV * Math.PI / 360.0) * width / height;
            return (float)(2.0 * Math.Atan(half) * 180.0 / Math.PI);
        }

        /// <summary>The picture scaled so its long side is at most <paramref name="side"/>, as a JPEG.</summary>
        private static byte[] EncodeScaledJpeg(System.Drawing.Bitmap source, int side, long quality, out int width, out int height)
        {
            width = source.Width;
            height = source.Height;
            if (width <= 0 || height <= 0) { return null; }
            float scale = MathF.Min(1f, side / (float)Math.Max(width, height));
            width = Math.Max(1, (int)MathF.Round(width * scale));
            height = Math.Max(1, (int)MathF.Round(height * scale));

            using var scaled = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var graphics = System.Drawing.Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                using var wrap = new System.Drawing.Imaging.ImageAttributes();
                wrap.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY); // no dark seam along the edges
                graphics.DrawImage(source, new System.Drawing.Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, System.Drawing.GraphicsUnit.Pixel, wrap);
            }

            System.Drawing.Imaging.ImageCodecInfo jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            if (jpeg == null) { return null; }
            using var output = new MemoryStream();
            using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
            parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            scaled.Save(output, jpeg, parameters);
            return output.ToArray();
        }

        #endregion

        #region Alignment

        /// <summary>The GPS → model fit from the pinned photos that have GPS (none: not aligned).</summary>
        private void RefreshSiteFit()
        {
            var anchors = new List<PhotoAnchor>();
            if (SitePhotos != null)
            {
                foreach (SitePhoto photo in SitePhotos.Photos)
                {
                    if (photo.Placement == PhotoPlacements.PINNED && photo.HasLocation)
                    {
                        anchors.Add(new PhotoAnchor(photo.Latitude.Value, photo.Longitude.Value, photo.X, photo.Y));
                    }
                }
            }
            _siteFitted = PhotoGeo.TryFit(anchors, _northAngle, out _siteFit);

            // The compass's error (magnetic declination, the phone's own), from the pins that recorded a heading
            var turns = new List<(float, float)>();
            if (_siteFitted)
            {
                foreach (SitePhoto photo in SitePhotos.Photos)
                {
                    if (photo.Placement == PhotoPlacements.PINNED && photo.HasLocation && photo.Heading is double heading)
                    {
                        turns.Add((photo.Yaw, _siteFit.YawOf(heading)));
                    }
                }
            }
            _siteHeadingCorrection = PhotoGeo.HeadingCorrection(turns);
        }

        /// <summary>
        /// Places every photo with GPS that isn't pinned through the fit (or takes them out when there is none). Call
        /// <see cref="PhotoStore.Save"/> after.
        /// </summary>
        /// <returns>How many are placed from GPS.</returns>
        private int ApplySiteAlignment()
        {
            RefreshSiteFit();
            int placed = 0;
            foreach (SitePhoto photo in SitePhotos.Photos)
            {
                if (photo.Placement == PhotoPlacements.PINNED) { continue; }
                if (!photo.HasLocation || !_siteFitted)
                {
                    if (photo.Placement == PhotoPlacements.GPS) { PhotoStore.Unplace(photo); }
                    continue;
                }
                _siteFit.Apply(photo.Latitude.Value, photo.Longitude.Value, out double x, out double y);
                Vector3 local = SitePhotos.ToLocal(x, y, Scene.OriginOffset.Z);
                local.Z = FloorUnder(local.X, local.Y) + SITE_PHOTO_EYE;
                Vector3 centre = Scene.Bounds.Center;
                float yaw = photo.Heading is double heading ? _siteFit.YawOf(heading) + _siteHeadingCorrection : MathF.Atan2(centre.Y - local.Y, centre.X - local.X);
                SitePhotos.Place(photo, local, yaw, 0f, photo.Fov, PhotoPlacements.GPS);
                placed++;
            }
            return placed;
        }

        /// <summary>The first floor (or ground) straight below a point from above the model, else the ground plane.</summary>
        private float FloorUnder(float x, float y)
        {
            float top = Scene.Bounds.Max.Z + 20f;
            var origin = new Vector3(x, y, top);
            float travelled = 0f;
            for (int i = 0; i < 24; i++)
            {
                if (!_bvh.Raycast(origin - Vector3.UnitZ * travelled, -Vector3.UnitZ, 5000f - travelled, _pickMask, out RayHit hit)) { break; }
                if (hit.Normal.Z > 0.7f && !(_sectionCount > 0 && SectionCut.IsCut(_sectionPlanes, _sectionCount, hit.Point)))
                {
                    return MathF.Max(hit.Point.Z, _groundZ);
                }
                travelled += hit.Distance + 0.05f;
            }
            return _groundZ;
        }

        /// <summary>The alignment in words, for the panel.</summary>
        private string SiteAlignmentText()
        {
            int total = SitePhotos.Photos.Count, located = 0, gps = 0, pinned = 0, loose = 0;
            foreach (SitePhoto photo in SitePhotos.Photos)
            {
                if (photo.HasLocation) { located++; }
                if (photo.Placement == PhotoPlacements.GPS) { gps++; }
                else if (photo.Placement == PhotoPlacements.PINNED) { pinned++; }
                else if (!photo.HasLocation) { loose++; }
            }
            if (total == 0)
            {
                return "Import JPEG photos of the site (phone photos record where they were taken). They are kept inside this file, so whoever opens it sees them.";
            }
            string without = loose > 0 ? $" {loose} without GPS: OPEN and PIN HERE to place them by hand." : string.Empty;
            if (!_siteFitted)
            {
                return located > 0
                    ? $"{located} photo{(located == 1 ? " has" : "s have")} GPS. To place them: OPEN one, fly until the model lines up with it and PIN HERE (one pin uses the model's true north; pin a second far from it to fit the turn too).{without}"
                    : $"None of these photos has GPS: OPEN each one and PIN HERE where it was taken.{without}";
            }
            return _siteFit.FromNorth
                ? $"Aligned by {_siteFit.Anchors} pin{(_siteFit.Anchors == 1 ? string.Empty : "s")} and the model's true north: {gps} placed from GPS, {pinned} pinned. Pin another photo far from it to fit the turn as well.{without}"
                : $"Aligned by {_siteFit.Anchors} pins (they agree within {_siteFit.Rms:0.0} m): {gps} placed from GPS, {pinned} pinned.{without}";
        }

        #endregion

        #region Pins and thumbnails

        /// <summary>The pins (3D, with the markers): a dot at each placed photo's eye, a heading tick and a stem to the floor.</summary>
        private void DrawSitePhotoPins(Overlay3D overlay)
        {
            if (SitePhotos == null || !SitePhotos.Document.ShowPins || SitePhotoViewOpen) { return; }
            uint colour = UiTheme.BOOKMARK;
            foreach (SitePhoto photo in SitePhotos.Photos)
            {
                if (!photo.IsPlaced) { continue; }
                Vector3 eye = photo.Local;
                Vector3 ahead = new(MathF.Cos(photo.Yaw), MathF.Sin(photo.Yaw), 0f);
                overlay.Line(eye, eye - new Vector3(0f, 0f, SITE_PHOTO_EYE), 1.5f, Rgba.WithAlpha(colour, 0.5f));
                overlay.Line(eye, eye + ahead * 1.2f, 2.5f, colour);
                overlay.Dot(eye, 7f, UiTheme.TEXT);
                overlay.Dot(eye, 5f, colour);
            }
        }

        /// <summary>Sizes the per-photo caches to the photo count (allocates only when the photos change).</summary>
        private void EnsureSiteCaches()
        {
            int count = SitePhotos.Photos.Count;
            if (_siteCacheRevision == SitePhotos.Revision && _siteCacheCount == count) { return; }
            _siteCacheRevision = SitePhotos.Revision;
            _siteCacheCount = count;
            if (_siteOrder.Length < count)
            {
                _siteOrder = new int[count];
                _siteDistance = new float[count];
            }
            _siteBlocked = new bool[count];
        }

        /// <summary>
        /// The nearest photos' thumbnails floating over their pins (2D, under the HUD): sized by distance, faint behind
        /// buildings, the hovered one outlined with its name. <paramref name="cursor"/>: the cursor hovers and a click
        /// opens (the viewer); else the crosshair hovers (the walkthrough's click is in <see cref="UpdateGuns"/>).
        /// </summary>
        private void DrawSiteThumbs(FontAtlas f, InputState input, bool cursor)
        {
            _siteThumbCount = 0;
            _siteHover = -1;
            if (SitePhotos == null || !SitePhotos.Document.ShowPins || SitePhotos.Photos.Count == 0 || SitePhotoViewOpen) { return; }
            EnsureSiteCaches();

            // Candidates in front of the camera, nearest first
            List<SitePhoto> photos = SitePhotos.Photos;
            Vector3 eye = Camera.Position;
            int n = 0;
            for (int i = 0; i < photos.Count; i++)
            {
                SitePhoto photo = photos[i];
                if (!photo.IsPlaced) { continue; }
                float distance = Vector3.Distance(eye, photo.Local);
                if (distance < 0.6f || distance > SITE_THUMB_FAR || Vector3.Dot(photo.Local - eye, Camera.Forward) <= 0f) { continue; }
                _siteOrder[n] = i;
                _siteDistance[n] = distance;
                n++;
            }
            if (n == 0) { return; }
            Array.Sort(_siteDistance, _siteOrder, 0, n);
            int shown = Math.Min(n, SITE_THUMBS_MAX);

            // A few sight lines a frame, round the shown ones (blocked: drawn faint)
            for (int k = 0; k < Math.Min(SITE_SIGHT_CHECKS, shown); k++)
            {
                int j = (_siteSightNext + k) % shown;
                SitePhoto photo = photos[_siteOrder[j]];
                Vector3 to = photo.Local - eye;
                float length = to.Length();
                _siteBlocked[_siteOrder[j]] = length > 0.7f && Pick(eye, to / length, length - 0.5f, out RayHit _);
            }
            _siteSightNext = (_siteSightNext + SITE_SIGHT_CHECKS) % Math.Max(shown, 1);

            // Drawn farthest first, so nearer ones sit on top; the hovered one is the nearest under the pointer (not when
            // the viewer's toolbar or panels are there, or a tool is armed: its clicks are for the model)
            Vector2 pointer = cursor ? input.MousePosition : new Vector2(_window.Width * 0.5f, _window.Height * 0.5f);
            bool pointable = !(_viewer && (!_unarmed || ViewerOverPanels(pointer)));
            int uploads = SITE_THUMB_UPLOADS;
            int hoveredSlot = -1;
            for (int j = shown - 1; j >= 0; j--)
            {
                int index = _siteOrder[j];
                SitePhoto photo = photos[index];
                if (!Camera.WorldToScreen(photo.Local + new Vector3(0f, 0f, 0.35f), out Vector2 anchor)) { continue; }
                float h = S(Math.Clamp(2400f / _siteDistance[j], 30f, 92f));
                float w = h * Math.Clamp(photo.Aspect, 0.5f, 2f);
                float x = MathF.Round(anchor.X - w * 0.5f), y = MathF.Round(anchor.Y - h - S(12));
                if (x > _window.Width || y > _window.Height || x + w < 0 || y + h < 0) { continue; }

                bool blocked = _siteBlocked[index];
                float alpha = blocked ? 0.35f : 1f;
                uint texture = SiteThumbTexture(photo, ref uploads);
                _ui.Line(anchor.X, anchor.Y, anchor.X, y + h, MathF.Max(1f, S(1.5f)), Rgba.WithAlpha(UiTheme.BOOKMARK, 0.7f * alpha));
                _ui.Rect(x - S(2), y - S(2), w + S(4), h + S(4), Rgba.Hex(0x0C0E12, 0.85f * alpha));
                if (texture != 0) { _ui.Image(texture, x, y, w, h, _window.Width, _window.Height, Rgba.Hex(0xFFFFFF, alpha)); }
                else { _ui.Rect(x, y, w, h, Rgba.WithAlpha(UiTheme.CONTROL, alpha)); }

                int slot = _siteThumbCount++;
                _siteThumbRects[slot] = new Vector4(x - S(2), y - S(2), w + S(4), h + S(4));
                _siteThumbPhoto[slot] = index;
                if (pointable && pointer.X >= x && pointer.X < x + w && pointer.Y >= y && pointer.Y < y + h) { hoveredSlot = slot; }
            }

            if (hoveredSlot < 0) { return; }
            _siteHover = _siteThumbPhoto[hoveredSlot];
            Vector4 r = _siteThumbRects[hoveredSlot];
            SitePhoto hovered = photos[_siteHover];
            _ui.Outline(r.X, r.Y, r.Z, r.W, S(2), UiTheme.ACCENT);
            string label = string.IsNullOrEmpty(hovered.Group) ? hovered.Name : hovered.Group;
            float lw = UiBatch.Measure(f.Small, label) + S(16);
            float lx = Math.Clamp(r.X + r.Z * 0.5f - lw * 0.5f, S(4), _window.Width - lw - S(4)), ly = r.Y - S(24);
            _ui.Panel(lx, ly, lw, S(20), UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
            _ui.Text(f.Small, lx + S(8), ly + S(3), label, UiTheme.TEXT);

            if (cursor && input.LeftPressed && _window.IsActive)
            {
                input.ConsumeClicks();
                OpenSitePhoto(hovered, adjust: false);
            }
        }

        /// <summary>The photo whose thumbnail (last frame) is under a window pixel, or -1.</summary>
        private int SiteThumbAt(Vector2 p)
        {
            for (int slot = _siteThumbCount - 1; slot >= 0; slot--) // nearest last
            {
                Vector4 r = _siteThumbRects[slot];
                if (p.X >= r.X && p.X < r.X + r.Z && p.Y >= r.Y && p.Y < r.Y + r.W) { return _siteThumbPhoto[slot]; }
            }
            return -1;
        }

        /// <summary>The walkthrough (crosshair, no gun): a click on the thumbnail under it opens the photo.</summary>
        /// <returns>True when one opened.</returns>
        private bool TryOpenSitePhotoUnderCrosshair()
        {
            if (_siteHover < 0 || SitePhotos == null || _siteHover >= SitePhotos.Photos.Count) { return false; }
            OpenSitePhoto(SitePhotos.Photos[_siteHover], adjust: false);
            return true;
        }

        #endregion

        #region Photo view

        /// <summary>
        /// Opens the photo view: the camera at the photo's eye with its lens and the photo over the model. A photo not yet
        /// in the model (the full walkthrough only) opens adjusting, from the current view.
        /// </summary>
        private void OpenSitePhoto(SitePhoto photo, bool adjust)
        {
            if (photo == null) { return; }
            if (!photo.IsPlaced && _viewer) { return; }
            if (_paused) { SetPaused(false); }
            CloseSectionEditor();
            CloseSunPanel();
            CloseGridsPanel();
            CloseFixtureDock();
            if (_viewer) { ViewerCancelDrag(); }
            if (_sitePhotoView == null) { _siteWasWalking = _viewer ? _viewerWalk : !_player.Flying; }

            _sitePhotoView = photo;
            _sitePhotoAdjust = !_viewer && (adjust || !photo.IsPlaced);
            _siteAdjustFov = photo.Fov;
            if (photo.IsPlaced) { PutPlayerAtSitePhoto(photo); }
            else if (!_player.Flying) { _player.ToggleFly(); } // adjusting flies

            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            Sound.Play(SoundId.UiClick);
        }

        /// <summary>The player at the photo's eye and view (flying), so closing the view leaves you there.</summary>
        private void PutPlayerAtSitePhoto(SitePhoto photo)
        {
            if (!_player.Flying) { _player.ToggleFly(); }
            _player.TeleportTo(photo.Local - new Vector3(0f, 0f, _player.Controller.StandEye), photo.Yaw, photo.Pitch);
        }

        /// <summary>Closes the photo view where it looked (walking on the floor below when walking before).</summary>
        private void CloseSitePhoto()
        {
            if (_sitePhotoView == null) { return; }
            _sitePhotoView = null;
            _sitePhotoAdjust = false;
            _siteGroupFilter = null;
            ReleaseSitePhotoPicture();
            if (_siteWasWalking && _player.Flying) { _player.ToggleFly(); }
            _window.Input.ReleaseAll();
            if (!_viewer && !_paused && _window.IsActive && !CursorModeOpen) { _window.SetCaptured(true); }
        }

        /// <summary>The photos ‹ PREVIOUS / NEXT › step through: placed ones (all, in the walkthrough), within the group being browsed.</summary>
        private bool SiteStepsTo(SitePhoto photo) =>
            (photo.IsPlaced || !_viewer) && (_siteGroupFilter == null || string.Equals(photo.Group, _siteGroupFilter, StringComparison.OrdinalIgnoreCase));

        private void StepSitePhoto(int direction)
        {
            List<SitePhoto> photos = SitePhotos.Photos;
            int at = photos.IndexOf(_sitePhotoView);
            for (int k = 1; k <= photos.Count; k++)
            {
                SitePhoto next = photos[((at + direction * k) % photos.Count + photos.Count) % photos.Count];
                if (SiteStepsTo(next))
                {
                    if (!ReferenceEquals(next, _sitePhotoView)) { OpenSitePhoto(next, adjust: false); }
                    return;
                }
            }
        }

        /// <summary>"3 / 12" for the photo view (allocates: drawn into the text buffer each frame instead).</summary>
        private void SitePhotoPosition(TextBuffer text, out int index, out int count)
        {
            index = count = 0;
            foreach (SitePhoto photo in SitePhotos.Photos)
            {
                if (!SiteStepsTo(photo)) { continue; }
                count++;
                if (ReferenceEquals(photo, _sitePhotoView)) { index = count; }
            }
            text.Append(index).Append(" / ").Append(count);
        }

        /// <summary>The browse entry points (the viewer's popover): the nearest placed photo, or a group's first.</summary>
        private void BrowseSitePhotos(string group)
        {
            _siteGroupFilter = group;
            SitePhoto best = null;
            float bestDistance = float.MaxValue;
            foreach (SitePhoto photo in SitePhotos.Photos)
            {
                if (!photo.IsPlaced || !SiteStepsTo(photo)) { continue; }
                if (group != null)
                {
                    best = photo;
                    break;
                }
                float distance = Vector3.DistanceSquared(photo.Local, Camera.Position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = photo;
                }
            }
            if (best == null)
            {
                _siteGroupFilter = null;
                Toast("No site photos stand in the model yet", 3f);
                return;
            }
            OpenSitePhoto(best, adjust: false); // the group filter stays until the view closes
        }

        /// <summary>PIN HERE: the photo stands at the current view and lens; with GPS it aligns the others.</summary>
        private void PinSitePhotoHere()
        {
            SitePhoto photo = _sitePhotoView;
            if (photo == null) { return; }
            SitePhotos.Place(photo, Camera.Position, Camera.Yaw, Camera.Pitch, _siteAdjustFov, PhotoPlacements.PINNED);
            int placed = ApplySiteAlignment();
            SitePhotos.Save();
            _sitePhotoAdjust = false;
            PutPlayerAtSitePhoto(photo);
            Sound.Play(SoundId.Commit);
            Flash(UiTheme.BOOKMARK, 0.2f);
            string message = !photo.HasLocation
                ? "Pinned here (this photo has no GPS, so it doesn't move the others)"
                : !_siteFitted ? "Pinned here"
                : _siteFit.FromNorth
                    ? $"Pinned: with the model's true north it places {placed} photo{(placed == 1 ? string.Empty : "s")} from GPS. Pin a second photo far from this one to fit the turn too"
                    : $"Pinned: {_siteFit.Anchors} pins place {placed} photo{(placed == 1 ? string.Empty : "s")} from GPS (they agree within {_siteFit.Rms:0.0} m)";
            Toast(SitePhotos.LastError ?? message, 6f, important: SitePhotos.LastError != null);
        }

        /// <summary>The window's horizontal field of view that puts the photo's lens across the photo's rectangle.</summary>
        private float SitePhotoWindowFov()
        {
            SitePhotoRect(out _, out _, out float w, out _);
            float lens = _sitePhotoAdjust ? _siteAdjustFov : _sitePhotoView.Fov;
            double half = Math.Tan(lens * Math.PI / 360.0) * _window.Width / MathF.Max(w, 1f);
            return Math.Clamp((float)(2.0 * Math.Atan(half) * 180.0 / Math.PI), 10f, 170f);
        }

        /// <summary>The photo fitted into the window (its own aspect).</summary>
        private void SitePhotoRect(out float x, out float y, out float w, out float h)
        {
            float width = MathF.Max(_window.Width, 1), height = MathF.Max(_window.Height, 1);
            float aspect = _sitePhotoView?.Aspect ?? 1.5f;
            if (width / height > aspect)
            {
                h = height;
                w = height * aspect;
            }
            else
            {
                w = width;
                h = width / aspect;
            }
            x = MathF.Round((width - w) * 0.5f);
            y = MathF.Round((height - h) * 0.5f);
        }

        /// <summary>
        /// Keys and mouse in the photo view: Esc closes (adjusting: goes back to the photo's view), the wheel fades (or,
        /// adjusting, sets the lens), adjusting flies on the move keys and looks with RMB-drag.
        /// </summary>
        private void UpdateSitePhotoView(InputState input)
        {
            if (_window.IsCaptured) { _window.SetCaptured(false); }
            if (input.Pressed(InputAction.Back))
            {
                if (_sitePhotoAdjust && _sitePhotoView.IsPlaced)
                {
                    _sitePhotoAdjust = false;
                    PutPlayerAtSitePhoto(_sitePhotoView);
                }
                else { CloseSitePhoto(); }
                return;
            }
            if (input.Pressed(InputAction.Fullscreen)) { _window.ToggleFullscreen(); }
            if (input.Pressed(InputAction.Screenshot)) { RequestScreenshot(); }

            Vector2 mouse = input.MousePosition;
            bool overPanel = Inside(_sitePhotoPanelRect, mouse);
            if (_sitePhotoAdjust)
            {
                if (input.Down(InputAction.DragLook)) { _player.Look(input.MouseDeltaX, input.MouseDeltaY, _sensitivity, _invertY); }
                PadEditorLook(input);
                if (input.Wheel != 0 && !overPanel)
                {
                    _siteAdjustFov = Math.Clamp(_siteAdjustFov - input.Wheel, SitePhoto.MIN_FOV, SitePhoto.MAX_FOV);
                    input.ConsumeWheel();
                }
            }
            else if (input.Wheel != 0 && !overPanel)
            {
                _sitePhotoOpacity = Math.Clamp(_sitePhotoOpacity - input.Wheel * 0.1f, 0f, 1f);
                input.ConsumeWheel();
            }
        }

        /// <summary>The photo view's camera (not adjusting): the photo's eye and view.</summary>
        private void SitePhotoCamera()
        {
            Camera.Position = _sitePhotoView.Local;
            Camera.Yaw = _sitePhotoView.Yaw;
            Camera.Pitch = _sitePhotoView.Pitch;
        }

        /// <summary>The photo view's 2D layer: the photo over the model, the dock (name, fade, lens, buttons) and toasts.</summary>
        private void BuildSitePhotoView(int width, int height)
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            if (!input.LeftDown) { _activeSlider = -1; }
            SitePhoto photo = _sitePhotoView;

            // The photo
            SitePhotoRect(out float px, out float py, out float pw, out float ph);
            float alpha = _sitePhotoAdjust ? _siteAdjustOpacity : _sitePhotoOpacity;
            uint texture = SitePhotoPictureTexture(photo);
            if (alpha > 0.004f)
            {
                if (texture != 0) { _ui.Image(texture, px, py, pw, ph, width, height, Rgba.Hex(0xFFFFFF, alpha)); }
                else
                {
                    int uploads = 1;
                    uint thumb = SiteThumbTexture(photo, ref uploads);
                    if (thumb != 0) { _ui.Image(thumb, px, py, pw, ph, width, height, Rgba.Hex(0xFFFFFF, alpha)); }
                }
            }
            if (_sitePhotoAdjust)
            {
                // Crosshair: line the model up through the middle
                float cx = MathF.Round(width * 0.5f), cy = MathF.Round(height * 0.5f);
                _ui.Rect(cx - S(10), cy - S(0.5f), S(20), MathF.Max(1f, UiScale), Rgba.Hex(0xFFFFFF, 0.6f));
                _ui.Rect(cx - S(0.5f), cy - S(10), MathF.Max(1f, UiScale), S(20), Rgba.Hex(0xFFFFFF, 0.6f));
            }

            // The dock
            float dw = MathF.Min(S(820), width - S(32)), dh = S(_sitePhotoAdjust ? 176 : 128);
            float dx = MathF.Round(width * 0.5f - dw * 0.5f), dy = height - S(20) - dh;
            _ui.Panel(dx, dy, dw, dh, UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
            _sitePhotoPanelRect = new Vector4(dx, dy, dw, dh);
            float ix = dx + S(16), iw = dw - S(32), iy = dy + S(12);

            TextBuffer text = Text.Clear();
            SitePhotoPosition(text, out _, out _);
            _ui.TextRight(f.Mono, ix + iw, iy + S(2), text.Span, UiTheme.TEXT_MUTED);
            _ui.TextWrapped(f.Bold, ix, iy, iw - S(80), photo.Name, UiTheme.TEXT, maxLines: 1);
            iy += S(24);
            text = Text.Clear();
            if (!string.IsNullOrEmpty(photo.Group)) { text.Append(photo.Group).Append(" · "); }
            if (!string.IsNullOrEmpty(photo.Taken)) { text.Append(photo.Taken).Append(" · "); }
            text.Append(photo.Placement == PhotoPlacements.PINNED ? "pinned by hand" : photo.Placement == PhotoPlacements.GPS ? "placed from GPS" : "not in the model yet");
            if (!photo.HasLocation) { text.Append(" · no GPS"); }
            _ui.Text(f.Small, ix, iy, text.Span, UiTheme.TEXT_MUTED);
            iy += S(20);

            // Fade (and, adjusting, the lens)
            float sliderW = _sitePhotoAdjust ? (iw - S(UiTheme.GAP_LARGE)) * 0.5f : iw;
            int percent = (int)MathF.Round(alpha * 100f);
            text = Text.Clear().Append(percent).Append(" %");
            float faded = Slider(f, input, SITE_SLIDER_OPACITY, ix, iy, sliderW, "Photo over the model", text.Span, alpha, 0f, 1f);
            if (_activeSlider == SITE_SLIDER_OPACITY)
            {
                if (_sitePhotoAdjust) { _siteAdjustOpacity = faded; }
                else { _sitePhotoOpacity = faded; }
            }
            if (_sitePhotoAdjust)
            {
                text = Text.Clear().Append(_siteAdjustFov, 0).Append('°');
                float lens = Slider(f, input, SITE_SLIDER_FOV, ix + sliderW + S(UiTheme.GAP_LARGE), iy, sliderW, "Lens (across the photo)", text.Span, _siteAdjustFov, SitePhoto.MIN_FOV, 120f);
                if (_activeSlider == SITE_SLIDER_FOV) { _siteAdjustFov = MathF.Round(lens * 2f) * 0.5f; }
            }
            iy += S(46);
            if (_sitePhotoAdjust)
            {
                _ui.TextWrapped(f.Small, ix, iy, iw, "Fly with the move keys (Space / Ctrl: up / down), right-drag to look, scroll for the lens: line the model up with the photo, then PIN HERE.", UiTheme.TEXT_FAINT, maxLines: 2);
                iy += S(34);
            }

            // Buttons
            float bh = S(UiTheme.SMALL_BUTTON_HEIGHT), gap = S(UiTheme.GAP_SMALL);
            int buttons = _viewer ? 3 : 4;
            float bw = (iw - gap * (buttons - 1)) / buttons, bx = ix;
            if (MenuButton(f, bx, iy, bw, "‹ PREVIOUS", false, false, height: bh, compact: true)) { StepSitePhoto(-1); return; }
            bx += bw + gap;
            if (MenuButton(f, bx, iy, bw, "NEXT ›", false, false, height: bh, compact: true)) { StepSitePhoto(+1); return; }
            bx += bw + gap;
            if (!_viewer)
            {
                if (_sitePhotoAdjust)
                {
                    if (MenuButton(f, bx, iy, bw, "PIN HERE", primary: true, danger: false, height: bh, compact: true)) { PinSitePhotoHere(); return; }
                }
                else if (MenuButton(f, bx, iy, bw, "ADJUST POSITION", false, false, height: bh, compact: true))
                {
                    _sitePhotoAdjust = true;
                    _siteAdjustFov = photo.Fov;
                    PutPlayerAtSitePhoto(photo);
                }
                bx += bw + gap;
            }
            if (MenuButton(f, bx, iy, bw, _sitePhotoAdjust && photo.IsPlaced ? "CANCEL" : "CLOSE", false, false, height: bh, compact: true))
            {
                if (_sitePhotoAdjust && photo.IsPlaced)
                {
                    _sitePhotoAdjust = false;
                    PutPlayerAtSitePhoto(photo);
                }
                else { CloseSitePhoto(); }
                return;
            }
            BuildToast(f, width);
        }

        #endregion

        #region Panel

        private void OpenSitePhotosPanel()
        {
            _sitePhotosOpen = true;
            _sitePhotoScroll.Reset();
            _siteDeleteArmed = null;
            _sitePhotosNotice = null;
        }

        /// <summary>One level back: the panel closes (to the pause menu).</summary>
        /// <returns>True if it was open.</returns>
        private bool CloseSitePhotosPanel()
        {
            if (!_sitePhotosOpen) { return false; }
            _sitePhotosOpen = false;
            _siteDeleteArmed = null;
            return true;
        }

        /// <summary>
        /// Pause menu → SITE PHOTOS: the alignment in words, the photos (thumbnail, name, group, time, how it is placed;
        /// OPEN, UNPIN, DELETE), IMPORT PHOTOS… / IMPORT FOLDER… and whether the pins show.
        /// </summary>
        private void BuildSitePhotosPanel()
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            int count = SitePhotos.Photos.Count;
            Text.Clear().AppendGrouped(count).Append(count == 1 ? " photo · kept in " : " photos · kept in ").Append(SitePhotos.FileName);
            const string FILES = "IMPORT PHOTOS…", FOLDER = "IMPORT FOLDER…";
            PanelFrame frame = BeginPanel(PanelSize.Standard, "SITE PHOTOS", UiTheme.BOOKMARK_LABEL, Text.Span,
                headerControls: ShellButtonWidth(f, FILES) + ShellButtonWidth(f, FOLDER) + S(UiTheme.GAP_SMALL));
            if (HeaderButton(ref frame, FOLDER))
            {
                ImportSitePhotos(folder: true);
                return;
            }
            if (HeaderButton(ref frame, FILES))
            {
                ImportSitePhotos(folder: false);
                return;
            }

            float y = frame.BodyTop;
            float textH = _ui.TextWrapped(f.Body, frame.Ix, y, frame.Iw, SiteAlignmentText(), UiTheme.TEXT_SOFT, maxLines: 3);
            y += textH + S(UiTheme.GAP);
            BuildSitePhotoRows(f, input, frame.Ix, y, frame.Iw, frame.BodyBottom);
            if (!_sitePhotosOpen) { return; } // OPEN left the panel

            int pins = FooterAction(SitePhotos.Document.ShowPins ? "PINS: SHOWN" : "PINS: HIDDEN");
            int pressed = EndPanel(frame, _sitePhotosNotice, "OPEN a photo to see it over the model; ADJUST POSITION and PIN HERE place it.");
            if (pressed == pins)
            {
                SitePhotos.Document.ShowPins = !SitePhotos.Document.ShowPins;
                SitePhotos.Save();
            }
        }

        private void BuildSitePhotoRows(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            List<SitePhoto> list = SitePhotos.Photos;
            if (list.Count == 0) { return; }
            if (_siteDeleteArmed != null && _clock > _siteDeleteArmedUntil) { _siteDeleteArmed = null; }

            float rowH = S(70);
            float top = BeginScroll(_sitePhotoScroll, input, x, y, w, bottom - y, out float cw);
            VisibleRows(_sitePhotoScroll, rowH, list.Count, out int first, out int last);
            float listX = x;
            x += S(8);
            w = cw - S(16);

            SitePhoto open = null, unpin = null, delete = null;
            float wide = S(84), gap = S(6), buttonsW = wide * 3 + gap * 2;
            int uploads = SITE_THUMB_UPLOADS;
            for (int i = first; i < last; i++)
            {
                SitePhoto photo = list[i];
                float ry = top + i * rowH + S(4);
                if ((i & 1) == 0) { _ui.Rect(listX, ry - S(4), cw, rowH - S(4), UiTheme.ROW_STRIPE); }

                float thumbH = S(58), thumbW = MathF.Min(S(104), thumbH * Math.Clamp(photo.Aspect, 0.5f, 2f));
                uint thumb = SiteThumbTexture(photo, ref uploads);
                if (thumb != 0) { _ui.Image(thumb, x, ry, thumbW, thumbH, _window.Width, _window.Height); }
                else { _ui.Panel(x, ry, thumbW, thumbH, UiTheme.CONTROL, UiTheme.CONTROL_BORDER); }

                float textX = x + S(116), textW = w - buttonsW - S(130);
                _ui.TextWrapped(f.Bold, textX, ry + S(6), textW, photo.Name, UiTheme.TEXT, maxLines: 1);
                TextBuffer text = Text.Clear();
                if (!string.IsNullOrEmpty(photo.Group)) { text.Append(photo.Group).Append(" · "); }
                if (!string.IsNullOrEmpty(photo.Taken)) { text.Append(photo.Taken).Append(" · "); }
                text.Append(photo.HasLocation ? "GPS" : "no GPS");
                _ui.TextWrapped(f.Small, textX, ry + S(28), textW, text.Span, UiTheme.TEXT_MUTED, maxLines: 1);
                string state = photo.Placement == PhotoPlacements.PINNED ? "Pinned by hand" : photo.Placement == PhotoPlacements.GPS ? "Placed from GPS" : "Not in the model yet";
                _ui.Text(f.Small, textX, ry + S(44), state, photo.IsPlaced ? UiTheme.BOOKMARK_LABEL : UiTheme.MEASURE_TEXT);

                float bx = x + w - buttonsW, by = ry + S(14), bh = S(30);
                if (SmallButton(f, input, bx, by, wide, bh, photo.IsPlaced ? "OPEN" : "PLACE")) { open = photo; }
                bx += wide + gap;
                if (photo.Placement == PhotoPlacements.PINNED && SmallButton(f, input, bx, by, wide, bh, "UNPIN")) { unpin = photo; }
                bx += wide + gap;
                bool armed = ReferenceEquals(_siteDeleteArmed, photo);
                if (SmallButton(f, input, bx, by, wide, bh, armed ? "SURE?" : "DELETE", danger: true)) { delete = photo; }
            }
            EndScroll(_sitePhotoScroll, input, top + list.Count * rowH);

            // Act after drawing (the list must not change while it is being walked)
            if (open != null)
            {
                CloseSitePhotosPanel();
                OpenSitePhoto(open, adjust: !open.IsPlaced);
            }
            else if (unpin != null)
            {
                PhotoStore.Unplace(unpin);
                int placed = ApplySiteAlignment();
                SitePhotos.Save();
                _sitePhotosNotice = SitePhotos.LastError ?? $"Unpinned “{unpin.Name}” ({placed} placed from GPS now)";
                Sound.Play(SoundId.Remove);
            }
            else if (delete != null)
            {
                if (ReferenceEquals(_siteDeleteArmed, delete))
                {
                    bool anchor = delete.Placement == PhotoPlacements.PINNED && delete.HasLocation;
                    if (_siteThumbTextures.Remove(delete.Id, out uint texture) && texture != 0) { Gl.DeleteTexture(texture); }
                    SitePhotos.Remove(delete);
                    if (anchor)
                    {
                        ApplySiteAlignment();
                        SitePhotos.Save();
                    }
                    _siteDeleteArmed = null;
                    _sitePhotosNotice = SitePhotos.LastError ?? $"Deleted “{delete.Name}”";
                    Sound.Play(SoundId.Remove);
                }
                else
                {
                    _siteDeleteArmed = delete;
                    _siteDeleteArmedUntil = _clock + 3f;
                }
            }
        }

        /// <summary>The groups (the viewer's popover; rebuilt when the photos change).</summary>
        private List<string> SitePhotoGroups()
        {
            if (_siteGroupsRevision != SitePhotos.Revision)
            {
                _siteGroupsRevision = SitePhotos.Revision;
                _siteGroups.Clear();
                foreach (SitePhoto photo in SitePhotos.Photos)
                {
                    if (!photo.IsPlaced || string.IsNullOrEmpty(photo.Group)) { continue; }
                    if (!_siteGroups.Exists(g => string.Equals(g, photo.Group, StringComparison.OrdinalIgnoreCase))) { _siteGroups.Add(photo.Group); }
                }
            }
            return _siteGroups;
        }

        #endregion

        #region Textures

        /// <summary>A photo's thumbnail texture (uploaded on first use, within a per-frame budget), or 0.</summary>
        private uint SiteThumbTexture(SitePhoto photo, ref int budget)
        {
            if (_siteThumbTextures.TryGetValue(photo.Id, out uint texture)) { return texture; }
            if (budget <= 0) { return 0; }
            budget--;
            texture = UploadJpegTexture(SitePhotos.ThumbnailOf(photo), mipmaps: true);
            _siteThumbTextures[photo.Id] = texture;
            return texture;
        }

        /// <summary>The open photo's picture texture (uploaded when the photo changes), or 0.</summary>
        private uint SitePhotoPictureTexture(SitePhoto photo)
        {
            if (photo == null) { return 0; }
            if (ReferenceEquals(_sitePhotoTextureFor, photo.Id)) { return _sitePhotoTexture; }
            ReleaseSitePhotoPicture();
            _sitePhotoTextureFor = photo.Id;
            _sitePhotoTexture = UploadJpegTexture(SitePhotos.PictureOf(photo), mipmaps: false);
            return _sitePhotoTexture;
        }

        /// <summary>Decodes a JPEG into an RGBA texture (top row first), or 0 when it can't be read.</summary>
        private static unsafe uint UploadJpegTexture(byte[] jpeg, bool mipmaps)
        {
            if (jpeg == null || jpeg.Length == 0) { return 0; }
            try
            {
                using var stream = new MemoryStream(jpeg);
                using var decoded = new System.Drawing.Bitmap(stream);
                using var bitmap = new System.Drawing.Bitmap(decoded.Width, decoded.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) { graphics.DrawImage(decoded, 0, 0, decoded.Width, decoded.Height); }
                System.Drawing.Imaging.BitmapData bits = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    uint texture = Gl.GenTexture();
                    Gl.BindTexture(Gl.TEXTURE_2D, texture);
                    Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, (int)(mipmaps ? Gl.LINEAR_MIPMAP_LINEAR : Gl.LINEAR));
                    Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, (int)Gl.LINEAR);
                    Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, (int)Gl.CLAMP_TO_EDGE);
                    Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, (int)Gl.CLAMP_TO_EDGE);
                    Gl.PixelStore(Gl.UNPACK_ALIGNMENT, 4);
                    Gl.TexImage2D(Gl.TEXTURE_2D, 0, Gl.RGBA8, bitmap.Width, bitmap.Height, Gl.BGRA, Gl.UNSIGNED_BYTE, (void*)bits.Scan0);
                    if (mipmaps) { Gl.GenerateMipmap(Gl.TEXTURE_2D); }
                    Gl.BindTexture(Gl.TEXTURE_2D, 0);
                    return texture;
                }
                finally
                {
                    bitmap.UnlockBits(bits);
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Site photo texture failed: {ex.Message}");
                return 0;
            }
        }

        #endregion
    }
}
