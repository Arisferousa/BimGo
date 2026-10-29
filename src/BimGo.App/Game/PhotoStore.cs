using System.Numerics;
using BimGo.Format;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Site photos round: the model's site photos (where each stands, its GPS and lens) and their pictures, persisted
    /// like the billboards:
    /// <list type="bullet">
    /// <item><b>Model folder</b> (Revit sessions): <c>photos.json</c> with the pictures in <c>photos-images</c> beside it,
    /// written on every change.</item>
    /// <item><b>Embedded</b> (.bimgo files): kept in memory and saved with the file; changes raise <see cref="Changed"/>
    /// so the document is marked dirty.</item>
    /// </list>
    /// Positions are Revit internal metres in the file; <see cref="SitePhoto.Local"/> is the scene-local copy.
    /// </summary>
    internal sealed class PhotoStore
    {
        private readonly string _path, _imagesFolder, _model;
        private readonly Vector3 _origin;

        /// <summary>The document (never null).</summary>
        public PhotoDocument Document { get; private set; } = new();

        /// <summary>The photos, in import order.</summary>
        public List<SitePhoto> Photos => Document.Photos;

        /// <summary>The pictures and thumbnails by file name (JPEG bytes).</summary>
        public Dictionary<string, byte[]> Images { get; } = new(StringComparer.Ordinal);

        /// <summary>The last IO error, or null.</summary>
        public string LastError { get; private set; }

        /// <summary>True when kept inside a .bimgo rather than the model folder.</summary>
        public bool IsEmbedded => _path == null;

        /// <summary>Where it's kept (for the panel).</summary>
        public string FileName => IsEmbedded ? "this file" : Path.GetFileName(_path);

        /// <summary>Incremented on every change (dirty tracking, the pins' caches).</summary>
        public int Revision { get; private set; }

        /// <summary>Raised after any change.</summary>
        public event Action Changed;

        /// <summary>Creates the store.</summary>
        /// <param name="sidecarPath">The model folder's photos.json, or null to keep them embedded.</param>
        public PhotoStore(string sidecarPath, string model, Vector3 origin)
        {
            _path = sidecarPath;
            _imagesFolder = PhotoFiles.ImagesFolderFor(sidecarPath);
            _model = model;
            _origin = origin;
        }

        #region Load / save

        /// <summary>Loads the model folder's file and pictures. No-op for embedded stores.</summary>
        public void Load()
        {
            if (IsEmbedded) { return; }
            PhotoDocument document = PhotoFiles.Read(_path, out string error);
            LastError = error;
            if (document != null) { LoadFrom(document, PhotoFiles.ReadImages(_imagesFolder, document)); }
        }

        /// <summary>Takes a document and its pictures (a .bimgo's). Does not count as a change.</summary>
        public void LoadFrom(PhotoDocument document, Dictionary<string, byte[]> images)
        {
            if (document == null) { return; }
            Document = document.Clean();
            Images.Clear();
            if (images != null)
            {
                foreach (KeyValuePair<string, byte[]> pair in images) { Images[pair.Key] = pair.Value; }
            }
            foreach (SitePhoto photo in Photos) { Prepare(photo); }
            Revision++;
        }

        /// <summary>The document for saving into a .bimgo (null when there are no photos).</summary>
        public PhotoDocument ToDocument()
        {
            if (Document.IsEmpty) { return null; }
            Document.Model = _model;
            return Document;
        }

        /// <summary>The pictures for saving into a .bimgo (only those a photo still uses).</summary>
        public Dictionary<string, byte[]> ImagesForSave()
        {
            var used = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (SitePhoto photo in Photos)
            {
                if (Images.TryGetValue(photo.Image, out byte[] full)) { used[photo.Image] = full; }
                if (Images.TryGetValue(photo.Thumb, out byte[] thumb)) { used[photo.Thumb] = thumb; }
            }
            return used;
        }

        /// <summary>Records a change: writes the model folder's file, or (embedded) flags it for the document.</summary>
        public bool Save()
        {
            Revision++;
            bool saved = true;
            if (!IsEmbedded)
            {
                Document.Model = _model;
                saved = PhotoFiles.Write(_path, Document, out string error);
                LastError = error;
            }
            try { Changed?.Invoke(); }
            catch (Exception ex) { Utilities.Log_Utils.Write($"Site photo change handler failed: {ex.Message}"); }
            return saved;
        }

        #endregion

        #region Photos

        /// <summary>
        /// Adds an imported photo and its pictures (written into the model folder at once in a live session). Call
        /// <see cref="Save"/> after a batch.
        /// </summary>
        /// <returns>False when the model already holds the most photos it keeps.</returns>
        public bool Add(SitePhoto photo, byte[] picture, byte[] thumbnail)
        {
            if (photo == null || Photos.Count >= PhotoDocument.MAX_PHOTOS || !photo.Clean()) { return false; }
            Images[photo.Image] = picture;
            if (thumbnail != null) { Images[photo.Thumb] = thumbnail; }
            if (!IsEmbedded)
            {
                if (!PhotoFiles.WriteImage(_imagesFolder, photo.Image, picture, out string error)) { LastError = error; }
                if (thumbnail != null && !PhotoFiles.WriteImage(_imagesFolder, photo.Thumb, thumbnail, out error)) { LastError = error; }
            }
            Prepare(photo);
            Photos.Add(photo);
            return true;
        }

        /// <summary>Removes a photo and its pictures.</summary>
        public void Remove(SitePhoto photo)
        {
            if (photo == null || !Photos.Remove(photo)) { return; }
            Images.Remove(photo.Image);
            Images.Remove(photo.Thumb);
            if (!IsEmbedded)
            {
                PhotoFiles.DeleteImage(_imagesFolder, photo.Image);
                PhotoFiles.DeleteImage(_imagesFolder, photo.Thumb);
            }
            Save();
        }

        /// <summary>
        /// Stands a photo in the model: the eye (scene-local), the view, its lens and how it got there. Call
        /// <see cref="Save"/> after.
        /// </summary>
        public void Place(SitePhoto photo, Vector3 local, float yaw, float pitch, float fov, string placement)
        {
            if (photo == null) { return; }
            photo.X = local.X + (double)_origin.X;
            photo.Y = local.Y + (double)_origin.Y;
            photo.Z = local.Z + (double)_origin.Z;
            photo.Yaw = yaw;
            photo.Pitch = Math.Clamp(pitch, -1.5f, 1.5f);
            photo.Fov = Math.Clamp(fov, SitePhoto.MIN_FOV, SitePhoto.MAX_FOV);
            photo.Placement = PhotoPlacements.Normalise(placement);
            Prepare(photo);
        }

        /// <summary>Takes a photo out of the model (it stays in the list). Call <see cref="Save"/> after.</summary>
        public static void Unplace(SitePhoto photo)
        {
            if (photo != null) { photo.Placement = PhotoPlacements.NONE; }
        }

        /// <summary>A photo's picture (JPEG), or null.</summary>
        public byte[] PictureOf(SitePhoto photo) => photo != null && Images.TryGetValue(photo.Image, out byte[] jpeg) ? jpeg : null;

        /// <summary>A photo's thumbnail (JPEG; the picture when it has none), or null.</summary>
        public byte[] ThumbnailOf(SitePhoto photo) => photo != null && Images.TryGetValue(photo.Thumb, out byte[] jpeg) ? jpeg : PictureOf(photo);

        /// <summary>A model point (Revit internal metres) in scene-local metres.</summary>
        public Vector3 ToLocal(double x, double y, double z) => new((float)(x - _origin.X), (float)(y - _origin.Y), (float)(z - _origin.Z));

        private void Prepare(SitePhoto photo) => photo.Local = ToLocal(photo.X, photo.Y, photo.Z);

        #endregion
    }
}
