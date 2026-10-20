using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round X-D1: light profiles (IES photometric files and BimGo's built-ins) per fixture type.
    /// <list type="bullet">
    /// <item>Each fixture type ("Family : Type") uses, unless changed: the .ies file its Revit family names (copied into the
    /// snapshot at extraction), else BimGo's own downward lobe as before.</item>
    /// <item>Pause → LIGHTS lists the types: ‹ › step through Revit's file, the built-ins, the imported files and the lobe;
    /// IMPORT .IES… reads a file and assigns it; RESET goes back to the default.</item>
    /// <item>Choices and imported files are kept in <c>light-profiles.json</c> (the model folder, or inside a .bimgo),
    /// like the entourage rules, so the next Go / F5 / Export keeps them.</item>
    /// <item>The renderer holds up to <see cref="LightProfiles.MAX_GPU_PROFILES"/> different profiles at once; past that,
    /// the remaining types fall back to the lobe (said in the tab).</item>
    /// <item>A profile only changes where a fixture's light goes, never how much (Revit's lumens; a fixture whose lumens
    /// were guessed takes an imported file's own output).</item>
    /// </list>
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private LightProfileDocument _lightProfileDoc = new();
        private string _lightProfilePath; // null = inside the .bimgo
        private int _lightProfileRevision;

        // Parsed profiles by key (null = unusable, logged once)
        private readonly Dictionary<string, LightProfile> _profileCache = new(StringComparer.Ordinal);

        // Per light: its renderer slot (-1 = lobe) and code at its extracted orientation
        private int[] _lightSlot = Array.Empty<int>();
        private float[] _lightCode = Array.Empty<float>();
        private readonly List<string> _slotKeys = new();
        private bool _profileOverflow;

        // The tab's rows (made once) and its last message
        private List<LightTypeRow> _lightTypeRows;
        private string _lightNotice;

        /// <summary>A fixture type in the LIGHTS tab.</summary>
        private sealed record LightTypeRow(string FamilyType, int Count, string RevitHash, string RevitName);

        #endregion

        #region Setup and saving

        /// <summary>
        /// Loads the choices (inside a .bimgo, else the model folder's light-profiles.json) and fills the renderer's tables.
        /// </summary>
        private void InitialiseLightProfiles()
        {
            if (IsFileMode)
            {
                _lightProfilePath = null;
                _lightProfileDoc = _options.Document?.LightProfiles ?? new LightProfileDocument();
            }
            else
            {
                _lightProfilePath = LightProfileFiles.SidecarFor(Scene.CommentsPath);
                _lightProfileDoc = (_lightProfilePath == null ? null : LightProfileFiles.Read(_lightProfilePath, out _)) ?? new LightProfileDocument();
            }
            _lightProfileDoc.Clean();
            ApplyLightProfiles();
            if (Scene.Lighting.Lights.Length > 0)
            {
                Utilities.Log_Utils.Write($"Light profiles: {_slotKeys.Count} in use, {Scene.Lighting.IesFiles.Length} .ies files from Revit, " +
                    $"{_lightProfileDoc.Imports.Count} imported, {_lightProfileDoc.Types.Count} type choices.");
            }
        }

        /// <summary>The choices for saving into a .bimgo (null when there are none).</summary>
        private LightProfileDocument LightProfilesForSave()
        {
            if (_lightProfileDoc.IsEmpty) { return null; }
            _lightProfileDoc.Model = Scene.ModelTitle;
            return _lightProfileDoc;
        }

        /// <summary>Records a change: re-fills the tables, writes the model folder's file (or marks the .bimgo changed).</summary>
        private void SaveLightProfiles()
        {
            _lightProfileRevision++;
            ApplyLightProfiles();
            if (_lightProfilePath != null)
            {
                _lightProfileDoc.Model = Scene.ModelTitle;
                if (!LightProfileFiles.Write(_lightProfilePath, _lightProfileDoc, out string error)) { _lightNotice = error; }
            }
            UpdateTitle();
        }

        #endregion

        #region Resolving

        /// <summary>
        /// The profile key a light uses: its type's choice, else its Revit file; null = the lobe.
        /// </summary>
        private string ProfileKeyOf(LightSource light)
        {
            string choice = _lightProfileDoc.ChoiceFor(Scene.Elements[light.Element].FamilyType ?? string.Empty);
            if (choice == null || choice == LightProfiles.REVIT) { return light.Ies == null ? null : LightProfiles.FILE_PREFIX + light.Ies; }
            return choice == LightProfiles.LOBE ? null : choice;
        }

        /// <summary>
        /// A profile by key (built-in, Revit's file or an imported one), parsed once; null if unknown or unusable.
        /// </summary>
        private LightProfile ProfileOf(string key)
        {
            if (key == null) { return null; }
            if (_profileCache.TryGetValue(key, out LightProfile cached)) { return cached; }
            LightProfile profile = LightProfiles.FindBuiltIn(key);
            string hash = LightProfiles.HashOf(key);
            if (profile == null && hash != null)
            {
                string name = null, text = null;
                LightIesFile revit = Scene.Lighting.FindIes(hash);
                if (revit != null) { name = revit.Name; text = revit.Text; }
                else
                {
                    LightProfileImport import = _lightProfileDoc.FindImport(hash);
                    if (import != null) { name = import.Name; text = import.Text; }
                }
                if (text != null)
                {
                    profile = LightProfile.FromIes(key, name, text, out string error);
                    if (profile == null) { Utilities.Log_Utils.Write($"Light profile {name} unusable: {error}"); }
                }
            }
            _profileCache[key] = profile;
            return profile;
        }

        /// <summary>
        /// Works out every light's profile slot and code, fills the renderer's tables and (for guessed outputs with an
        /// imported file) the light's output. Called at start and on every choice change.
        /// </summary>
        private void ApplyLightProfiles()
        {
            LightSource[] lights = Scene.Lighting.Lights;
            if (_lightSlot.Length != lights.Length)
            {
                _lightSlot = new int[lights.Length];
                _lightCode = new float[lights.Length];
            }
            _slotKeys.Clear();
            _profileOverflow = false;
            ArtificialLighting a = _renderer?.Artificial;
            for (int i = 0; i < lights.Length; i++)
            {
                LightSource light = lights[i];
                string key = ProfileKeyOf(light);
                LightProfile profile = ProfileOf(key);
                int slot = -1;
                if (profile != null)
                {
                    slot = _slotKeys.IndexOf(key);
                    if (slot < 0)
                    {
                        if (_slotKeys.Count < LightProfiles.MAX_GPU_PROFILES)
                        {
                            slot = _slotKeys.Count;
                            _slotKeys.Add(key);
                            a?.SetProfile(slot, profile);
                        }
                        else { _profileOverflow = true; }
                    }
                }
                _lightSlot[i] = slot;
                _lightCode[i] = slot < 0 ? light.Downward : LightProfiles.Encode(slot, light.Aim, light.Across);

                // Output: Revit's lumens; a guessed output takes an imported / Revit file's own (when it has one)
                float lumens = light.Lumens;
                if (profile != null && profile.IsFile && light.Estimated && MathF.Abs(light.Lumens - 1000f) < 0.5f && profile.Lumens >= 10f)
                {
                    lumens = Math.Clamp(profile.Lumens, 50f, 50000f);
                }
                if (i < _lightColour.Length)
                {
                    _lightColour[i] = LightingData.KelvinToRgb(light.Kelvin) * (lumens * LIGHT_SCALE);
                    _lightRadius[i] = Math.Clamp(MathF.Sqrt(lumens) * 0.16f, 2.5f, 9f);
                }
            }
        }

        /// <summary>X-D1: a light's code at its extracted orientation (its downward share when it has no profile).</summary>
        private float LightCodeOf(int light) => light < _lightCode.Length ? _lightCode[light] : Scene.Lighting.Lights[light].Downward;

        /// <summary>X-D1: a light's code turned with a moved or cloned fixture.</summary>
        private float LightCodeOf(int light, in Matrix4x4 model)
        {
            if (light >= _lightSlot.Length || _lightSlot[light] < 0) { return LightCodeOf(light); }
            LightSource source = Scene.Lighting.Lights[light];
            return LightProfiles.Encode(_lightSlot[light], Vector3.TransformNormal(source.Aim, model), Vector3.TransformNormal(source.Across, model));
        }

        #endregion

        #region LIGHTS tab

        /// <summary>The fixture types with lights, by name (made once).</summary>
        private List<LightTypeRow> LightTypeRows()
        {
            if (_lightTypeRows != null) { return _lightTypeRows; }
            var counts = new Dictionary<string, (int Count, string Hash)>(StringComparer.Ordinal);
            int lastElement = -1;
            foreach (LightSource light in Scene.Lighting.Lights)
            {
                string type = Scene.Elements[light.Element].FamilyType ?? string.Empty;
                counts.TryGetValue(type, out (int Count, string Hash) entry);
                // Fixtures, not light sources (X-D1 r3: a fixture's nested sources are consecutive lights)
                counts[type] = (entry.Count + (light.Element == lastElement ? 0 : 1), entry.Hash ?? light.Ies);
                lastElement = light.Element;
            }
            _lightTypeRows = counts
                .OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new LightTypeRow(p.Key, p.Value.Count, p.Value.Hash, Scene.Lighting.FindIes(p.Value.Hash)?.Name))
                .ToList();
            return _lightTypeRows;
        }

        /// <summary>
        /// The choices a type steps through: Revit's file (when it has one), the built-ins, the imported files, the lobe.
        /// </summary>
        private List<string> ProfileOptionsFor(LightTypeRow row)
        {
            var options = new List<string>();
            if (row.RevitHash != null) { options.Add(LightProfiles.REVIT); }
            foreach (LightProfile p in LightProfiles.BuiltIns) { options.Add(p.Key); }
            foreach (LightProfileImport import in _lightProfileDoc.Imports)
            {
                string key = LightProfiles.FILE_PREFIX + import.Hash;
                if (import.Hash != row.RevitHash) { options.Add(key); }
            }
            options.Add(LightProfiles.LOBE);
            return options;
        }

        /// <summary>The choice a type is on now (its default when none was made).</summary>
        private string CurrentChoice(LightTypeRow row) =>
            _lightProfileDoc.ChoiceFor(row.FamilyType) ?? (row.RevitHash != null ? LightProfiles.REVIT : LightProfiles.LOBE);

        /// <summary>Appends what a choice is to <see cref="Text"/>.</summary>
        private void AppendChoiceLabel(LightTypeRow row, string choice)
        {
            if (choice == LightProfiles.LOBE) { Text.Append("BimGo lobe (no profile)"); return; }
            string key = choice == LightProfiles.REVIT ? LightProfiles.FILE_PREFIX + row.RevitHash : choice;
            LightProfile profile = ProfileOf(key);
            if (choice == LightProfiles.REVIT) { Text.Append("Revit: ").Append(row.RevitName ?? "its .ies"); }
            else if (profile == null) { Text.Append("Missing profile"); return; }
            else if (profile.IsFile) { Text.Append("Imported: ").Append(profile.Name); }
            else { Text.Append(profile.Name); }
            if (profile == null) { Text.Append(" (unusable)"); return; }
            if (profile.IsFile && profile.BeamAngle > 0f && profile.BeamAngle < 180f) { Text.Append(" · ").Append(profile.BeamAngle, 0).Append("° beam"); }
        }

        /// <summary>
        /// Pause → LIGHTS: each fixture type with its count, its profile, and ‹ › · IMPORT .IES… · RESET.
        /// </summary>
        /// <returns>The bottom of the content.</returns>
        private float BuildLightsTab(FontAtlas f, InputState input, float ix, float y, float iw)
        {
            List<LightTypeRow> rows = LightTypeRows();
            if (rows.Count == 0)
            {
                y += _ui.TextWrapped(f.Small, ix, y, iw,
                    "No lighting fixtures in this model. Light profiles (IES files and built-ins) apply to Lighting Fixtures elements.",
                    UiTheme.TEXT_FAINT, maxLines: 4);
                return y;
            }

            Text.Clear().AppendGrouped(rows.Count).Append(rows.Count == 1 ? " fixture type · " : " fixture types · ")
                .AppendGrouped(Scene.Lighting.IesFiles.Length).Append(Scene.Lighting.IesFiles.Length == 1 ? " .ies file from Revit" : " .ies files from Revit");
            _ui.TextWrapped(f.Small, ix, y, iw, Text.Span, UiTheme.TEXT_MUTED, maxLines: 2);
            y += S(20);
            string notice = _lightNotice ?? (_profileOverflow ? $"Over {LightProfiles.MAX_GPU_PROFILES} different profiles in use: the rest use the lobe" : null)
                ?? (_lightMode != ArtificialLightMode.Lights ? "Profiles show with Lights → Glow + light (sun panel, or K)" : null);
            if (notice != null)
            {
                y += _ui.TextWrapped(f.Small, ix, y, iw, notice, UiTheme.MEASURE_LABEL, maxLines: 3) + S(6);
            }
            y += S(6);

            float bw = S(28), gap = S(6);
            foreach (LightTypeRow row in rows)
            {
                _ui.TextWrapped(f.Body, ix, y, iw - S(56), row.FamilyType.Length == 0 ? "(no type)" : row.FamilyType, UiTheme.TEXT, maxLines: 1);
                Text.Clear().Append('×').AppendGrouped(row.Count);
                _ui.TextRight(f.Small, ix + iw, y + S(2), Text.Span, UiTheme.TEXT_MUTED);
                y += S(20);

                string current = CurrentChoice(row);
                bool set = _lightProfileDoc.ChoiceFor(row.FamilyType) != null;
                Text.Clear();
                AppendChoiceLabel(row, current);
                if (set) { Text.Append(" (set)"); }
                _ui.TextWrapped(f.Small, ix, y, iw, Text.Span, set ? UiTheme.PLACE_LABEL : UiTheme.TEXT_MUTED, maxLines: 1);
                y += S(20);

                float importW = iw - 2f * (bw + gap) - S(64) - gap;
                if (SmallButton(f, input, ix, y, bw, S(26), "‹")) { StepProfile(row, -1); }
                if (SmallButton(f, input, ix + bw + gap, y, bw, S(26), "›")) { StepProfile(row, +1); }
                if (SmallButton(f, input, ix + 2f * (bw + gap), y, importW, S(26), "IMPORT .IES…")) { ImportIes(row); }
                if (set && SmallButton(f, input, ix + iw - S(64), y, S(64), S(26), "RESET"))
                {
                    _lightProfileDoc.SetChoice(row.FamilyType, null);
                    _lightNotice = null;
                    SaveLightProfiles();
                }
                y += S(38);
            }
            y += _ui.TextWrapped(f.Small, ix, y, iw,
                "An IES file's C 0° runs along the family's X axis; a wall washer's C 0° should face the wall. Photos and videos light every fixture in view.",
                UiTheme.TEXT_FAINT, maxLines: 4);
            return y;
        }

        /// <summary>‹ / ›: the type's previous / next choice.</summary>
        private void StepProfile(LightTypeRow row, int direction)
        {
            List<string> options = ProfileOptionsFor(row);
            int at = options.IndexOf(CurrentChoice(row));
            int next = ((at < 0 ? 0 : at + direction) % options.Count + options.Count) % options.Count;
            string choice = options[next];
            string fallback = row.RevitHash != null ? LightProfiles.REVIT : LightProfiles.LOBE;
            _lightProfileDoc.SetChoice(row.FamilyType, choice == fallback ? null : choice);
            _lightNotice = null;
            SaveLightProfiles();
        }

        /// <summary>
        /// IMPORT .IES…: reads a photometric file, keeps it (once, by content) and assigns it to the type.
        /// </summary>
        private void ImportIes(LightTypeRow row)
        {
            _window.Input.ReleaseAll();
            string path = FileDialogs.ShowOpen(_window.Handle, $"Photometric file (IES) for “{row.FamilyType}”", "IES photometric files (*.ies)|*.ies|All files (*.*)|*.*", null);
            _window.Input.ReleaseAll();
            if (path == null) { return; }

            string text;
            try
            {
                if (new FileInfo(path).Length > LightProfile.MAX_IES_BYTES) { Fail("That file is too big for an .ies file"); return; }
                text = File.ReadAllText(path, System.Text.Encoding.Latin1);
            }
            catch (Exception ex)
            {
                Fail($"The file couldn't be read: {ex.Message}");
                return;
            }
            string key = LightProfiles.KeyOf(text), hash = LightProfiles.HashOf(key), name = Path.GetFileName(path);
            if (LightProfile.FromIes(key, name, text, out string error) == null)
            {
                Fail($"{name}: {error}");
                return;
            }
            if (Scene.Lighting.FindIes(hash) == null && _lightProfileDoc.FindImport(hash) == null)
            {
                if (_lightProfileDoc.Imports.Count >= LightProfileDocument.MAX_IMPORTS)
                {
                    Fail($"Already {LightProfileDocument.MAX_IMPORTS} imported files: reset some types first");
                    return;
                }
                _lightProfileDoc.Imports.Add(new LightProfileImport { Hash = hash, Name = name, Text = text });
            }
            _profileCache.Remove(key);
            _lightProfileDoc.SetChoice(row.FamilyType, hash == row.RevitHash ? null : key);
            _lightNotice = $"Every “{row.FamilyType}”: {name}";
            Sound.Play(SoundId.UiClick);
            SaveLightProfiles();

            void Fail(string message)
            {
                _lightNotice = message;
                Sound.Play(SoundId.Error);
            }
        }

        #endregion
    }
}
