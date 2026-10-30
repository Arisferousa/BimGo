using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Materials round: how one material (by name), one category or one element should look: an image (a built-in
    /// texture, an Autodesk library image or one of your own, copied into the file), a colour, the image's real-world
    /// size and angle, shine, metal, water, grass, a bump and a cutout. Made for IFC models, whose export carries
    /// material names and colours but no textures (Revit LT can only export IFC).
    /// </summary>
    public sealed class MaterialMatch
    {
        /// <summary>The material name this matches (any case), or null for a category or element override.</summary>
        public string Material { get; set; }

        /// <summary>An override for every element of this category (<see cref="CategoryCatalog"/> key), or null.</summary>
        public string Category { get; set; }

        /// <summary>An override for one element (its UniqueId: the IFC GlobalId), or null.</summary>
        public string Element { get; set; }

        /// <summary>The override's name as listed (the element's or the category's), or null.</summary>
        public string Label { get; set; }

        /// <summary>The colour image ("textures/…" in the file or library), or null for a plain colour.</summary>
        public string Image { get; set; }

        /// <summary>Where the image came from, as shown ("Brick (built-in)", "Autodesk: …", a file name), or null.</summary>
        public string ImageName { get; set; }

        /// <summary>The colour as "#RRGGBB", or null to keep the model's own.</summary>
        public string Colour { get; set; }

        /// <summary>Real-world width one image repeat covers (m).</summary>
        public float SizeU { get; set; } = 1f;

        /// <summary>Real-world height one image repeat covers (m).</summary>
        public float SizeV { get; set; } = 1f;

        /// <summary>Image rotation (degrees).</summary>
        public float Angle { get; set; }

        /// <summary>Reflection strength (0–1).</summary>
        public float Shine { get; set; }

        /// <summary>How blurred the reflection is (0 sharp – 1 matt).</summary>
        public float Roughness { get; set; } = 1f;

        /// <summary>A metal (its reflection takes its colour).</summary>
        public bool Metallic { get; set; }

        /// <summary>Water (ripples).</summary>
        public bool Water { get; set; }

        /// <summary>Grass blades grow on it.</summary>
        public bool Grass { get; set; }

        /// <summary>A bump (height) image ("textures/…"), or null.</summary>
        public string Bump { get; set; }

        /// <summary>The colour image's lightness is the bump.</summary>
        public bool BumpFromImage { get; set; }

        /// <summary>Bump strength (0–2).</summary>
        public float BumpStrength { get; set; } = 1f;

        /// <summary>A cutout image ("textures/…": black is a hole), or null.</summary>
        public string Cutout { get; set; }

        /// <summary>The colour image's transparency (PNG alpha) cuts the holes.</summary>
        public bool CutoutFromImage { get; set; }

        /// <summary>Below this the cutout is a hole (0–1).</summary>
        public float CutoutThreshold { get; set; } = 0.5f;

        /// <summary>True for a category or element override (not a material name).</summary>
        [JsonIgnore]
        public bool IsOverride => Material == null;

        /// <summary>What the match is for: "material:…", "category:…" or "element:…" (one match per key).</summary>
        [JsonIgnore]
        public string Key => Material != null ? "material:" + Material.ToUpperInvariant() : Element != null ? "element:" + Element : "category:" + Category;

        /// <summary>The images it uses (colour, bump, cutout), nulls skipped.</summary>
        public IEnumerable<string> Images()
        {
            if (Image != null) { yield return Image; }
            if (Bump != null) { yield return Bump; }
            if (Cutout != null) { yield return Cutout; }
        }

        /// <summary>A copy with every value in range, or null when it matches nothing.</summary>
        public MaterialMatch Clean()
        {
            string material = Trimmed(Material), category = Trimmed(Category), element = Trimmed(Element);
            if (material == null && category == null && element == null) { return null; }
            return new MaterialMatch
            {
                Material = material,
                Category = material == null && element == null ? category : null,
                Element = material == null ? element : null,
                Label = Trimmed(Label),
                Image = ImageEntry(Image),
                ImageName = Trimmed(ImageName),
                Colour = TryParseColour(Colour, out _) ? Colour.Trim().ToUpperInvariant() : null,
                SizeU = Size(SizeU),
                SizeV = Size(SizeV),
                Angle = float.IsFinite(Angle) ? Angle % 360f : 0f,
                Shine = Unit(Shine, 0f),
                Roughness = Unit(Roughness, 1f),
                Metallic = Metallic,
                Water = Water,
                Grass = Grass,
                Bump = ImageEntry(Bump),
                BumpFromImage = BumpFromImage,
                BumpStrength = float.IsFinite(BumpStrength) ? Math.Clamp(BumpStrength, 0f, 2f) : 1f,
                Cutout = ImageEntry(Cutout),
                CutoutFromImage = CutoutFromImage,
                CutoutThreshold = Unit(CutoutThreshold, 0.5f)
            };

            static string Trimmed(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            static float Size(float value) => float.IsFinite(value) && value > 0.001f ? Math.Min(value, 1000f) : 1f;
            static float Unit(float value, float fallback) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : fallback;
        }

        /// <summary>A copy (for editing without touching the original).</summary>
        public MaterialMatch Copy() => (MaterialMatch)MemberwiseClone();

        /// <summary>An image entry name, or null when it isn't a safe "textures/…" name.</summary>
        public static string ImageEntry(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { return null; }
            name = name.Trim();
            return name.StartsWith(BimGoFormat.TEXTURE_FOLDER, StringComparison.Ordinal) && !name.Contains("..", StringComparison.Ordinal) && name.Length < 200 ? name : null;
        }

        /// <summary>Reads "#RRGGBB" as 0–1 RGB.</summary>
        public static bool TryParseColour(string text, out Vector3 colour)
        {
            colour = default;
            if (string.IsNullOrWhiteSpace(text)) { return false; }
            text = text.Trim();
            if (text.Length != 7 || text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)) { return false; }
            colour = new Vector3(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
            return true;
        }

        /// <summary>0–1 RGB as "#RRGGBB".</summary>
        public static string FormatColour(Vector3 colour)
        {
            static int Byte(float v) => Math.Clamp((int)MathF.Round((float.IsFinite(v) ? v : 0.8f) * 255f), 0, 255);
            return $"#{Byte(colour.X):X2}{Byte(colour.Y):X2}{Byte(colour.Z):X2}";
        }
    }

    /// <summary>
    /// Materials round: a model's matches (material-matches.json in the .bimgo), with the model's own material table
    /// before matching, so the matches can be changed, removed or applied to a newer export of the model.
    /// </summary>
    public sealed class MaterialMatchDocument
    {
        /// <summary>Most matches kept.</summary>
        public const int MAX_MATCHES = 5000;

        /// <summary>The format's version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>The matches (one per <see cref="MaterialMatch.Key"/>).</summary>
        public List<MaterialMatch> Matches { get; set; } = new();

        /// <summary>The model's material table as it came in (before matching), or null.</summary>
        public List<SceneMaterial> BaseMaterials { get; set; }

        /// <summary>True when nothing is matched.</summary>
        [JsonIgnore]
        public bool IsEmpty => Matches == null || Matches.Count == 0;

        /// <summary>A copy with bad and repeated matches dropped (the last of a key wins) and the base table cleaned.</summary>
        public MaterialMatchDocument Clean()
        {
            var matches = new List<MaterialMatch>();
            var at = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MaterialMatch raw in Matches ?? new List<MaterialMatch>())
            {
                MaterialMatch match = raw?.Clean();
                if (match == null) { continue; }
                if (at.TryGetValue(match.Key, out int index)) { matches[index] = match; }
                else if (matches.Count < MAX_MATCHES)
                {
                    at[match.Key] = matches.Count;
                    matches.Add(match);
                }
            }
            return new MaterialMatchDocument
            {
                Version = 1,
                Matches = matches,
                BaseMaterials = BaseMaterials?.Where(m => m != null).Take(MaterialData.MAX_MATERIALS).Select(m => m.Clean()).ToList()
            };
        }

        /// <summary>A deep copy (matches copied; the base table shared: it never changes).</summary>
        public MaterialMatchDocument Copy() => new()
        {
            Version = Version,
            Matches = (Matches ?? new List<MaterialMatch>()).Select(m => m.Copy()).ToList(),
            BaseMaterials = BaseMaterials
        };

        /// <summary>The match for a material name, or null.</summary>
        public MaterialMatch ForMaterial(string name) =>
            name == null ? null : Matches?.FirstOrDefault(m => m.Material != null && string.Equals(m.Material, name.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>The match with this key, or null.</summary>
        public MaterialMatch ForKey(string key) => Matches?.FirstOrDefault(m => m.Key == key);

        /// <summary>Adds or replaces the match with the same key.</summary>
        public void Set(MaterialMatch match)
        {
            match = match?.Clean();
            if (match == null) { return; }
            Matches ??= new List<MaterialMatch>();
            int index = Matches.FindIndex(m => m.Key == match.Key);
            if (index >= 0) { Matches[index] = match; }
            else if (Matches.Count < MAX_MATCHES) { Matches.Add(match); }
        }

        /// <summary>Removes the match with this key.</summary>
        /// <returns>True if there was one.</returns>
        public bool Remove(string key) => Matches != null && Matches.RemoveAll(m => m.Key == key) > 0;

        /// <summary>The images the matches use.</summary>
        public HashSet<string> Images()
        {
            var images = new HashSet<string>(StringComparer.Ordinal);
            foreach (MaterialMatch match in Matches ?? new List<MaterialMatch>())
            {
                foreach (string image in match.Images()) { images.Add(image); }
            }
            return images;
        }
    }

    /// <summary>
    /// Materials round: applies matches to a model's own materials. Matches by name change that material everywhere;
    /// category and element overrides each get a material of their own (added to the table) that their elements'
    /// vertices are pointed at. The model's own table and per-vertex indices are never changed: a new
    /// <see cref="MaterialData"/> comes back, so applying again (after an edit, or to a newer export) starts clean.
    /// </summary>
    public static class MaterialMatcher
    {
        /// <summary>
        /// The materials with the matches applied.
        /// </summary>
        /// <param name="original">The model's own materials (as imported).</param>
        /// <param name="matches">The matches (null or empty: the original comes back).</param>
        /// <param name="images">The matches' images by entry name.</param>
        /// <param name="scene">The model (its elements and indices, for overrides).</param>
        public static MaterialData Apply(MaterialData original, MaterialMatchDocument matches, IReadOnlyDictionary<string, byte[]> images, SceneData scene)
        {
            if (original == null || original.IsEmpty || matches == null || matches.IsEmpty) { return original; }
            images ??= new Dictionary<string, byte[]>(StringComparer.Ordinal);
            bool Has(string name) => name != null && (images.ContainsKey(name) || original.Textures.ContainsKey(name));

            var table = original.Materials.Select(m => m.Clean()).ToList();
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < table.Count; i++) { byName.TryAdd(table[i].Name.Trim(), i); }

            foreach (MaterialMatch match in matches.Matches)
            {
                if (match.Material != null && byName.TryGetValue(match.Material, out int index)) { ApplyLook(table[index], match, Has); }
            }

            // Overrides: categories first, then elements (an element's own wins)
            ushort[] vertexMaterial = original.VertexMaterial;
            if (scene != null && scene.Vertices.Length == vertexMaterial.Length)
            {
                foreach (MaterialMatch match in matches.Matches.Where(m => m.Category != null).Concat(matches.Matches.Where(m => m.Element != null)))
                {
                    int added = -1;
                    for (int e = 0; e < scene.Elements.Length; e++)
                    {
                        ElementRecord element = scene.Elements[e];
                        if (!Overrides(match, element)) { continue; }
                        if (added < 0)
                        {
                            if (table.Count >= MaterialData.MAX_MATERIALS) { break; }
                            ushort first = FirstMaterial(original.VertexMaterial, scene, element);
                            SceneMaterial entry = first < original.Materials.Length ? original.Materials[first].Clean() : new SceneMaterial { Schema = MaterialData.IFC_SCHEMA };
                            entry.Name = match.Label ?? (match.Element != null ? element.Name : CategoryLabel(match.Category));
                            entry.MaterialId = table.Count + 1;
                            ApplyLook(entry, match, Has);
                            added = table.Count;
                            table.Add(entry);
                            if (ReferenceEquals(vertexMaterial, original.VertexMaterial)) { vertexMaterial = (ushort[])original.VertexMaterial.Clone(); }
                        }
                        Point(vertexMaterial, scene, element, (ushort)added);
                    }
                }
            }

            // The images: the original's still used, and the matches'
            var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (SceneMaterial material in table)
            {
                foreach (string image in material.Images())
                {
                    if (textures.ContainsKey(image)) { continue; }
                    if (images.TryGetValue(image, out byte[] bytes) || original.Textures.TryGetValue(image, out bytes)) { textures[image] = bytes; }
                }
            }
            return new MaterialData
            {
                Materials = table.ToArray(),
                VertexMaterial = vertexMaterial,
                VertexUv = original.VertexUv,
                Textures = textures,
                TextureMaxSize = original.TextureMaxSize
            };
        }

        /// <summary>
        /// A match's look on a material: its image (placed by its size and angle), colour, shine, metal, water, grass,
        /// bump and cutout. Images it names that aren't there are left out.
        /// </summary>
        public static void ApplyLook(SceneMaterial material, MaterialMatch match, Func<string, bool> hasImage)
        {
            if (MaterialMatch.TryParseColour(match.Colour, out Vector3 colour))
            {
                material.Colour = colour;
                material.RenderColour = null;
            }
            if (match.Image != null && hasImage(match.Image))
            {
                material.Texture = match.Image;
                material.TextureState = TextureState.Embedded;
                material.TextureOrigin = TextureOrigins.OVERRIDE;
                material.TextureSource = match.ImageName;
                material.Proxy = null;
                material.Fade = 1f;
                material.Tint = Vector3.One;
                material.Invert = false;
                material.ScaleU = match.SizeU;
                material.ScaleV = match.SizeV;
                material.OffsetU = material.OffsetV = 0f;
                material.Angle = match.Angle;
            }
            material.Shine = match.Shine;
            material.Roughness = match.Roughness;
            material.Metallic = match.Metallic;
            material.Water = match.Water;
            if (match.Water && material.WaterBump <= 0f) { material.WaterBump = MaterialOverrides.KEYWORD_WATER_BUMP; }
            material.Grass = match.Grass;
            material.Bump = match.Bump != null && hasImage(match.Bump) ? match.Bump : null;
            material.BumpFromTexture = match.BumpFromImage && material.Texture != null;
            material.BumpStrength = material.Bump != null || material.BumpFromTexture ? match.BumpStrength : 0f;
            material.Cutout = match.Cutout != null && hasImage(match.Cutout) ? match.Cutout : null;
            material.CutoutFromTexture = match.CutoutFromImage && material.Texture != null;
            material.CutoutThreshold = material.HasCutout ? match.CutoutThreshold : 0f;
        }

        /// <summary>Whether an override covers an element.</summary>
        public static bool Overrides(MaterialMatch match, ElementRecord element)
        {
            if (match.Element != null) { return string.Equals(element.UniqueId, match.Element, StringComparison.OrdinalIgnoreCase); }
            return match.Category != null && string.Equals(CategoryKey(element.CategoryIndex), match.Category, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A category's key by its index, or null.</summary>
        public static string CategoryKey(int index) => CategoryCatalog.All.FirstOrDefault(d => d.Index == index)?.Key;

        /// <summary>A category's label by its key (the key itself when unknown).</summary>
        public static string CategoryLabel(string key) => CategoryCatalog.Find(key)?.Label ?? key;

        /// <summary>The material of an element's first vertex (NONE when it has none).</summary>
        private static ushort FirstMaterial(ushort[] vertexMaterial, SceneData scene, ElementRecord element)
        {
            int start = element.OpaqueCount > 0 ? element.OpaqueStart : element.TransparentStart;
            if (element.OpaqueCount + element.TransparentCount == 0 || start >= scene.Indices.Length) { return MaterialData.NONE; }
            uint vertex = scene.Indices[start];
            return vertex < vertexMaterial.Length ? vertexMaterial[vertex] : MaterialData.NONE;
        }

        /// <summary>Points an element's vertices at a material.</summary>
        private static void Point(ushort[] vertexMaterial, SceneData scene, ElementRecord element, ushort material)
        {
            void Range(int start, int count)
            {
                int end = Math.Min(start + count, scene.Indices.Length);
                for (int i = Math.Max(start, 0); i < end; i++)
                {
                    uint vertex = scene.Indices[i];
                    if (vertex < vertexMaterial.Length) { vertexMaterial[vertex] = material; }
                }
            }
            Range(element.OpaqueStart, element.OpaqueCount);
            Range(element.TransparentStart, element.TransparentCount);
        }

        /// <summary>
        /// The model's own materials behind an applied set: the saved base table and per-vertex indices when the file
        /// has them, else the set itself (nothing was matched yet).
        /// </summary>
        /// <param name="applied">The materials as read (matches applied).</param>
        /// <param name="matches">The file's matches, or null.</param>
        /// <param name="baseVertices">The saved per-vertex indices before overrides, or null (none: the same as applied).</param>
        public static MaterialData OriginalOf(MaterialData applied, MaterialMatchDocument matches, ushort[] baseVertices)
        {
            if (applied == null || applied.IsEmpty || matches?.BaseMaterials == null || matches.BaseMaterials.Count == 0) { return applied; }
            ushort[] vertices = baseVertices != null && baseVertices.Length == applied.VertexMaterial.Length ? baseVertices : applied.VertexMaterial;
            SceneMaterial[] table = matches.BaseMaterials.Select(m => m.Clean()).ToArray();
            if (vertices.Any(v => v != MaterialData.NONE && v >= table.Length)) { return applied; } // doesn't fit: keep what was saved
            return new MaterialData
            {
                Materials = table,
                VertexMaterial = vertices,
                VertexUv = applied.VertexUv,
                Textures = new Dictionary<string, byte[]>(StringComparer.Ordinal),
                TextureMaxSize = applied.TextureMaxSize
            };
        }
    }

    /// <summary>
    /// Materials round: a material library (a .bimgo-materials file: library.json and the images under textures/):
    /// matches by material name, kept between projects. A new model's materials with the same names start matched.
    /// </summary>
    public sealed class MaterialLibrary
    {
        /// <summary>The file extension.</summary>
        public const string EXTENSION = ".bimgo-materials";

        /// <summary>Open / Save dialog filter.</summary>
        public const string DIALOG_FILTER = "BimGo material library (*.bimgo-materials)|*.bimgo-materials|All files (*.*)|*.*";

        private const string ENTRY = "library.json";
        private static readonly JsonSerializerOptions JSON = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        /// <summary>The matches (by material name only).</summary>
        public List<MaterialMatch> Matches { get; set; } = new();

        /// <summary>Their images by entry name.</summary>
        [JsonIgnore]
        public Dictionary<string, byte[]> Images { get; } = new(StringComparer.Ordinal);

        /// <summary>The match for a material name, or null.</summary>
        public MaterialMatch Find(string name) =>
            name == null ? null : Matches.FirstOrDefault(m => string.Equals(m.Material, name.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>Adds or replaces a match (by material name), with its images.</summary>
        public void Put(MaterialMatch match, IReadOnlyDictionary<string, byte[]> images)
        {
            match = match?.Clean();
            if (match?.Material == null) { return; }
            Matches.RemoveAll(m => string.Equals(m.Material, match.Material, StringComparison.OrdinalIgnoreCase));
            Matches.Add(match);
            foreach (string image in match.Images())
            {
                if (images != null && images.TryGetValue(image, out byte[] bytes) && bytes != null) { Images[image] = bytes; }
            }
            DropUnused();
        }

        /// <summary>Removes a material's match.</summary>
        public bool Remove(string name)
        {
            bool removed = Matches.RemoveAll(m => string.Equals(m.Material, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) { DropUnused(); }
            return removed;
        }

        /// <summary>Takes in another library's matches (theirs win for the same name).</summary>
        /// <returns>How many were added or replaced.</returns>
        public int Merge(MaterialLibrary other)
        {
            int n = 0;
            foreach (MaterialMatch match in other?.Matches ?? new List<MaterialMatch>())
            {
                Put(match, other.Images);
                n++;
            }
            return n;
        }

        /// <summary>
        /// Matches a model's materials that aren't matched yet from the library (same name, any case).
        /// </summary>
        /// <param name="names">The model's material names.</param>
        /// <param name="matches">The model's matches (added to).</param>
        /// <param name="images">The model's match images (added to).</param>
        /// <returns>How many materials were matched.</returns>
        public int ApplyTo(IEnumerable<string> names, MaterialMatchDocument matches, Dictionary<string, byte[]> images)
        {
            int n = 0;
            foreach (string name in names ?? Enumerable.Empty<string>())
            {
                if (matches.ForMaterial(name) != null) { continue; }
                MaterialMatch match = Find(name);
                if (match == null) { continue; }
                MaterialMatch copy = match.Copy();
                copy.Material = name;
                matches.Set(copy);
                foreach (string image in copy.Images())
                {
                    if (Images.TryGetValue(image, out byte[] bytes)) { images[image] = bytes; }
                }
                n++;
            }
            return n;
        }

        private void DropUnused()
        {
            var used = new HashSet<string>(Matches.SelectMany(m => m.Images()), StringComparer.Ordinal);
            foreach (string name in Images.Keys.Where(k => !used.Contains(k)).ToList()) { Images.Remove(name); }
        }

        /// <summary>Reads a library (an empty one when the file isn't there). Never throws.</summary>
        /// <returns>The library, or null with a reason when the file is there but can't be read.</returns>
        public static MaterialLibrary Read(string path, out string error)
        {
            error = null;
            var library = new MaterialLibrary();
            try
            {
                if (!File.Exists(path)) { return library; }
                using ZipArchive zip = ZipFile.OpenRead(path);
                ZipArchiveEntry entry = zip.GetEntry(ENTRY);
                if (entry == null)
                {
                    error = "it isn't a BimGo material library";
                    return null;
                }
                MaterialLibrary read;
                using (Stream stream = entry.Open()) { read = JsonSerializer.Deserialize<MaterialLibrary>(stream, JSON); }
                foreach (MaterialMatch raw in read?.Matches ?? new List<MaterialMatch>())
                {
                    MaterialMatch match = raw?.Clean();
                    if (match?.Material == null || library.Matches.Count >= MaterialMatchDocument.MAX_MATCHES) { continue; }
                    library.Matches.RemoveAll(m => string.Equals(m.Material, match.Material, StringComparison.OrdinalIgnoreCase));
                    library.Matches.Add(match);
                    foreach (string image in match.Images())
                    {
                        if (library.Images.ContainsKey(image)) { continue; }
                        ZipArchiveEntry imageEntry = zip.GetEntry(image);
                        if (imageEntry == null || imageEntry.Length > BimGoFormat.MAX_TEXTURE_BYTES) { continue; }
                        using Stream stream = imageEntry.Open();
                        using var memory = new MemoryStream();
                        stream.CopyTo(memory);
                        library.Images[image] = memory.ToArray();
                    }
                }
                return library;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Utilities.Log_Utils.Write($"Material library {path} could not be read: {ex.Message}");
                return null;
            }
        }

        /// <summary>Writes the library (through a temporary file). Never throws.</summary>
        public bool Write(string path, out string error)
        {
            error = null;
            string temp = path + ".tmp";
            try
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(folder)) { Directory.CreateDirectory(folder); }
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    ZipArchiveEntry entry = zip.CreateEntry(ENTRY, CompressionLevel.Optimal);
                    using (Stream stream = entry.Open()) { JsonSerializer.Serialize(stream, this, JSON); }
                    foreach (KeyValuePair<string, byte[]> image in Images)
                    {
                        if (MaterialMatch.ImageEntry(image.Key) == null || image.Value == null) { continue; }
                        ZipArchiveEntry imageEntry = zip.CreateEntry(image.Key, CompressionLevel.NoCompression);
                        using Stream stream = imageEntry.Open();
                        stream.Write(image.Value);
                    }
                }
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Utilities.Log_Utils.Write($"Material library {path} could not be written: {ex.Message}");
                try { if (File.Exists(temp)) { File.Delete(temp); } }
                catch { /* best effort */ }
                return false;
            }
        }
    }
}
