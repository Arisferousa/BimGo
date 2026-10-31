using SD = System.Drawing;
using SDT = System.Drawing.Text;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// UI round: the interface's fonts. Four open-licence families ship with BimGo (Resources\Fonts, SIL Open Font
    /// Licence: Inter, Roboto, Source Sans 3, JetBrains Mono), loaded privately (never installed into Windows); any font
    /// installed in Windows can be picked too. <see cref="Current"/> is what <see cref="FontAtlas.Build"/> rasterises:
    /// the text font (labels, body, headings, titles), the numbers font (readouts) and the text size.
    /// </summary>
    internal static class UiFonts
    {
        /// <summary>The text font BimGo has always used (narrow, clear at small sizes).</summary>
        public const string DEFAULT_TEXT = "Bahnschrift";

        /// <summary>The numbers font BimGo has always used.</summary>
        public const string DEFAULT_NUMBERS = "Consolas";

        /// <summary>Text size multipliers offered (on top of the UI scale).</summary>
        public const float MIN_TEXT_SIZE = 0.9f, MAX_TEXT_SIZE = 1.25f;

        /// <summary>A font family's faces for the interface's roles: regular, semibold (labels, headings) and bold (titles).</summary>
        public sealed record Faces(string Name, bool Bundled, (string Family, SD.FontStyle Style) Regular, (string Family, SD.FontStyle Style) SemiBold,
            (string Family, SD.FontStyle Style) Bold, bool Monospaced = false);

        /// <summary>The bundled families (their GDI family names, as in the files' name tables).</summary>
        public static readonly Faces[] BUNDLED =
        {
            new("Inter", true, ("Inter", SD.FontStyle.Regular), ("Inter SemiBold", SD.FontStyle.Regular), ("Inter", SD.FontStyle.Bold)),
            new("Roboto", true, ("Roboto", SD.FontStyle.Regular), ("Roboto Medium", SD.FontStyle.Regular), ("Roboto", SD.FontStyle.Bold)),
            new("Source Sans 3", true, ("Source Sans 3", SD.FontStyle.Regular), ("Source Sans 3 Semibold", SD.FontStyle.Regular), ("Source Sans 3", SD.FontStyle.Bold)),
            new("JetBrains Mono", true, ("JetBrains Mono", SD.FontStyle.Regular), ("JetBrains Mono", SD.FontStyle.Bold), ("JetBrains Mono", SD.FontStyle.Bold), Monospaced: true)
        };

        /// <summary>The fonts in use (set from the settings before an atlas is built).</summary>
        public static (string Text, string Numbers, float TextSize) Current { get; set; } = (DEFAULT_TEXT, DEFAULT_NUMBERS, 1f);

        private static SDT.PrivateFontCollection _bundled;
        private static List<string> _installed;

        /// <summary>The bundled fonts, loaded once from Resources\Fonts beside the exe (missing files are skipped).</summary>
        private static SDT.PrivateFontCollection Bundled
        {
            get
            {
                if (_bundled != null) { return _bundled; }
                _bundled = new SDT.PrivateFontCollection();
                try
                {
                    string folder = Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts");
                    if (Directory.Exists(folder))
                    {
                        foreach (string file in Directory.GetFiles(folder, "*.ttf"))
                        {
                            try { _bundled.AddFontFile(file); }
                            catch (Exception ex) { Utilities.Log_Utils.Write($"Font {Path.GetFileName(file)} skipped: {ex.Message}"); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Bundled fonts unavailable: {ex.Message}");
                }
                return _bundled;
            }
        }

        /// <summary>
        /// The font names to offer: the bundled families first, then BimGo's usual ones, then every other installed
        /// family (alphabetical).
        /// </summary>
        public static List<string> Choices(bool numbers)
        {
            var names = new List<string>();
            foreach (Faces faces in BUNDLED)
            {
                if (numbers == faces.Monospaced && Find(faces.Regular.Family) != null) { names.Add(faces.Name); }
            }
            foreach (string name in numbers ? new[] { DEFAULT_NUMBERS, "Cascadia Mono", "Courier New", "Lucida Console" } : new[] { DEFAULT_TEXT, "Segoe UI", "Calibri", "Arial", "Verdana", "Tahoma" })
            {
                if (!names.Contains(name) && Installed().Contains(name, StringComparer.OrdinalIgnoreCase)) { names.Add(name); }
            }
            foreach (string name in Installed())
            {
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) { names.Add(name); }
            }
            return names;
        }

        /// <summary>The installed font families (read once).</summary>
        public static List<string> Installed()
        {
            if (_installed != null) { return _installed; }
            _installed = new List<string>();
            try
            {
                using var collection = new SDT.InstalledFontCollection();
                foreach (SD.FontFamily family in collection.Families)
                {
                    if (family.IsStyleAvailable(SD.FontStyle.Regular)) { _installed.Add(family.Name); }
                }
                _installed.Sort(StringComparer.CurrentCultureIgnoreCase);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Installed fonts couldn't be listed: {ex.Message}");
            }
            return _installed;
        }

        /// <summary>
        /// A family's faces for the roles: a bundled one as described; BimGo's usual Bahnschrift with its SemiBold face;
        /// any other installed family with its SemiBold / Semibold / Medium face when installed, else its bold style.
        /// </summary>
        public static Faces Resolve(string name, bool numbers)
        {
            name = string.IsNullOrWhiteSpace(name) ? (numbers ? DEFAULT_NUMBERS : DEFAULT_TEXT) : name.Trim();
            Faces bundled = BUNDLED.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (bundled != null) { return bundled; }
            (string, SD.FontStyle) semibold = (name, SD.FontStyle.Bold);
            foreach (string suffix in new[] { " SemiBold", " Semibold", " Medium", " Demibold" })
            {
                if (Find(name + suffix) != null)
                {
                    semibold = (name + suffix, SD.FontStyle.Regular);
                    break;
                }
            }
            return new Faces(name, false, (name, SD.FontStyle.Regular), semibold, (name, SD.FontStyle.Bold), numbers);
        }

        /// <summary>A font family by name: bundled first, else installed; null when neither has it.</summary>
        public static SD.FontFamily Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { return null; }
            foreach (SD.FontFamily family in Bundled.Families)
            {
                if (string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase)) { return family; }
            }
            try
            {
                var family = new SD.FontFamily(name);
                return string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase) ? family : null;
            }
            catch (ArgumentException)
            {
                return null; // not installed
            }
        }

        /// <summary>
        /// A font of the first face that exists with its style (falling back to the regular style, then to the next
        /// candidate), at a pixel size; Windows' sans-serif when none does.
        /// </summary>
        public static SD.Font Create(float pixelSize, params (string Family, SD.FontStyle Style)[] candidates)
        {
            foreach ((string name, SD.FontStyle style) in candidates)
            {
                SD.FontFamily family = Find(name);
                if (family == null) { continue; }
                if (family.IsStyleAvailable(style)) { return new SD.Font(family, pixelSize, style, SD.GraphicsUnit.Pixel); }
                if (family.IsStyleAvailable(SD.FontStyle.Regular)) { return new SD.Font(family, pixelSize, SD.FontStyle.Regular, SD.GraphicsUnit.Pixel); }
            }
            return new SD.Font(SD.FontFamily.GenericSansSerif, pixelSize, candidates.Length > 0 ? candidates[0].Style : SD.FontStyle.Regular, SD.GraphicsUnit.Pixel);
        }
    }
}
