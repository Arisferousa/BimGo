using System.Numerics;
using BimGo.Audio;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// UI round: the interface's look and the pause menu's layout.
    /// <list type="bullet">
    /// <item><b>INTERFACE</b> (a settings tab): the theme (dark, light, high contrast), the accent colour, the interface
    /// size (on top of Windows' scaling), the text size, the text and numbers fonts (bundled or installed), the HUD
    /// layout and whether its panels fade while moving. Colours change at once; sizes and fonts rebuild the font atlas
    /// when a slider is let go or a font picked. Saved with the settings, so the home screen follows.</item>
    /// <item><b>The pause menu</b>: a sidebar of sections (MODEL, REVIEW, MEDIA, SETTINGS), each entry with an icon, a
    /// search box that narrows them as you type, and the settings beside it.</item>
    /// <item><b>HUD layouts</b>: Minimal (the crosshair, the gun bar, messages), Standard (a compact status, the gun's
    /// panel, the minimap) and Full (everything, as BimGo always showed it).</item>
    /// </list>
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private const int UI_SLIDER_SCALE = 9401, UI_SLIDER_TEXT = 9402;
        private const int MENU_SEARCH_MAX = 32;

        // The interface settings
        private string _uiThemeName = UiThemes.DARK, _uiAccent = UiThemes.DEFAULT_ACCENT;
        private string _uiFont = UiThemes.DEFAULT_FONT, _uiNumbersFont = UiThemes.DEFAULT_NUMBERS_FONT;
        private float _uiUserScale = 1f, _uiTextSize = 1f;
        private string _hudLayout = HudLayouts.STANDARD;
        private bool _hudFade = true;

        // A size slider being dragged (applied when let go: the atlas is rebuilt once)
        private float _uiScalePending = -1f, _uiTextPending = -1f;

        // HUD fade: how visible the information panels are, and since when the player has been still
        private float _hudAlpha = 1f, _hudStillSince;
        private Vector3 _hudLastFeet;

        // The pause menu's search and sidebar
        private readonly char[] _menuSearch = new char[MENU_SEARCH_MAX];
        private int _menuSearchLength;
        private string _menuSearchText = string.Empty;
        private readonly ScrollRegion _sideScroll = new();
        private List<string> _textFontChoices, _numberFontChoices;

        private static readonly string[] THEME_OPTIONS = { "DARK", "LIGHT", "HIGH CONTRAST" };
        private static readonly string[] HUD_OPTIONS = { "MINIMAL", "STANDARD", "FULL" };
        private const int DROP_UI_FONT = 41, DROP_UI_NUMBERS = 42;
        private readonly object _interfaceDropdowns = new();

        // Menu icons
        private const int MI_RESUME = 0, MI_HOME = 1, MI_PIN = 2, MI_SAVE = 3, MI_SAVE_AS = 4, MI_VIEWER = 5, MI_UPDATE = 6, MI_PUSH = 7, MI_KIOSK = 8,
            MI_REVIT = 9, MI_MATERIALS = 10, MI_LIBRARY = 11, MI_LAYERS = 12, MI_EYE = 13, MI_COMMENT = 14, MI_BOOKMARK = 15, MI_CLASH = 16, MI_ROOM = 17,
            MI_SUN = 18, MI_MEDIA = 19, MI_PEOPLE = 20, MI_PHOTO = 21, MI_KEYS = 22, MI_CLOSE = 23;

        #endregion

        #region Settings

        /// <summary>Reads the interface settings and applies the colours and fonts (before the UI batch is built).</summary>
        private void ReadInterfaceSettings(LaunchSettings settings)
        {
            _uiThemeName = UiThemes.Clean(settings.UiTheme);
            _uiAccent = string.IsNullOrWhiteSpace(settings.UiAccent) ? UiThemes.DEFAULT_ACCENT : settings.UiAccent;
            _uiFont = string.IsNullOrWhiteSpace(settings.UiFont) ? UiThemes.DEFAULT_FONT : settings.UiFont;
            _uiNumbersFont = string.IsNullOrWhiteSpace(settings.UiNumbersFont) ? UiThemes.DEFAULT_NUMBERS_FONT : settings.UiNumbersFont;
            _uiUserScale = Math.Clamp(float.IsFinite(settings.UiScale) ? settings.UiScale : 1f, UiThemes.MIN_SCALE, UiThemes.MAX_SCALE);
            _uiTextSize = Math.Clamp(float.IsFinite(settings.UiTextSize) ? settings.UiTextSize : 1f, UiThemes.MIN_TEXT_SIZE, UiThemes.MAX_TEXT_SIZE);
            _hudLayout = HudLayouts.Clean(settings.HudLayout);
            _hudFade = settings.HudFade;
            UiLook.Apply(settings);
        }

        private void WriteInterfaceSettings(LaunchSettings settings)
        {
            settings.UiTheme = _uiThemeName;
            settings.UiAccent = _uiAccent;
            settings.UiFont = _uiFont;
            settings.UiNumbersFont = _uiNumbersFont;
            settings.UiScale = _uiUserScale;
            settings.UiTextSize = _uiTextSize;
            settings.HudLayout = _hudLayout;
            settings.HudFade = _hudFade;
        }

        /// <summary>The interface's scale: Windows' display scaling times the size chosen.</summary>
        private float InterfaceScale => _window.DpiScale * _uiUserScale;

        /// <summary>Colours changed: applied at once (the next frame draws with them) and saved.</summary>
        private void ApplyInterfaceColours()
        {
            UiTheme.Apply(_uiThemeName, _uiAccent);
            SaveSettings();
        }

        /// <summary>
        /// Sizes or fonts changed: saved now, and the font atlas built again at the start of the next frame (this
        /// frame's text is already queued with the old one).
        /// </summary>
        private void ApplyInterfaceFonts()
        {
            _interfaceRebuild = true;
            SaveSettings();
        }

        private bool _interfaceRebuild;

        /// <summary>Once a frame, before anything is drawn: a pending font atlas rebuild.</summary>
        private void RebuildInterfaceIfPending()
        {
            if (!_interfaceRebuild) { return; }
            _interfaceRebuild = false;
            UiFonts.Current = (_uiFont, _uiNumbersFont, _uiTextSize);
            UiScale = InterfaceScale;
            _ui.Rebuild(UiScale);
            _ui.Radius = S(UiTheme.RADIUS);
            _settingsScroll.Reset();
            _sideScroll.Reset();
        }

        #endregion

        #region The INTERFACE tab

        /// <summary>The INTERFACE settings tab.</summary>
        /// <returns>The bottom of what was drawn.</returns>
        private float BuildInterfaceTab(FontAtlas f, InputState input, float x, float y, float w)
        {
            // Theme
            _ui.Text(f.Small, x, y, "THEME", UiTheme.TEXT_MUTED, S(1.5f));
            int theme = Array.IndexOf(UiThemes.ALL, _uiThemeName);
            int themePicked = Segmented(f, input, x, y + S(20), w, THEME_OPTIONS, Math.Max(0, theme));
            if (themePicked != theme)
            {
                _uiThemeName = UiThemes.ALL[themePicked];
                ApplyInterfaceColours();
                Sound.Play(SoundId.UiClick);
            }
            y += S(64);

            // Accent: swatches, and any colour
            _ui.Text(f.Small, x, y, "ACCENT COLOUR", UiTheme.TEXT_MUTED, S(1.5f));
            y += S(22);
            float sw = S(26), gap = S(8);
            float sx = x;
            foreach (string hex in UiThemes.ACCENTS)
            {
                LaunchSettings.TryParseColour(hex, out uint rgb);
                bool on = string.Equals(hex, _uiAccent, StringComparison.OrdinalIgnoreCase);
                bool hover = Hover(input, sx, y, sw, sw);
                _ui.Circle(sx + sw * 0.5f, y + sw * 0.5f, sw * 0.5f - S(1), Rgba.Hex(rgb), 20);
                if (on || hover) { _ui.Ring(sx + sw * 0.5f, y + sw * 0.5f, sw * 0.5f + S(2), MathF.Max(1.5f, S(2)), on ? UiTheme.TEXT : UiTheme.TEXT_MUTED, 24); }
                if (hover && input.LeftPressed)
                {
                    input.ConsumeClicks();
                    _uiAccent = hex;
                    ApplyInterfaceColours();
                    Sound.Play(SoundId.UiClick);
                }
                sx += sw + gap;
            }
            if (sx + S(80) <= x + w && MenuButton(f, sx, y - S(2), x + w - sx, "OTHER…", false, false, height: S(30), compact: true))
            {
                LaunchSettings.TryParseColour(_uiAccent, out uint current);
                uint? picked = FileDialogs.PickColour(_window.Handle, current);
                _window.Input.ReleaseAll();
                if (picked is uint c)
                {
                    _uiAccent = $"#{c:X6}";
                    ApplyInterfaceColours();
                }
            }
            y += sw + S(16);

            // Sizes (applied when let go)
            float scale = _uiScalePending >= 0f ? _uiScalePending : _uiUserScale;
            TextBuffer text = Text.Clear().Append(scale * 100f, 0).Append(" %");
            float scaleMoved = Slider(f, input, UI_SLIDER_SCALE, x, y, w, "Interface size", text.Span, scale, UiThemes.MIN_SCALE, UiThemes.MAX_SCALE);
            if (_activeSlider == UI_SLIDER_SCALE) { _uiScalePending = Math.Clamp(MathF.Round(scaleMoved * 20f) / 20f, UiThemes.MIN_SCALE, UiThemes.MAX_SCALE); }
            y += S(48);
            float size = _uiTextPending >= 0f ? _uiTextPending : _uiTextSize;
            text = Text.Clear().Append(size * 100f, 0).Append(" %");
            float sizeMoved = Slider(f, input, UI_SLIDER_TEXT, x, y, w, "Text size", text.Span, size, UiThemes.MIN_TEXT_SIZE, UiThemes.MAX_TEXT_SIZE);
            if (_activeSlider == UI_SLIDER_TEXT) { _uiTextPending = Math.Clamp(MathF.Round(sizeMoved * 20f) / 20f, UiThemes.MIN_TEXT_SIZE, UiThemes.MAX_TEXT_SIZE); }
            y += S(52);
            if (!input.LeftDown && (_uiScalePending >= 0f || _uiTextPending >= 0f))
            {
                bool changed = (_uiScalePending >= 0f && MathF.Abs(_uiScalePending - _uiUserScale) > 0.001f) || (_uiTextPending >= 0f && MathF.Abs(_uiTextPending - _uiTextSize) > 0.001f);
                if (_uiScalePending >= 0f) { _uiUserScale = _uiScalePending; }
                if (_uiTextPending >= 0f) { _uiTextSize = _uiTextPending; }
                _uiScalePending = _uiTextPending = -1f;
                if (changed)
                {
                    ApplyInterfaceFonts();
                    return y; // drawn again next frame at the new size
                }
            }

            // Fonts
            float half = (w - S(UiTheme.GAP_SMALL)) * 0.5f;
            _ui.Text(f.Small, x, y, "TEXT FONT", UiTheme.TEXT_MUTED, S(1.5f));
            _ui.Text(f.Small, x + half + S(UiTheme.GAP_SMALL), y, "NUMBERS FONT", UiTheme.TEXT_MUTED, S(1.5f));
            y += S(20);
            if (DropdownButton(f, input, x, y, half, S(30), _uiFont, IsDropdownOpen(_interfaceDropdowns, DROP_UI_FONT)))
            {
                _textFontChoices ??= UiFonts.Choices(numbers: false);
                OpenDropdown(_interfaceDropdowns, DROP_UI_FONT, x, y, MathF.Max(half, S(240)), S(30), _textFontChoices, _textFontChoices.IndexOf(_uiFont), k =>
                {
                    _uiFont = _textFontChoices[k];
                    ApplyInterfaceFonts();
                });
            }
            if (DropdownButton(f, input, x + half + S(UiTheme.GAP_SMALL), y, half, S(30), _uiNumbersFont, IsDropdownOpen(_interfaceDropdowns, DROP_UI_NUMBERS)))
            {
                _numberFontChoices ??= UiFonts.Choices(numbers: true);
                OpenDropdown(_interfaceDropdowns, DROP_UI_NUMBERS, x + half + S(UiTheme.GAP_SMALL), y, MathF.Max(half, S(240)), S(30), _numberFontChoices,
                    _numberFontChoices.IndexOf(_uiNumbersFont), k =>
                    {
                        _uiNumbersFont = _numberFontChoices[k];
                        ApplyInterfaceFonts();
                    });
            }
            y += S(40);
            _ui.TextWrapped(f.Small, x, y, w, "Inter, Roboto, Source Sans 3 and JetBrains Mono come with BimGo; any font installed in Windows works too.", UiTheme.TEXT_FAINT, maxLines: 2);
            y += S(36);

            // HUD
            _ui.Text(f.Small, x, y, "WHILE WALKING (HUD)", UiTheme.TEXT_MUTED, S(1.5f));
            int layout = Array.IndexOf(HudLayouts.ALL, _hudLayout);
            int layoutPicked = Segmented(f, input, x, y + S(20), w, HUD_OPTIONS, Math.Max(0, layout));
            if (layoutPicked != layout)
            {
                _hudLayout = HudLayouts.ALL[layoutPicked];
                SaveSettings();
                Sound.Play(SoundId.UiClick);
            }
            y += S(56);
            _ui.TextWrapped(f.Small, x, y, w, _hudLayout switch
            {
                HudLayouts.MINIMAL => "The crosshair, the gun bar and messages only. Tab still shows the minimap.",
                HudLayouts.FULL => "Everything: status, ground and colour mode, coordinates, legends, the gun's panel, the sun.",
                _ => "A compact status (mode, level, room), the gun's panel and the minimap."
            }, UiTheme.TEXT_FAINT, maxLines: 2);
            y += S(36);
            bool fade = Checkbox(f, input, x, y, w, "Fade the panels while moving", _hudFade);
            if (fade != _hudFade)
            {
                _hudFade = fade;
                SaveSettings();
            }
            y += S(34);

            // Back to how BimGo always looked
            bool defaults = _uiThemeName == UiThemes.DARK && string.Equals(_uiAccent, UiThemes.DEFAULT_ACCENT, StringComparison.OrdinalIgnoreCase)
                && _uiFont == UiThemes.DEFAULT_FONT && _uiNumbersFont == UiThemes.DEFAULT_NUMBERS_FONT && _uiUserScale == 1f && _uiTextSize == 1f
                && _hudLayout == HudLayouts.STANDARD && _hudFade;
            if (MenuButton(f, x, y, w, "RESET THE INTERFACE", false, false, enabled: !defaults, height: S(30), compact: true))
            {
                _uiThemeName = UiThemes.DARK;
                _uiAccent = UiThemes.DEFAULT_ACCENT;
                _uiFont = UiThemes.DEFAULT_FONT;
                _uiNumbersFont = UiThemes.DEFAULT_NUMBERS_FONT;
                _uiUserScale = _uiTextSize = 1f;
                _hudLayout = HudLayouts.STANDARD;
                _hudFade = true;
                UiTheme.Apply(_uiThemeName, _uiAccent);
                ApplyInterfaceFonts();
            }
            return y + S(40);
        }

        #endregion

        #region The pause menu's sidebar

        /// <summary>Where the sidebar is drawing this frame, and which section's heading it has shown.</summary>
        private struct SideMenu
        {
            public float X, W, Y, RowH;
            public string Section;
            public int Shown;
        }

        /// <summary>The pause menu's search text (typed while the menu shows; Backspace deletes).</summary>
        private void TypeIntoMenuSearch(InputState input)
        {
            if (_editing) { return; }
            int before = _menuSearchLength;
            foreach (char c in input.Chars)
            {
                if (c == '\b')
                {
                    if (input.IsDown(KeyCodes.CTRL)) { _menuSearchLength = 0; }
                    else if (_menuSearchLength > 0) { _menuSearchLength--; }
                }
                else if (c > ' ' || (c == ' ' && _menuSearchLength > 0))
                {
                    if (c != 127 && _menuSearchLength < MENU_SEARCH_MAX) { _menuSearch[_menuSearchLength++] = c; }
                }
            }
            if (_menuSearchLength != before)
            {
                _menuSearchText = new string(_menuSearch, 0, _menuSearchLength).Trim();
                _sideScroll.Reset();
            }
        }

        /// <summary>Esc with a search typed: the search clears first (the menu stays).</summary>
        private bool ClearMenuSearch()
        {
            if (!_paused || AnyBigPanelOpen || _menuSearchLength == 0) { return false; }
            _menuSearchLength = 0;
            _menuSearchText = string.Empty;
            return true;
        }

        /// <summary>
        /// One sidebar entry: an icon and its label, under its section's heading (drawn before the section's first
        /// entry). With a search typed, only entries whose label (or section) matches show.
        /// </summary>
        /// <returns>True when clicked.</returns>
        private bool SideItem(ref SideMenu m, FontAtlas f, InputState input, string section, int icon, string label, bool enabled = true, bool danger = false)
        {
            if (_menuSearchText.Length > 0 && !label.Contains(_menuSearchText, StringComparison.OrdinalIgnoreCase)
                && !section.Contains(_menuSearchText, StringComparison.OrdinalIgnoreCase)) { return false; }
            if (!ReferenceEquals(m.Section, section))
            {
                m.Section = section;
                if (m.Shown > 0) { m.Y += S(8); }
                _ui.Text(f.Small, m.X + S(10), m.Y + S(6), section, UiTheme.TEXT_FAINT, S(1.8f));
                m.Y += S(26);
            }
            float h = m.RowH - S(4);
            bool hover = enabled && Hover(input, m.X, m.Y, m.W, h);
            if (hover) { _ui.RoundRect(m.X, m.Y, m.W, h, S(UiTheme.RADIUS_SMALL), UiTheme.Ink(0.08f)); }
            uint colour = !enabled ? UiTheme.TEXT_FAINT : danger ? UiTheme.DANGER : hover ? UiTheme.TEXT : UiTheme.TEXT_SOFT;
            DrawMenuIcon(icon, m.X + S(20), m.Y + h * 0.5f, S(8), hover && !danger ? UiTheme.ACCENT : colour);
            _ui.TextWrapped(f.Body, m.X + S(40), m.Y + h * 0.5f - f.Body.LineHeight * 0.5f, m.W - S(48), label, colour, maxLines: 1);
            m.Y += m.RowH;
            m.Shown++;

            bool clicked = hover && input.LeftPressed;
            if (clicked)
            {
                input.ConsumeClicks();
                Sound.Play(SoundId.UiClick);
            }
            return clicked;
        }

        /// <summary>
        /// The pause menu's sidebar: the model's name, RESUME, the search, then the sections (scrolling when the window
        /// is short) and CLOSE MODEL at its foot.
        /// </summary>
        /// <returns>False when an entry left the menu this frame (the caller returns).</returns>
        private bool BuildPauseSidebar(FontAtlas f, InputState input, float sideW, int height)
        {
            float pad = S(18);
            _ui.Rect(0, 0, sideW, height, UiTheme.PANEL_STRONG);
            _ui.Rect(sideW - MathF.Max(1f, UiScale), 0, MathF.Max(1f, UiScale), height, UiTheme.DIVIDER);
            float x = pad, w = sideW - pad * 2f, y = S(22);

            _ui.Text(f.Small, x, y, "BIMGO · PAUSED", UiTheme.TEXT_FAINT, S(2f));
            y += S(20);
            string title = string.IsNullOrEmpty(Scene.ModelTitle) ? DocumentName : Scene.ModelTitle;
            _ui.TextWrapped(f.Bold, x, y, w, title, UiTheme.TEXT, maxLines: 1);
            y += S(24);
            string state = Source.IsRevit ? (_live == null || _live.Connected ? "Live from Revit" : "Revit: offline") : IsDirty ? "Unsaved changes" : "Saved";
            _ui.Text(f.Small, x, y, state, IsDirty && !Source.IsRevit ? UiTheme.MEASURE_LABEL : UiTheme.TEXT_MUTED);
            y += S(26);

            float bh = S(36);
            if (MenuButton(f, x, y, w, "RESUME", primary: true, danger: false, height: bh, compact: true))
            {
                SetPaused(false);
                return false;
            }
            y += bh + S(10);

            // Search
            float sh = S(32);
            _ui.RoundRect(x, y, w, sh, S(UiTheme.RADIUS_SMALL), UiTheme.CONTROL);
            _ui.RoundOutline(x, y, w, sh, S(UiTheme.RADIUS_SMALL), MathF.Max(1f, UiScale), _menuSearchLength > 0 ? UiTheme.ACCENT : UiTheme.CONTROL_BORDER);
            float ty = y + sh * 0.5f - f.Body.LineHeight * 0.5f;
            DrawMenuIcon(-1, x + S(16), y + sh * 0.5f, S(6), UiTheme.TEXT_FAINT); // a magnifier
            if (_menuSearchLength == 0) { _ui.Text(f.Body, x + S(32), ty, "Type to find…", UiTheme.TEXT_FAINT); }
            else
            {
                float used = _ui.Text(f.Body, x + S(32), ty, _menuSearch.AsSpan(0, _menuSearchLength), UiTheme.TEXT);
                if (((int)(_clock * 2f) & 1) == 0) { _ui.Rect(x + S(33) + used, y + S(7), S(2), sh - S(14), UiTheme.TEXT_SOFT); }
            }
            y += sh + S(8);

            // The entries (scrolling), CLOSE MODEL below them
            float closeH = S(40);
            float listBottom = height - pad - closeH - S(8);
            float top = BeginScroll(_sideScroll, input, 0f, y, sideW, listBottom - y, out float cw);
            var m = new SideMenu { X = x, W = MathF.Min(w, cw - pad), Y = top, RowH = S(36) };
            bool stay = SidebarEntries(ref m, f, input);
            if (m.Shown == 0) { _ui.TextWrapped(f.Body, x + S(10), top + S(8), w - S(20), "Nothing matches.", UiTheme.TEXT_MUTED, maxLines: 1); }
            EndScroll(_sideScroll, input, m.Y + S(8));
            if (!stay) { return false; }

            var close = new SideMenu { X = x, W = w, Y = height - pad - closeH, RowH = closeH, Section = string.Empty };
            if (SideItem(ref close, f, input, string.Empty, MI_CLOSE, _live != null ? "LEAVE SESSION" : _options.InApp ? "CLOSE MODEL" : "END SESSION", danger: true))
            {
                _endRequested = true;
                return false;
            }
            return true;
        }

        /// <summary>Every entry, by section (an entry that leaves the menu returns false at once).</summary>
        private bool SidebarEntries(ref SideMenu m, FontAtlas f, InputState input)
        {
            const string MODEL = "MODEL", REVIEW = "REVIEW", MEDIA = "MEDIA", SETTINGS = "SETTINGS";

            // MODEL: where you are, the file, the model's content
            if (SideItem(ref m, f, input, MODEL, MI_HOME, "RETURN HOME")) { _player.GoHome(); SetPaused(false); return false; }
            if (SideItem(ref m, f, input, MODEL, MI_PIN, _clock < _homeSetUntil ? "HOME SAVED HERE" : "SET HOME HERE")) { SetHomeHere(); }
            if (IsFileMode)
            {
                if (SideItem(ref m, f, input, MODEL, MI_SAVE, IsDirty ? "SAVE *" : "SAVE")) { Save(saveAs: false); return false; }
                if (SideItem(ref m, f, input, MODEL, MI_SAVE_AS, "SAVE AS…")) { Save(saveAs: true); return false; }
            }
            else if (SideItem(ref m, f, input, MODEL, MI_SAVE_AS, "SAVE AS .BIMGO…")) { Save(saveAs: true); return false; }
            if (_viewerFile)
            {
                if (SideItem(ref m, f, input, MODEL, MI_VIEWER, "SIMPLE VIEWER")) { EnterViewer(fromWalkthrough: true); SetPaused(false); return false; }
            }
            else if (SideItem(ref m, f, input, MODEL, MI_VIEWER, "SAVE VIEWER COPY…")) { SaveViewerCopy(); return false; }
            if (IsFileMode)
            {
                if (SideItem(ref m, f, input, MODEL, MI_UPDATE, "UPDATE FROM IFC…")) { ReloadFromIfc(); return false; }
                if (SideItem(ref m, f, input, MODEL, MI_PUSH, PushMenuLabel())) { OpenPush(); return false; }
                if (SideItem(ref m, f, input, MODEL, MI_KIOSK, "MAKE KIOSK SHORTCUT")) { MakeKioskShortcut(); return false; }
            }
            if (_live != null)
            {
                if (SideItem(ref m, f, input, MODEL, MI_REVIT, "NEW REVIT VIEW HERE", enabled: _live.Connected)) { SendRevitView(BimGo.Live.RevitViewAction.Create, sound: false); }
                if (SideItem(ref m, f, input, MODEL, MI_REVIT, "UPDATE REVIT VIEW", enabled: _live.Connected)) { SendRevitView(BimGo.Live.RevitViewAction.Update, sound: false); }
            }
            if (HasMaterialsPanel)
            {
                if (SideItem(ref m, f, input, MODEL, MI_MATERIALS, MaterialsMenuLabel())) { OpenMaterials(); return false; }
            }
            else if (HasTexturePanel && SideItem(ref m, f, input, MODEL, MI_MATERIALS, TexturesMenuLabel())) { OpenTextures(); return false; }
            if (HasLibrary && SideItem(ref m, f, input, MODEL, MI_LIBRARY, LibraryMenuLabel())) { OpenLibrary(); return false; }
            if (SideItem(ref m, f, input, MODEL, MI_LAYERS, GeometryMenuLabel())) { OpenGeometry(); return false; }
            int hidden = HiddenThingsCount();
            if (hidden > 0)
            {
                if (_showAllCount != hidden)
                {
                    _showAllCount = hidden;
                    _showAllLabel = $"SHOW ALL ({hidden} HIDDEN)";
                }
                if (SideItem(ref m, f, input, MODEL, MI_EYE, _showAllLabel)) { ShowAll(); }
            }

            // REVIEW
            if (SideItem(ref m, f, input, REVIEW, MI_COMMENT, CommentsMenuLabel())) { OpenComments(); return false; }
            if (SideItem(ref m, f, input, REVIEW, MI_BOOKMARK, BookmarksMenuLabel())) { OpenBookmarks(); return false; }
            if (SideItem(ref m, f, input, REVIEW, MI_CLASH, ClashesMenuLabel())) { OpenClashes(); return false; }
            if (Scene.Rooms.Length > 0 && SideItem(ref m, f, input, REVIEW, MI_ROOM, "FIND ROOM")) { OpenRooms(); return false; }
            if (SideItem(ref m, f, input, REVIEW, MI_SUN, "SUN HOURS STUDY")) { OpenSunHours(); return false; }

            // MEDIA
            if (SideItem(ref m, f, input, MEDIA, MI_MEDIA, "MEDIA")) { OpenMedia(_mediaTab); return false; }
            if (SideItem(ref m, f, input, MEDIA, MI_PEOPLE, EntourageMenuLabel())) { OpenEntourage(); return false; }
            if (SideItem(ref m, f, input, MEDIA, MI_PHOTO, SitePhotosMenuLabel())) { OpenSitePhotosPanel(); return false; }

            // SETTINGS (the tabs are beside the sidebar)
            if (SideItem(ref m, f, input, SETTINGS, MI_KEYS, "CONTROLS")) { OpenControls(); return false; }
            return true;
        }

        /// <summary>
        /// A small line icon for a menu entry, centred on (cx, cy), about <paramref name="r"/> from the centre to its
        /// edge (-1: a magnifier for the search box).
        /// </summary>
        private void DrawMenuIcon(int icon, float cx, float cy, float r, uint colour)
        {
            float t = MathF.Max(1.25f, S(1.5f));
            switch (icon)
            {
                case -1: // magnifier
                    _ui.Ring(cx - r * 0.2f, cy - r * 0.2f, r * 0.7f, t, colour, 16);
                    _ui.Line(cx + r * 0.3f, cy + r * 0.3f, cx + r, cy + r, t, colour);
                    break;
                case MI_HOME:
                    _ui.Line(cx - r, cy, cx, cy - r, t, colour);
                    _ui.Line(cx, cy - r, cx + r, cy, t, colour);
                    _ui.Outline(cx - r * 0.7f, cy - r * 0.1f, r * 1.4f, r * 1.1f, t, colour);
                    break;
                case MI_PIN:
                    _ui.Ring(cx, cy - r * 0.35f, r * 0.5f, t, colour, 14);
                    _ui.Line(cx, cy + r * 0.15f, cx, cy + r, t, colour);
                    break;
                case MI_SAVE:
                case MI_SAVE_AS:
                    _ui.Outline(cx - r, cy - r, r * 2f, r * 2f, t, colour);
                    _ui.Rect(cx - r * 0.5f, cy - r, r, r * 0.7f, colour);
                    if (icon == MI_SAVE_AS) { _ui.Line(cx + r * 0.2f, cy + r * 0.6f, cx + r * 1.1f, cy - r * 0.3f, t, colour); }
                    break;
                case MI_VIEWER:
                case MI_EYE:
                    _ui.Line(cx - r, cy, cx, cy - r * 0.6f, t, colour);
                    _ui.Line(cx, cy - r * 0.6f, cx + r, cy, t, colour);
                    _ui.Line(cx - r, cy, cx, cy + r * 0.6f, t, colour);
                    _ui.Line(cx, cy + r * 0.6f, cx + r, cy, t, colour);
                    _ui.Circle(cx, cy, r * 0.3f, colour, 10);
                    break;
                case MI_UPDATE:
                    _ui.Ring(cx, cy, r * 0.8f, t, colour, 16);
                    _ui.Triangle(cx + r * 0.8f, cy - r * 0.6f, cx + r * 1.2f, cy, cx + r * 0.4f, cy, colour);
                    break;
                case MI_PUSH:
                    _ui.Line(cx, cy + r * 0.6f, cx, cy - r, t, colour);
                    _ui.Line(cx, cy - r, cx - r * 0.6f, cy - r * 0.4f, t, colour);
                    _ui.Line(cx, cy - r, cx + r * 0.6f, cy - r * 0.4f, t, colour);
                    _ui.Rect(cx - r, cy + r - t, r * 2f, t, colour);
                    break;
                case MI_KIOSK:
                    _ui.Outline(cx - r, cy - r * 0.8f, r * 2f, r * 1.3f, t, colour);
                    _ui.Line(cx, cy + r * 0.5f, cx, cy + r, t, colour);
                    _ui.Line(cx - r * 0.5f, cy + r, cx + r * 0.5f, cy + r, t, colour);
                    break;
                case MI_REVIT:
                case MI_PHOTO:
                    _ui.Outline(cx - r, cy - r * 0.6f, r * 2f, r * 1.4f, t, colour);
                    _ui.Ring(cx, cy + r * 0.1f, r * 0.4f, t, colour, 12);
                    _ui.Rect(cx - r * 0.4f, cy - r * 0.9f, r * 0.8f, r * 0.3f, colour);
                    break;
                case MI_MATERIALS:
                    _ui.Outline(cx - r, cy - r, r * 2f, r * 2f, t, colour);
                    _ui.Rect(cx - r, cy - r, r, r, colour);
                    _ui.Rect(cx, cy, r, r, colour);
                    break;
                case MI_LIBRARY:
                    _ui.Outline(cx - r, cy - r * 0.4f, r * 2f, r * 1.4f, t, colour);
                    _ui.Line(cx - r, cy - r * 0.4f, cx - r * 0.6f, cy - r, t, colour);
                    _ui.Line(cx + r, cy - r * 0.4f, cx + r * 0.6f, cy - r, t, colour);
                    _ui.Line(cx - r * 0.6f, cy - r, cx + r * 0.6f, cy - r, t, colour);
                    break;
                case MI_LAYERS:
                    for (int k = -1; k <= 1; k++)
                    {
                        float yy = cy + k * r * 0.6f;
                        _ui.Line(cx - r, yy, cx, yy - r * 0.4f, t, colour);
                        _ui.Line(cx, yy - r * 0.4f, cx + r, yy, t, colour);
                    }
                    break;
                case MI_COMMENT:
                    _ui.Outline(cx - r, cy - r * 0.8f, r * 2f, r * 1.3f, t, colour);
                    _ui.Triangle(cx - r * 0.5f, cy + r * 0.5f, cx, cy + r * 0.5f, cx - r * 0.6f, cy + r, colour);
                    break;
                case MI_BOOKMARK:
                    _ui.Line(cx - r * 0.6f, cy - r, cx - r * 0.6f, cy + r, t, colour);
                    _ui.Triangle(cx - r * 0.6f, cy - r, cx + r, cy - r * 0.55f, cx - r * 0.6f, cy - r * 0.1f, colour);
                    break;
                case MI_CLASH:
                    _ui.Outline(cx - r, cy - r, r * 1.3f, r * 1.3f, t, colour);
                    _ui.Outline(cx - r * 0.3f, cy - r * 0.3f, r * 1.3f, r * 1.3f, t, colour);
                    break;
                case MI_ROOM:
                    _ui.Outline(cx - r, cy - r, r * 2f, r * 2f, t, colour);
                    _ui.Rect(cx - r * 0.2f, cy - r, r * 0.4f, r * 1.2f, colour);
                    break;
                case MI_SUN:
                    _ui.Circle(cx, cy, r * 0.45f, colour, 12);
                    for (int k = 0; k < 8; k++)
                    {
                        float a = k * MathF.PI / 4f;
                        _ui.Line(cx + MathF.Cos(a) * r * 0.7f, cy + MathF.Sin(a) * r * 0.7f, cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r, t, colour);
                    }
                    break;
                case MI_MEDIA:
                    _ui.Outline(cx - r, cy - r * 0.8f, r * 2f, r * 1.6f, t, colour);
                    _ui.Triangle(cx - r * 0.3f, cy - r * 0.45f, cx + r * 0.5f, cy, cx - r * 0.3f, cy + r * 0.45f, colour);
                    break;
                case MI_PEOPLE:
                    _ui.Circle(cx, cy - r * 0.5f, r * 0.4f, colour, 12);
                    _ui.Line(cx - r * 0.7f, cy + r, cx - r * 0.5f, cy + r * 0.1f, t, colour);
                    _ui.Line(cx - r * 0.5f, cy + r * 0.1f, cx + r * 0.5f, cy + r * 0.1f, t, colour);
                    _ui.Line(cx + r * 0.5f, cy + r * 0.1f, cx + r * 0.7f, cy + r, t, colour);
                    break;
                case MI_KEYS:
                    _ui.Outline(cx - r, cy - r * 0.6f, r * 2f, r * 1.2f, t, colour);
                    for (int k = -1; k <= 1; k++) { _ui.Rect(cx + k * r * 0.55f - t * 0.5f, cy - r * 0.25f, t, t, colour); }
                    _ui.Rect(cx - r * 0.5f, cy + r * 0.2f, r, t, colour);
                    break;
                case MI_CLOSE:
                    _ui.Line(cx - r * 0.7f, cy - r * 0.7f, cx + r * 0.7f, cy + r * 0.7f, t, colour);
                    _ui.Line(cx + r * 0.7f, cy - r * 0.7f, cx - r * 0.7f, cy + r * 0.7f, t, colour);
                    break;
                default:
                    _ui.Circle(cx, cy, r * 0.3f, colour, 10);
                    break;
            }
        }

        #endregion

        #region HUD layouts

        private bool HudMinimal => _hudLayout == HudLayouts.MINIMAL;

        private bool HudFull => _hudLayout == HudLayouts.FULL;

        /// <summary>The status panel's height: compact (mode, level, room) in Standard, the full panel in Full.</summary>
        private float StatusPanelHeight => S(HudFull ? UiTheme.STATUS_HEIGHT : UiTheme.STATUS_HEIGHT - 40f);

        /// <summary>
        /// Once a frame: the information panels fade to a quarter while the player moves (when on), and come back
        /// after a second still.
        /// </summary>
        private void UpdateHudFade(float dt)
        {
            Vector3 feet = _player.Feet;
            bool moving = Vector3.DistanceSquared(feet, _hudLastFeet) > 1e-6f;
            _hudLastFeet = feet;
            if (moving) { _hudStillSince = _clock; }
            bool faded = _hudFade && !_paused && _window.IsCaptured && _clock - _hudStillSince < 1f;
            float target = faded ? 0.25f : 1f;
            _hudAlpha += (target - _hudAlpha) * Math.Clamp(dt * (faded ? 4f : 3f), 0f, 1f);
        }

        #endregion
    }

    /// <summary>
    /// UI round: applies the interface settings that every UI batch shares (the theme and accent, the fonts and text
    /// size). The session and the home screen call it with the settings they read.
    /// </summary>
    internal static class UiLook
    {
        public static void Apply(LaunchSettings settings)
        {
            if (settings == null) { return; }
            UiTheme.Apply(settings.UiTheme, settings.UiAccent);
            UiFonts.Current = (settings.UiFont, settings.UiNumbersFont, settings.UiTextSize);
        }

        /// <summary>The interface scale for a window: Windows' display scaling times the size chosen.</summary>
        public static float ScaleFor(float dpiScale, LaunchSettings settings) =>
            dpiScale * Math.Clamp(settings != null && float.IsFinite(settings.UiScale) ? settings.UiScale : 1f, UiThemes.MIN_SCALE, UiThemes.MAX_SCALE);
    }
}
