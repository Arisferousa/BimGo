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
    /// Materials round: the pause menu's MATERIALS panel, for IFC models (Revit LT exports IFC with material names and
    /// colours but no textures). Each of the model's materials can be matched to a look: an image (BimGo's built-in
    /// textures, the Autodesk Material Library's images found on this PC, or one of your own; copied into the file),
    /// its real-world size and angle, a colour, shine, metal, water, grass, a bump and a cutout. Overrides do the same
    /// for one element or a whole category.
    /// <list type="bullet">
    /// <item>The matches live in the .bimgo (material-matches.json) with the model's own materials, and the file holds
    /// the look applied, so a viewer copy shows it with nothing else to send. UPDATE FROM IFC matches the new export by
    /// material name (overrides by GlobalId and category).</item>
    /// <item>The material library (%LocalAppData%\BimGo\Material library.bimgo-materials) keeps matches by name between
    /// projects: a newly imported IFC starts matched from it; it can be exported and imported to share.</item>
    /// <item>PICK ON MODEL (the eyedropper) finds a surface's material; there an element or its category can be
    /// overridden. SHOWING: toggles between the matched look and the IFC's own colours.</item>
    /// </list>
    /// Sliders preview live (the material table only) and apply when let go; everything else applies at once.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private const int MATCH_SLIDER = 9301; // 9301–9309
        private const int MATCH_SEARCH_MAX = 48;
        private const int MATCH_UPLOADS = 4; // preview images uploaded a frame
        private const int PICKER_EDITOR = 0, PICKER_BUILT_IN = 1, PICKER_AUTODESK = 2;

        /// <summary>A row of the list: a material of the model, or an override.</summary>
        private sealed class MatchRow
        {
            public string Key;
            public int Material = -1;
            public string Label, Detail;
            public uint Colour;
            public bool Matched, Override;
        }

        // The model's own materials, the matches and their images, and the matches applied (what is saved)
        private bool _matchAvailable;
        private MaterialData _matchOriginal, _matchApplied;
        private MaterialMatchDocument _matchDoc;
        private Dictionary<string, byte[]> _matchImages = new(StringComparer.Ordinal);
        private bool _matchShowOriginal;
        private int _materialsShown; // every set the renderer was given (shadows, grass and probes follow it)

        // The panel
        private bool _materialsOpen;
        private string _matchSelected;
        private int _matchFilter;
        private int _matchPicker;
        private string _materialsNotice;
        private bool _matchReveal;
        private MaterialMatch _matchPending; // a slider being dragged: applied when let go
        private readonly ScrollRegion _matchListScroll = new(), _matchEditScroll = new(), _matchPickerScroll = new();
        private readonly List<MatchRow> _matchRows = new();
        private (int Revision, int Filter, string Search) _matchRowsFor = (-1, 0, null);
        private int _matchRevision, _matchMatchedCount;
        private readonly char[] _matchSearch = new char[MATCH_SEARCH_MAX];
        private int _matchSearchLength, _matchSearchPause;
        private string _matchSearchText = string.Empty;
        private readonly Dictionary<string, uint> _matchPreviews = new(StringComparer.Ordinal);
        private string _materialsMenuLabel;
        private List<ProxyKeyword> _builtInTextures;
        private int _materialsMenuLabelFor = -1;

        // The eyedropper: picking, and the element last picked
        private bool _matchPicking;
        private int _matchPickedElement = -1;

        // The material library
        private MaterialLibrary _library;

        // The Autodesk Material Library browser
        private List<string> _adskRoots;
        private Task<List<string>> _adskTask;
        private List<string> _adskFiles;
        private readonly List<string> _adskShown = new();
        private string _adskShownFor;
        private MaterialImages.Thumbnailer _adskThumbs;
        private readonly Dictionary<string, uint> _adskTextures = new(StringComparer.OrdinalIgnoreCase);
        private readonly char[] _adskSearch = new char[MATCH_SEARCH_MAX];
        private int _adskSearchLength;
        private string _adskSearchText = string.Empty;

        private static readonly string[] MATCH_FILTERS = { "ALL", "TO MATCH", "MATCHED", "OVERRIDES" };
        private static readonly string[] BUMP_OPTIONS = { "NONE", "FROM THE IMAGE", "OWN IMAGE…" };
        private static readonly string[] CUTOUT_OPTIONS = { "NONE", "IMAGE'S TRANSPARENCY", "MASK IMAGE…" };

        /// <summary>Where the material library is kept.</summary>
        private static string LibraryPath => Path.Combine(Utilities.Log_Utils.Folder, "Material library" + MaterialLibrary.EXTENSION);

        #endregion

        #region State

        /// <summary>True for an IFC model with materials (MATERIALS replaces TEXTURES in the pause menu).</summary>
        private bool HasMaterialsPanel => _matchAvailable;

        /// <summary>True while the MATERIALS panel replaces the pause menu.</summary>
        private bool IsMaterialsPanelOpen => _materialsOpen;

        /// <summary>
        /// Called once the renderer is up (after <see cref="InitialiseTextures"/>): the model's own materials behind the
        /// file's matches, and a new IFC import matched from the material library.
        /// </summary>
        private void InitialiseMaterialMatches()
        {
            MaterialData scene = Scene.Materials;
            _matchAvailable = IsFileMode && scene != null && !scene.IsEmpty && scene.Materials.Any(m => m.Schema == MaterialData.IFC_SCHEMA);
            if (!_matchAvailable) { return; }

            BimGoDocument document = _options.Document;
            _matchOriginal = MaterialMatcher.OriginalOf(scene, document?.MaterialMatches, document?.MaterialBaseVertices);
            _matchDoc = document?.MaterialMatches?.Copy() ?? new MaterialMatchDocument();
            _matchDoc.BaseMaterials = _matchOriginal.Materials.ToList();
            _matchImages = document?.MatchImages != null ? new Dictionary<string, byte[]>(document.MatchImages, StringComparer.Ordinal) : new(StringComparer.Ordinal);
            _matchApplied = document?.MaterialMatches != null ? scene : null; // the file holds the look applied
            if (_matchApplied != null && (!_realistic || _whitecard) && document?.Viewer?.Open != true) // (a viewer file opens Realistic)
            {
                Toast("This model's materials are matched: the Realistic colour mode shows their images (Esc → MATERIALS)", 5f);
            }

            // A new import: materials with a name in the library start matched
            if (document?.Kind == FileKinds.IFC_IMPORT && document.MaterialMatches == null)
            {
                int matched = MatchLibrary().ApplyTo(_matchOriginal.Materials.Select(m => m.Name), _matchDoc, _matchImages);
                if (matched > 0)
                {
                    ApplyMatches();
                    Toast($"{matched} material{(matched == 1 ? " was" : "s were")} matched from your material library (Esc → MATERIALS)", 5f);
                }
            }
        }

        /// <summary>The matches applied to the model's own materials, then shown (unless showing the IFC's colours).</summary>
        private void ApplyMatches()
        {
            _matchApplied = MaterialMatcher.Apply(_matchOriginal, _matchDoc, _matchImages, Scene);
            _materialsRevision++;
            _matchRevision++;
            ShowMatchState();
            UpdateTitle();
        }

        /// <summary>The renderer draws the matched look, or the IFC's own colours (SHOWING: …).</summary>
        private void ShowMatchState()
        {
            _materialData = _matchShowOriginal ? _matchOriginal : _matchApplied ?? _matchOriginal;
            ReloadRendererMaterials();
        }

        /// <summary>What Save writes as the materials: the matches applied (whatever is shown), else the TEXTURES panel's set.</summary>
        private MaterialData MaterialsForSave() =>
            _matchAvailable && _matchApplied != null ? _matchApplied : MaterialsChanged ? CurrentMaterials : null;

        /// <summary>The matches Save writes (with the images they use), or null when there are none.</summary>
        private MaterialMatchDocument MatchesForSave() => _matchAvailable && _matchDoc != null && !_matchDoc.IsEmpty ? _matchDoc : null;

        private Dictionary<string, byte[]> MatchImagesForSave()
        {
            if (MatchesForSave() == null) { return null; }
            var used = _matchDoc.Images();
            return _matchImages.Where(p => used.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        }

        /// <summary>The per-vertex indices before overrides, when overrides changed them (else null).</summary>
        private ushort[] MatchBaseVerticesForSave() =>
            MatchesForSave() != null && _matchApplied != null && !ReferenceEquals(_matchApplied.VertexMaterial, _matchOriginal.VertexMaterial)
                ? _matchOriginal.VertexMaterial
                : null;

        private bool HasMatchImage(string name) => name != null && (_matchImages.ContainsKey(name) || _matchOriginal.Textures.ContainsKey(name));

        /// <summary>The material library (read once; an unreadable one starts empty and says so).</summary>
        private MaterialLibrary MatchLibrary()
        {
            if (_library != null) { return _library; }
            _library = MaterialLibrary.Read(LibraryPath, out string error);
            if (_library == null)
            {
                _library = new MaterialLibrary();
                _materialsNotice = $"Your material library couldn't be read ({error}); a new one starts empty.";
            }
            return _library;
        }

        private void SaveLibrary()
        {
            if (!MatchLibrary().Write(LibraryPath, out string error)) { _materialsNotice = $"The material library couldn't be saved: {error}"; }
        }

        #endregion

        #region Open / close

        /// <summary>"MATERIALS (n TO MATCH)" (rebuilt only when the matches change).</summary>
        private string MaterialsMenuLabel()
        {
            if (_materialsMenuLabel == null || _materialsMenuLabelFor != _matchRevision)
            {
                _materialsMenuLabelFor = _matchRevision;
                int open = _matchOriginal.Materials.Count(m => _matchDoc.ForMaterial(m.Name) == null);
                _materialsMenuLabel = open == 0 ? "MATERIALS" : $"MATERIALS ({open} TO MATCH)";
            }
            return _materialsMenuLabel;
        }

        private void OpenMaterials()
        {
            _materialsOpen = true;
            ShowTip("materials"); // tutorial round: the first time
            _matchPicker = PICKER_EDITOR;
            _materialsNotice = null;
            _matchSearchPause = 2;
            _matchListScroll.Reset();
            _matchEditScroll.Reset();
            _matchRowsFor = (-1, 0, null);
            _renderer.KeepDecodedMaterials = true; // each change rebuilds the textures: decode each image once
            MatchLibrary();
        }

        /// <summary>One level back: a picker goes back to the material, then the panel closes.</summary>
        private bool CloseMaterials()
        {
            if (!_materialsOpen) { return false; }
            CommitMatchSlider(force: true);
            if (_matchPicker != PICKER_EDITOR)
            {
                _matchPicker = PICKER_EDITOR;
                return true;
            }
            _materialsOpen = false;
            ReleaseMatchPreviews();
            _renderer.KeepDecodedMaterials = false;
            return true;
        }

        private void ReleaseMatchPreviews()
        {
            foreach (uint texture in _matchPreviews.Values) { Gl.DeleteTexture(texture); }
            _matchPreviews.Clear();
            foreach (uint texture in _adskTextures.Values) { Gl.DeleteTexture(texture); }
            _adskTextures.Clear();
            _adskThumbs?.Dispose();
            _adskThumbs = null;
        }

        #endregion

        #region Rows

        private static string MaterialKey(string name) => new MaterialMatch { Material = name }.Key;

        /// <summary>The list: the model's materials (by name), then the overrides, as filtered and searched.</summary>
        private void RefreshMatchRows()
        {
            var stamp = (_matchRevision, _matchFilter, _matchSearchText);
            if (stamp == _matchRowsFor) { return; }
            _matchRowsFor = stamp;
            _matchRows.Clear();
            SceneMaterial[] table = _matchOriginal.Materials;
            _matchMatchedCount = table.Count(m => _matchDoc.ForMaterial(m.Name) != null);
            var order = Enumerable.Range(0, table.Length).OrderBy(i => table[i].Name, StringComparer.CurrentCultureIgnoreCase);
            foreach (int i in order)
            {
                SceneMaterial material = table[i];
                MaterialMatch match = _matchDoc.ForMaterial(material.Name);
                if (_matchFilter == 1 && match != null || _matchFilter == 2 && match == null || _matchFilter == 3) { continue; }
                if (_matchSearchText.Length > 0 && !material.Name.Contains(_matchSearchText, StringComparison.OrdinalIgnoreCase)) { continue; }
                _matchRows.Add(new MatchRow
                {
                    Key = MaterialKey(material.Name),
                    Material = i,
                    Label = material.Name,
                    Detail = match != null ? MatchDetail(match) : SuggestionDetail(material),
                    Colour = Rgba.FromFloat(material.Colour.X, material.Colour.Y, material.Colour.Z, 1f),
                    Matched = match != null
                });
            }
            if (_matchFilter is 0 or 3)
            {
                foreach (MaterialMatch match in _matchDoc.Matches.Where(m => m.IsOverride))
                {
                    string label = OverrideLabel(match);
                    if (_matchSearchText.Length > 0 && !label.Contains(_matchSearchText, StringComparison.OrdinalIgnoreCase)) { continue; }
                    MaterialMatch.TryParseColour(match.Colour, out Vector3 colour);
                    _matchRows.Add(new MatchRow
                    {
                        Key = match.Key, Label = label, Detail = MatchDetail(match), Matched = true, Override = true,
                        Colour = match.Colour != null ? Rgba.FromFloat(colour.X, colour.Y, colour.Z, 1f) : UiTheme.TEXT_MUTED
                    });
                }
            }
        }

        private static string OverrideLabel(MaterialMatch match) =>
            match.Element != null ? $"Element: {match.Label ?? match.Element}" : $"All {MaterialMatcher.CategoryLabel(match.Category).ToLowerInvariant()}";

        private static string MatchDetail(MaterialMatch match)
        {
            var parts = new List<string> { match.ImageName ?? (match.Image != null ? "an image" : match.Colour != null ? "colour " + match.Colour : "IFC colour") };
            if (match.Image != null) { parts.Add($"{match.SizeU:0.##} × {match.SizeV:0.##} m"); }
            if (match.Bump != null || match.BumpFromImage) { parts.Add("bump"); }
            if (match.Cutout != null || match.CutoutFromImage) { parts.Add("cutout"); }
            if (match.Water) { parts.Add("water"); }
            if (match.Grass) { parts.Add("grass"); }
            if (match.Metallic) { parts.Add("metal"); }
            else if (match.Shine > 0f) { parts.Add("shiny"); }
            return string.Join(" · ", parts);
        }

        private static string SuggestionDetail(SceneMaterial material)
        {
            string keyword = ProxyCatalog.Suggest(material.Name, null);
            ProxyKeyword entry = keyword != null && ProxyPack.Shared.Has(keyword) ? ProxyCatalog.Find(keyword) : null;
            return entry != null ? $"not matched · looks like {entry.Label.ToLowerInvariant()}" : "not matched";
        }

        /// <summary>The selected row's match to edit: its own (a copy), or a new one for an unmatched material.</summary>
        private MaterialMatch SelectedMatch(MatchRow row)
        {
            MaterialMatch match = _matchDoc.ForKey(row.Key);
            if (match != null) { return match.Copy(); }
            return row.Material >= 0 ? new MaterialMatch { Material = _matchOriginal.Materials[row.Material].Name } : null;
        }

        #endregion

        #region Panel

        /// <summary>Draws and handles the MATERIALS panel (in place of the pause menu).</summary>
        private void BuildMaterialsPanel()
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            if (!input.LeftDown)
            {
                CommitMatchSlider(force: false);
                _activeSlider = -1;
            }
            if (_matchSearchPause > 0) { _matchSearchPause--; }
            else if (_matchPicker == PICKER_EDITOR) { TypeInto(input, _matchSearch, ref _matchSearchLength, ref _matchSearchText); }
            else if (_matchPicker == PICKER_AUTODESK) { TypeInto(input, _adskSearch, ref _adskSearchLength, ref _adskSearchText); }
            RefreshMatchRows();

            int total = _matchOriginal.Materials.Length, matched = _matchMatchedCount;
            Text.Clear().Append(total).Append(" materials from the IFC · ").Append(matched).Append(" matched · your library: ").Append(MatchLibrary().Matches.Count);
            string showing = _matchShowOriginal ? "SHOWING: IFC COLOURS" : "SHOWING: MATCHED";
            const string PICK = "PICK ON MODEL";
            PanelFrame frame = BeginPanel(PanelSize.Wide, "MATERIALS", UiTheme.ACCENT, Text.Span,
                headerControls: ShellButtonWidth(f, PICK) + ShellButtonWidth(f, "SHOWING: IFC COLOURS") + S(UiTheme.GAP_SMALL));
            if (HeaderButton(ref frame, showing, _matchShowOriginal ? ShellStyle.Primary : ShellStyle.Normal, width: ShellButtonWidth(f, "SHOWING: IFC COLOURS")))
            {
                CommitMatchSlider(force: true);
                _matchShowOriginal = !_matchShowOriginal;
                ShowMatchState();
            }
            if (HeaderButton(ref frame, PICK))
            {
                StartMaterialPick();
                return;
            }

            float y = frame.BodyTop;
            if (!_realistic || _whitecard)
            {
                // Images, bump and cutouts are the Realistic colour mode's
                float bh = S(UiTheme.SMALL_BUTTON_HEIGHT);
                _ui.TextWrapped(f.Body, frame.Ix, y + S(6), frame.Iw - S(180), "Images, bump and cutouts show in the Realistic colour mode.", UiTheme.MEASURE_TEXT, maxLines: 1);
                if (MenuButton(f, frame.Ix + frame.Iw - S(170), y, S(170), "TURN IT ON", primary: true, danger: false, height: bh, compact: true))
                {
                    _realistic = true;
                    _whitecard = false;
                }
                y += bh + S(UiTheme.GAP_SMALL);
            }

            float gap = S(UiTheme.GAP_LARGE);
            float listW = MathF.Min(S(420), frame.Iw * 0.4f);
            BuildMatchList(f, input, frame.Ix, y, listW, frame.BodyBottom);
            float rx = frame.Ix + listW + gap, rw = frame.Iw - listW - gap;
            if (_matchPicker == PICKER_BUILT_IN) { BuildBuiltInPicker(f, input, rx, y, rw, frame.BodyBottom); }
            else if (_matchPicker == PICKER_AUTODESK) { BuildAutodeskBrowser(f, input, rx, y, rw, frame.BodyBottom); }
            else { BuildMatchEditor(f, input, rx, y, rw, frame.BodyBottom); }

            int suggest = FooterAction("USE SUGGESTIONS");
            int apply = FooterAction("APPLY LIBRARY", enabled: MatchLibrary().Matches.Count > 0);
            int saveAll = FooterAction("SAVE ALL TO LIBRARY", enabled: matched > 0);
            int import = FooterAction("IMPORT LIBRARY…");
            int export = FooterAction("EXPORT LIBRARY…", enabled: MatchLibrary().Matches.Count > 0);
            int pressed = EndPanel(frame, _materialsNotice, "Pick a material, then a look for it · PICK ON MODEL finds a surface's material · saved with the file");
            if (pressed == FOOTER_LEFT) { return; }
            if (pressed == suggest) { UseMatchSuggestions(); }
            else if (pressed == apply) { ApplyLibraryToModel(); }
            else if (pressed == saveAll) { SaveAllToLibrary(); }
            else if (pressed == import) { ImportLibrary(); }
            else if (pressed == export) { ExportLibrary(); }
        }

        /// <summary>The left side: search, filter and the rows (a colour or image swatch, the name, what it's matched to).</summary>
        private void BuildMatchList(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            float boxH = SearchField(f, input, x, y, w, _matchSearch, ref _matchSearchLength, ref _matchSearchText, "Search the materials", _matchPicker == PICKER_EDITOR);
            y += boxH + S(UiTheme.GAP_SMALL);
            int filter = Segmented(f, input, x, y, w, MATCH_FILTERS, _matchFilter);
            if (filter != _matchFilter)
            {
                _matchFilter = filter;
                _matchListScroll.Reset();
            }
            y += S(32) + S(UiTheme.GAP_SMALL);

            float rowH = S(52);
            int n = _matchRows.Count;
            float top = BeginScroll(_matchListScroll, input, x, y, w, bottom - y, out float cw);
            if (_matchReveal)
            {
                _matchReveal = false;
                int at = _matchRows.FindIndex(r => r.Key == _matchSelected);
                if (at >= 0) { _matchListScroll.Reveal(at * rowH, at * rowH + rowH); }
            }
            VisibleRows(_matchListScroll, rowH, n, out int first, out int last);
            int uploads = MATCH_UPLOADS;
            for (int i = first; i < last; i++)
            {
                MatchRow row = _matchRows[i];
                float ry = top + i * rowH;
                bool selected = row.Key == _matchSelected;
                bool hover = Hover(input, x, ry, cw, rowH - S(4));
                _ui.Rect(x, ry, cw, rowH - S(4), selected ? UiTheme.Accent(0.14f) : hover ? UiTheme.HOVER : (i & 1) == 0 ? UiTheme.ROW_STRIPE : UiTheme.CONTROL);
                if (selected) { _ui.Rect(x, ry, S(3), rowH - S(4), UiTheme.ACCENT); }

                // Swatch: the matched image, else the colour
                float sw = rowH - S(14);
                MaterialMatch match = row.Matched ? _matchDoc.ForKey(row.Key) : null;
                uint preview = match?.Image != null ? MatchPreview(match.Image, ref uploads) : 0;
                _ui.Rect(x + S(10), ry + S(5), sw, sw, row.Colour);
                if (preview != 0) { _ui.Image(preview, x + S(10), ry + S(5), sw, sw, _window.Width, _window.Height); }
                _ui.Outline(x + S(10), ry + S(5), sw, sw, MathF.Max(1f, UiScale), UiTheme.CONTROL_BORDER);

                float tx = x + S(20) + sw, tw = cw - sw - S(30);
                _ui.TextWrapped(f.Body, tx, ry + S(6), tw, row.Label, selected ? UiTheme.ACCENT : UiTheme.TEXT, maxLines: 1);
                _ui.TextWrapped(f.Small, tx, ry + S(26), tw, row.Detail, row.Matched ? UiTheme.BOOKMARK_LABEL : UiTheme.TEXT_FAINT, maxLines: 1);
                if (hover && input.LeftPressed)
                {
                    input.ConsumeClicks();
                    CommitMatchSlider(force: true);
                    _matchSelected = row.Key;
                    _matchPicker = PICKER_EDITOR;
                    _matchEditScroll.Reset();
                    Sound.Play(SoundId.UiClick);
                }
            }
            if (n == 0) { _ui.TextWrapped(f.Body, x + S(8), top + S(8), cw - S(16), _matchSearchText.Length > 0 ? "No material matches the search." : "Nothing here.", UiTheme.TEXT_MUTED, maxLines: 2); }
            EndScroll(_matchListScroll, input, top + n * rowH);
        }

        /// <summary>
        /// The right side: the selected material's (or override's) look: the image and where it came from, colour,
        /// size, angle, shine, polish, metal, water, grass, bump and cutout; save to the library, remove; and, after a
        /// pick on the model, overrides for that element or its category.
        /// </summary>
        private void BuildMatchEditor(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            MatchRow row = _matchRows.Find(r => r.Key == _matchSelected) ?? MatchRowFor(_matchSelected);
            if (row == null)
            {
                _ui.TextWrapped(f.Body, x, y + S(8), w, "Pick a material on the left (or PICK ON MODEL), then choose how it should look: one of BimGo's textures, one from the Autodesk library on this PC, or an image of your own.",
                    UiTheme.TEXT_MUTED, maxLines: 4);
                return;
            }
            MaterialMatch match = _matchPending != null && _matchPending.Key == row.Key ? _matchPending : SelectedMatch(row);
            if (match == null) { return; }
            bool exists = _matchDoc.ForKey(row.Key) != null;

            float top = BeginScroll(_matchEditScroll, input, x, y, w, bottom - y, out float cw);
            float cy = top;
            float bh = S(UiTheme.SMALL_BUTTON_HEIGHT), gap = S(UiTheme.GAP_SMALL);

            // Title
            _ui.TextWrapped(f.Bold, x, cy, cw, row.Label, UiTheme.TEXT, maxLines: 1);
            cy += S(24);
            if (row.Material >= 0)
            {
                SceneMaterial own = _matchOriginal.Materials[row.Material];
                Text.Clear().Append("From the IFC: ").Append(MaterialMatch.FormatColour(own.Colour)).Append(own.SeeThrough ? " · see-through" : string.Empty);
                _ui.Text(f.Small, x, cy, Text.Span, UiTheme.TEXT_MUTED);
            }
            else { _ui.Text(f.Small, x, cy, match.Element != null ? "Overrides this element only" : "Overrides every element of this category", UiTheme.TEXT_MUTED); }
            cy += S(26);

            // The image
            float pv = S(120);
            int uploads = MATCH_UPLOADS;
            uint preview = match.Image != null ? MatchPreview(match.Image, ref uploads) : 0;
            MaterialMatch.TryParseColour(match.Colour, out Vector3 colour);
            Vector3 shown = match.Colour != null ? colour : row.Material >= 0 ? _matchOriginal.Materials[row.Material].Colour : new Vector3(0.7f);
            _ui.Rect(x, cy, pv, pv, Rgba.FromFloat(shown.X, shown.Y, shown.Z, 1f));
            if (preview != 0) { _ui.Image(preview, x, cy, pv, pv, _window.Width, _window.Height); }
            _ui.Outline(x, cy, pv, pv, MathF.Max(1f, UiScale), UiTheme.CONTROL_BORDER);
            float bx = x + pv + S(UiTheme.GAP), bw = MathF.Min(S(260), cw - pv - S(UiTheme.GAP));
            float by = cy;
            if (MenuButton(f, bx, by, bw, "BIMGO TEXTURES…", false, false, height: bh, compact: true)) { OpenMatchPicker(PICKER_BUILT_IN); }
            by += bh + gap;
            if (MenuButton(f, bx, by, bw, "AUTODESK LIBRARY…", false, false, height: bh, compact: true)) { OpenMatchPicker(PICKER_AUTODESK); }
            by += bh + gap;
            if (MenuButton(f, bx, by, bw, "YOUR IMAGE…", false, false, height: bh, compact: true))
            {
                string picked = PickMatchImage(png: false, out string name);
                if (picked != null) { SetMatchImage(match, picked, name, 1f, 1f); }
                EndScroll(_matchEditScroll, input, cy);
                return;
            }
            by += bh + gap;
            if (match.Image != null && MenuButton(f, bx, by, bw, "NO IMAGE (COLOUR ONLY)", false, false, height: bh, compact: true))
            {
                match.Image = match.ImageName = null;
                match.BumpFromImage = match.CutoutFromImage = false;
                CommitMatch(match);
            }
            cy += pv + S(6);
            _ui.TextWrapped(f.Small, x, cy, cw, match.ImageName ?? (match.Image != null ? "An image" : "No image: a plain colour"), UiTheme.TEXT_FAINT, maxLines: 1);
            cy += S(24);

            // Colour
            _ui.Rect(x, cy, S(30), S(30), Rgba.FromFloat(shown.X, shown.Y, shown.Z, 1f));
            _ui.Outline(x, cy, S(30), S(30), MathF.Max(1f, UiScale), UiTheme.CONTROL_BORDER);
            if (MenuButton(f, x + S(40), cy, S(130), "COLOUR…", false, false, height: S(30), compact: true))
            {
                uint rgb = ((uint)(shown.X * 255f + 0.5f) << 16) | ((uint)(shown.Y * 255f + 0.5f) << 8) | (uint)(shown.Z * 255f + 0.5f);
                uint? chosen = FileDialogs.PickColour(_window.Handle, rgb);
                _window.Input.ReleaseAll();
                if (chosen is uint c)
                {
                    match.Colour = MaterialMatch.FormatColour(new Vector3(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f));
                    CommitMatch(match);
                }
                EndScroll(_matchEditScroll, input, cy);
                return;
            }
            if (match.Colour != null && row.Material >= 0 && MenuButton(f, x + S(180), cy, S(150), "IFC COLOUR", false, false, height: S(30), compact: true))
            {
                match.Colour = null;
                CommitMatch(match);
            }
            _ui.Text(f.Small, x + S(340), cy + S(8), match.Image != null ? "(under the image where it's see-through)" : string.Empty, UiTheme.TEXT_FAINT);
            cy += S(44);

            // Size and angle (the image's), shine and polish
            float half = (cw - S(UiTheme.GAP_LARGE)) * 0.5f;
            if (match.Image != null)
            {
                MatchSlider(f, input, 0, x, cy, half, "Image width", match.SizeU, 0.05f, 6f, 0.05f, " m", match, (m, v) => m.SizeU = v);
                MatchSlider(f, input, 1, x + half + S(UiTheme.GAP_LARGE), cy, half, "Image height", match.SizeV, 0.05f, 6f, 0.05f, " m", match, (m, v) => m.SizeV = v);
                cy += S(52);
                MatchSlider(f, input, 2, x, cy, half, "Turn", ((match.Angle % 360f) + 360f) % 360f, 0f, 345f, 15f, "°", match, (m, v) => m.Angle = v);
                cy += S(52);
            }
            MatchSlider(f, input, 3, x, cy, half, "Shine", match.Shine, 0f, 1f, 0.05f, null, match, (m, v) => m.Shine = v);
            MatchSlider(f, input, 4, x + half + S(UiTheme.GAP_LARGE), cy, half, "Polish (sharp reflection)", 1f - match.Roughness, 0f, 1f, 0.05f, null, match, (m, v) => m.Roughness = 1f - v);
            cy += S(52);
            float third = cw / 3f;
            bool metal = Checkbox(f, input, x, cy, third, "Metal", match.Metallic);
            bool water = Checkbox(f, input, x + third, cy, third, "Water", match.Water);
            bool grass = Checkbox(f, input, x + third * 2f, cy, third, "Grass blades", match.Grass);
            if (metal != match.Metallic || water != match.Water || grass != match.Grass)
            {
                match.Metallic = metal;
                match.Water = water;
                match.Grass = grass;
                CommitMatch(match);
            }
            cy += S(34);

            // Bump
            _ui.Text(f.Small, x, cy, "BUMP (RELIEF)", UiTheme.TEXT_MUTED, S(1f));
            cy += S(20);
            int bump = match.Bump != null ? 2 : match.BumpFromImage ? 1 : 0;
            int bumpPicked = Segmented(f, input, x, cy, cw, BUMP_OPTIONS, bump);
            if (bumpPicked != bump)
            {
                if (bumpPicked == 0) { match.Bump = null; match.BumpFromImage = false; CommitMatch(match); }
                else if (bumpPicked == 1)
                {
                    if (match.Image == null) { _materialsNotice = "Pick an image first: its lightness becomes the bump."; }
                    else { match.Bump = null; match.BumpFromImage = true; CommitMatch(match); }
                }
                else
                {
                    string picked = PickMatchImage(png: false, out _);
                    if (picked != null) { match.Bump = picked; match.BumpFromImage = false; CommitMatch(match); }
                    EndScroll(_matchEditScroll, input, cy);
                    return;
                }
            }
            cy += S(40);
            if (bump != 0)
            {
                MatchSlider(f, input, 5, x, cy, half, "Bump strength", match.BumpStrength, 0.1f, 2f, 0.05f, null, match, (m, v) => m.BumpStrength = v);
                if (match.Bump != null && MenuButton(f, x + half + S(UiTheme.GAP_LARGE), cy + S(14), half, "ANOTHER BUMP IMAGE…", false, false, height: bh, compact: true))
                {
                    string picked = PickMatchImage(png: false, out _);
                    if (picked != null) { match.Bump = picked; CommitMatch(match); }
                    EndScroll(_matchEditScroll, input, cy);
                    return;
                }
                cy += S(52);
            }

            // Cutout
            _ui.Text(f.Small, x, cy, "CUTOUT (HOLES)", UiTheme.TEXT_MUTED, S(1f));
            cy += S(20);
            int cut = match.Cutout != null ? 2 : match.CutoutFromImage ? 1 : 0;
            int cutPicked = Segmented(f, input, x, cy, cw, CUTOUT_OPTIONS, cut);
            if (cutPicked != cut)
            {
                if (cutPicked == 0) { match.Cutout = null; match.CutoutFromImage = false; CommitMatch(match); }
                else if (cutPicked == 1)
                {
                    if (match.Image == null) { _materialsNotice = "Pick an image with transparency (a PNG) first: its see-through parts become holes."; }
                    else { match.Cutout = null; match.CutoutFromImage = true; CommitMatch(match); }
                }
                else
                {
                    string picked = PickMatchImage(png: true, out _);
                    if (picked != null) { match.Cutout = picked; match.CutoutFromImage = false; CommitMatch(match); }
                    EndScroll(_matchEditScroll, input, cy);
                    return;
                }
            }
            cy += S(40);
            if (cut != 0)
            {
                MatchSlider(f, input, 6, x, cy, half, "Cut below", match.CutoutThreshold, 0.05f, 0.95f, 0.05f, null, match, (m, v) => m.CutoutThreshold = v);
                _ui.TextWrapped(f.Small, x + half + S(UiTheme.GAP_LARGE), cy + S(6), half, cut == 2 ? "Black (or see-through) parts of the mask are holes." : "See-through parts of the image are holes.", UiTheme.TEXT_FAINT, maxLines: 2);
                cy += S(52);
            }

            // Library and removal
            float aw = (cw - gap) * 0.5f;
            if (row.Material >= 0 && MenuButton(f, x, cy, aw, "SAVE TO MY LIBRARY", false, false, enabled: exists, height: bh, compact: true))
            {
                MatchLibrary().Put(_matchDoc.ForKey(row.Key), _matchImages);
                SaveLibrary();
                _materialsNotice ??= $"“{row.Label}” is in your material library: models with a material of that name start matched";
                Sound.Play(SoundId.Commit);
            }
            if (MenuButton(f, x + aw + gap, cy, aw, row.Override ? "REMOVE OVERRIDE" : "REMOVE MATCH", false, danger: true, enabled: exists, height: bh, compact: true))
            {
                _matchDoc.Remove(row.Key);
                if (row.Override) { _matchSelected = null; }
                ApplyMatches();
                _materialsNotice = $"“{row.Label}” is back as the IFC has it";
                Sound.Play(SoundId.Remove);
                EndScroll(_matchEditScroll, input, cy + bh);
                return;
            }
            cy += bh + S(UiTheme.GAP);

            // After a pick on the model: this element only, or its whole category
            if (row.Material >= 0 && _matchPickedElement >= 0 && _matchPickedElement < Scene.Elements.Length)
            {
                ElementRecord element = Scene.Elements[_matchPickedElement];
                Text.Clear().Append("Picked on: ").Append(element.Name).Append(" (").Append(element.CategoryName).Append(')');
                _ui.TextWrapped(f.Small, x, cy, cw, Text.Span, UiTheme.TEXT_MUTED, maxLines: 1);
                cy += S(20);
                if (MenuButton(f, x, cy, aw, "OVERRIDE THIS ELEMENT", false, false, enabled: !string.IsNullOrEmpty(element.UniqueId), height: bh, compact: true)) { CreateOverride(match, element, oneElement: true); }
                if (MenuButton(f, x + aw + gap, cy, aw, "OVERRIDE ITS CATEGORY", false, false, height: bh, compact: true)) { CreateOverride(match, element, oneElement: false); }
                cy += bh + S(UiTheme.GAP);
            }
            EndScroll(_matchEditScroll, input, cy);
        }

        /// <summary>A row for a key that the filter or search hides (the editor still shows what was selected).</summary>
        private MatchRow MatchRowFor(string key)
        {
            if (key == null) { return null; }
            MaterialMatch match = _matchDoc.ForKey(key);
            if (match?.IsOverride == true) { return new MatchRow { Key = key, Label = OverrideLabel(match), Matched = true, Override = true }; }
            for (int i = 0; i < _matchOriginal.Materials.Length; i++)
            {
                if (MaterialKey(_matchOriginal.Materials[i].Name) == key) { return new MatchRow { Key = key, Material = i, Label = _matchOriginal.Materials[i].Name, Matched = match != null }; }
            }
            return null;
        }

        /// <summary>
        /// A slider on the match: dragging previews it live (the material table only); letting go applies it
        /// (<see cref="CommitMatchSlider"/>).
        /// </summary>
        private void MatchSlider(FontAtlas f, InputState input, int slot, float x, float y, float w, string label, float value, float min, float max, float step, string unit,
            MaterialMatch match, Action<MaterialMatch, float> set)
        {
            int id = MATCH_SLIDER + slot;
            TextBuffer text = Text.Clear();
            if (unit == "°") { text.Append(value, 0).Append('°'); }
            else if (unit != null) { text.Append(value, 2).Append(unit); }
            else { text.Append(value * 100f, 0).Append(" %"); }
            float moved = Slider(f, input, id, x, y, w, label, text.Span, value, min, max);
            if (_activeSlider != id) { return; }
            float stepped = Math.Clamp(MathF.Round(moved / step) * step, min, max);
            if (MathF.Abs(stepped - value) < step * 0.25f) { return; }
            MaterialMatch pending = match.Copy();
            set(pending, stepped);
            _matchPending = pending;
            PreviewMatch(pending);
        }

        /// <summary>A slider's change shown at once: the material's table entry only (no images decoded).</summary>
        private void PreviewMatch(MaterialMatch match)
        {
            if (_matchShowOriginal || _matchApplied == null || match.Material == null || !ReferenceEquals(_materialData, _matchApplied)) { return; }
            SceneMaterial[] table = _matchApplied.Materials;
            for (int i = 0; i < _matchOriginal.Materials.Length && i < table.Length; i++)
            {
                if (!string.Equals(_matchOriginal.Materials[i].Name, match.Material, StringComparison.OrdinalIgnoreCase)) { continue; }
                if (match.Image != null && table[i].Texture != match.Image) { return; } // not applied yet: wait for the release
                MaterialMatcher.ApplyLook(table[i], match.Clean(), HasMatchImage);
                _renderer.RefreshMaterialTable();
                return;
            }
        }

        /// <summary>A slider let go (or the panel left): its value applied for good.</summary>
        private void CommitMatchSlider(bool force)
        {
            if (_matchPending == null) { return; }
            if (!force && _activeSlider >= MATCH_SLIDER && _activeSlider < MATCH_SLIDER + 10 && _window.Input.LeftDown) { return; }
            MaterialMatch pending = _matchPending;
            _matchPending = null;
            CommitMatch(pending);
        }

        /// <summary>A change applied: the match stored (made when new) and the materials applied again.</summary>
        private void CommitMatch(MaterialMatch match)
        {
            _matchDoc.Set(match);
            ApplyMatches();
            _materialsNotice = null;
        }

        /// <summary>An image (an entry already in the match images) as the match's colour image, at a real-world size.</summary>
        private void SetMatchImage(MaterialMatch match, string entry, string name, float sizeU, float sizeV)
        {
            match.Image = entry;
            match.ImageName = name;
            match.SizeU = sizeU;
            match.SizeV = sizeV;
            match.Angle = 0f;
            CommitMatch(match);
            Sound.Play(SoundId.Commit);
        }

        /// <summary>Picks an image file and adds it to the match images.</summary>
        /// <returns>Its entry name, or null.</returns>
        private string PickMatchImage(bool png, out string name)
        {
            name = null;
            _window.SetCaptured(false);
            string path = FileDialogs.ShowOpen(_window.Handle, "Choose an image", MaterialImages.FILTER, null);
            _window.Input.ReleaseAll();
            if (path == null) { return null; }
            (string Entry, byte[] Bytes)? encoded = MaterialImages.EncodeFile(path, MaterialData.IFC_TEXTURE_SIZE, png, out string error);
            if (encoded == null)
            {
                _materialsNotice = $"{Path.GetFileName(path)} couldn't be read: {error}";
                Sound.Play(SoundId.Error);
                return null;
            }
            _matchImages[encoded.Value.Entry] = encoded.Value.Bytes;
            name = Path.GetFileName(path);
            return encoded.Value.Entry;
        }

        /// <summary>A preview texture for a match image (uploaded on first use, within the frame's budget), or 0.</summary>
        private uint MatchPreview(string entry, ref int budget)
        {
            if (_matchPreviews.TryGetValue(entry, out uint texture)) { return texture; }
            if (budget <= 0 || !_matchImages.TryGetValue(entry, out byte[] bytes) && !_matchOriginal.Textures.TryGetValue(entry, out bytes)) { return 0; }
            budget--;
            texture = UploadJpegTexture(bytes, mipmaps: true);
            _matchPreviews[entry] = texture;
            return texture;
        }

        /// <summary>A search box (typed into while its view is up; × clears).</summary>
        private float SearchField(FontAtlas f, InputState input, float x, float y, float w, char[] buffer, ref int length, ref string text, string placeholder, bool active)
        {
            float h = S(34);
            _ui.Panel(x, y, w, h, UiTheme.CONTROL, length > 0 ? UiTheme.ACCENT : UiTheme.CONTROL_BORDER);
            float textY = y + h * 0.5f - f.Body.LineHeight * 0.5f;
            if (length == 0) { _ui.TextWrapped(f.Body, x + S(10), textY, w - S(20), placeholder, UiTheme.TEXT_FAINT, maxLines: 1); }
            else
            {
                _ui.PushClip(x + S(4), y, w - S(40), h);
                float used = _ui.Text(f.Body, x + S(10), textY, buffer.AsSpan(0, length), UiTheme.TEXT);
                if (active && ((int)(_clock * 2f) & 1) == 0) { _ui.Rect(x + S(11) + used, y + S(8), S(2), h - S(16), UiTheme.TEXT_SOFT); }
                _ui.PopClip();
                bool hover = Hover(input, x + w - S(34), y, S(34), h);
                _ui.TextCentred(f.Bold, x + w - S(17), textY - S(1), "×", hover ? UiTheme.ACCENT : UiTheme.TEXT_MUTED);
                if (hover && input.LeftPressed)
                {
                    input.ConsumeClicks();
                    length = 0;
                    text = string.Empty;
                }
            }
            return h;
        }

        /// <summary>Typed characters into a search buffer (Backspace deletes, Ctrl+Backspace clears).</summary>
        private static void TypeInto(InputState input, char[] buffer, ref int length, ref string text)
        {
            int before = length;
            foreach (char c in input.Chars)
            {
                if (c == '\b')
                {
                    if (input.IsDown(KeyCodes.CTRL)) { length = 0; }
                    else if (length > 0) { length--; }
                }
                else if (c >= ' ' && c != 127 && length < buffer.Length) { buffer[length++] = c; }
            }
            if (length != before || input.Chars.Length > 0) { text = new string(buffer, 0, length).Trim(); }
        }

        #endregion

        #region Pickers

        private void OpenMatchPicker(int picker)
        {
            CommitMatchSlider(force: true);
            _matchPicker = picker;
            _matchPickerScroll.Reset();
            _matchSearchPause = 2;
            if (picker == PICKER_AUTODESK && _adskRoots == null)
            {
                _adskRoots = MaterialImages.AutodeskRoots();
                List<string> roots = _adskRoots;
                if (roots.Count > 0) { _adskTask = Task.Run(() => MaterialImages.ListImages(roots)); }
            }
        }

        /// <summary>BimGo's built-in textures as tiles: a click puts one on the material, at its real-world size.</summary>
        private void BuildBuiltInPicker(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            MatchRow row = MatchRowFor(_matchSelected);
            MaterialMatch match = row != null ? SelectedMatch(row) : null;
            float bh = S(UiTheme.SMALL_BUTTON_HEIGHT);
            if (MenuButton(f, x, y, S(220), "‹ BACK TO THE MATERIAL", false, false, height: bh, compact: true) || match == null)
            {
                _matchPicker = PICKER_EDITOR;
                return;
            }
            _ui.Text(f.Bold, x + S(236), y + S(5), "BIMGO TEXTURES", UiTheme.TEXT);
            y += bh + S(UiTheme.GAP);

            ProxyPack pack = ProxyPack.Shared;
            _builtInTextures ??= ProxyCatalog.ALL.Where(e => pack.Has(e.Keyword)).ToList();
            List<ProxyKeyword> entries = _builtInTextures;
            float gap = S(UiTheme.GAP), tile = S(150);
            float top = BeginScroll(_matchPickerScroll, input, x, y, w, bottom - y, out float cw);
            int columns = Math.Max(1, (int)((cw + gap) / (tile + gap)));
            tile = (cw - gap * (columns - 1)) / columns;
            float tileH = tile + S(40);
            int uploads = MATCH_UPLOADS;
            ProxyKeyword picked = null;
            for (int i = 0; i < entries.Count; i++)
            {
                ProxyKeyword entry = entries[i];
                float tx = x + (i % columns) * (tile + gap), ty = top + (i / columns) * (tileH + gap);
                bool hover = Hover(input, tx, ty, tile, tileH);
                _ui.Rect(tx, ty, tile, tileH, hover ? UiTheme.HOVER : UiTheme.CONTROL);
                _ui.Outline(tx, ty, tile, tileH, MathF.Max(1f, UiScale), hover ? UiTheme.ACCENT : UiTheme.CONTROL_BORDER);
                uint texture = BuiltInPreview(entry.Keyword, ref uploads);
                if (texture != 0) { _ui.Image(texture, tx + S(4), ty + S(4), tile - S(8), tile - S(8), _window.Width, _window.Height); }
                _ui.TextWrapped(f.Body, tx + S(8), ty + tile + S(2), tile - S(16), entry.Label, UiTheme.TEXT, maxLines: 1);
                (float su, float sv) = pack.SizeOf(entry.Keyword);
                Text.Clear().Append(su, 2).Append(" × ").Append(sv, 2).Append(" m");
                _ui.Text(f.Small, tx + S(8), ty + tile + S(20), Text.Span, UiTheme.TEXT_FAINT);
                if (hover && input.LeftPressed)
                {
                    input.ConsumeClicks();
                    picked = entry;
                }
            }
            EndScroll(_matchPickerScroll, input, top + ((entries.Count + columns - 1) / columns) * (tileH + gap));
            if (picked == null) { return; }
            byte[] bytes = pack.ImageBytes(picked.Keyword);
            if (bytes == null) { return; }
            string image = MaterialImages.EntryName(bytes, png: false);
            _matchImages[image] = bytes;
            (float u, float v) = pack.SizeOf(picked.Keyword);
            _matchPicker = PICKER_EDITOR;
            SetMatchImage(match, image, picked.Label + " (BimGo)", u, v);
        }

        private uint BuiltInPreview(string keyword, ref int budget)
        {
            string key = "builtin:" + keyword;
            if (_matchPreviews.TryGetValue(key, out uint texture)) { return texture; }
            if (budget <= 0) { return 0; }
            budget--;
            texture = UploadJpegTexture(ProxyPack.Shared.ImageBytes(keyword), mipmaps: true);
            _matchPreviews[key] = texture;
            return texture;
        }

        /// <summary>
        /// The Autodesk Material Library's images on this PC (installed with Revit and Revit LT): a search box and
        /// thumbnail tiles. A click copies the image into the file (with the library's own bump image beside it, when
        /// there is one).
        /// </summary>
        private void BuildAutodeskBrowser(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            MatchRow row = MatchRowFor(_matchSelected);
            MaterialMatch match = row != null ? SelectedMatch(row) : null;
            float bh = S(UiTheme.SMALL_BUTTON_HEIGHT);
            if (MenuButton(f, x, y, S(220), "‹ BACK TO THE MATERIAL", false, false, height: bh, compact: true) || match == null)
            {
                _matchPicker = PICKER_EDITOR;
                return;
            }
            _ui.Text(f.Bold, x + S(236), y + S(5), "AUTODESK MATERIAL LIBRARY", UiTheme.TEXT);
            y += bh + S(UiTheme.GAP);

            if (_adskRoots == null || _adskRoots.Count == 0)
            {
                _ui.TextWrapped(f.Body, x, y, w, "The Autodesk Material Library wasn't found on this PC (it installs with Revit and Revit LT, under Common Files\\Autodesk Shared\\Materials). Use BimGo's textures or your own images instead.",
                    UiTheme.TEXT_MUTED, maxLines: 4);
                return;
            }
            if (_adskFiles == null)
            {
                if (_adskTask != null && _adskTask.IsCompleted)
                {
                    _adskFiles = _adskTask.Status == TaskStatus.RanToCompletion ? _adskTask.Result : new List<string>();
                    _adskTask = null;
                }
                else
                {
                    _ui.Text(f.Body, x, y, "Looking through the library…", UiTheme.TEXT_MUTED);
                    return;
                }
            }

            float boxH = SearchField(f, input, x, y, MathF.Min(S(420), w), _adskSearch, ref _adskSearchLength, ref _adskSearchText, "Search: brick, oak, concrete…", true);
            if (_adskShownFor != _adskSearchText)
            {
                _adskShownFor = _adskSearchText;
                _adskShown.Clear();
                foreach (string file in _adskFiles)
                {
                    if (_adskSearchText.Length > 0 && !Path.GetFileName(file).Contains(_adskSearchText, StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (Path.GetFileNameWithoutExtension(file).EndsWith("bump", StringComparison.OrdinalIgnoreCase)) { continue; } // bump images come with their colour image
                    _adskShown.Add(file);
                }
                _matchPickerScroll.Reset();
            }
            Text.Clear().AppendGrouped(_adskShown.Count).Append(" of ").AppendGrouped(_adskFiles.Count).Append(" images");
            _ui.TextRight(f.Small, x + w, y + S(10), Text.Span, UiTheme.TEXT_FAINT);
            y += boxH + S(UiTheme.GAP_SMALL);

            _adskThumbs ??= new MaterialImages.Thumbnailer((int)S(128));
            for (int k = 0; k < 8 && _adskThumbs.TryTake(out string done, out byte[] jpeg); k++)
            {
                _adskTextures[done] = jpeg != null ? UploadJpegTexture(jpeg, mipmaps: false) : 0;
            }

            float gap = S(UiTheme.GAP_SMALL), tile = S(128);
            float top = BeginScroll(_matchPickerScroll, input, x, y, w, bottom - y, out float cw);
            int columns = Math.Max(1, (int)((cw + gap) / (tile + gap)));
            tile = (cw - gap * (columns - 1)) / columns;
            float tileH = tile + S(22);
            int rows = (_adskShown.Count + columns - 1) / columns;
            VisibleRows(_matchPickerScroll, tileH + gap, rows, out int firstRow, out int lastRow);
            string picked = null;
            for (int r = firstRow; r < lastRow; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    int i = r * columns + c;
                    if (i >= _adskShown.Count) { break; }
                    string file = _adskShown[i];
                    float tx = x + c * (tile + gap), ty = top + r * (tileH + gap);
                    bool hover = Hover(input, tx, ty, tile, tileH);
                    _ui.Rect(tx, ty, tile, tileH, hover ? UiTheme.HOVER : UiTheme.CONTROL);
                    if (_adskTextures.TryGetValue(file, out uint texture)) { if (texture != 0) { _ui.Image(texture, tx + S(3), ty + S(3), tile - S(6), tile - S(6), _window.Width, _window.Height); } }
                    else { _adskThumbs.Request(file); }
                    _ui.TextWrapped(f.Small, tx + S(4), ty + tile + S(2), tile - S(8), Path.GetFileNameWithoutExtension(file), hover ? UiTheme.ACCENT : UiTheme.TEXT_MUTED, maxLines: 1);
                    if (hover) { _ui.Outline(tx, ty, tile, tileH, MathF.Max(1f, UiScale), UiTheme.ACCENT); }
                    if (hover && input.LeftPressed)
                    {
                        input.ConsumeClicks();
                        picked = file;
                    }
                }
            }
            EndScroll(_matchPickerScroll, input, top + rows * (tileH + gap));
            if (picked == null) { return; }

            (string Entry, byte[] Bytes)? encoded = MaterialImages.EncodeFile(picked, MaterialData.IFC_TEXTURE_SIZE, png: false, out string error);
            if (encoded == null)
            {
                _materialsNotice = $"{Path.GetFileName(picked)} couldn't be read: {error}";
                Sound.Play(SoundId.Error);
                return;
            }
            _matchImages[encoded.Value.Entry] = encoded.Value.Bytes;
            string bumpFile = MaterialImages.AutodeskBumpFor(picked);
            if (bumpFile != null && MaterialImages.EncodeFile(bumpFile, MaterialData.IFC_TEXTURE_SIZE, png: false, out _) is { } bump)
            {
                _matchImages[bump.Entry] = bump.Bytes;
                match.Bump = bump.Entry;
                match.BumpFromImage = false;
                if (match.BumpStrength <= 0f) { match.BumpStrength = 1f; }
            }
            _matchPicker = PICKER_EDITOR;
            SetMatchImage(match, encoded.Value.Entry, "Autodesk: " + Path.GetFileNameWithoutExtension(picked), match.Image != null ? match.SizeU : 1f, match.Image != null ? match.SizeV : 1f);
            if (bumpFile != null) { _materialsNotice = "Its bump image came with it. Set the image's size to how big it is on the real surface."; }
        }

        #endregion

        #region Eyedropper and overrides

        /// <summary>PICK ON MODEL: back to the model; a click (the crosshair) picks a surface's material.</summary>
        private void StartMaterialPick()
        {
            CommitMatchSlider(force: true);
            _matchPicking = true;
            _materialsOpen = false;
            SetPaused(false);
            Toast("Aim at a surface and click to pick its material · Esc goes back to MATERIALS", 30f);
            Sound.Play(SoundId.UiClick);
        }

        /// <summary>Esc while picking: straight back to the panel.</summary>
        private void CancelMaterialPick()
        {
            _matchPicking = false;
            Toast(string.Empty, 0.01f);
            SetPaused(true);
            _materialsOpen = true;
        }

        /// <summary>The click while picking: the material under the crosshair, selected in the panel with its element.</summary>
        private void PickMaterialUnderCrosshair()
        {
            if (!Pick(Camera.Position, Camera.Forward, 5000f, out RayHit hit) || hit.Element < 0 || hit.Element >= Scene.Elements.Length)
            {
                Sound.Play(SoundId.Error);
                Toast("Nothing there: aim at a surface (Esc goes back)", 2f);
                return;
            }
            int material = MaterialAt(hit.Element, hit.Point, hit.DynamicId != 0);
            _matchPicking = false;
            _matchPickedElement = hit.Element;
            if (material >= 0)
            {
                _matchSelected = MaterialKey(_matchOriginal.Materials[material].Name);
                _matchFilter = 0;
                _matchSearchLength = 0;
                _matchSearchText = string.Empty;
                _matchReveal = true;
                _matchRowsFor = (-1, 0, null);
            }
            Toast(string.Empty, 0.01f);
            Sound.Play(SoundId.ScanLock);
            SetPaused(true);
            OpenMaterials();
            _matchPicker = PICKER_EDITOR;
            _materialsNotice = material >= 0 ? null : "That surface has no material: OVERRIDE ITS CATEGORY or THIS ELEMENT still work from a material's page.";
        }

        /// <summary>
        /// The model's own material at a point on an element: the triangle nearest the point (a moved copy: the
        /// element's most used material).
        /// </summary>
        private int MaterialAt(int elementIndex, Vector3 point, bool moved)
        {
            ElementRecord element = Scene.Elements[elementIndex];
            ushort[] vertexMaterial = _matchOriginal.VertexMaterial;
            uint[] indices = Scene.Indices;
            SceneVertex[] vertices = Scene.Vertices;
            float best = float.MaxValue;
            int found = -1;
            var counts = new Dictionary<int, int>();
            void Range(int start, int count)
            {
                int end = Math.Min(start + count, indices.Length);
                for (int i = Math.Max(0, start); i + 2 < end; i += 3)
                {
                    uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    if (a >= vertexMaterial.Length) { continue; }
                    int m = vertexMaterial[a];
                    if (m == MaterialData.NONE) { continue; }
                    counts[m] = counts.GetValueOrDefault(m) + 1;
                    if (moved) { continue; }
                    float d = Vector3.DistanceSquared(ClosestOnTriangle(point, vertices[a].Position, vertices[b].Position, vertices[c].Position), point);
                    if (d < best)
                    {
                        best = d;
                        found = m;
                    }
                }
            }
            Range(element.OpaqueStart, element.OpaqueCount);
            Range(element.TransparentStart, element.TransparentCount);
            if (found < 0 && counts.Count > 0) { found = counts.OrderByDescending(p => p.Value).First().Key; }
            return found < _matchOriginal.Materials.Length ? found : -1;
        }

        /// <summary>The point of a triangle nearest p (Ericson, Real-Time Collision Detection 5.1.5).</summary>
        private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { return a; }
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { return b; }
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) { return a + ab * (d1 / (d1 - d3)); }
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { return c; }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) { return a + ac * (d2 / (d2 - d6)); }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) { return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6))); }
            float denom = 1f / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }

        /// <summary>An override for the picked element, or every element of its category, starting from the material's look.</summary>
        private void CreateOverride(MaterialMatch from, ElementRecord element, bool oneElement)
        {
            MaterialMatch match = from.Copy();
            match.Material = null;
            if (oneElement)
            {
                match.Element = element.UniqueId;
                match.Label = element.Name;
            }
            else
            {
                match.Category = MaterialMatcher.CategoryKey(element.CategoryIndex);
                match.Label = null;
                if (match.Category == null) { return; }
            }
            _matchDoc.Set(match);
            ApplyMatches();
            _matchSelected = match.Clean().Key;
            _matchReveal = true;
            _matchEditScroll.Reset();
            _materialsNotice = oneElement ? $"“{element.Name}” has a look of its own now" : $"Every element of {element.CategoryName.ToLowerInvariant()} has this look now";
            Sound.Play(SoundId.Commit);
        }

        #endregion

        #region Suggestions and library

        /// <summary>USE SUGGESTIONS: every unmatched material whose name suggests one of BimGo's textures gets it.</summary>
        private void UseMatchSuggestions()
        {
            ProxyPack pack = ProxyPack.Shared;
            int n = 0;
            foreach (SceneMaterial material in _matchOriginal.Materials)
            {
                if (_matchDoc.ForMaterial(material.Name) != null) { continue; }
                string keyword = ProxyCatalog.Suggest(material.Name, null);
                if (keyword == null || !pack.Has(keyword)) { continue; }
                byte[] bytes = pack.ImageBytes(keyword);
                if (bytes == null) { continue; }
                string image = MaterialImages.EntryName(bytes, png: false);
                _matchImages[image] = bytes;
                (float u, float v) = pack.SizeOf(keyword);
                _matchDoc.Set(new MaterialMatch
                {
                    Material = material.Name, Image = image, ImageName = ProxyCatalog.Find(keyword).Label + " (BimGo)", SizeU = u, SizeV = v,
                    Grass = keyword == "grass", Metallic = keyword.StartsWith("metal", StringComparison.Ordinal), Shine = keyword.StartsWith("metal", StringComparison.Ordinal) ? 0.5f : 0f
                });
                n++;
            }
            if (n > 0) { ApplyMatches(); }
            _materialsNotice = n == 0 ? "No unmatched material's name suggests one of BimGo's textures." : $"{n} material{(n == 1 ? "" : "s")} matched from their names: check them, and set each image's real size";
            Sound.Play(n > 0 ? SoundId.Commit : SoundId.UiClick);
        }

        private void ApplyLibraryToModel()
        {
            int n = MatchLibrary().ApplyTo(_matchOriginal.Materials.Select(m => m.Name), _matchDoc, _matchImages);
            if (n > 0) { ApplyMatches(); }
            _materialsNotice = n == 0 ? "None of this model's unmatched materials is in your library (by name)." : $"{n} material{(n == 1 ? "" : "s")} matched from your library";
        }

        private void SaveAllToLibrary()
        {
            int n = 0;
            foreach (MaterialMatch match in _matchDoc.Matches.Where(m => !m.IsOverride))
            {
                MatchLibrary().Put(match, _matchImages);
                n++;
            }
            SaveLibrary();
            _materialsNotice ??= $"{n} match{(n == 1 ? " is" : "es are")} in your material library";
            Sound.Play(SoundId.Commit);
        }

        private void ImportLibrary()
        {
            string path = FileDialogs.ShowOpen(_window.Handle, "Import a material library", MaterialLibrary.DIALOG_FILTER, null);
            _window.Input.ReleaseAll();
            if (path == null) { return; }
            MaterialLibrary other = MaterialLibrary.Read(path, out string error);
            if (other == null)
            {
                _materialsNotice = $"{Path.GetFileName(path)} couldn't be read: {error}";
                Sound.Play(SoundId.Error);
                return;
            }
            int n = MatchLibrary().Merge(other);
            SaveLibrary();
            _materialsNotice ??= $"{n} match{(n == 1 ? "" : "es")} added to your library from {Path.GetFileName(path)} (APPLY LIBRARY uses them here)";
            Sound.Play(SoundId.Commit);
        }

        private void ExportLibrary()
        {
            string path = FileDialogs.ShowSave(_window.Handle, "Export your material library", MaterialLibrary.DIALOG_FILTER, null, "Material library" + MaterialLibrary.EXTENSION, MaterialLibrary.EXTENSION);
            _window.Input.ReleaseAll();
            if (path == null) { return; }
            _materialsNotice = MatchLibrary().Write(path, out string error)
                ? $"Exported to {Path.GetFileName(path)}: colleagues IMPORT LIBRARY… it into theirs"
                : $"The library couldn't be exported: {error}";
        }

        #endregion
    }
}
