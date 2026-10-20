using System.Text.Json.Serialization;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Round X-D1: the model's light profile choices (<c>light-profiles.json</c> in a .bimgo or the live model folder):
    /// per fixture type ("Family : Type") which distribution its lights use, and the .ies files imported in the app.
    /// A type without a choice uses the file its Revit family names, else BimGo's own lobe.
    /// </summary>
    public sealed class LightProfileDocument
    {
        /// <summary>Most imported files kept.</summary>
        public const int MAX_IMPORTS = 32;

        /// <summary>Layout version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>The model title when last saved (information only).</summary>
        public string Model { get; set; }

        /// <summary>The per-type choices.</summary>
        public List<LightProfileChoice> Types { get; set; } = new();

        /// <summary>The imported files (text kept here: .ies files are a few KB).</summary>
        public List<LightProfileImport> Imports { get; set; } = new();

        /// <summary>True when there is nothing to save.</summary>
        [JsonIgnore]
        public bool IsEmpty => (Types == null || Types.Count == 0) && (Imports == null || Imports.Count == 0);

        /// <summary>
        /// The choice for a type: <see cref="LightProfiles.REVIT"/>, <see cref="LightProfiles.LOBE"/>, a built-in key or
        /// an <c>ies:</c> key; null when none was made.
        /// </summary>
        public string ChoiceFor(string familyType)
        {
            foreach (LightProfileChoice choice in Types)
            {
                if (string.Equals(choice.FamilyType, familyType, StringComparison.Ordinal)) { return choice.Profile; }
            }
            return null;
        }

        /// <summary>Sets (or with null clears) a type's choice.</summary>
        public void SetChoice(string familyType, string profile)
        {
            Types.RemoveAll(c => string.Equals(c.FamilyType, familyType, StringComparison.Ordinal));
            if (profile != null) { Types.Add(new LightProfileChoice { FamilyType = familyType, Profile = profile }); }
        }

        /// <summary>The imported file with a hash, or null.</summary>
        public LightProfileImport FindImport(string hash)
        {
            foreach (LightProfileImport import in Imports) { if (import.Hash == hash) { return import; } }
            return null;
        }

        /// <summary>
        /// Drops damaged parts: choices without a type or a known kind of value, imports without a valid hash and text
        /// (or past <see cref="MAX_IMPORTS"/>), duplicates (the last choice per type wins).
        /// </summary>
        public LightProfileDocument Clean()
        {
            Types ??= new List<LightProfileChoice>();
            Imports ??= new List<LightProfileImport>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var types = new List<LightProfileChoice>();
            for (int i = Types.Count - 1; i >= 0; i--)
            {
                LightProfileChoice c = Types[i];
                if (c == null || string.IsNullOrWhiteSpace(c.FamilyType) || !IsKnownValue(c.Profile) || !seen.Add(c.FamilyType)) { continue; }
                types.Insert(0, c);
            }
            Types = types;
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            Imports.RemoveAll(i => i == null || !LightProfiles.IsSafeHash(i.Hash) || string.IsNullOrEmpty(i.Text)
                || i.Text.Length > LightProfile.MAX_IES_BYTES || !hashes.Add(i.Hash));
            if (Imports.Count > MAX_IMPORTS) { Imports.RemoveRange(MAX_IMPORTS, Imports.Count - MAX_IMPORTS); }
            return this;
        }

        private static bool IsKnownValue(string value) =>
            value == LightProfiles.REVIT || value == LightProfiles.LOBE
            || (value != null && value.StartsWith(LightProfiles.BUILT_IN_PREFIX, StringComparison.Ordinal) && value.Length < 64)
            || LightProfiles.HashOf(value) != null;
    }

    /// <summary>
    /// Round X-D1: one fixture type's choice.
    /// </summary>
    public sealed class LightProfileChoice
    {
        /// <summary>"Family : Type" as the elements record it.</summary>
        public string FamilyType { get; set; }

        /// <summary><c>revit</c>, <c>lobe</c>, <c>builtin:…</c> or <c>ies:&lt;hash&gt;</c>.</summary>
        public string Profile { get; set; }
    }

    /// <summary>
    /// Round X-D1: an .ies file imported in the app.
    /// </summary>
    public sealed class LightProfileImport
    {
        /// <summary>16 hex characters (<see cref="LightProfiles.KeyOf"/> without its prefix).</summary>
        public string Hash { get; set; }

        /// <summary>The file name it was imported from.</summary>
        public string Name { get; set; }

        /// <summary>Its text.</summary>
        public string Text { get; set; }
    }

    /// <summary>
    /// Round X-D1: where a live session keeps its light profile choices: <c>light-profiles.json</c> in the model's BimGo
    /// folder (or &lt;model&gt;.bimgo-light-profiles.json beside an older snapshot's comments). Never throws.
    /// </summary>
    public static class LightProfileFiles
    {
        /// <summary>The file that goes with a comments sidecar, or null.</summary>
        public static string SidecarFor(string commentsSidecarPath) => SidecarJson.BesideComments(commentsSidecarPath, BimGoFormat.LIGHT_PROFILES_SIDECAR_SUFFIX);

        /// <summary>Reads the file (null if missing or unreadable).</summary>
        public static LightProfileDocument Read(string path, out string error) => SidecarJson.Read<LightProfileDocument>(path, "Light profiles", out error)?.Clean();

        /// <summary>Writes the file atomically.</summary>
        public static bool Write(string path, LightProfileDocument document, out string error) => SidecarJson.Write(path, document, "Light profiles", out error);
    }
}
