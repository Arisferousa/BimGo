using BimGo.Edits;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Updating a reviewed model from a newer IFC export: the new geometry goes into the .bimgo in place of the old, and
    /// everything reviewed on it stays (comments, views, site photos, measurements, the sun, billboards, paths, light
    /// profiles, the cut and what is hidden, clashes, the viewer flag). All of those are stored in the model's own
    /// coordinates or by element UniqueId, so they stay where they were. Edits made in the walkthrough and picked
    /// textures belong to the old geometry and are left out.
    /// </summary>
    public static class ModelUpdate
    {
        /// <summary>An IFC saved this long after its import counts as changed (file systems round times).</summary>
        private static readonly TimeSpan CHANGED_AFTER = TimeSpan.FromSeconds(2);

        /// <summary>Folders with more .bimgo files than this are not searched for a model made from an IFC.</summary>
        public const int MAX_FOLDER_FILES = 60;

        /// <summary>
        /// The reviewed document with a new model in it.
        /// </summary>
        /// <param name="reviewed">The document as it would be saved now.</param>
        /// <param name="scene">The newly imported model.</param>
        /// <returns>A new document (the inputs are not changed).</returns>
        public static BimGoDocument WithNewModel(BimGoDocument reviewed, SceneData scene)
        {
            ArgumentNullException.ThrowIfNull(reviewed);
            ArgumentNullException.ThrowIfNull(scene);
            return new BimGoDocument
            {
                Scene = scene,
                Comments = reviewed.Comments ?? new CommentDocument(),
                Journal = new EditJournal(),
                Bookmarks = reviewed.Bookmarks ?? new BookmarkDocument(),
                Paths = reviewed.Paths,
                Entourage = reviewed.Entourage,
                EntourageImages = reviewed.EntourageImages,
                Photos = reviewed.Photos,
                PhotoImages = reviewed.PhotoImages,
                LightProfiles = reviewed.LightProfiles,
                Measurements = reviewed.Measurements,
                Sun = reviewed.Sun,
                Visibility = reviewed.Visibility,
                Materials = null,
                Thumbnail = reviewed.Thumbnail,
                Clashes = reviewed.Clashes,
                Viewer = reviewed.Viewer,
                CreatedUtc = DateTime.UtcNow,
                Kind = reviewed.Kind,
                Path = reviewed.Path,
                ReadFormatVersion = reviewed.ReadFormatVersion
            };
        }

        /// <summary>
        /// The IFC file a model was imported from, or null (made in Revit, or the path is unknown).
        /// </summary>
        public static string SourceIfc(ModelProvenance provenance)
        {
            if (provenance == null || string.IsNullOrWhiteSpace(provenance.ModelPath)) { return null; }
            if (provenance.ModelKey == null || !provenance.ModelKey.StartsWith("ifc:", StringComparison.Ordinal)) { return null; }
            return IfcImporter.HasExtension(provenance.ModelPath) ? provenance.ModelPath : null;
        }

        /// <summary>
        /// Whether the IFC a model was imported from is still there and has been saved again since.
        /// </summary>
        /// <param name="provenance">The model's provenance.</param>
        /// <param name="ifcPath">The IFC file (when true).</param>
        /// <param name="ifcSavedUtc">When it was last saved (when true).</param>
        public static bool SourceChanged(ModelProvenance provenance, out string ifcPath, out DateTime ifcSavedUtc)
        {
            ifcPath = SourceIfc(provenance);
            ifcSavedUtc = default;
            if (ifcPath == null) { return false; }
            try
            {
                if (!File.Exists(ifcPath)) { return false; }
                ifcSavedUtc = File.GetLastWriteTimeUtc(ifcPath);
                return ifcSavedUtc > provenance.ExtractedUtc + CHANGED_AFTER;
            }
            catch (Exception)
            {
                return false; // unreadable: as if not there
            }
        }

        /// <summary>
        /// Whether a .bimgo's model was imported from this IFC file.
        /// </summary>
        public static bool MadeFrom(ManifestDto manifest, string ifcPath)
        {
            string source = SourceIfc(manifest?.Provenance);
            return source != null && ifcPath != null && string.Equals(FullPath(source), FullPath(ifcPath), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Finds the .bimgo made from an IFC file: among the candidates (e.g. recent files) and the .bimgo files beside
        /// the IFC, the most recently saved whose model came from it.
        /// </summary>
        /// <param name="ifcPath">The IFC file.</param>
        /// <param name="candidates">Other .bimgo files to look at (may be null).</param>
        /// <returns>The .bimgo's path, or null.</returns>
        public static string FindModelFor(string ifcPath, IEnumerable<string> candidates)
        {
            if (string.IsNullOrWhiteSpace(ifcPath)) { return null; }
            var paths = new List<string>();
            if (candidates != null) { paths.AddRange(candidates.Where(p => BimGoFormat.HasExtension(p ?? string.Empty))); }
            try
            {
                string folder = Path.GetDirectoryName(FullPath(ifcPath));
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    string[] beside = Directory.GetFiles(folder, "*" + BimGoFormat.EXTENSION);
                    if (beside.Length <= MAX_FOLDER_FILES) { paths.AddRange(beside); }
                }
            }
            catch (Exception)
            {
                // The folder can't be listed: the candidates only
            }

            string best = null;
            DateTime bestSaved = DateTime.MinValue;
            foreach (string path in paths.Select(FullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!File.Exists(path)) { continue; }
                    ManifestDto manifest = BimGoReader.ReadManifest(path, out _);
                    if (!MadeFrom(manifest, ifcPath)) { continue; }
                    DateTime saved = manifest.SavedUtc != default ? manifest.SavedUtc : File.GetLastWriteTimeUtc(path);
                    if (best == null || saved > bestSaved) { best = path; bestSaved = saved; }
                }
                catch (Exception)
                {
                    // Not readable: skipped
                }
            }
            return best;
        }

        private static string FullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception) { return path; }
        }
    }
}
