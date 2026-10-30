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
    /// The site map (site photos round): the model from straight above with the site photos on it.
    /// <list type="bullet">
    /// <item><b>Looks</b>: aerial (everything seen from above: roofs, roads, landscape; the default) or a floor plan cut
    /// 1.2 m above a level (walls, room outlines). Both draw the model with the minimap's plan shading.</item>
    /// <item><b>Marks</b>: each placed photo as a small arrow the way it looks (the current one larger), the groups' names
    /// at their photos' middle, the rooms' names on the level shown (when zoomed in enough to read them), and in the
    /// simple viewer where the camera is.</item>
    /// <item><b>Where</b>: a small map in the corner of the photo view (centred on the photo; a click on a photo opens it,
    /// EXPAND or a click elsewhere opens the big map), the big map (the viewer's map button, SITE PHOTOS → SITE MAP, or
    /// EXPAND): drag pans, the wheel zooms at the cursor, a click on a photo opens it, a double-click elsewhere goes there
    /// (the viewer), AERIAL / PLAN, the level, FIT SITE, CLOSE. The walkthrough's minimap shows the photos too.</item>
    /// </list>
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>The photo view's small map: its width across at first (m); the wheel zooms it.</summary>
        private const float SITE_MINI_ACROSS = 150f;

        /// <summary>One wheel notch on a map: this much nearer.</summary>
        private const float SITE_MAP_WHEEL = 0.82f;

        private float _sitePhotoMapAcross = SITE_MINI_ACROSS;

        private bool _siteMapOpen;
        private bool _siteMapAerial = true;
        private int _siteMapLevel;
        private Vector2 _siteMapCentre;
        private float _siteMapAcross = 200f;
        private Vector4 _siteMapRect, _siteMapBarRect;
        private int _siteMapHover = -1;

        // Dragging the big map: the button down, where, whether it moved; the last click (double-clicks)
        private bool _siteMapPressed, _siteMapDragged;
        private Vector2 _siteMapPressAt, _siteMapMouse;
        private float _siteMapLastClick = -10f;

        // The groups' middles and the rooms' labels (rebuilt when the photos change; once)
        private readonly List<(string Group, Vector2 Centre)> _siteGroupCentres = new();
        private int _siteGroupCentresRevision = -1;
        private string[] _siteRoomLabels;

        private readonly Vector4[] _siteMapPlanes = new Vector4[6];

        #endregion

        #region Open / close

        /// <summary>Opens the big map centred on a point (scene-local), on that point's level.</summary>
        private void OpenSiteMap(Vector3 centre, float across)
        {
            _siteMapOpen = true;
            _siteMapCentre = new Vector2(centre.X, centre.Y);
            _siteMapAcross = Math.Clamp(across, 20f, SiteMapFitAcross() * 1.5f);
            _siteMapLevel = Scene.Levels.Length > 0 ? LevelIndexAt(centre.Z - SITE_PHOTO_EYE) : 0;
            _siteMapPressed = _siteMapDragged = false;
            _siteMapMouse = _window.Input.MousePosition;
            if (_viewer) { ViewerCancelDrag(); }
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            Sound.Play(SoundId.UiClick);
        }

        /// <summary>Opens the big map on the whole site (the viewer's map button, SITE PHOTOS → SITE MAP).</summary>
        private void OpenSiteMapOnSite()
        {
            Vector3 eye = Camera.Position;
            Aabb bounds = Scene.Bounds;
            OpenSiteMap(new Vector3(bounds.Center.X, bounds.Center.Y, eye.Z), SiteMapFitAcross());
            _siteMapLevel = Scene.Levels.Length > 0 ? LevelIndexAt(_player.Feet.Z) : 0;
        }

        /// <summary>The walkthrough's minimap expanded (Shift+Tab): the site map around you, on your level.</summary>
        private void OpenWalkthroughSiteMap()
        {
            OpenSiteMap(_player.Feet + new Vector3(0f, 0f, SITE_PHOTO_EYE), 160f);
        }

        private void CloseSiteMap()
        {
            if (!_siteMapOpen) { return; }
            _siteMapOpen = false;
            _siteMapPressed = false;
            _window.Input.ReleaseAll();
            if (!_viewer && !_paused && _window.IsActive && !CursorModeOpen) { _window.SetCaptured(true); }
        }

        /// <summary>The width across (m) that shows the whole model (and every placed photo) with a margin.</summary>
        private float SiteMapFitAcross()
        {
            Aabb bounds = Scene.Bounds;
            float minX = bounds.Min.X, maxX = bounds.Max.X, minY = bounds.Min.Y, maxY = bounds.Max.Y;
            if (SitePhotos != null)
            {
                foreach (SitePhoto photo in SitePhotos.Photos)
                {
                    if (!photo.IsPlaced) { continue; }
                    minX = MathF.Min(minX, photo.Local.X);
                    maxX = MathF.Max(maxX, photo.Local.X);
                    minY = MathF.Min(minY, photo.Local.Y);
                    maxY = MathF.Max(maxY, photo.Local.Y);
                }
            }
            float aspect = _siteMapRect.Z > 0f && _siteMapRect.W > 0f ? _siteMapRect.Z / _siteMapRect.W : (float)_window.Width / Math.Max(_window.Height, 1);
            return MathF.Max(MathF.Max(maxX - minX, (maxY - minY) * aspect) * 1.15f, 30f);
        }

        #endregion

        #region Drawing

        /// <summary>
        /// Draws the model from above into a rectangle of the window (what the UI queued so far is drawn first, under it).
        /// </summary>
        private void DrawSiteMapPlan(float x, float y, float w, float h, Vector2 centre, float across, bool aerial, int level)
        {
            int width = _window.Width, height = _window.Height;
            _ui.Flush(width, height);
            int vx = (int)x, vy = height - (int)(y + h), vw = Math.Max(1, (int)w), vh = Math.Max(1, (int)h);

            Gl.Enable(Gl.SCISSOR_TEST);
            Gl.Scissor(vx, vy, vw, vh);
            Gl.Viewport(vx, vy, vw, vh);
            Vector4 background = Rgba.ToVector(UiTheme.MAP_BACKGROUND);
            Gl.ClearColor(background.X, background.Y, background.Z, 1f);
            Gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);

            Aabb bounds = Scene.Bounds;
            float elevation = Scene.Levels.Length > 0 ? Scene.Levels[Math.Clamp(level, 0, Scene.Levels.Length - 1)].Elevation : bounds.Min.Z;
            float top = aerial ? bounds.Max.Z + 50f : elevation + 60f;
            float depth = aerial ? bounds.Max.Z - MathF.Min(bounds.Min.Z, _groundZ) + 120f : 200f;
            var eye = new Vector3(centre.X, centre.Y, top);
            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY);
            Matrix4x4 projection = FpsCamera.Orthographic(across, across * h / MathF.Max(w, 1f), 1f, depth);
            Matrix4x4 viewProjection = view * projection;
            FpsCamera.ExtractPlanes(viewProjection, _siteMapPlanes);

            var p = new SceneDrawParams
            {
                ViewProjection = viewProjection,
                Planes = _siteMapPlanes,
                Eye = eye,
                Whitecard = _whitecard,
                Realistic = _realistic,
                Tint = _tintMode,
                Plan = true,
                ClipZ = aerial ? new Vector2(-1e7f, 1e7f) : new Vector2(elevation - 0.3f, elevation + 1.2f),
                FogDensity = 0f
            };
            Gl.Enable(Gl.DEPTH_TEST);
            _renderer.DrawStatic(p, _groupVisible, transparent: false);
            _renderer.DrawDynamic(p, Dynamics, transparent: false);

            Gl.Disable(Gl.SCISSOR_TEST);
            Gl.Viewport(0, 0, width, height);
        }

        /// <summary>A scene-local point on the map (window pixels).</summary>
        private static Vector2 SiteMapPoint(Vector3 world, Vector4 rect, Vector2 centre, float across)
        {
            float perMetre = rect.Z / across;
            return new Vector2(rect.X + rect.Z * 0.5f + (world.X - centre.X) * perMetre, rect.Y + rect.W * 0.5f - (world.Y - centre.Y) * perMetre);
        }

        /// <summary>A window pixel on the map as a scene-local X / Y.</summary>
        private static Vector2 SiteMapWorld(Vector2 pixel, Vector4 rect, Vector2 centre, float across)
        {
            float perPixel = across / rect.Z;
            return new Vector2(centre.X + (pixel.X - rect.X - rect.Z * 0.5f) * perPixel, centre.Y - (pixel.Y - rect.Y - rect.W * 0.5f) * perPixel);
        }

        /// <summary>
        /// The map's marks over the plan: room outlines (plan) and names, the groups' names, the photos as arrows (the
        /// current one larger), the viewer's camera; the photo under <paramref name="pointer"/> outlined, with its
        /// thumbnail. Returns that photo's index, or -1.
        /// </summary>
        private int DrawSiteMapMarks(FontAtlas f, Vector4 rect, Vector2 centre, float across, bool aerial, int level, SitePhoto current, Vector2 pointer, bool pointable,
            bool showCamera)
        {
            float metresPerPixel = across / rect.Z;
            _ui.PushClip(rect.X, rect.Y, rect.Z, rect.W);

            // Rooms on the level (outlines in plan, names once they can be read)
            if (Scene.Rooms.Length > 0 && Scene.Levels.Length > 0)
            {
                _siteRoomLabels ??= BuildRoomLabels();
                for (int i = 0; i < Scene.Rooms.Length; i++)
                {
                    RoomInfo room = Scene.Rooms[i];
                    if (LevelIndexAt(room.BottomZ) != level) { continue; }
                    float roomW = (room.Max.X - room.Min.X) / metresPerPixel;
                    if (roomW < S(18)) { continue; }
                    Vector2 a = SiteMapPoint(new Vector3(room.Min, 0f), rect, centre, across), b = SiteMapPoint(new Vector3(room.Max, 0f), rect, centre, across);
                    if (MathF.Max(a.X, b.X) < rect.X || MathF.Min(a.X, b.X) > rect.X + rect.Z || MathF.Max(a.Y, b.Y) < rect.Y || MathF.Min(a.Y, b.Y) > rect.Y + rect.W) { continue; }
                    if (!aerial && room.Loops != null)
                    {
                        foreach (Vector2[] loop in room.Loops)
                        {
                            if (loop == null || loop.Length < 2) { continue; }
                            for (int k = 0; k < loop.Length; k++)
                            {
                                Vector2 p0 = SiteMapPoint(new Vector3(loop[k], 0f), rect, centre, across);
                                Vector2 p1 = SiteMapPoint(new Vector3(loop[(k + 1) % loop.Length], 0f), rect, centre, across);
                                _ui.Line(p0.X, p0.Y, p1.X, p1.Y, MathF.Max(1f, UiScale), Rgba.Hex(0x7DD3FC, 0.35f));
                            }
                        }
                    }
                    string label = _siteRoomLabels[i];
                    float labelW = UiBatch.Measure(f.Small, label);
                    if (labelW > roomW * 1.6f) { continue; }
                    float lx = (a.X + b.X) * 0.5f, ly = (a.Y + b.Y) * 0.5f - S(7);
                    _ui.Rect(lx - labelW * 0.5f - S(4), ly - S(1), labelW + S(8), S(17), Rgba.Hex(0x0C0E12, 0.55f));
                    _ui.TextCentred(f.Small, lx, ly, label, UiTheme.TEXT_SOFT);
                }
            }

            int hovered = -1;
            if (SitePhotos != null && SitePhotos.Photos.Count > 0)
            {
                // Groups' names at their photos' middle, in their colours (faint when another group is the one shown)
                foreach ((string group, Vector2 middle) in SiteGroupCentres())
                {
                    Vector2 g = SiteMapPoint(new Vector3(middle, 0f), rect, centre, across);
                    if (g.X < rect.X - S(80) || g.X > rect.X + rect.Z + S(80) || g.Y < rect.Y - S(30) || g.Y > rect.Y + rect.W + S(30)) { continue; }
                    bool inFocus = _siteFocusGroup == null || string.Equals(group, _siteFocusGroup, StringComparison.OrdinalIgnoreCase);
                    float gw = UiBatch.Measure(f.Small, group) + S(10);
                    uint colour = SiteGroupColour(group);
                    _ui.Rect(g.X - gw * 0.5f, g.Y - S(26), gw, S(17), Rgba.Hex(0x0C0E12, inFocus ? 0.75f : 0.35f));
                    _ui.Rect(g.X - gw * 0.5f, g.Y - S(10), gw, MathF.Max(1.5f, S(2)), Rgba.WithAlpha(colour, inFocus ? 1f : 0.3f));
                    _ui.TextCentred(f.Small, g.X, g.Y - S(25), group, Rgba.WithAlpha(colour, inFocus ? 1f : 0.35f));
                }

                // The photos: arrows the way they look; the nearest to the pointer within reach is hovered
                float best = S(11) * S(11);
                List<SitePhoto> photos = SitePhotos.Photos;
                for (int i = 0; i < photos.Count; i++)
                {
                    SitePhoto photo = photos[i];
                    if (!photo.IsPlaced) { continue; }
                    Vector2 at = SiteMapPoint(photo.Local, rect, centre, across);
                    if (at.X < rect.X - S(10) || at.X > rect.X + rect.Z + S(10) || at.Y < rect.Y - S(10) || at.Y > rect.Y + rect.W + S(10)) { continue; }
                    bool isCurrent = ReferenceEquals(photo, current);
                    uint colour = isCurrent ? UiTheme.ACCENT : SitePhotoColour(photo);
                    if (!isCurrent && !SitePhotoInFocus(photo)) { colour = Rgba.WithAlpha(colour, 0.3f); }
                    DrawMapCamera(at, photo.Yaw, photo.Fov, isCurrent ? S(8) : S(6), colour);
                    if (isCurrent) { _ui.Ring(at.X, at.Y, S(13), MathF.Max(1.5f, S(1.5f)), UiTheme.ACCENT, 20); }
                    float d = Vector2.DistanceSquared(at, pointer);
                    if (pointable && d < best)
                    {
                        best = d;
                        hovered = i;
                    }
                }
            }

            // The viewer's camera
            if (showCamera)
            {
                Vector2 me = SiteMapPoint(Camera.Position, rect, centre, across);
                float halfFov = Camera.HorizontalFovDegrees * MathF.PI / 360f;
                _ui.Wedge(me.X, me.Y, S(44), -Camera.Yaw - halfFov, -Camera.Yaw + halfFov, Rgba.WithAlpha(UiTheme.TEXT, 0.12f));
                DrawMapArrow(me, Camera.Yaw, S(9), UiTheme.TEXT);
            }
            _ui.PopClip();

            if (hovered >= 0)
            {
                SitePhoto photo = SitePhotos.Photos[hovered];
                Vector2 at = SiteMapPoint(photo.Local, rect, centre, across);
                _ui.Ring(at.X, at.Y, S(10), S(2), UiTheme.TEXT, 20);
                int uploads = 1;
                uint thumb = SiteThumbTexture(photo, ref uploads);
                float tw = S(150), th = tw / Math.Clamp(photo.Aspect, 0.5f, 2f);
                string label = string.IsNullOrEmpty(photo.Group) ? photo.Name : photo.Group;
                float tx = Math.Clamp(at.X + S(14), rect.X, rect.X + rect.Z - tw - S(12)), ty = Math.Clamp(at.Y - th - S(30), rect.Y, rect.Y + rect.W - th - S(30));
                _ui.Panel(tx, ty, tw + S(12), th + S(32), UiTheme.PANEL_STRONG, SitePhotoColour(photo));
                if (thumb != 0) { _ui.Image(thumb, tx + S(6), ty + S(6), tw, th, _window.Width, _window.Height); }
                _ui.TextWrapped(f.Small, tx + S(6), ty + th + S(11), tw, label, UiTheme.TEXT, maxLines: 1);
            }
            return hovered;
        }

        /// <summary>
        /// A site photo on a map: a small camera, with a faint wedge the way it looks and as wide as its lens (north up,
        /// screen Y down). The player (the viewer's camera) is an arrow instead.
        /// </summary>
        private void DrawMapCamera(Vector2 at, float yaw, float fov, float size, uint colour)
        {
            float half = Math.Clamp(fov, 10f, 150f) * MathF.PI / 360f;
            _ui.Wedge(at.X, at.Y, size * 3.2f, -yaw - half, -yaw + half, Rgba.WithAlpha(colour, 0.22f), 10);
            float w = size * 1.7f, h = size * 1.15f;
            _ui.Rect(at.X - w * 0.5f - S(1), at.Y - h * 0.5f - S(1), w + S(2), h + S(2), Rgba.Hex(0x0C0E12, 0.8f));
            _ui.Rect(at.X - w * 0.5f, at.Y - h * 0.5f, w, h, colour);
            _ui.Rect(at.X - w * 0.18f, at.Y - h * 0.5f - size * 0.3f, w * 0.36f, size * 0.3f, colour); // the viewfinder bump
            _ui.Circle(at.X, at.Y, h * 0.32f, Rgba.Hex(0x0C0E12, 0.9f), 10); // the lens
        }

        /// <summary>A small arrow on a map: the way a yaw looks (north up, screen Y down).</summary>
        private void DrawMapArrow(Vector2 at, float yaw, float size, uint colour)
        {
            float angle = -yaw;
            var forward = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var side = new Vector2(-forward.Y, forward.X);
            Vector2 tip = at + forward * size, left = at - forward * size * 0.6f + side * size * 0.7f, right = at - forward * size * 0.6f - side * size * 0.7f;
            Vector2 notch = at - forward * size * 0.2f;
            _ui.Circle(at.X, at.Y, size * 0.75f, Rgba.Hex(0x0C0E12, 0.6f), 12);
            _ui.Triangle(tip.X, tip.Y, left.X, left.Y, notch.X, notch.Y, colour);
            _ui.Triangle(tip.X, tip.Y, notch.X, notch.Y, right.X, right.Y, colour);
        }

        private List<(string Group, Vector2 Centre)> SiteGroupCentres()
        {
            if (_siteGroupCentresRevision == SitePhotos.Revision) { return _siteGroupCentres; }
            _siteGroupCentresRevision = SitePhotos.Revision;
            _siteGroupCentres.Clear();
            foreach (string group in SitePhotoGroups())
            {
                Vector2 sum = Vector2.Zero;
                int n = 0;
                foreach (SitePhoto photo in SitePhotos.Photos)
                {
                    if (!photo.IsPlaced || !string.Equals(photo.Group, group, StringComparison.OrdinalIgnoreCase)) { continue; }
                    sum += new Vector2(photo.Local.X, photo.Local.Y);
                    n++;
                }
                if (n > 0) { _siteGroupCentres.Add((group, sum / n)); }
            }
            return _siteGroupCentres;
        }

        private string[] BuildRoomLabels()
        {
            var labels = new string[Scene.Rooms.Length];
            for (int i = 0; i < labels.Length; i++) { labels[i] = RoomLabel(i); }
            return labels;
        }

        #endregion

        #region The small map (photo view)

        /// <summary>
        /// The photo view's small map (top right): centred on the photo. A click on a photo opens it; EXPAND or a click
        /// elsewhere on it opens the big map.
        /// </summary>
        private void BuildSitePhotoMiniMap(FontAtlas f, InputState input, int width)
        {
            SitePhoto photo = _sitePhotoView;
            if (photo == null || !photo.IsPlaced) { return; }
            float w = S(260), h = S(220), x = width - S(UiTheme.COLUMN_MARGIN) - w, y = S(UiTheme.COLUMN_MARGIN);
            float header = S(28);
            var rect = new Vector4(x + S(6), y + header, w - S(12), h - header - S(6));
            var centre = new Vector2(photo.Local.X, photo.Local.Y);
            int level = Scene.Levels.Length > 0 ? LevelIndexAt(photo.Local.Z - SITE_PHOTO_EYE) : 0;
            Vector2 mouse = input.MousePosition;
            bool inside = Inside(rect, mouse);
            if (inside) { _sitePhotoMapAcross = ZoomMiniMap(input, _sitePhotoMapAcross); }
            float across = _sitePhotoMapAcross;

            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
            _ui.Text(f.Small, x + S(10), y + S(8), "MAP", UiTheme.TEXT_MUTED, S(1f));
            float ew = MathF.Max(S(78), UiBatch.Measure(f.Body, HINT_MAP_EXPAND.Text) + S(16));
            bool expand = MenuButton(f, x + w - S(6) - ew, y + S(4), ew, HINT_MAP_EXPAND.Text, false, false, height: S(20), compact: true);
            DrawSiteMapPlan(rect.X, rect.Y, rect.Z, rect.W, centre, across, _siteMapAerial, level);
            int hovered = DrawSiteMapMarks(f, rect, centre, across, _siteMapAerial, level, photo, mouse, inside, showCamera: false);
            _sitePhotoMapRect = new Vector4(x, y, w, h);

            if (expand)
            {
                OpenSiteMap(photo.Local, across * 2f);
                return;
            }
            if (inside && input.LeftPressed)
            {
                input.ConsumeClicks();
                if (hovered >= 0 && !ReferenceEquals(SitePhotos.Photos[hovered], photo)) { OpenSitePhoto(SitePhotos.Photos[hovered], adjust: false); }
                else { OpenSiteMap(photo.Local, across * 2f); }
            }
        }

        /// <summary>The wheel over a small map: nearer or farther (the width across, clamped to the site).</summary>
        private float ZoomMiniMap(InputState input, float across)
        {
            if (input.Wheel == 0) { return across; }
            across = Math.Clamp(across * MathF.Pow(SITE_MAP_WHEEL, input.Wheel), 15f, MathF.Max(SiteMapFitAcross() * 1.5f, 60f));
            input.ConsumeWheel();
            return across;
        }

        #endregion

        #region The viewer's corner map

        private bool _viewerMapShown = true;
        private Vector4 _viewerMapRect;

        // The wheel's zoom on the corner map (× the width it picks itself)
        private float _viewerMapZoom = 1f;

        /// <summary>
        /// The simple viewer's corner map (top right; under the tool panel when one is open): centred on what the camera
        /// looks at (orbiting) or where you stand (walking), wider the higher the camera, the wheel over it zooms; the
        /// photos and where you are. A click on a photo opens it; elsewhere, the site map there. Tab hides / shows it,
        /// Shift+Tab (or EXPAND) opens it as the site map.
        /// </summary>
        private void BuildViewerMiniMap(FontAtlas f, InputState input, int width, int height)
        {
            float w = S(290), h = S(230), header = S(26);
            float x = width - S(UiTheme.COLUMN_MARGIN) - w, y = S(UiTheme.COLUMN_MARGIN);
            if (_viewerPanelRect.W > 0f && _viewerPanelRect.X < x + w) { y = _viewerPanelRect.Y + _viewerPanelRect.W + S(UiTheme.GAP_SMALL); } // under the tool panel
            if (_sectionOpen && _sectionPanelRect.W > 0f && _sectionPanelRect.X + _sectionPanelRect.Z > x && _sectionPanelRect.Y < y + h)
            {
                y = MathF.Max(y, _sectionPanelRect.Y + _sectionPanelRect.W + S(UiTheme.GAP_SMALL)); // under the section box's dock
            }
            float bottom = _viewerBarRect.W > 0f ? _viewerBarRect.Y - S(12) : height - S(24);
            if (!_viewerMapShown || y + h > bottom || _viewerTitleRect.X + _viewerTitleRect.Z > x - S(8) && y < _viewerTitleRect.Y + _viewerTitleRect.W)
            {
                _viewerMapRect = Vector4.Zero; // hidden, or no room for it (a small window)
                return;
            }
            _viewerMapRect = new Vector4(x, y, w, h);

            // What it shows: walking, around the player; orbiting, around what the middle of the view looks at
            Vector3 focus = _viewerWalk ? _player.Feet + new Vector3(0f, 0f, SITE_PHOTO_EYE) : Camera.Position;
            float across = 90f;
            if (!_viewerWalk)
            {
                if (Pick(Camera.Position, Camera.Forward, 5000f, out RayHit hit)) { focus = hit.Point + new Vector3(0f, 0f, SITE_PHOTO_EYE); }
                across = Math.Clamp((Camera.Position.Z - _groundZ) * 2.2f, 80f, MathF.Max(SiteMapFitAcross(), 80f));
            }
            int level = Scene.Levels.Length > 0 ? LevelIndexAt(focus.Z - SITE_PHOTO_EYE) : 0;
            var rect = new Vector4(x + S(6), y + header, w - S(12), h - header - S(6));
            var centre = new Vector2(focus.X, focus.Y);
            Vector2 mouse = input.MousePosition;
            bool inside = Inside(rect, mouse) && _viewerPopup == VP_NONE;
            if (inside && input.Wheel != 0)
            {
                float zoomed = ZoomMiniMap(input, across * _viewerMapZoom);
                _viewerMapZoom = Math.Clamp(zoomed / across, 0.05f, 20f);
            }
            across = Math.Clamp(across * _viewerMapZoom, 15f, MathF.Max(SiteMapFitAcross() * 1.5f, 60f));

            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
            _ui.Text(f.Small, x + S(10), y + S(7), HINT_VIEWER_MAP.Text, UiTheme.TEXT_MUTED);
            float ew = MathF.Max(S(78), UiBatch.Measure(f.Body, HINT_MAP_EXPAND.Text) + S(16));
            bool expand = MenuButton(f, x + w - S(6) - ew, y + S(3), ew, HINT_MAP_EXPAND.Text, false, false, height: S(20), compact: true);
            DrawSiteMapPlan(rect.X, rect.Y, rect.Z, rect.W, centre, across, _siteMapAerial, level);
            int hovered = DrawSiteMapMarks(f, rect, centre, across, _siteMapAerial, level, null, mouse, inside, showCamera: true);

            if (expand)
            {
                OpenSiteMap(new Vector3(centre, focus.Z), across * 2f);
                return;
            }
            if (inside && input.LeftPressed)
            {
                input.ConsumeClicks();
                if (hovered >= 0) { OpenSitePhoto(SitePhotos.Photos[hovered], adjust: false); }
                else
                {
                    Vector2 there = SiteMapWorld(mouse, rect, centre, across);
                    OpenSiteMap(new Vector3(there, focus.Z), across * 1.5f);
                }
            }
        }

        private static readonly KeyHint HINT_VIEWER_MAP = new(() => $"MAP ({Key(InputAction.ViewerMap)} hides)");
        private static readonly KeyHint HINT_MAP_EXPAND = new(() => $"EXPAND {KeyUpper(InputAction.SiteMap)}");

        #endregion

        #region The big map

        /// <summary>Keys and mouse on the big map: Esc closes, drag pans, the wheel zooms at the cursor, clicks open or go.</summary>
        private void UpdateSiteMap(InputState input)
        {
            if (_window.IsCaptured) { _window.SetCaptured(false); }
            Vector2 mouse = input.MousePosition;
            Vector2 delta = mouse - _siteMapMouse;
            _siteMapMouse = mouse;
            if (input.Pressed(InputAction.Back) || input.Pressed(InputAction.SiteMap))
            {
                CloseSiteMap();
                return;
            }
            if (input.Pressed(InputAction.Fullscreen)) { _window.ToggleFullscreen(); }
            if (input.Pressed(InputAction.Screenshot)) { RequestScreenshot(); }
            if (_siteMapRect.Z <= 0f) { return; } // not drawn yet

            bool onMap = Inside(_siteMapRect, mouse) && !Inside(_siteMapBarRect, mouse);
            if (input.Wheel != 0 && onMap)
            {
                Vector2 before = SiteMapWorld(mouse, _siteMapRect, _siteMapCentre, _siteMapAcross);
                _siteMapAcross = Math.Clamp(_siteMapAcross * MathF.Pow(SITE_MAP_WHEEL, input.Wheel), 15f, SiteMapFitAcross() * 2f);
                Vector2 after = SiteMapWorld(mouse, _siteMapRect, _siteMapCentre, _siteMapAcross);
                _siteMapCentre += before - after; // the point under the cursor stays put
                input.ConsumeWheel();
            }

            if (!_siteMapPressed && onMap && (input.LeftPressed || input.RightPressed))
            {
                _siteMapPressed = true;
                _siteMapDragged = false;
                _siteMapPressAt = mouse;
            }
            if (!_siteMapPressed) { return; }
            if (!_siteMapDragged && Vector2.Distance(mouse, _siteMapPressAt) > S(VIEWER_DRAG_PIXELS)) { _siteMapDragged = true; }
            if (_siteMapDragged && delta != Vector2.Zero)
            {
                float perPixel = _siteMapAcross / _siteMapRect.Z;
                _siteMapCentre += new Vector2(-delta.X * perPixel, delta.Y * perPixel);
            }
            if (input.LeftDown || input.RightDown) { return; }

            // Let go
            _siteMapPressed = false;
            if (_siteMapDragged) { return; }
            if (_siteMapHover >= 0 && SitePhotos != null && _siteMapHover < SitePhotos.Photos.Count)
            {
                SitePhoto photo = SitePhotos.Photos[_siteMapHover];
                CloseSiteMap();
                OpenSitePhoto(photo, adjust: false);
                return;
            }
            bool second = _clock - _siteMapLastClick < VIEWER_DOUBLE_CLICK;
            _siteMapLastClick = second ? -10f : _clock;
            if (second && !SitePhotoViewOpen) { GoToSiteMapPoint(SiteMapWorld(mouse, _siteMapRect, _siteMapCentre, _siteMapAcross)); }
        }

        /// <summary>A double-click on the big map: there (the viewer orbits to it; walking, or the walkthrough, stands on the floor there).</summary>
        private void GoToSiteMapPoint(Vector2 point)
        {
            CloseSiteMap();
            if (_viewer && !_viewerWalk)
            {
                Vector3 looked = OrbitPivotAt(new Vector2(_window.Width * 0.5f, _window.Height * 0.5f));
                var shift = new Vector3(point.X - looked.X, point.Y - looked.Y, 0f);
                GlideTo(_orbitEye + shift, _orbitYaw, _orbitPitch);
                Sound.Play(SoundId.UiClick);
                return;
            }
            _player.TeleportTo(new Vector3(point.X, point.Y, FloorUnder(point.X, point.Y) + 0.02f));
            Sound.Play(SoundId.Teleport);
        }

        /// <summary>
        /// The big map (over the whole window but a margin): the plan, the marks, and the bar: AERIAL / PLAN, the level,
        /// FIT SITE, CLOSE.
        /// </summary>
        private void BuildSiteMap(int width, int height)
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            float margin = S(UiTheme.PANEL_MARGIN), bar = S(48);
            float x = margin, y = margin, w = width - margin * 2f, h = height - margin * 2f;
            _ui.Rect(0, 0, width, height, Rgba.Hex(0x000000, 0.6f));
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
            var rect = new Vector4(x + S(6), y + bar, w - S(12), h - bar - S(6));
            _siteMapRect = rect;
            int levels = Scene.Levels.Length;
            _siteMapLevel = levels > 0 ? Math.Clamp(_siteMapLevel, 0, levels - 1) : 0;

            DrawSiteMapPlan(rect.X, rect.Y, rect.Z, rect.W, _siteMapCentre, _siteMapAcross, _siteMapAerial, _siteMapLevel);
            Vector2 mouse = input.MousePosition;
            _siteMapHover = DrawSiteMapMarks(f, rect, _siteMapCentre, _siteMapAcross, _siteMapAerial, _siteMapLevel, _sitePhotoView, mouse,
                pointable: Inside(rect, mouse) && !_siteMapDragged, showCamera: !SitePhotoViewOpen);

            // Scale bar (bottom left): a round number of metres
            float perPixel = _siteMapAcross / rect.Z;
            float metres = NiceMetres(perPixel * S(120));
            float barW = metres / perPixel, sx = rect.X + S(14), sy = rect.Y + rect.W - S(18);
            _ui.Rect(sx - S(6), sy - S(18), barW + S(12), S(28), Rgba.Hex(0x0C0E12, 0.6f));
            _ui.Rect(sx, sy, barW, MathF.Max(2f, S(2)), UiTheme.TEXT);
            _ui.Text(f.Small, sx, sy - S(16), Text.Clear().Append(metres, metres < 10f ? 1 : 0).Append(" m").Span, UiTheme.TEXT);

            // The bar
            float bx = x + S(UiTheme.PANEL_PAD), by = y + S(9), bh = S(30);
            _siteMapBarRect = new Vector4(x, y, w, bar);
            _ui.Text(f.Bold, bx, by + S(5), "SITE MAP", UiTheme.BOOKMARK_LABEL);
            bx += S(110);
            int look = Segmented(f, input, bx, by - S(1), S(200), MAP_LOOKS, _siteMapAerial ? 0 : 1);
            _siteMapAerial = look == 0;
            bx += S(212);
            if (levels > 0)
            {
                if (MenuButton(f, bx, by, S(34), "‹", false, false, enabled: _siteMapLevel > 0, height: bh, compact: true)) { _siteMapLevel--; }
                string levelName = Scene.Levels[_siteMapLevel].Name;
                float lw = MathF.Max(S(120), UiBatch.Measure(f.Body, levelName) + S(20));
                _ui.TextCentred(f.Body, bx + S(40) + lw * 0.5f, by + S(6), levelName, UiTheme.TEXT);
                if (MenuButton(f, bx + S(46) + lw, by, S(34), "›", false, false, enabled: _siteMapLevel < levels - 1, height: bh, compact: true)) { _siteMapLevel++; }
                bx += S(92) + lw;
            }
            if (MenuButton(f, bx, by, S(100), "FIT SITE", false, false, height: bh, compact: true))
            {
                Aabb bounds = Scene.Bounds;
                _siteMapCentre = new Vector2(bounds.Center.X, bounds.Center.Y);
                _siteMapAcross = SiteMapFitAcross();
            }
            if (MenuButton(f, x + w - S(UiTheme.PANEL_PAD) - S(100), by, S(100), "CLOSE", false, false, height: bh, compact: true))
            {
                CloseSiteMap();
                return;
            }
            string hint = !SitePhotoViewOpen
                ? "Drag to pan · scroll to zoom · click a photo to look through it · double-click to go there"
                : "Drag to pan · scroll to zoom · click a photo to look through it";
            _ui.TextRight(f.Small, x + w - S(UiTheme.PANEL_PAD) - S(112), by + S(9), hint, UiTheme.TEXT_FAINT);
            BuildToast(f, width);
        }

        private static readonly string[] MAP_LOOKS = { "AERIAL", "PLAN" };

        /// <summary>1, 2 or 5 × a power of ten, at most <paramref name="metres"/>.</summary>
        private static float NiceMetres(float metres)
        {
            if (metres <= 0f || !float.IsFinite(metres)) { return 1f; }
            float power = MathF.Pow(10f, MathF.Floor(MathF.Log10(metres)));
            float first = metres / power;
            return (first >= 5f ? 5f : first >= 2f ? 2f : 1f) * power;
        }

        #endregion
    }
}
