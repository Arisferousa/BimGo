using System.Numerics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Site photos round: how a photo stands in the model (<see cref="SitePhoto.Placement"/>).
    /// </summary>
    public static class PhotoPlacements
    {
        /// <summary>Not in the model yet (no GPS, or GPS but no alignment): listed in the panel only.</summary>
        public const string NONE = "none";

        /// <summary>Placed from its GPS position and heading through the alignment (moves when the alignment changes).</summary>
        public const string GPS = "gps";

        /// <summary>Pinned by hand at a view matched to the model (PIN HERE); with GPS it also aligns the others.</summary>
        public const string PINNED = "pinned";

        /// <summary>A stored value, or <see cref="NONE"/> when unknown.</summary>
        public static string Normalise(string value) => value switch
        {
            GPS => GPS,
            PINNED => PINNED,
            _ => NONE
        };
    }

    /// <summary>
    /// Site photos round: one site photograph: where it was taken (GPS, heading, lens), where it stands in the model
    /// (the eye point in Revit internal metres, the view's yaw and pitch, as bookmarks), and its pictures
    /// (<c>photos/&lt;id&gt;.jpg</c> up to 2048 px, and <c>photos/&lt;id&gt;_t.jpg</c>, a small thumbnail).
    /// </summary>
    public sealed class SitePhoto
    {
        /// <summary>Longest name, group or note.</summary>
        public const int MAX_TEXT = 120;

        /// <summary>Narrowest / widest field of view (horizontal degrees).</summary>
        public const float MIN_FOV = 10f, MAX_FOV = 150f;

        /// <summary>The field of view used when the photo doesn't record its lens (a phone's main camera, landscape).</summary>
        public const float DEFAULT_FOV = 66f;

        /// <summary>Stable id (GUID, no dashes).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>The file it was imported from (without the folder).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>A group, such as the building it shows (the sub-folder it was imported from), or empty.</summary>
        public string Group { get; set; } = string.Empty;

        /// <summary>A note, or empty.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string Note { get; set; }

        /// <summary>When it was taken ("yyyy-MM-dd HH:mm", the camera's clock), or null.</summary>
        public string Taken { get; set; }

        /// <summary>GPS latitude (degrees), or null.</summary>
        public double? Latitude { get; set; }

        /// <summary>GPS longitude (degrees), or null.</summary>
        public double? Longitude { get; set; }

        /// <summary>GPS altitude (m), or null.</summary>
        public double? Altitude { get; set; }

        /// <summary>The compass direction the camera faced (degrees), or null.</summary>
        public double? Heading { get; set; }

        /// <summary>The stored picture's size (pixels, upright).</summary>
        public int Width { get; set; }

        /// <summary>The stored picture's height (pixels, upright).</summary>
        public int Height { get; set; }

        /// <summary>Horizontal field of view (degrees).</summary>
        public float Fov { get; set; } = DEFAULT_FOV;

        /// <summary><see cref="PhotoPlacements"/>.</summary>
        public string Placement { get; set; } = PhotoPlacements.NONE;

        /// <summary>Eye X (Revit internal metres).</summary>
        public double X { get; set; }

        /// <summary>Eye Y (Revit internal metres).</summary>
        public double Y { get; set; }

        /// <summary>Eye Z (Revit internal metres).</summary>
        public double Z { get; set; }

        /// <summary>View yaw (radians, the model's axes: 0 looks along +X).</summary>
        public float Yaw { get; set; }

        /// <summary>View pitch (radians, positive looks up).</summary>
        public float Pitch { get; set; }

        /// <summary>The eye in scene-local metres (set by the store from the scene origin; not saved).</summary>
        [JsonIgnore]
        public Vector3 Local { get; set; }

        /// <summary>True when the photo records a usable GPS position.</summary>
        [JsonIgnore]
        public bool HasLocation => Latitude.HasValue && Longitude.HasValue;

        /// <summary>True when it stands in the model (pinned or placed from GPS).</summary>
        [JsonIgnore]
        public bool IsPlaced => Placement != PhotoPlacements.NONE;

        /// <summary>The picture's file name.</summary>
        [JsonIgnore]
        public string Image => Id + ".jpg";

        /// <summary>The thumbnail's file name.</summary>
        [JsonIgnore]
        public string Thumb => Id + "_t.jpg";

        /// <summary>Width / height (1.5 when unknown).</summary>
        [JsonIgnore]
        public float Aspect => Width > 0 && Height > 0 ? (float)Width / Height : 1.5f;

        /// <summary>Clamps and fills in values; false when the photo is unusable (a bad id).</summary>
        public bool Clean()
        {
            if (!PhotoDocument.IsSafeId(Id)) { return false; }
            Name = Trim(Name) ?? "Photo";
            Group = Trim(Group) ?? string.Empty;
            Note = Trim(Note);
            if (Latitude is double la && (!double.IsFinite(la) || Math.Abs(la) > 90)) { Latitude = null; }
            if (Longitude is double lo && (!double.IsFinite(lo) || Math.Abs(lo) > 180)) { Longitude = null; }
            if (!Latitude.HasValue || !Longitude.HasValue) { Latitude = Longitude = null; }
            if (Altitude is double al && !double.IsFinite(al)) { Altitude = null; }
            if (Heading is double h) { Heading = double.IsFinite(h) ? ((h % 360.0) + 360.0) % 360.0 : null; }
            Width = Math.Clamp(Width, 0, 65535);
            Height = Math.Clamp(Height, 0, 65535);
            Fov = float.IsFinite(Fov) ? Math.Clamp(Fov, MIN_FOV, MAX_FOV) : DEFAULT_FOV;
            Placement = PhotoPlacements.Normalise(Placement);
            if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Z)) { X = Y = Z = 0; Placement = PhotoPlacements.NONE; }
            Yaw = float.IsFinite(Yaw) ? Yaw : 0f;
            Pitch = float.IsFinite(Pitch) ? Math.Clamp(Pitch, -1.5f, 1.5f) : 0f;
            if (Placement == PhotoPlacements.GPS && !HasLocation) { Placement = PhotoPlacements.NONE; }
            return true;
        }

        private static string Trim(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) { return null; }
            text = text.Trim();
            return text.Length > MAX_TEXT ? text[..MAX_TEXT] : text;
        }
    }

    /// <summary>
    /// Site photos round: the model's site photos (<c>photos.json</c> in a .bimgo, beside the comments in a live
    /// session's model folder) and whether their pins show.
    /// </summary>
    public sealed class PhotoDocument
    {
        /// <summary>The most photos one model keeps.</summary>
        public const int MAX_PHOTOS = 2000;

        /// <summary>Always 1.</summary>
        public int Version { get; set; } = 1;

        /// <summary>The model title (for people reading the file).</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>Pins and floating thumbnails show in the model.</summary>
        public bool ShowPins { get; set; } = true;

        /// <summary>Smallest / largest <see cref="ThumbScale"/>.</summary>
        public const float MIN_THUMB_SCALE = 0.5f, MAX_THUMB_SCALE = 2.5f;

        /// <summary>The floating thumbnails' size (1 = the default; kept with the file, so a viewer copy shows them as set).</summary>
        public float ThumbScale { get; set; } = 1f;

        /// <summary>Nearest / farthest <see cref="ThumbDistance"/> (m).</summary>
        public const float MIN_THUMB_DISTANCE = 20f, MAX_THUMB_DISTANCE = 1000f;

        /// <summary>
        /// How far away a photo's floating thumbnail still shows (m; it fades out over the last fifth, the pin stays), so a
        /// site with many photos isn't cluttered from afar.
        /// </summary>
        public float ThumbDistance { get; set; } = 150f;

        /// <summary>The photos, in import order.</summary>
        public List<SitePhoto> Photos { get; set; } = new();

        /// <summary>True when there are no photos.</summary>
        [JsonIgnore]
        public bool IsEmpty => Photos == null || Photos.Count == 0;

        private static readonly Regex SAFE_ID = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
        private static readonly Regex SAFE_IMAGE = new("^[A-Za-z0-9_-]{1,64}(_t)?\\.jpg$", RegexOptions.CultureInvariant);

        /// <summary>True for an id this format writes.</summary>
        public static bool IsSafeId(string id) => id != null && SAFE_ID.IsMatch(id);

        /// <summary>True for a picture file name this format writes (<c>&lt;id&gt;.jpg</c>, <c>&lt;id&gt;_t.jpg</c>).</summary>
        public static bool IsSafeImageName(string name) => name != null && SAFE_IMAGE.IsMatch(name);

        /// <summary>Drops unusable and repeated photos and clamps the rest.</summary>
        public PhotoDocument Clean()
        {
            Version = 1;
            Model ??= string.Empty;
            ThumbScale = float.IsFinite(ThumbScale) ? Math.Clamp(ThumbScale, MIN_THUMB_SCALE, MAX_THUMB_SCALE) : 1f;
            ThumbDistance = float.IsFinite(ThumbDistance) ? Math.Clamp(ThumbDistance, MIN_THUMB_DISTANCE, MAX_THUMB_DISTANCE) : 150f;
            Photos ??= new List<SitePhoto>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Photos.RemoveAll(p => p == null || !p.Clean() || !seen.Add(p.Id));
            if (Photos.Count > MAX_PHOTOS) { Photos.RemoveRange(MAX_PHOTOS, Photos.Count - MAX_PHOTOS); }
            return this;
        }
    }

    /// <summary>
    /// Site photos round: the live model folder's photos (<c>photos.json</c>, the pictures in <c>photos-images</c> beside
    /// it). A .bimgo carries them as <c>photos.json</c> and <c>photos/</c>.
    /// </summary>
    public static class PhotoFiles
    {
        /// <summary>The model folder's photos file.</summary>
        public const string FOLDER_FILE = "photos.json";

        /// <summary>The photos file that goes with a comments sidecar, or null.</summary>
        public static string SidecarFor(string commentsSidecarPath)
        {
            if (string.IsNullOrWhiteSpace(commentsSidecarPath)) { return null; }
            string modelFolder = ModelFolders.FolderOf(commentsSidecarPath);
            if (modelFolder != null) { return Path.Combine(modelFolder, FOLDER_FILE); }
            return SidecarJson.BesideComments(commentsSidecarPath, BimGoFormat.PHOTOS_SIDECAR_SUFFIX);
        }

        /// <summary>The pictures' folder for a photos file.</summary>
        public static string ImagesFolderFor(string photosPath) =>
            string.IsNullOrEmpty(photosPath) ? null
            : Path.Combine(Path.GetDirectoryName(photosPath) ?? string.Empty, Path.GetFileNameWithoutExtension(photosPath) + "-images");

        /// <summary>Reads the file (null if missing or unreadable).</summary>
        public static PhotoDocument Read(string path, out string error) => SidecarJson.Read<PhotoDocument>(path, "Site photos", out error)?.Clean();

        /// <summary>Writes the file atomically.</summary>
        public static bool Write(string path, PhotoDocument document, out string error) => SidecarJson.Write(path, document, "Site photos", out error);

        /// <summary>Reads the photos' pictures and thumbnails from the folder (missing ones are left out).</summary>
        public static Dictionary<string, byte[]> ReadImages(string folder, PhotoDocument document)
        {
            var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(folder) || document?.Photos == null) { return images; }
            foreach (SitePhoto photo in document.Photos)
            {
                foreach (string name in new[] { photo.Image, photo.Thumb })
                {
                    try
                    {
                        string path = Path.Combine(folder, name);
                        if (File.Exists(path) && new FileInfo(path).Length <= BimGoFormat.MAX_PHOTO_IMAGE_BYTES) { images[name] = File.ReadAllBytes(path); }
                    }
                    catch (Exception ex)
                    {
                        Utilities.Log_Utils.Write($"Site photo {name} not read: {ex.Message}");
                    }
                }
            }
            return images;
        }

        /// <summary>Writes one picture into the folder (atomically).</summary>
        public static bool WriteImage(string folder, string name, byte[] bytes, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(folder) || !PhotoDocument.IsSafeImageName(name) || bytes == null)
            {
                error = "The photo has no place to go.";
                return false;
            }
            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, name), temp = path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error = $"The photo could not be saved: {ex.Message}";
                Utilities.Log_Utils.Write(error);
                return false;
            }
        }

        /// <summary>Deletes one picture from the folder (quietly).</summary>
        public static void DeleteImage(string folder, string name)
        {
            if (string.IsNullOrEmpty(folder) || !PhotoDocument.IsSafeImageName(name)) { return; }
            try
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path)) { File.Delete(path); }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Site photo {name} not deleted: {ex.Message}");
            }
        }
    }
}
