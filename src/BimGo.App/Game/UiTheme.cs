using BimGo.Rendering;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// HUD colours and spacing. UI round: the interface's colours follow a theme (<see cref="Apply"/>: dark, as BimGo
    /// always looked; light, for bright rooms and presentations; high contrast, for projectors) and an accent colour.
    /// The tools' own colours (scan, measure, portals, comments, the sun…) are the same in every theme; their label and
    /// text shades darken in the light theme so they read on white.
    /// </summary>
    internal static class UiTheme
    {
        #region Themed (set by Apply)

        public static uint PANEL { get; private set; }
        public static uint PANEL_STRONG { get; private set; }
        public static uint PANEL_BORDER { get; private set; }
        public static uint TEXT { get; private set; }
        public static uint TEXT_SOFT { get; private set; }
        public static uint TEXT_MUTED { get; private set; }
        public static uint TEXT_FAINT { get; private set; }
        public static uint ACCENT { get; private set; }

        /// <summary>UI round: a lighter (dark theme) or deeper (light theme) shade of the accent: focus rings, values.</summary>
        public static uint ACCENT_SOFT { get; private set; }

        /// <summary>UI round: text drawn on an accent fill (dark or white, whichever reads).</summary>
        public static uint ON_ACCENT { get; private set; }

        public static uint GOOD { get; private set; }
        public static uint DANGER { get; private set; }

        public static uint SCAN_LABEL { get; private set; }
        public static uint SCAN_TAG_TEXT { get; private set; }
        public static uint MEASURE_LABEL { get; private set; }
        public static uint MEASURE_TEXT { get; private set; }
        public static uint PORTAL_LABEL { get; private set; }
        public static uint COMMENT_LABEL { get; private set; }
        public static uint SUN_LABEL { get; private set; }
        public static uint BOOKMARK_LABEL { get; private set; }
        public static uint COORDS { get; private set; }
        public static uint TELEPORT_LABEL { get; private set; }
        public static uint HAMMER_LABEL { get; private set; }
        public static uint GIZMO_LABEL { get; private set; }
        public static uint CLONE_LABEL { get; private set; }
        public static uint PLACE_LABEL { get; private set; }

        public static uint MAP_BACKGROUND { get; private set; }
        public static uint MENU_BACKGROUND { get; private set; }
        public static uint CARD { get; private set; }
        public static uint CARD_BORDER { get; private set; }
        public static uint CONTROL { get; private set; }
        public static uint CONTROL_BORDER { get; private set; }

        /// <summary>Keyboard / gamepad focus ring and a focused row's tint (polish round P-A).</summary>
        public static uint FOCUS { get; private set; }
        public static uint HOVER { get; private set; }
        public static uint ROW_STRIPE { get; private set; }
        public static uint SCROLL_TRACK { get; private set; }
        public static uint SCROLL_THUMB { get; private set; }
        public static uint SCROLL_THUMB_HOT { get; private set; }
        public static uint DIVIDER { get; private set; }

        /// <summary>The theme in use (<see cref="Scene.UiThemes"/>).</summary>
        public static string Name { get; private set; } = Scene.UiThemes.DARK;

        /// <summary>True in the light theme (dark ink on light panels).</summary>
        public static bool IsLight { get; private set; }

        // The ink for overlays (hover tints, tracks, hairlines), the shade behind labels over the model, the accent's RGB
        private static uint _ink = 0xFFFFFF, _shade = 0x0C0E12, _accent = 0x22D3EE;

        /// <summary>UI round: the theme's overlay ink at an opacity (white on dark themes, near-black on light).</summary>
        public static uint Ink(float alpha) => Rgba.Hex(_ink, alpha);

        /// <summary>UI round: the theme's panel shade at an opacity (behind labels and cards).</summary>
        public static uint Shade(float alpha) => Rgba.Hex(_shade, alpha);

        /// <summary>UI round: the accent at an opacity (selected rows, open controls).</summary>
        public static uint Accent(float alpha) => Rgba.WithAlpha(ACCENT, alpha);

        static UiTheme() => Apply(Scene.UiThemes.DARK, Scene.UiThemes.DEFAULT_ACCENT);

        /// <summary>
        /// UI round: sets every themed colour (call before drawing; the next frame shows it). Unknown themes read as dark,
        /// a bad accent as BimGo's cyan.
        /// </summary>
        public static void Apply(string theme, string accentHex)
        {
            Name = Scene.UiThemes.Clean(theme);
            _accent = Scene.LaunchSettings.TryParseColour(accentHex, out uint rgb) ? rgb : 0x22D3EE;
            IsLight = Name == Scene.UiThemes.LIGHT;
            bool contrast = Name == Scene.UiThemes.CONTRAST;
            if (IsLight) { ApplyLight(); }
            else if (contrast) { ApplyContrast(); }
            else { ApplyDark(); }

            // The accent: as chosen on dark, deeper on light (it carries text there), lighter on high contrast
            uint accent = IsLight ? Mix(_accent, 0x000000, 0.28f) : contrast ? Mix(_accent, 0xFFFFFF, 0.2f) : _accent;
            ACCENT = Rgba.Hex(accent);
            ACCENT_SOFT = Rgba.Hex(IsLight ? Mix(_accent, 0x000000, 0.42f) : Mix(_accent, 0xFFFFFF, 0.35f));
            ON_ACCENT = Rgba.Hex(Luminance(accent) > 0.45f ? 0x06232Au : 0xFFFFFFu);
            FOCUS = contrast ? Rgba.Hex(0xFFFF00) : ACCENT_SOFT;
            SCROLL_THUMB_HOT = Rgba.WithAlpha(ACCENT_SOFT, 0.7f);
        }

        private static void ApplyDark()
        {
            _ink = 0xFFFFFF;
            _shade = 0x0C0E12;
            PANEL = Rgba.Hex(0x0C0E12, 0.74f);
            PANEL_STRONG = Rgba.Hex(0x0C0E12, 0.88f);
            PANEL_BORDER = Rgba.Hex(0xFFFFFF, 0.12f);
            TEXT = Rgba.Hex(0xF4F5F7);
            TEXT_SOFT = Rgba.Hex(0xC9CDD3);
            TEXT_MUTED = Rgba.Hex(0xA1A7B0);
            TEXT_FAINT = Rgba.Hex(0x8A919B);
            GOOD = Rgba.Hex(0x86EFAC);
            DANGER = Rgba.Hex(0xFCA5A5);
            BrightLabels();
            MAP_BACKGROUND = Rgba.Hex(0x14171C);
            MENU_BACKGROUND = Rgba.Hex(0x101318, 0.94f);
            CARD = Rgba.Hex(0x181C22);
            CARD_BORDER = Rgba.Hex(0xFFFFFF, 0.08f);
            CONTROL = Rgba.Hex(0x0F1216);
            CONTROL_BORDER = Rgba.Hex(0xFFFFFF, 0.2f);
            HOVER = Rgba.Hex(0xFFFFFF, 0.08f);
            ROW_STRIPE = Rgba.Hex(0xFFFFFF, 0.03f);
            SCROLL_TRACK = Rgba.Hex(0xFFFFFF, 0.05f);
            SCROLL_THUMB = Rgba.Hex(0xFFFFFF, 0.28f);
            DIVIDER = Rgba.Hex(0xFFFFFF, 0.1f);
        }

        private static void ApplyLight()
        {
            _ink = 0x0F172A;
            _shade = 0xF8FAFC;
            PANEL = Rgba.Hex(0xF8FAFC, 0.86f);
            PANEL_STRONG = Rgba.Hex(0xFFFFFF, 0.96f);
            PANEL_BORDER = Rgba.Hex(0x0F172A, 0.14f);
            TEXT = Rgba.Hex(0x0F172A);
            TEXT_SOFT = Rgba.Hex(0x334155);
            TEXT_MUTED = Rgba.Hex(0x475569);
            TEXT_FAINT = Rgba.Hex(0x64748B);
            GOOD = Rgba.Hex(0x15803D);
            DANGER = Rgba.Hex(0xB91C1C);
            SCAN_LABEL = Rgba.Hex(0x0E7490);
            SCAN_TAG_TEXT = Rgba.Hex(0x06232A);
            MEASURE_LABEL = Rgba.Hex(0xB45309);
            MEASURE_TEXT = Rgba.Hex(0x92400E);
            PORTAL_LABEL = Rgba.Hex(0x1D4ED8);
            COMMENT_LABEL = Rgba.Hex(0x6D28D9);
            SUN_LABEL = Rgba.Hex(0xB45309);
            BOOKMARK_LABEL = Rgba.Hex(0x0369A1);
            COORDS = Rgba.Hex(0x1F2937);
            TELEPORT_LABEL = Rgba.Hex(0x047857);
            HAMMER_LABEL = Rgba.Hex(0xC2410C);
            GIZMO_LABEL = Rgba.Hex(0xBE185D);
            CLONE_LABEL = Rgba.Hex(0x4D7C0F);
            PLACE_LABEL = Rgba.Hex(0xB45309);
            MAP_BACKGROUND = Rgba.Hex(0xE8ECF1);
            MENU_BACKGROUND = Rgba.Hex(0xF1F5F9, 0.96f);
            CARD = Rgba.Hex(0xFFFFFF);
            CARD_BORDER = Rgba.Hex(0x0F172A, 0.10f);
            CONTROL = Rgba.Hex(0xF1F5F9);
            CONTROL_BORDER = Rgba.Hex(0x0F172A, 0.22f);
            HOVER = Rgba.Hex(0x0F172A, 0.06f);
            ROW_STRIPE = Rgba.Hex(0x0F172A, 0.03f);
            SCROLL_TRACK = Rgba.Hex(0x0F172A, 0.06f);
            SCROLL_THUMB = Rgba.Hex(0x0F172A, 0.3f);
            DIVIDER = Rgba.Hex(0x0F172A, 0.1f);
        }

        private static void ApplyContrast()
        {
            _ink = 0xFFFFFF;
            _shade = 0x000000;
            PANEL = Rgba.Hex(0x000000, 0.92f);
            PANEL_STRONG = Rgba.Hex(0x000000, 0.98f);
            PANEL_BORDER = Rgba.Hex(0xFFFFFF, 0.6f);
            TEXT = Rgba.Hex(0xFFFFFF);
            TEXT_SOFT = Rgba.Hex(0xF2F2F2);
            TEXT_MUTED = Rgba.Hex(0xE0E0E0);
            TEXT_FAINT = Rgba.Hex(0xCCCCCC);
            GOOD = Rgba.Hex(0x4ADE80);
            DANGER = Rgba.Hex(0xFF7A7A);
            BrightLabels();
            MAP_BACKGROUND = Rgba.Hex(0x000000);
            MENU_BACKGROUND = Rgba.Hex(0x000000, 0.97f);
            CARD = Rgba.Hex(0x000000);
            CARD_BORDER = Rgba.Hex(0xFFFFFF, 0.5f);
            CONTROL = Rgba.Hex(0x000000);
            CONTROL_BORDER = Rgba.Hex(0xFFFFFF, 0.75f);
            HOVER = Rgba.Hex(0xFFFFFF, 0.2f);
            ROW_STRIPE = Rgba.Hex(0xFFFFFF, 0.06f);
            SCROLL_TRACK = Rgba.Hex(0xFFFFFF, 0.15f);
            SCROLL_THUMB = Rgba.Hex(0xFFFFFF, 0.7f);
            DIVIDER = Rgba.Hex(0xFFFFFF, 0.5f);
        }

        /// <summary>The label and text shades for dark backgrounds (as BimGo always had them).</summary>
        private static void BrightLabels()
        {
            SCAN_LABEL = Rgba.Hex(0x67E8F9);
            SCAN_TAG_TEXT = Rgba.Hex(0x06232A);
            MEASURE_LABEL = Rgba.Hex(0xFCD34D);
            MEASURE_TEXT = Rgba.Hex(0xFDE68A);
            PORTAL_LABEL = Rgba.Hex(0x93C5FD);
            COMMENT_LABEL = Rgba.Hex(0xC4B5FD);
            SUN_LABEL = Rgba.Hex(0xFDE68A);
            BOOKMARK_LABEL = Rgba.Hex(0x7DD3FC);
            COORDS = Rgba.Hex(0xE5E7EB);
            TELEPORT_LABEL = Rgba.Hex(0x6EE7B7);
            HAMMER_LABEL = Rgba.Hex(0xFDBA74);
            GIZMO_LABEL = Rgba.Hex(0xF9A8D4);
            CLONE_LABEL = Rgba.Hex(0xBEF264);
            PLACE_LABEL = Rgba.Hex(0xFCD34D);
        }

        /// <summary>Mixes two RGB colours (t = 0: a, 1: b).</summary>
        private static uint Mix(uint a, uint b, float t)
        {
            static int C(uint v, int shift) => (int)((v >> shift) & 0xFF);
            int Lerp(int shift) => (int)MathF.Round(C(a, shift) + (C(b, shift) - C(a, shift)) * t);
            return (uint)((Lerp(16) << 16) | (Lerp(8) << 8) | Lerp(0));
        }

        /// <summary>Relative lightness (0–1) of an RGB colour.</summary>
        private static float Luminance(uint rgb) => (0.299f * ((rgb >> 16) & 0xFF) + 0.587f * ((rgb >> 8) & 0xFF) + 0.114f * (rgb & 0xFF)) / 255f;

        #endregion

        #region The tools' own colours (every theme)

        public static readonly uint SCAN = Rgba.Hex(0x22D3EE);
        public static readonly uint MEASURE = Rgba.Hex(0xFBBF24);

        public static readonly uint PORTAL_BLUE = Rgba.Hex(0x3B82F6);
        public static readonly uint PORTAL_BLUE_LIGHT = Rgba.Hex(0x60A5FA);
        public static readonly uint PORTAL_BLUE_DARK = Rgba.Hex(0x1E3A8A);
        public static readonly uint PORTAL_RED = Rgba.Hex(0xEF4444);
        public static readonly uint PORTAL_RED_LIGHT = Rgba.Hex(0xF87171);
        public static readonly uint PORTAL_RED_DARK = Rgba.Hex(0x7F1D1D);

        public static readonly uint COMMENT = Rgba.Hex(0xA78BFA);

        /// <summary>Comment status colours: open (the comment colour), in progress (amber), closed (green).</summary>
        public static readonly uint STATUS_OPEN = Rgba.Hex(0xA78BFA);
        public static readonly uint STATUS_PROGRESS = Rgba.Hex(0xFBBF24);
        public static readonly uint STATUS_CLOSED = Rgba.Hex(0x4ADE80);

        /// <summary>The colour of a comment status.</summary>
        public static uint StatusColour(string status) => status switch
        {
            Format.CommentStatus.IN_PROGRESS => STATUS_PROGRESS,
            Format.CommentStatus.CLOSED => STATUS_CLOSED,
            _ => STATUS_OPEN
        };

        public static readonly uint SUN = Rgba.Hex(0xFBBF24);
        public static readonly uint BOOKMARK = Rgba.Hex(0x38BDF8);
        public static readonly uint TELEPORT = Rgba.Hex(0x34D399);
        public static readonly uint TELEPORT_BLOCKED = Rgba.Hex(0xF87171);
        public static readonly uint HAMMER = Rgba.Hex(0xFB923C);
        public static readonly uint HAMMER_PRIMED = Rgba.Hex(0xEF4444);
        public static readonly uint GIZMO = Rgba.Hex(0xF472B6);
        public static readonly uint CLONE = Rgba.Hex(0xA3E635);
        public static readonly uint PLACE = Rgba.Hex(0xFBBF24);

        public static readonly uint AXIS_X = Rgba.Hex(0xF87171);
        public static readonly uint AXIS_Y = Rgba.Hex(0x4ADE80);
        public static readonly uint AXIS_Z = Rgba.Hex(0x60A5FA);

        #endregion

        // ---- UI round: corner radii (unscaled pixels): cards and panels, and buttons / controls

        /// <summary>Cards, panels and docks.</summary>
        public const float RADIUS = 10f;

        /// <summary>Buttons, inputs, chips and rows.</summary>
        public const float RADIUS_SMALL = 6f;

        // ---- Spacing tokens (polish round P-A1): unscaled pixels, scaled by GameSession.S. The panel shell and its
        // widgets read these instead of per-panel numbers.

        /// <summary>Panel card widths: standard, wide (two panes or a grid), side (the model shows on the left).</summary>
        public const float PANEL_WIDTH = 1000f, PANEL_WIDTH_WIDE = 1180f, PANEL_WIDTH_SIDE = 780f;

        /// <summary>Space between the window edge and a panel card (small windows, then from 900 px high).</summary>
        public const float PANEL_MARGIN = 16f, PANEL_MARGIN_LARGE = 40f;

        /// <summary>A card's inner padding: left / right, and top / bottom.</summary>
        public const float PANEL_PAD = 24f, PANEL_PAD_Y = 16f;

        /// <summary>The header: title and sub-line (the divider sits at its foot).</summary>
        public const float HEADER_HEIGHT = 56f;

        /// <summary>The footer's button height, and the notice / hint strip above it.</summary>
        public const float FOOTER_HEIGHT = 40f, NOTICE_HEIGHT = 24f;

        /// <summary>Footer slot widths: BACK and CLOSE, and the bounds of a centre action.</summary>
        public const float FOOTER_SIDE_WIDTH = 132f, FOOTER_ACTION_MIN = 96f, FOOTER_ACTION_MAX = 230f;

        /// <summary>Standard gaps.</summary>
        public const float GAP = 12f, GAP_SMALL = 8f, GAP_LARGE = 20f;

        /// <summary>Row heights: a list row, a compact row (settings, checkboxes).</summary>
        public const float ROW_HEIGHT = 44f, ROW_COMPACT = 28f;

        /// <summary>Control heights: buttons, small buttons, inputs / segmented / dropdowns.</summary>
        public const float BUTTON_HEIGHT = 40f, SMALL_BUTTON_HEIGHT = 30f, CONTROL_HEIGHT = 32f;

        /// <summary>A label column beside a control (forms).</summary>
        public const float LABEL_WIDTH = 140f;

        /// <summary>A scroll region's bar, the gutter kept for it, and one wheel notch.</summary>
        public const float SCROLLBAR_WIDTH = 6f, SCROLL_GUTTER = 14f, WHEEL_STEP = 72f;

        /// <summary>The cursor-mode docks (sun, study, section, grids, clash dock): width and header height (P-A2).</summary>
        public const float DOCK_WIDTH = 340f, DOCK_HEADER_HEIGHT = 34f;

        /// <summary>The wider dock (the sun and daylight study).</summary>
        public const float DOCK_WIDTH_WIDE = 380f;

        /// <summary>A dock's inner padding: left / right, and top / bottom.</summary>
        public const float DOCK_PAD = 16f, DOCK_PAD_Y = 10f;

        /// <summary>A dock's footer buttons, and its notice strip (two small lines) above them.</summary>
        public const float DOCK_FOOTER_HEIGHT = 30f, DOCK_NOTICE_HEIGHT = 32f;

        /// <summary>
        /// The right-hand column's edge margin and the gap under the minimap. A window whose column under the minimap is
        /// shorter than <see cref="DOCK_MIN_HEIGHT"/> puts the dock at the top instead (the minimap hides while it's open).
        /// </summary>
        public const float COLUMN_MARGIN = 20f, COLUMN_GAP = 12f, DOCK_MIN_HEIGHT = 320f;

        /// <summary>The minimap's size (the right column's top), and the sun icon's (the column's foot).</summary>
        public const float MINIMAP_WIDTH = 220f, MINIMAP_HEIGHT = 208f, SUN_ICON_SIZE = 52f;

        /// <summary>The left column's panels: status and coordinates (their heights), and its width.</summary>
        public const float STATUS_HEIGHT = 146f, COORDS_HEIGHT = 100f, INFO_WIDTH = 250f;

        /// <summary>A legend collapsed to its title row (the F1 sheet is open, or the column ran out of room).</summary>
        public const float LEGEND_COLLAPSED_HEIGHT = 30f;
    }
}
