using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Game.Guns;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// The simple viewer (viewer round): a .bimgo saved with pause menu → SAVE VIEWER COPY… (manifest <c>viewer</c>)
    /// opens with a clean toolbar instead of the full walkthrough's HUD, guns and settings, for someone who only needs to
    /// look at the model.
    /// <list type="bullet">
    /// <item><b>Orbit</b> (the default): the cursor stays free; LMB-drag orbits around the point under the cursor, RMB / MMB-drag
    /// (or Shift + LMB) pans, the wheel zooms toward the cursor, a double-click glides to the point clicked. The player
    /// follows the camera (flying), so bookmarks, comment views and home work as in the walkthrough.</item>
    /// <item><b>Walk</b>: first person from the point in the middle of the view; the move keys walk, a drag looks, a
    /// double-click on a floor walks there. A walking bookmark or comment view switches to walk, a flying one to orbit.</item>
    /// <item><b>Toolbar</b>: home, orbit / walk, measure (the Measure gun, aimed by the cursor), section box (the box
    /// editor, orbiting), comments (the Comment gun; the list), views (bookmarks and the tour) and the sun (shadows, time
    /// and month), fullscreen and the menu (save, close, and OPEN IN FULL BIMGO, which switches to the full walkthrough;
    /// its pause menu has SIMPLE VIEWER to come back).</item>
    /// </list>
    /// Saving keeps the file a viewer file, so comments made in the viewer go back to its author in the same file.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>Orbit: radians per pixel dragged.</summary>
        private const float VIEWER_ORBIT_RATE = 0.0065f;

        /// <summary>Orbit: one wheel notch moves this share of the way toward the cursor's point.</summary>
        private const float VIEWER_ZOOM_STEP = 0.18f;

        /// <summary>Orbit: never closer than this to the zoom target (m).</summary>
        private const float VIEWER_MIN_RANGE = 0.4f;

        /// <summary>A press that moves this far (px, unscaled) is a drag, not a click.</summary>
        private const float VIEWER_DRAG_PIXELS = 4f;

        /// <summary>Two clicks within this long (s) and a few pixels are a double-click.</summary>
        private const float VIEWER_DOUBLE_CLICK = 0.4f;

        /// <summary>Glides (double-click, home) take this long (s).</summary>
        private const float VIEWER_GLIDE_SECONDS = 0.45f;

        /// <summary>Walking: radians per pixel dragged (look).</summary>
        private const float VIEWER_LOOK_RATE = 0.004f;

        /// <summary>The mode hint under the title shows this long after the mode changes (s).</summary>
        private const float VIEWER_HINT_SECONDS = 10f;

        // Toolbar buttons
        private const int VT_HOME = 0, VT_ORBIT = 1, VT_WALK = 2, VT_GHOST = 3, VT_MEASURE = 4, VT_SECTION = 5, VT_COMMENT = 6, VT_VIEWS = 7,
            VT_PHOTOS = 8, VT_MAP = 9, VT_SUN = 10, VT_FULLSCREEN = 11, VT_MENU = 12, VT_COUNT = 13;

        private static readonly string[] VIEWER_TOOL_LABELS =
        {
            "Home view", "Orbit", "Walk", "Pass through walls", "Measure", "Section box", "Comments", "Views and tour", "Site photos", "Site map",
            "Sun and shadows", "Fullscreen", "Menu"
        };

        // Groups: a gap after these buttons
        private static readonly bool[] VIEWER_GAP_AFTER = { true, false, false, true, false, false, true, false, false, false, true, false, false };

        private static readonly KeyHint HINT_VIEWER_WALKING = new(() => $"Move keys walk · drag to look · double-click a floor to go there · {Key(InputAction.Fly)} orbits");
        private static readonly KeyHint HINT_VIEWER_ORBITING = new(() => $"Drag to orbit · right-drag to pan · scroll to zoom · double-click to focus · {Key(InputAction.Fly)} walks");
        private static readonly KeyHint HINT_VIEWER_GHOST_ON = new(() =>
            $"Passing through walls: the move keys go where you look ({Key(InputAction.Jump)} / {Key(InputAction.Crouch)} up and down; {Key(InputAction.ViewerGhost)} walks again)");
        private static readonly KeyHint HINT_VIEWER_GHOST = new(() => $"Pass through walls ({Key(InputAction.ViewerGhost)})");
        private static readonly KeyHint HINT_VIEWER_ORBIT = new(() => $"Orbit ({Key(InputAction.Fly)})");
        private static readonly KeyHint HINT_VIEWER_WALK = new(() => $"Walk ({Key(InputAction.Fly)}: drop from here)");

        private static readonly string[] VIEWER_MEASURE_MODES = { "DISTANCE", "AREA", "ANGLE", "CLEARANCE" };

        private static readonly string[] MONTH_NAMES = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        private const int VIEWER_SLIDER_TIME = 9101, VIEWER_SLIDER_MONTH = 9102;

        /// <summary>The popover above the toolbar: none, views, sun.</summary>
        private const int VP_NONE = 0, VP_VIEWS = 1, VP_SUN = 2, VP_PHOTOS = 3;

        private bool _viewer;              // the simple viewer is showing (else the full walkthrough)
        private bool _viewerFile;          // the file opens in the viewer (kept when it is saved)
        private string _viewerStart = ViewerStart.ORBIT;
        private bool _viewerWalk;          // walking (else orbiting)
        private bool _viewerGhost;         // walking through walls (the player flies, no-clip, and stays walking)
        private float _viewerSpeed = 1f;   // the walking speed (× normal)
        private bool _viewerFromBar;       // a panel (views, comments) was opened from the toolbar: closing it goes back to the model
        private int _viewerPopup;          // VP_*
        private bool _viewerPhotosShown;   // the site photos button shows (the file has photos in the model; set as the bar is drawn)
        private float _viewerHintFrom;     // when the mode hint came back (clock)

        // The orbit camera: a free pose (eye, yaw, pitch) turned about a pivot that each drag picks
        private Vector3 _orbitEye;
        private float _orbitYaw, _orbitPitch;
        private Vector3 _orbitPivot;
        private bool _orbitPivotShown;     // the pivot dot shows while orbiting

        // The pose last written to the player: anything else moving the player (bookmark, comment view, tour end) is taken up
        private Vector3 _viewerSyncedFeet;
        private float _viewerSyncedYaw, _viewerSyncedPitch;
        private bool _viewerSyncedFlying;

        // Mouse: 0 none, 1 orbit / look, 2 pan; the button pressed (1 left, 2 right, 3 middle), where, and whether it moved
        private int _viewerDrag;
        private int _viewerButton;
        private Vector2 _viewerPressAt, _viewerMouse;
        private bool _viewerDragged;
        private float _viewerLastClick = -10f;
        private Vector2 _viewerLastClickAt;
        private int _viewerFire;           // 1 primary, 2 secondary: a click for the armed tool (fired in UpdateGuns, aimed by the cursor)

        // Glide (orbit): from / to pose, 0–1 progress (1 = none)
        private float _glideT = 1f;
        private Vector3 _glideFromEye, _glideToEye;
        private float _glideFromYaw, _glideToYaw, _glideFromPitch, _glideToPitch;

        // Labels with a count (rebuilt only when it changes: the menu and popover are drawn every frame)
        private string _viewerViewsLabel, _viewerCommentsLabel, _viewerAllViewsLabel;
        private int _viewerViewsLabelFor = -1, _viewerCommentsLabelFor = -1, _viewerAllViewsLabelFor = -1;

        // Screen rectangles last drawn (x, y, w, h): clicks there belong to the UI, not the model
        private Vector4 _viewerBarRect, _viewerPopupRect, _viewerPanelRect, _viewerTitleRect;

        #endregion

        #region State

        /// <summary>True while the viewer orbits (the player's own keys wait).</summary>
        private bool ViewerOrbiting => _viewer && !_viewerWalk;

        /// <summary>
        /// Where tools aim: the window centre (crosshair), or in the viewer the cursor (the mouse is never captured there).
        /// </summary>
        public Vector2 AimPixel => _viewer && !_window.IsCaptured
            ? _window.Input.MousePosition
            : new Vector2(Camera.ViewportWidth * 0.5f, Camera.ViewportHeight * 0.5f);

        /// <summary>True when a window pixel is over the viewer's UI (toolbar, popover, tool panel, title, section dock).</summary>
        private bool ViewerOverUi(Vector2 p)
        {
            if (_unarmed && SiteThumbAt(p) >= 0) { return true; } // a site photo's thumbnail (a tool armed: clicks go to the model)
            return ViewerOverPanels(p);
        }

        /// <summary>True when a window pixel is over the toolbar, the title, the popover, the tool panel or the section dock.</summary>
        private bool ViewerOverPanels(Vector2 p)
        {
            if (Inside(_viewerBarRect, p) || Inside(_viewerTitleRect, p) || Inside(_viewerMapRect, p)) { return true; }
            if (_viewerPopup != VP_NONE && Inside(_viewerPopupRect, p)) { return true; }
            if (!_unarmed && Inside(_viewerPanelRect, p)) { return true; }
            return _sectionOpen && Inside(_sectionPanelRect, p);
        }

        #endregion

        #region Enter / leave

        /// <summary>
        /// Reads the file's viewer flag (Initialise): a viewer file opens in the viewer unless it is a kiosk launch.
        /// </summary>
        private void ReadViewerFlag()
        {
            ViewerDto viewer = IsFileMode ? _options.Document?.Viewer : null;
            _viewerFile = viewer?.Open == true;
            _viewerStart = viewer?.Start == ViewerStart.WALK ? ViewerStart.WALK : ViewerStart.ORBIT;
        }

        /// <summary>
        /// Shows the simple viewer: no gun, no minimap, the cursor free; orbiting from the start view (the saved home, else
        /// the whole model from the south-west), or walking when the file says so.
        /// </summary>
        /// <param name="fromWalkthrough">True from the full walkthrough's menu (SIMPLE VIEWER): orbit from where the player is.</param>
        private void EnterViewer(bool fromWalkthrough)
        {
            _viewer = true;
            ViewerDisarm();
            _showMap = false;
            _showHelp = false;
            _uiHidden = false;
            _viewerPopup = VP_NONE;
            _viewerDrag = 0;
            _viewerButton = 0;
            _viewerFromBar = false;
            _glideT = 1f;
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            _viewerMouse = _window.Input.MousePosition;
            _viewerWalk = false; // each branch below walks or orbits
            _viewerGhost = false;
            _player.SpeedScale = _viewerSpeed;

            if (fromWalkthrough)
            {
                if (_player.Flying) { OrbitFromPlayer(); }
                else { SetViewerWalk(true, announce: false, keepPose: true); }
            }
            else if (_options.Pose != null)
            {
                // Reopened (reload from IFC): carry on from where the player stood
                if (_player.Flying) { OrbitFromPlayer(); }
                else { SetViewerWalk(true, announce: false, keepPose: true); }
            }
            else if (_viewerStart == ViewerStart.WALK)
            {
                SetViewerWalk(true, announce: false, keepPose: true); // at the spawn (the saved home)
            }
            else
            {
                if (_startedAtSavedHome) { OrbitFromPlayer(); }
                else { OrbitFit(glide: false); }
            }
            if (!_viewerWalk) { OrbitToPlayer(); }
            _viewerHintFrom = _clock;
            if (_toast == null || !_toastImportant) // a start notice (safe start, an error) stays
            {
                Toast(_viewerWalk ? "Walking: drag to look, the move keys walk" : "Drag to orbit, right-drag to pan, scroll to zoom", 4f);
            }
            Utilities.Log_Utils.Write($"Simple viewer: {DocumentName} ({(_viewerWalk ? "walking" : "orbiting")}).");
        }

        /// <summary>
        /// Menu → OPEN IN FULL BIMGO: the full walkthrough from the viewer's camera (flying where it orbited), every tool
        /// and setting. The file stays a viewer file (its pause menu has SIMPLE VIEWER to come back).
        /// </summary>
        private void LeaveViewer()
        {
            if (!_viewer) { return; }
            _viewer = false;
            _viewerGhost = false;
            _player.SpeedScale = 1f; // the walkthrough walks at its own speed
            _viewerPopup = VP_NONE;
            _viewerDrag = 0;
            _viewerFromBar = false;
            _orbitPivotShown = false;
            _showMap = true;
            _viewerBarRect = _viewerPopupRect = _viewerPanelRect = _viewerTitleRect = Vector4.Zero;
            SetPaused(false);
            Toast($"Full BimGo: every tool and setting ({Key(InputAction.Back)} → SIMPLE VIEWER goes back)", 4f);
            Utilities.Log_Utils.Write("Simple viewer left: full walkthrough.");
        }

        #endregion

        #region Camera

        /// <summary>
        /// Orbit from the player's eye and view (a bookmark, a comment view, home, the walkthrough).
        /// </summary>
        private void OrbitFromPlayer()
        {
            _orbitEye = _player.Feet + new Vector3(0f, 0f, _player.Controller.StandEye);
            _orbitYaw = WrapAngle(_player.Yaw);
            _orbitPitch = Math.Clamp(_player.Pitch, -1.5f, 1.5f);
            _glideT = 1f;
        }

        /// <summary>
        /// The whole model in view from the south-west, looking down (home without a saved home).
        /// </summary>
        private void OrbitFit(bool glide)
        {
            Aabb bounds = Scene.Bounds;
            Vector3 centre = bounds.Center;
            float radius = MathF.Max(bounds.Size.Length() * 0.5f, 2f);
            float aspect = (float)_window.Width / Math.Max(_window.Height, 1);
            float halfH = _fov * MathF.PI / 360f;
            float halfV = MathF.Atan(MathF.Tan(halfH) / MathF.Max(aspect, 0.1f));
            float distance = radius / MathF.Sin(MathF.Max(MathF.Min(halfH, halfV), 0.1f)) * 1.05f;
            float yaw = MathF.PI * 0.25f, pitch = -0.6f;
            Vector3 eye = centre - Forward(yaw, pitch) * distance;
            if (glide) { GlideTo(eye, yaw, pitch); }
            else
            {
                _orbitEye = eye;
                _orbitYaw = yaw;
                _orbitPitch = pitch;
                _glideT = 1f;
            }
        }

        /// <summary>Home (the toolbar, H): the saved home (walking or orbiting as it was saved), else the whole model.</summary>
        private void ViewerGoHome()
        {
            Sound.Play(SoundId.UiClick);
            if (Bookmarks?.Home != null || _startedAtSavedHome)
            {
                _player.GoHome(); // taken up next frame: a walking home walks, a flying one orbits
                return;
            }
            if (_viewerWalk) { SetViewerWalk(false, announce: false); }
            OrbitFit(glide: true);
        }

        private void GlideTo(Vector3 eye, float yaw, float pitch)
        {
            _glideFromEye = _orbitEye;
            _glideFromYaw = _orbitYaw;
            _glideFromPitch = _orbitPitch;
            _glideToEye = eye;
            _glideToYaw = _orbitYaw + WrapAngle(yaw - _orbitYaw); // the short way round
            _glideToPitch = Math.Clamp(pitch, -1.5f, 1.5f);
            _glideT = 0f;
        }

        private void AdvanceGlide(float dt)
        {
            if (_glideT >= 1f) { return; }
            _glideT = MathF.Min(1f, _glideT + dt / VIEWER_GLIDE_SECONDS);
            float s = _glideT * _glideT * (3f - 2f * _glideT);
            _orbitEye = Vector3.Lerp(_glideFromEye, _glideToEye, s);
            _orbitYaw = WrapAngle(_glideFromYaw + (_glideToYaw - _glideFromYaw) * s);
            _orbitPitch = _glideFromPitch + (_glideToPitch - _glideFromPitch) * s;
        }

        /// <summary>
        /// Per frame while orbiting (UpdateCamera): takes up a move another feature made, glides, sets the camera and
        /// writes the pose back to the player (flying at the eye).
        /// </summary>
        private void UpdateViewerCamera(float dt)
        {
            if (PlayerMovedElsewhere())
            {
                if (!_player.Flying)
                {
                    // A walking bookmark, comment view or home: walk from there
                    SetViewerWalk(true, announce: true, keepPose: true);
                    return;
                }
                OrbitFromPlayer();
            }
            AdvanceGlide(dt);

            Camera.Position = _orbitEye;
            Camera.Yaw = _orbitYaw;
            Camera.Pitch = _orbitPitch;
            OrbitToPlayer();
        }

        /// <summary>Puts the player at the orbit eye (flying) and remembers the pose written.</summary>
        private void OrbitToPlayer()
        {
            if (!_player.Flying) { _player.ToggleFly(); }
            _player.TeleportTo(_orbitEye - new Vector3(0f, 0f, _player.Controller.StandEye), _orbitYaw, _orbitPitch);
            RememberPlayerPose();
        }

        private void RememberPlayerPose()
        {
            _viewerSyncedFeet = _player.Feet;
            _viewerSyncedYaw = _player.Yaw;
            _viewerSyncedPitch = _player.Pitch;
            _viewerSyncedFlying = _player.Flying;
        }

        private bool PlayerMovedElsewhere() =>
            _player.Feet != _viewerSyncedFeet || _player.Yaw != _viewerSyncedYaw || _player.Pitch != _viewerSyncedPitch || _player.Flying != _viewerSyncedFlying;

        /// <summary>
        /// Orbit ↔ walk. Walking starts on the floor under the middle of the view (or where the player was put, with
        /// <paramref name="keepPose"/>), facing the same way; orbiting starts from the walking eye.
        /// </summary>
        private void SetViewerWalk(bool walk, bool announce, bool keepPose = false)
        {
            ViewerCancelDrag();
            _viewerHintFrom = _clock;
            _viewerPopup = VP_NONE;
            if (!walk)
            {
                bool wasWalking = _viewerWalk;
                _viewerWalk = false;
                _viewerGhost = false;
                if (wasWalking) { OrbitFromPlayer(); }
                OrbitToPlayer(); // flying at the eye at once: a fall or a step this tick would read as a move made elsewhere
                if (announce) { Toast("Orbit: drag to turn around the model, right-drag to pan, scroll to zoom", 3f); }
                return;
            }

            _viewerWalk = true;
            _viewerGhost = false;
            _glideT = 1f;
            if (!keepPose)
            {
                Vector3 feet = WalkStartUnderView(out float yaw);
                if (_player.Flying) { _player.ToggleFly(); }
                _player.TeleportTo(feet, yaw, 0f);
            }
            else if (_player.Flying)
            {
                _player.ToggleFly();
            }
            if (announce) { Toast($"Walking: the move keys walk ({Key(InputAction.Run)} runs), drag to look, double-click a floor to go there", 4f); }
        }

        /// <summary>
        /// Where walking starts: the floor under the point in the middle of the view (a wall's foot, a roof, the ground
        /// plane), facing the way the camera looks.
        /// </summary>
        private Vector3 WalkStartUnderView(out float yaw)
        {
            Vector3 eye = _orbitEye;
            Vector3 forward = Forward(_orbitYaw, _orbitPitch);
            yaw = _orbitYaw;
            Vector3 spot = eye;
            if (Pick(eye, forward, 5000f, out RayHit hit))
            {
                spot = hit.Normal.Z > 0.7f ? hit.Point : hit.Point - new Vector3(forward.X, forward.Y, 0f) * 0.6f;
            }
            if (Pick(new Vector3(spot.X, spot.Y, spot.Z + 1.2f), -Vector3.UnitZ, 2000f, out RayHit floor) && floor.Normal.Z > 0.7f)
            {
                return floor.Point + new Vector3(0f, 0f, 0.02f);
            }
            return new Vector3(spot.X, spot.Y, _groundZ + 0.02f);
        }

        private static Vector3 Forward(float yaw, float pitch)
        {
            float cp = MathF.Cos(pitch);
            return new Vector3(cp * MathF.Cos(yaw), cp * MathF.Sin(yaw), MathF.Sin(pitch));
        }

        private static float WrapAngle(float a)
        {
            while (a > MathF.PI) { a -= MathF.Tau; }
            while (a < -MathF.PI) { a += MathF.Tau; }
            return a;
        }

        /// <summary>
        /// What the mouse turns about: the point under the cursor, else the point the view centre hits, else the model's
        /// centre when it is ahead, else 30 m ahead.
        /// </summary>
        private Vector3 OrbitPivotAt(Vector2 pixel)
        {
            ScreenRay(pixel, out Vector3 origin, out Vector3 direction);
            if (Pick(origin, direction, 5000f, out RayHit hit)) { return hit.Point; }
            Vector3 forward = Forward(_orbitYaw, _orbitPitch);
            if (Pick(_orbitEye, forward, 5000f, out hit)) { return hit.Point; }
            Vector3 centre = Scene.Bounds.Center;
            float ahead = Vector3.Dot(centre - _orbitEye, forward);
            return ahead > 1f ? _orbitEye + forward * ahead : _orbitEye + forward * 30f;
        }

        /// <summary>Turns the orbit camera about the pivot by the pixels dragged.</summary>
        private void OrbitBy(float dx, float dy)
        {
            float yawStep = -dx * VIEWER_ORBIT_RATE;
            float pitch = Math.Clamp(_orbitPitch - dy * VIEWER_ORBIT_RATE, -1.5f, 1.5f);
            float pitchStep = pitch - _orbitPitch;

            Vector3 offset = _orbitEye - _orbitPivot;
            offset = Vector3.Transform(offset, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yawStep));
            float yaw = WrapAngle(_orbitYaw + yawStep);
            var right = new Vector3(MathF.Sin(yaw), -MathF.Cos(yaw), 0f);
            offset = Vector3.Transform(offset, Quaternion.CreateFromAxisAngle(right, pitchStep));

            _orbitEye = _orbitPivot + offset;
            _orbitYaw = yaw;
            _orbitPitch = pitch;
        }

        /// <summary>Slides the orbit camera so the grabbed point follows the cursor.</summary>
        private void PanBy(float dx, float dy)
        {
            Vector3 forward = Forward(_orbitYaw, _orbitPitch);
            var right = new Vector3(MathF.Sin(_orbitYaw), -MathF.Cos(_orbitYaw), 0f);
            Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
            float depth = MathF.Max(Vector3.Dot(_orbitPivot - _orbitEye, forward), 1f);
            float perPixel = depth * Camera.PixelScale;
            Vector3 move = (-right * dx + up * dy) * perPixel;
            _orbitEye += move;
            _orbitPivot += move;
        }

        /// <summary>The wheel: toward (positive notches) or away from the point under the cursor.</summary>
        private void ZoomAt(Vector2 pixel, int notches)
        {
            ScreenRay(pixel, out Vector3 origin, out Vector3 direction);
            Vector3 target = Pick(origin, direction, 5000f, out RayHit hit)
                ? hit.Point
                : origin + direction * MathF.Max(Vector3.Distance(_orbitEye, _orbitPivot), 10f);
            float range = Vector3.Distance(_orbitEye, target);
            float limit = MathF.Max(Scene.Bounds.Size.Length() * 6f, 200f);
            float factor = notches > 0 ? MathF.Pow(1f - VIEWER_ZOOM_STEP, notches) : MathF.Pow(1f / (1f - VIEWER_ZOOM_STEP), -notches);
            float next = Math.Clamp(range * factor, VIEWER_MIN_RANGE, limit);
            if (range < 1e-4f) { return; }
            _orbitEye = target + (_orbitEye - target) * (next / range);
            _glideT = 1f;
        }

        /// <summary>Double-click (orbit): glides to look at the point from a little closer.</summary>
        private void FocusAt(Vector2 pixel)
        {
            ScreenRay(pixel, out Vector3 origin, out Vector3 direction);
            if (!Pick(origin, direction, 5000f, out RayHit hit)) { return; }
            Vector3 toPoint = hit.Point - _orbitEye;
            float range = toPoint.Length();
            if (range < 1e-3f) { return; }
            Vector3 look = toPoint / range;
            float yaw = MathF.Atan2(look.Y, look.X);
            float pitch = MathF.Asin(Math.Clamp(look.Z, -1f, 1f));
            float keep = Math.Clamp(range * 0.55f, MathF.Min(range, 3f), range);
            GlideTo(hit.Point - look * keep, yaw, pitch);
            _orbitPivot = hit.Point;
            Sound.Play(SoundId.UiClick);
        }

        /// <summary>Double-click (walking): walks to the floor clicked.</summary>
        private void WalkTo(Vector2 pixel)
        {
            ScreenRay(pixel, out Vector3 origin, out Vector3 direction);
            if (!Pick(origin, direction, 400f, out RayHit hit) || hit.Normal.Z < 0.7f)
            {
                Sound.Play(SoundId.Error);
                Toast("Double-click a floor or the ground to walk there", 2.5f);
                return;
            }
            _player.TeleportTo(hit.Point + new Vector3(0f, 0f, 0.02f));
            Sound.Play(SoundId.Teleport);
        }

        #endregion

        #region Per frame

        /// <summary>
        /// The viewer's keys and mouse (UpdateFrame, not paused, nothing else open). Mirrors
        /// <see cref="InputCatalog.SIT_VIEWER"/>.
        /// </summary>
        private void UpdateViewer(InputState input, float dt)
        {
            if (_window.IsCaptured) { _window.SetCaptured(false); } // the cursor stays free (a panel closing may have taken it)
            Vector2 mouse = input.MousePosition;
            Vector2 delta = mouse - _viewerMouse;
            _viewerMouse = mouse;
            _viewerFire = 0;
            _viewerFromBar = false; // back at the model: a panel opened from the toolbar has closed

            if (input.Pressed(InputAction.Back))
            {
                if (_viewerPopup != VP_NONE) { _viewerPopup = VP_NONE; }
                else if (!_unarmed) { ViewerDisarm(); }
                else { SetPaused(true); }
                ViewerCancelDrag();
                return;
            }
            if (input.Pressed(InputAction.Fullscreen)) { _window.ToggleFullscreen(); }
            if (input.Pressed(InputAction.Screenshot)) { RequestScreenshot(); }
            if (input.Pressed(InputAction.Save) && IsFileMode)
            {
                Save(saveAs: false);
                return;
            }
            if (input.Pressed(InputAction.GoHome)) { ViewerGoHome(); }
            if (input.Pressed(InputAction.Fly))
            {
                // V, as the walkthrough's fly key: orbit → walk where the camera is (gravity takes it down), walk → orbit
                if (_viewerWalk) { SetViewerWalk(false, announce: true); }
                else
                {
                    SetViewerWalk(true, announce: true, keepPose: true);
                    if (_player.Feet.Z > _groundZ + 3f) { Toast($"Falling to the ground… ({Key(InputAction.Fly)} flies back to orbit)", 3f); }
                }
                return;
            }

            // One key per toolbar tool
            if (input.Pressed(InputAction.ViewerMeasure)) { ViewerBarClick(VT_MEASURE); }
            if (input.Pressed(InputAction.ViewerComment)) { ViewerBarClick(VT_COMMENT); }
            if (input.Pressed(InputAction.ViewerSection)) { ViewerBarClick(VT_SECTION); }
            if (input.Pressed(InputAction.ViewerViews)) { ViewerBarClick(VT_VIEWS); }
            if (input.Pressed(InputAction.ViewerSun)) { ViewerBarClick(VT_SUN); }
            if (input.Pressed(InputAction.ViewerGhost)) { ToggleViewerGhost(); }
            if (input.Pressed(InputAction.ViewerPhotos) && _viewerPhotosShown) { ViewerBarClick(VT_PHOTOS); }
            if (input.Pressed(InputAction.ViewerGallery) && SitePhotos.Photos.Count > 0)
            {
                ViewerOpenGallery();
                return;
            }
            if (input.Pressed(InputAction.ViewerMap))
            {
                _viewerMapShown = !_viewerMapShown;
                Sound.Play(SoundId.UiClick);
            }
            if (input.Pressed(InputAction.ViewerBigMap))
            {
                ViewerBarClick(VT_MAP);
                return;
            }
            if (input.PressedOrRepeated(InputAction.ViewerFaster)) { StepViewerSpeed(+1); }
            if (input.PressedOrRepeated(InputAction.ViewerSlower)) { StepViewerSpeed(-1); }

            if (_viewerWalk)
            {
                UpdateRoom();
                if (_player.Flying && !_viewerGhost)
                {
                    SetViewerWalk(false, announce: false); // a flying bookmark or view: orbit from there
                    return;
                }
                if (!_player.Flying && _viewerGhost) { _viewerGhost = false; } // a walking bookmark or view lands
                if (input.Pressed(InputAction.Jump) && !_player.Flying) { _player.QueueJump(); }
                if (_window.IsActive) { PadLook(input); }
            }

            ViewerMouse(input, mouse, delta, leftFree: true);
        }

        /// <summary>
        /// Orbit, pan, zoom (or look, walking) and clicks for the armed tool, for presses that start off the UI.
        /// </summary>
        /// <param name="leftFree">False when LMB belongs to something else this frame (a section box handle).</param>
        private void ViewerMouse(InputState input, Vector2 mouse, Vector2 delta, bool leftFree)
        {
            bool overUi = ViewerOverUi(mouse);

            // A press: the start of a drag or a click (the toolbar and panels take their own)
            if (_viewerButton == 0 && !overUi && _window.IsActive)
            {
                int button = input.LeftPressed && leftFree ? 1 : input.RightPressed ? 2 : input.IsDown(KeyCodes.MIDDLE_MOUSE) ? 3 : 0;
                if (button != 0)
                {
                    if (_viewerPopup != VP_NONE) { _viewerPopup = VP_NONE; } // a click outside closes the popover
                    _viewerButton = button;
                    _viewerPressAt = mouse;
                    _viewerDragged = false;
                    bool pan = button != 1 || input.IsDown(KeyCodes.SHIFT);
                    _viewerDrag = pan ? 2 : 1;
                    if (!_viewerWalk)
                    {
                        _orbitPivot = OrbitPivotAt(mouse);
                        _glideT = 1f;
                    }
                }
            }

            // Held: dragging
            if (_viewerButton != 0)
            {
                bool held = _viewerButton == 1 ? input.LeftDown : _viewerButton == 2 ? input.RightDown : input.IsDown(KeyCodes.MIDDLE_MOUSE);
                if (!_viewerDragged && Vector2.Distance(mouse, _viewerPressAt) > S(VIEWER_DRAG_PIXELS)) { _viewerDragged = true; }
                if (_viewerDragged && delta != Vector2.Zero)
                {
                    if (_viewerWalk)
                    {
                        // Walking: any drag looks, the way the mouse goes (as mouse look)
                        _player.Turn(-delta.X * VIEWER_LOOK_RATE, -delta.Y * VIEWER_LOOK_RATE);
                    }
                    else if (_viewerDrag == 1) { OrbitBy(delta.X, delta.Y); }
                    else { PanBy(delta.X, delta.Y); }
                }
                _orbitPivotShown = !_viewerWalk && _viewerDragged && _viewerDrag == 1;

                if (!held)
                {
                    // Released without moving: a click
                    if (!_viewerDragged) { ViewerClick(_viewerButton, mouse); }
                    ViewerCancelDrag();
                }
            }

            // The wheel zooms (orbiting) toward the cursor; walking, it sets the speed
            if (input.Wheel != 0 && !overUi)
            {
                if (_viewerWalk) { StepViewerSpeed(Math.Sign(input.Wheel)); }
                else { ZoomAt(mouse, input.Wheel); }
                input.ConsumeWheel();
            }
        }

        private void ViewerCancelDrag()
        {
            _viewerButton = 0;
            _viewerDrag = 0;
            _viewerDragged = false;
            _orbitPivotShown = false;
        }

        /// <summary>
        /// G / the toolbar's pass-through button: walking through walls (the player flies with no collision: the move keys
        /// go where you look, Space / Ctrl up and down) or back to walking with gravity. From orbiting it starts walking
        /// there, through walls, where the camera is.
        /// </summary>
        private void ToggleViewerGhost()
        {
            if (!_viewerWalk)
            {
                SetViewerWalk(true, announce: false, keepPose: true);
                if (!_player.Flying) { _player.ToggleFly(); } // stay where the camera is
                _viewerGhost = true;
            }
            else
            {
                _viewerGhost = !_viewerGhost;
                if (_player.Flying != _viewerGhost) { _player.ToggleFly(); }
            }
            Sound.Play(SoundId.UiClick);
            _viewerHintFrom = _clock;
            Toast(_viewerGhost ? HINT_VIEWER_GHOST_ON.Text : "Walking: walls and floors stop you again", 3.5f);
        }

        /// <summary>Walk faster (+1) or slower (−1): ×1.25 a step, 0.25× to 4×.</summary>
        private void StepViewerSpeed(int direction)
        {
            float speed = Math.Clamp(_viewerSpeed * MathF.Pow(1.25f, direction), 0.25f, 4f);
            if (MathF.Abs(speed - 1f) < 0.06f) { speed = 1f; } // land on normal again
            _viewerSpeed = speed;
            _player.SpeedScale = speed;
            Toast($"Walking speed {speed:0.##}× ({Key(InputAction.ViewerSlower)} / {Key(InputAction.ViewerFaster)} or the wheel while walking)", 2f);
        }

        /// <summary>A click on the model: the armed tool fires (aimed by the cursor); a double-click focuses or walks there.</summary>
        private void ViewerClick(int button, Vector2 mouse)
        {
            if (button == 1)
            {
                bool second = _clock - _viewerLastClick < VIEWER_DOUBLE_CLICK && Vector2.Distance(mouse, _viewerLastClickAt) < S(8);
                _viewerLastClick = second ? -10f : _clock;
                _viewerLastClickAt = mouse;
                if (!_unarmed)
                {
                    _viewerFire = 1;
                    return;
                }
                if (second)
                {
                    if (_viewerWalk) { WalkTo(mouse); }
                    else { FocusAt(mouse); }
                }
                return;
            }
            // RMB click: the tool's secondary (Measure: close the area, cancel, remove the last); never deletes a comment
            if (button == 2 && !_unarmed && _guns[_activeGun] != _commentGun) { _viewerFire = 2; }
        }

        /// <summary>Arms a tool (the Measure or Comment gun) for the cursor; the same tool again puts it away.</summary>
        private void ViewerArm(Gun gun)
        {
            int index = Array.IndexOf(_guns, gun);
            if (index < 0) { return; }
            if (!_unarmed && _activeGun == index)
            {
                ViewerDisarm();
                return;
            }
            if (!_unarmed) { _guns[_activeGun].OnDeselect(); }
            _activeGun = index;
            _unarmed = false;
            _viewerPopup = VP_NONE;
            Sound.Play(SoundId.UiClick);
        }

        private void ViewerDisarm()
        {
            if (_unarmed) { return; }
            _guns[_activeGun].OnDeselect();
            _unarmed = true;
        }

        /// <summary>The toolbar's section box: the box editor (a box around the whole model the first time), or closes it.</summary>
        private void ViewerToggleSection()
        {
            if (_sectionOpen)
            {
                CloseSectionEditor();
                return;
            }
            ViewerDisarm();
            _viewerPopup = VP_NONE;
            if (!_section.BoxOn && !_section.PlaneOn) { FitSectionToModel(); }
            OpenSectionEditor();
            _sectionNotice = "Drag a face's handle to cut; drag elsewhere to orbit";
        }

        /// <summary>A panel from the toolbar or the menu (views, comments): it replaces the menu while open.</summary>
        private void ViewerOpenPanel(bool comments, bool fromBar)
        {
            _viewerPopup = VP_NONE;
            ViewerCancelDrag();
            SetPaused(true);
            _viewerFromBar = fromBar;
            if (comments) { OpenComments(); }
            else { OpenBookmarks(); }
        }

        #endregion

        #region HUD

        /// <summary>
        /// The viewer's 2D layer: labels (measurements, comment markers), the title, the tool panel or section dock, the
        /// popover, the toolbar and toasts.
        /// </summary>
        private void BuildViewerHud(int width, int height)
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            if (!input.LeftDown) { _activeSlider = -1; }

            for (int i = 0; i < _guns.Length; i++) { _guns[i].DrawLabels(_ui, i == _activeGun && !_unarmed); }
            DrawSiteThumbs(f, input, cursor: true); // site photos round: click one to look through it

            // The pivot while orbiting
            if (_orbitPivotShown && Camera.WorldToScreen(_orbitPivot, out Vector2 pivot))
            {
                _ui.Circle(pivot.X, pivot.Y, S(5), Rgba.Hex(0x000000, 0.5f), 14);
                _ui.Circle(pivot.X, pivot.Y, S(3.5f), UiTheme.ACCENT, 14);
            }

            BuildViewerTitle(f);
            if (_sectionOpen)
            {
                BuildSectionPanel(f, input);
                _viewerPanelRect = Vector4.Zero;
            }
            else if (!_unarmed) { BuildViewerToolPanel(f, input, width); }
            else { _viewerPanelRect = Vector4.Zero; }

            BuildViewerBar(f, input, width, height);
            BuildViewerMiniMap(f, input, width, height); // site photos round
            if (SitePhotoViewOpen || _siteMapOpen) { return; } // a click on the map opened a photo or the site map
            if (_viewerPopup == VP_VIEWS) { BuildViewerViews(f, input); }
            else if (_viewerPopup == VP_SUN) { BuildViewerSun(f, input); }
            else if (_viewerPopup == VP_PHOTOS && _viewerPhotosShown) { BuildViewerPhotos(f, input); }
            else { _viewerPopupRect = Vector4.Zero; }

            BuildToast(f, width);
        }

        /// <summary>Top left: the model's name, where you are (walking), and the mouse hint for a while.</summary>
        private void BuildViewerTitle(FontAtlas f)
        {
            float x = S(UiTheme.COLUMN_MARGIN), y = S(UiTheme.COLUMN_MARGIN);
            string name = string.IsNullOrEmpty(Scene.ModelTitle) ? DocumentName : Scene.ModelTitle;
            float shown = _clock - _viewerHintFrom;
            float hintAlpha = shown < VIEWER_HINT_SECONDS ? 1f : MathF.Max(0f, 1f - (shown - VIEWER_HINT_SECONDS) / 1.5f);
            string hint = _viewerWalk
                ? HINT_VIEWER_WALKING.Text
                : HINT_VIEWER_ORBITING.Text;

            TextBuffer where = Text.Clear();
            if (_viewerWalk)
            {
                where.Append(CurrentLevelName);
                if (_roomIndex >= 0 && _roomIndex < Scene.Rooms.Length)
                {
                    if (_viewerRoomLabelFor != _roomIndex) // the room's label, made once per room entered
                    {
                        _viewerRoomLabelFor = _roomIndex;
                        _viewerRoomLabel = RoomLabel(_roomIndex);
                    }
                    where.Append(" · ").Append(_viewerRoomLabel);
                }
                if (_viewerGhost) { where.Append(" · through walls"); }
                if (_viewerSpeed != 1f) { where.Append(" · speed ").Append(_viewerSpeed, _viewerSpeed < 1f ? 2 : 1).Append('×'); }
            }
            else
            {
                where.Append(IsDirty ? "Simple viewer · unsaved changes" : "Simple viewer");
            }

            float w = MathF.Max(UiBatch.Measure(f.Bold, name), UiBatch.Measure(f.Small, where.Span)) + S(28);
            if (hintAlpha > 0f) { w = MathF.Max(w, UiBatch.Measure(f.Small, hint) + S(28)); }
            w = MathF.Min(w, MathF.Max(S(200), _window.Width * 0.5f));
            float h = S(hintAlpha > 0f ? 70 : 52);
            _ui.Panel(x, y, w, h, UiTheme.PANEL, UiTheme.PANEL_BORDER);
            _ui.Text(f.Bold, x + S(14), y + S(9), name, UiTheme.TEXT);
            _ui.Text(f.Small, x + S(14), y + S(31), where.Span, UiTheme.TEXT_MUTED);
            if (hintAlpha > 0f) { _ui.Text(f.Small, x + S(14), y + S(48), hint, Rgba.WithAlpha(UiTheme.TEXT_FAINT, hintAlpha)); }
            _viewerTitleRect = new Vector4(x, y, w, h);
        }

        /// <summary>Top right: the armed tool's panel (Measure: modes, hints, remove last; Comments: hints, the list).</summary>
        private void BuildViewerToolPanel(FontAtlas f, InputState input, int width)
        {
            Gun active = _guns[_activeGun];
            bool measure = active == _measureGun;
            float w = S(measure ? 380 : 320);
            float x = width - S(UiTheme.COLUMN_MARGIN) - w, y = S(UiTheme.COLUMN_MARGIN);
            float h = S(measure ? 176 : 142);
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, Rgba.WithAlpha(active.Colour, 0.6f));
            float ix = x + S(16), iw = w - S(32), iy = y + S(12);
            _ui.Text(f.Small, ix, iy, measure ? "MEASURE" : "COMMENTS", measure ? UiTheme.MEASURE_LABEL : UiTheme.COMMENT_LABEL, S(1.1f));
            iy += S(22);

            if (measure)
            {
                int mode = (int)_measureGun.Mode;
                int picked = Segmented(f, input, ix, iy, iw, VIEWER_MEASURE_MODES, mode);
                for (int guard = 0; picked != (int)_measureGun.Mode && guard < VIEWER_MEASURE_MODES.Length; guard++) { _measureGun.CycleMode(+1); }
                iy += S(42);
                _ui.Text(f.Body, ix, iy, Text.Clear().Append("Click: ").Append(active.HintPrimary).Span, UiTheme.TEXT);
                iy += S(20);
                _ui.Text(f.Body, ix, iy, Text.Clear().Append("Right-click: ").Append(active.HintSecondary).Span, UiTheme.TEXT_MUTED);
                iy += S(30);
                float bw = (iw - S(UiTheme.GAP_SMALL)) * 0.5f;
                if (MenuButton(f, ix, iy, bw, "REMOVE LAST", false, false, height: S(UiTheme.SMALL_BUTTON_HEIGHT), compact: true)) { _measureGun.OnSecondary(default); }
                if (MenuButton(f, ix + bw + S(UiTheme.GAP_SMALL), iy, bw, "DONE", false, false, height: S(UiTheme.SMALL_BUTTON_HEIGHT), compact: true)) { ViewerDisarm(); }
            }
            else
            {
                _ui.TextWrapped(f.Body, ix, iy, iw, "Click the model to place a comment. Hover a marker to read it.", UiTheme.TEXT, maxLines: 2);
                iy += S(42);
                _ui.Text(f.Small, ix, iy, Text.Clear().Append(Comments.Comments.Count).Append(Comments.Comments.Count == 1 ? " comment" : " comments").Span, UiTheme.TEXT_MUTED);
                iy += S(22);
                float bw = (iw - S(UiTheme.GAP_SMALL)) * 0.5f;
                if (MenuButton(f, ix, iy, bw, "ALL COMMENTS…", false, false, height: S(UiTheme.SMALL_BUTTON_HEIGHT), compact: true)) { ViewerOpenPanel(comments: true, fromBar: true); }
                if (MenuButton(f, ix + bw + S(UiTheme.GAP_SMALL), iy, bw, "DONE", false, false, height: S(UiTheme.SMALL_BUTTON_HEIGHT), compact: true)) { ViewerDisarm(); }
            }
            _viewerPanelRect = new Vector4(x, y, w, h);
        }

        /// <summary>Bottom centre: the toolbar (icons; the hovered one's name above it).</summary>
        private void BuildViewerBar(FontAtlas f, InputState input, int width, int height)
        {
            float size = S(44), groupGap = S(14), pad = S(8);
            _viewerPhotosShown = HasPlacedSitePhotos;
            float barW = pad * 2f + ViewerButtonX(VT_MENU) + size;
            float barH = size + pad * 2f;
            float bx = MathF.Round(width * 0.5f - barW * 0.5f), by = height - S(24) - barH;
            _ui.Panel(bx, by, barW, barH, UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
            _viewerBarRect = new Vector4(bx, by, barW, barH);

            int hovered = -1;
            float y = by + pad;
            for (int t = 0; t < VT_COUNT; t++)
            {
                if (!ViewerToolShown(t)) { continue; }
                float x = bx + pad + ViewerButtonX(t);
                bool on = t switch
                {
                    VT_ORBIT => !_viewerWalk,
                    VT_WALK => _viewerWalk && !_viewerGhost,
                    VT_GHOST => _viewerGhost,
                    VT_MEASURE => !_unarmed && _guns[_activeGun] == _measureGun,
                    VT_COMMENT => !_unarmed && _guns[_activeGun] == _commentGun,
                    VT_SECTION => _sectionOpen,
                    VT_VIEWS => _viewerPopup == VP_VIEWS,
                    VT_SUN => _viewerPopup == VP_SUN,
                    VT_PHOTOS => _viewerPopup == VP_PHOTOS,
                    VT_FULLSCREEN => _window.IsFullscreen,
                    _ => false
                };
                bool hover = Hover(input, x, y, size, size);
                if (hover) { hovered = t; }
                if (on || hover) { _ui.Rect(x, y, size, size, on ? Rgba.WithAlpha(UiTheme.ACCENT, 0.22f) : UiTheme.HOVER); }
                if (on) { _ui.Rect(x + S(10), y + size - S(3), size - S(20), S(2), UiTheme.ACCENT); }
                uint colour = on ? UiTheme.ACCENT : hover ? UiTheme.TEXT : UiTheme.TEXT_SOFT;
                if (t == VT_SECTION && _section.IsActive && !on) { colour = UiTheme.SCAN_LABEL; } // a cut is on
                if (t == VT_SUN && ShadowsOn && !on) { colour = UiTheme.SUN_LABEL; }
                DrawViewerIcon(t, x + size * 0.5f, y + size * 0.5f, size * 0.5f, colour);

                if (hover && input.LeftPressed)
                {
                    input.ConsumeClicks();
                    ViewerBarClick(t);
                }

                // The divider between groups
                if (t < VT_COUNT - 1 && VIEWER_GAP_AFTER[t])
                {
                    _ui.Rect(x + size + groupGap * 0.5f - S(0.5f), y + S(8), MathF.Max(1f, UiScale), size - S(16), UiTheme.DIVIDER);
                }
            }

            // The tooltip above the hovered button (not over an open popover)
            if (hovered >= 0 && _viewerPopup == VP_NONE)
            {
                string label = hovered == VT_ORBIT ? HINT_VIEWER_ORBIT.Text : hovered == VT_WALK ? HINT_VIEWER_WALK.Text
                    : hovered == VT_GHOST ? HINT_VIEWER_GHOST.Text : ViewerToolTip(hovered);
                if (hovered == VT_FULLSCREEN && _window.IsFullscreen) { label = "Leave fullscreen"; }
                float tw = UiBatch.Measure(f.Body, label) + S(20);
                float tx = Math.Clamp(ViewerButtonCentre(hovered, bx) - tw * 0.5f, S(8), width - tw - S(8));
                float ty = by - S(36);
                _ui.Panel(tx, ty, tw, S(28), UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
                _ui.Text(f.Body, tx + S(10), ty + S(6), label, UiTheme.TEXT);
            }
        }

        /// <summary>A tool's tooltip with its key ("Measure (M)"; cached: drawn every frame while hovered).</summary>
        private string ViewerToolTip(int tool)
        {
            InputAction key = tool switch
            {
                VT_MEASURE => InputAction.ViewerMeasure,
                VT_SECTION => InputAction.ViewerSection,
                VT_COMMENT => InputAction.ViewerComment,
                VT_VIEWS => InputAction.ViewerViews,
                VT_SUN => InputAction.ViewerSun,
                VT_PHOTOS => InputAction.ViewerPhotos,
                VT_MAP => InputAction.ViewerBigMap,
                VT_HOME => InputAction.GoHome,
                VT_FULLSCREEN => InputAction.Fullscreen,
                _ => InputAction.None
            };
            if (key == InputAction.None) { return VIEWER_TOOL_LABELS[tool]; }
            _viewerTipCache ??= new string[VT_COUNT];
            string label = Key(key);
            string cached = _viewerTipCache[tool];
            if (cached == null || _viewerTipKeys == null || _viewerTipKeys[tool] != label)
            {
                (_viewerTipKeys ??= new string[VT_COUNT])[tool] = label;
                _viewerTipCache[tool] = cached = string.IsNullOrEmpty(label) ? VIEWER_TOOL_LABELS[tool] : $"{VIEWER_TOOL_LABELS[tool]} ({label})";
            }
            return cached;
        }

        private string[] _viewerTipCache, _viewerTipKeys;
        private string _viewerRoomLabel;
        private int _viewerRoomLabelFor = -1;

        /// <summary>A toolbar button's centre X (the popovers sit over their button).</summary>
        private float ViewerButtonCentre(int tool, float barX) => barX + S(8) + ViewerButtonX(tool) + S(44) * 0.5f;

        /// <summary>True when a toolbar button shows (site photos: only with photos in the model).</summary>
        private bool ViewerToolShown(int tool) => tool != VT_PHOTOS || _viewerPhotosShown;

        /// <summary>A button's left edge from the bar's padding: the shown buttons before it, a wider gap between groups.</summary>
        private float ViewerButtonX(int tool)
        {
            float size = S(44), gap = S(4), groupGap = S(14);
            float x = 0f;
            for (int t = 0; t < tool; t++)
            {
                if (ViewerToolShown(t)) { x += size + (VIEWER_GAP_AFTER[t] ? groupGap : gap); }
            }
            return x;
        }

        private void ViewerBarClick(int tool)
        {
            Sound.Play(SoundId.UiClick);
            switch (tool)
            {
                case VT_HOME: ViewerGoHome(); break;
                case VT_ORBIT: if (_viewerWalk) { SetViewerWalk(false, announce: true); } break;
                case VT_WALK:
                    if (!_viewerWalk) { SetViewerWalk(true, announce: true); }
                    else if (_viewerGhost) { ToggleViewerGhost(); } // back on the floor
                    break;
                case VT_GHOST: ToggleViewerGhost(); break;
                case VT_MEASURE:
                    if (_sectionOpen) { CloseSectionEditor(); }
                    ViewerArm(_measureGun);
                    break;
                case VT_COMMENT:
                    if (_sectionOpen) { CloseSectionEditor(); }
                    ViewerArm(_commentGun);
                    break;
                case VT_SECTION: ViewerToggleSection(); break;
                case VT_VIEWS: _viewerPopup = _viewerPopup == VP_VIEWS ? VP_NONE : VP_VIEWS; break;
                case VT_SUN: _viewerPopup = _viewerPopup == VP_SUN ? VP_NONE : VP_SUN; break;
                case VT_PHOTOS: _viewerPopup = _viewerPopup == VP_PHOTOS ? VP_NONE : VP_PHOTOS; break;
                case VT_MAP:
                    _viewerPopup = VP_NONE;
                    if (_sectionOpen) { CloseSectionEditor(); }
                    OpenSiteMapOnSite();
                    break;
                case VT_FULLSCREEN: _window.ToggleFullscreen(); break;
                case VT_MENU:
                    _viewerPopup = VP_NONE;
                    _viewerFromBar = false;
                    SetPaused(true);
                    break;
            }
        }

        /// <summary>The views popover: the bookmarks (go there), PLAY TOUR, SAVE THIS VIEW and the full list.</summary>
        private void BuildViewerViews(FontAtlas f, InputState input)
        {
            const int MAX_ROWS = 8;
            int count = Bookmarks.Bookmarks.Count;
            int rows = Math.Min(count, MAX_ROWS);
            float w = S(320), rowH = S(32);
            float h = S(UiTheme.PANEL_PAD_Y) * 2f + S(24) + (rows == 0 ? S(44) : rows * (rowH + S(4))) + S(UiTheme.SMALL_BUTTON_HEIGHT) * 3f + S(UiTheme.GAP_SMALL) * 3f;
            float x = Math.Clamp(ViewerButtonCentre(VT_VIEWS, _viewerBarRect.X) - w * 0.5f, S(8), _window.Width - w - S(8));
            float y = _viewerBarRect.Y - S(10) - h;
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
            _viewerPopupRect = new Vector4(x, y, w, h);

            float ix = x + S(16), iw = w - S(32), iy = y + S(UiTheme.PANEL_PAD_Y);
            _ui.Text(f.Small, ix, iy, "VIEWS", UiTheme.BOOKMARK_LABEL, S(1.1f));
            iy += S(24);
            if (rows == 0)
            {
                _ui.TextWrapped(f.Body, ix, iy, iw, "No saved views yet: SAVE THIS VIEW keeps the one you're looking at.", UiTheme.TEXT_MUTED, maxLines: 2);
                iy += S(44);
            }
            for (int i = 0; i < rows; i++)
            {
                BookmarkRecord record = Bookmarks.Bookmarks[i];
                if (MenuButton(f, ix, iy, iw, string.IsNullOrWhiteSpace(record.Name) ? "(unnamed view)" : record.Name, false, false, height: rowH, compact: true))
                {
                    _viewerPopup = VP_NONE;
                    GoToBookmark(record);
                    return;
                }
                iy += rowH + S(4);
            }

            float bh = S(UiTheme.SMALL_BUTTON_HEIGHT);
            iy += S(UiTheme.GAP_SMALL) - S(4);
            if (MenuButton(f, ix, iy, iw, "PLAY TOUR", primary: count > 0, danger: false, enabled: count > 0, height: bh, compact: true))
            {
                _viewerPopup = VP_NONE;
                ViewerCancelDrag();
                if (!StartTour()) { Toast(_bookmarksNotice ?? "No views to tour", 3f); }
                return;
            }
            iy += bh + S(UiTheme.GAP_SMALL);
            if (MenuButton(f, ix, iy, iw, "SAVE THIS VIEW", false, false, height: bh, compact: true))
            {
                _viewerPopup = VP_NONE;
                AddBookmarkHere();
                return;
            }
            iy += bh + S(UiTheme.GAP_SMALL);
            string all = count > MAX_ROWS ? CountLabel(ref _viewerAllViewsLabel, ref _viewerAllViewsLabelFor, count, "ALL ", " VIEWS…") : "MANAGE VIEWS…";
            if (MenuButton(f, ix, iy, iw, all, false, false, height: bh, compact: true)) { ViewerOpenPanel(comments: false, fromBar: true); }
        }

        /// <summary>
        /// The site photos popover: show the pins and thumbnails, BROWSE PHOTOS (the nearest first), and one button per
        /// group (such as a building) to step through its photos.
        /// </summary>
        private void BuildViewerPhotos(FontAtlas f, InputState input)
        {
            const int MAX_GROUPS = 8;
            List<string> groups = SitePhotoGroups();
            int rows = Math.Min(groups.Count, MAX_GROUPS);
            float w = S(320), rowH = S(30), bh = S(UiTheme.SMALL_BUTTON_HEIGHT);
            float h = S(UiTheme.PANEL_PAD_Y) * 2f + S(24) + S(30) + S(96) + (bh + S(UiTheme.GAP_SMALL)) * 2f + (rows > 0 ? S(22) + rows * (rowH + S(4)) : 0f);
            float x = Math.Clamp(ViewerButtonCentre(VT_PHOTOS, _viewerBarRect.X) - w * 0.5f, S(8), _window.Width - w - S(8));
            float y = _viewerBarRect.Y - S(10) - h;
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
            _viewerPopupRect = new Vector4(x, y, w, h);

            float ix = x + S(16), iw = w - S(32), iy = y + S(UiTheme.PANEL_PAD_Y);
            _ui.Text(f.Small, ix, iy, "SITE PHOTOS", UiTheme.BOOKMARK_LABEL, S(1.1f));
            iy += S(24);
            bool show = Checkbox(f, input, ix, iy, iw, "Show the photos in the model", SitePhotos.Document.ShowPins);
            if (show != SitePhotos.Document.ShowPins)
            {
                SitePhotos.Document.ShowPins = show;
                SitePhotos.Save();
            }
            iy += S(30);
            SiteThumbSizeSlider(f, input, ix, iy, iw); // and the distance under it
            iy += S(96);
            if (MenuButton(f, ix, iy, iw, "BROWSE FROM THE NEAREST", primary: true, danger: false, height: bh, compact: true))
            {
                _viewerPopup = VP_NONE;
                BrowseSitePhotos(null);
                return;
            }
            iy += bh + S(UiTheme.GAP_SMALL);
            if (MenuButton(f, ix, iy, iw, "GALLERY…", false, false, height: bh, compact: true))
            {
                ViewerOpenGallery();
                return;
            }
            iy += bh + S(UiTheme.GAP_SMALL);
            if (rows == 0) { return; }
            _ui.Text(f.Small, ix, iy, "BY GROUP", UiTheme.TEXT_MUTED);
            iy += S(22);
            for (int i = 0; i < rows; i++)
            {
                if (MenuButton(f, ix, iy, iw, groups[i], false, false, height: rowH, compact: true))
                {
                    _viewerPopup = VP_NONE;
                    BrowseSitePhotos(groups[i]);
                    return;
                }
                iy += rowH + S(4);
            }
        }

        /// <summary>The sun popover: shadows on / off, the time of day and the month.</summary>
        private void BuildViewerSun(FontAtlas f, InputState input)
        {
            float w = S(340), h = S(214);
            float x = Math.Clamp(ViewerButtonCentre(VT_SUN, _viewerBarRect.X) - w * 0.5f, S(8), _window.Width - w - S(8));
            float y = _viewerBarRect.Y - S(10) - h;
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
            _viewerPopupRect = new Vector4(x, y, w, h);

            float ix = x + S(16), iw = w - S(32), iy = y + S(UiTheme.PANEL_PAD_Y);
            _ui.Text(f.Small, ix, iy, "SUN AND SHADOWS", UiTheme.SUN_LABEL, S(1.1f));
            iy += S(28);

            bool on = Checkbox(f, input, ix, iy, iw, "Shadows from the sun", _sun.Enabled);
            if (on != _sun.Enabled)
            {
                _sun.Enabled = on;
                if (!on) { _sunPlaying = false; }
                SunChanged();
            }
            iy += S(32);

            SunTime time = _sun.Time;
            int minutes = Math.Clamp(time.Minutes, 0, 1439);
            TextBuffer clock = Text.Clear().Append(minutes / 60).Append(':');
            if (minutes % 60 < 10) { clock.Append('0'); }
            clock.Append(minutes % 60);
            float picked = Slider(f, input, VIEWER_SLIDER_TIME, ix, iy, iw, "Time of day", clock.Span, minutes, 0f, 1439f);
            int pickedMinutes = Math.Clamp((int)MathF.Round(picked / 5f) * 5, 0, 1439);
            if (pickedMinutes != minutes && _activeSlider == VIEWER_SLIDER_TIME)
            {
                _sunPlaying = false;
                _sun.Time.Minutes = pickedMinutes;
                if (!_sun.Enabled) { _sun.Enabled = true; }
                SunChanged();
            }
            iy += S(56);

            int month = Math.Clamp(time.Month, 1, 12);
            float pickedMonth = Slider(f, input, VIEWER_SLIDER_MONTH, ix, iy, iw, "Month", MONTH_NAMES[month - 1], month, 1f, 12f);
            int newMonth = Math.Clamp((int)MathF.Round(pickedMonth), 1, 12);
            if (newMonth != month && _activeSlider == VIEWER_SLIDER_MONTH)
            {
                _sunPlaying = false;
                _sun.Time.Month = newMonth;
                _sun.Time.Day = Math.Min(_sun.Time.Day, DateTime.DaysInMonth(DateTime.Today.Year, newMonth));
                if (!_sun.Enabled) { _sun.Enabled = true; }
                SunChanged();
            }
        }

        /// <summary>
        /// The viewer's menu (paused, no panel open): resume, save, views, comments, fullscreen, close, and the switch to
        /// the full walkthrough. A panel opened from the toolbar goes straight back to the model when it closes.
        /// </summary>
        private void BuildViewerMenu()
        {
            if (_viewerFromBar)
            {
                _viewerFromBar = false;
                SetPaused(false);
                return;
            }

            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            int width = _window.Width, height = _window.Height;
            _ui.Rect(0, 0, width, height, Rgba.Hex(0x000000, 0.5f));

            float w = S(380), bh = S(UiTheme.BUTTON_HEIGHT), step = bh + S(UiTheme.GAP_SMALL);
            int buttons = IsFileMode ? 7 : 5;
            float h = S(UiTheme.PANEL_PAD_Y) * 2f + S(62) + buttons * step + S(78);
            float x = MathF.Round(width * 0.5f - w * 0.5f), y = MathF.Round(MathF.Max(S(16), height * 0.5f - h * 0.5f));
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
            float ix = x + S(UiTheme.PANEL_PAD), iw = w - S(UiTheme.PANEL_PAD) * 2f, iy = y + S(UiTheme.PANEL_PAD_Y);

            string name = string.IsNullOrEmpty(Scene.ModelTitle) ? DocumentName : Scene.ModelTitle;
            _ui.Text(f.Bold, ix, iy, name, UiTheme.TEXT);
            _ui.Text(f.Small, ix, iy + S(24), DocumentName, UiTheme.TEXT_MUTED);
            iy += S(62);

            if (MenuButton(f, ix, iy, iw, "BACK TO THE MODEL", primary: true, danger: false, height: bh, compact: true)) { SetPaused(false); return; }
            iy += step;
            if (IsFileMode)
            {
                if (MenuButton(f, ix, iy, iw, IsDirty ? "SAVE *" : "SAVE", false, false, height: bh, compact: true)) { Save(saveAs: false); return; }
                iy += step;
                if (MenuButton(f, ix, iy, iw, "SAVE AS…", false, false, height: bh, compact: true)) { Save(saveAs: true); return; }
                iy += step;
            }
            if (MenuButton(f, ix, iy, iw, CountLabel(ref _viewerViewsLabel, ref _viewerViewsLabelFor, Bookmarks.Bookmarks.Count, "VIEWS (", ")…"), false, false, height: bh, compact: true))
            {
                ViewerOpenPanel(comments: false, fromBar: false);
                return;
            }
            iy += step;
            if (MenuButton(f, ix, iy, iw, CountLabel(ref _viewerCommentsLabel, ref _viewerCommentsLabelFor, Comments.Comments.Count, "COMMENTS (", ")…"), false, false, height: bh, compact: true))
            {
                ViewerOpenPanel(comments: true, fromBar: false);
                return;
            }
            iy += step;
            if (MenuButton(f, ix, iy, iw, _window.IsFullscreen ? "LEAVE FULLSCREEN" : "FULLSCREEN", false, false, height: bh, compact: true)) { _window.ToggleFullscreen(); }
            iy += step;
            if (MenuButton(f, ix, iy, iw, "CLOSE MODEL", false, danger: true, height: bh, compact: true)) { _endRequested = true; return; }
            iy += step + S(UiTheme.GAP_SMALL);

            _ui.TextWrapped(f.Small, ix, iy, iw, "Drag to orbit · right-drag to pan · scroll to zoom · double-click to focus", UiTheme.TEXT_FAINT, maxLines: 2);
            iy += S(34);

            // The way out to every tool and setting, kept small
            const string full = "Open in full BimGo";
            float fw = UiBatch.Measure(f.Small, full) + S(16);
            bool hover = Hover(input, ix - S(8), iy - S(4), fw, S(24));
            _ui.Text(f.Small, ix, iy, full, hover ? UiTheme.ACCENT : UiTheme.TEXT_MUTED);
            if (hover) { _ui.Rect(ix, iy + S(17), fw - S(16), MathF.Max(1f, UiScale), UiTheme.ACCENT); }
            if (hover && input.LeftPressed)
            {
                input.ConsumeClicks();
                Sound.Play(SoundId.UiClick);
                LeaveViewer();
            }
        }

        /// <summary>"VIEWS (4)…" and the like, rebuilt only when the count changes.</summary>
        private static string CountLabel(ref string cache, ref int cacheFor, int count, string before, string after)
        {
            if (cache == null || count != cacheFor)
            {
                cacheFor = count;
                cache = before + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + after;
            }
            return cache;
        }

        #endregion

        #region Icons

        /// <summary>A toolbar icon, centred on (cx, cy), about <paramref name="r"/> from the centre to the edge of its square.</summary>
        private void DrawViewerIcon(int tool, float cx, float cy, float r, uint colour)
        {
            float t = MathF.Max(1.5f, S(1.8f));
            float s = r * 0.5f;
            switch (tool)
            {
                case VT_HOME:
                    _ui.Line(cx - s, cy - s * 0.05f, cx, cy - s * 0.95f, t, colour);
                    _ui.Line(cx, cy - s * 0.95f, cx + s, cy - s * 0.05f, t, colour);
                    _ui.Outline(cx - s * 0.7f, cy - s * 0.2f, s * 1.4f, s * 1.1f, t, colour);
                    _ui.Rect(cx - s * 0.18f, cy + s * 0.35f, s * 0.36f, s * 0.55f, colour);
                    break;
                case VT_ORBIT:
                    GunIcons.EllipseRing(_ui, cx, cy, s, s * 0.45f, t, colour);
                    _ui.Circle(cx, cy, s * 0.28f, colour, 12);
                    GunIcons.Arrowhead(_ui, cx + s * 0.3f, cy + s * 0.45f, 1f, 0f, s * 0.45f, colour);
                    break;
                case VT_WALK:
                    _ui.Circle(cx + s * 0.1f, cy - s * 0.78f, s * 0.22f, colour, 12);
                    _ui.Line(cx + s * 0.05f, cy - s * 0.45f, cx - s * 0.1f, cy + s * 0.2f, t, colour);
                    _ui.Line(cx - s * 0.1f, cy + s * 0.2f, cx - s * 0.5f, cy + s * 0.95f, t, colour);
                    _ui.Line(cx - s * 0.1f, cy + s * 0.2f, cx + s * 0.35f, cy + s * 0.55f, t, colour);
                    _ui.Line(cx + s * 0.35f, cy + s * 0.55f, cx + s * 0.35f, cy + s * 0.95f, t, colour);
                    _ui.Line(cx - s * 0.55f, cy - s * 0.15f, cx + s * 0.02f, cy - s * 0.35f, t, colour);
                    _ui.Line(cx + s * 0.02f, cy - s * 0.35f, cx + s * 0.5f, cy - s * 0.05f, t, colour);
                    break;
                case VT_GHOST:
                {
                    // An arrow through a wall
                    _ui.Rect(cx + s * 0.15f, cy - s * 0.95f, t * 1.4f, s * 0.55f, colour);
                    _ui.Rect(cx + s * 0.15f, cy + s * 0.4f, t * 1.4f, s * 0.55f, colour);
                    _ui.Line(cx - s * 0.9f, cy, cx + s * 0.75f, cy, t, colour);
                    GunIcons.Arrowhead(_ui, cx + s * 0.95f, cy, 1f, 0f, s * 0.45f, colour);
                    break;
                }
                case VT_MEASURE: GunIcons.Measure(_ui, cx, cy, r * 1.1f, colour); break;
                case VT_COMMENT: GunIcons.Comment(_ui, cx, cy, r * 1.1f, colour); break;
                case VT_SECTION:
                {
                    // A box drawn in parallel projection, its front face solid
                    float d = s * 0.45f;
                    _ui.Outline(cx - s * 0.75f, cy - s * 0.4f, s * 1.2f, s * 1.2f, t, colour);
                    _ui.Line(cx - s * 0.75f, cy - s * 0.4f, cx - s * 0.75f + d, cy - s * 0.4f - d, t, colour);
                    _ui.Line(cx + s * 0.45f, cy - s * 0.4f, cx + s * 0.45f + d, cy - s * 0.4f - d, t, colour);
                    _ui.Line(cx + s * 0.45f, cy + s * 0.8f, cx + s * 0.45f + d, cy + s * 0.8f - d, t, colour);
                    _ui.Line(cx - s * 0.75f + d, cy - s * 0.4f - d, cx + s * 0.45f + d, cy - s * 0.4f - d, t, colour);
                    _ui.Line(cx + s * 0.45f + d, cy - s * 0.4f - d, cx + s * 0.45f + d, cy + s * 0.8f - d, t, colour);
                    break;
                }
                case VT_VIEWS:
                    _ui.Line(cx - s * 0.55f, cy - s * 0.9f, cx + s * 0.55f, cy - s * 0.9f, t, colour);
                    _ui.Line(cx - s * 0.55f, cy - s * 0.9f, cx - s * 0.55f, cy + s * 0.9f, t, colour);
                    _ui.Line(cx + s * 0.55f, cy - s * 0.9f, cx + s * 0.55f, cy + s * 0.9f, t, colour);
                    _ui.Line(cx - s * 0.55f, cy + s * 0.9f, cx, cy + s * 0.4f, t, colour);
                    _ui.Line(cx, cy + s * 0.4f, cx + s * 0.55f, cy + s * 0.9f, t, colour);
                    break;
                case VT_SUN:
                    _ui.Circle(cx, cy, s * 0.42f, colour, 16);
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * MathF.PI * 0.25f;
                        float c = MathF.Cos(a), n = MathF.Sin(a);
                        _ui.Line(cx + c * s * 0.65f, cy + n * s * 0.65f, cx + c * s * 0.95f, cy + n * s * 0.95f, t, colour);
                    }
                    break;
                case VT_MAP:
                {
                    // A folded map: three panels, a route across
                    _ui.Line(cx - s * 0.85f, cy - s * 0.6f, cx - s * 0.3f, cy - s * 0.8f, t, colour);
                    _ui.Line(cx - s * 0.3f, cy - s * 0.8f, cx + s * 0.3f, cy - s * 0.6f, t, colour);
                    _ui.Line(cx + s * 0.3f, cy - s * 0.6f, cx + s * 0.85f, cy - s * 0.8f, t, colour);
                    _ui.Line(cx - s * 0.85f, cy + s * 0.8f, cx - s * 0.3f, cy + s * 0.6f, t, colour);
                    _ui.Line(cx - s * 0.3f, cy + s * 0.6f, cx + s * 0.3f, cy + s * 0.8f, t, colour);
                    _ui.Line(cx + s * 0.3f, cy + s * 0.8f, cx + s * 0.85f, cy + s * 0.6f, t, colour);
                    _ui.Line(cx - s * 0.85f, cy - s * 0.6f, cx - s * 0.85f, cy + s * 0.8f, t, colour);
                    _ui.Line(cx + s * 0.85f, cy - s * 0.8f, cx + s * 0.85f, cy + s * 0.6f, t, colour);
                    _ui.Line(cx - s * 0.3f, cy - s * 0.8f, cx - s * 0.3f, cy + s * 0.6f, t, Rgba.WithAlpha(colour, 0.6f));
                    _ui.Line(cx + s * 0.3f, cy - s * 0.6f, cx + s * 0.3f, cy + s * 0.8f, t, Rgba.WithAlpha(colour, 0.6f));
                    break;
                }
                case VT_PHOTOS:
                {
                    // A camera: body, lens, viewfinder bump
                    _ui.Outline(cx - s * 0.85f, cy - s * 0.45f, s * 1.7f, s * 1.2f, t, colour);
                    _ui.Rect(cx - s * 0.35f, cy - s * 0.7f, s * 0.7f, s * 0.25f, colour);
                    _ui.Ring(cx, cy + s * 0.15f, s * 0.38f, t, colour, 16);
                    break;
                }
                case VT_FULLSCREEN:
                {
                    float a = s * 0.85f, l = s * 0.45f;
                    _ui.Line(cx - a, cy - a, cx - a + l, cy - a, t, colour);
                    _ui.Line(cx - a, cy - a, cx - a, cy - a + l, t, colour);
                    _ui.Line(cx + a, cy - a, cx + a - l, cy - a, t, colour);
                    _ui.Line(cx + a, cy - a, cx + a, cy - a + l, t, colour);
                    _ui.Line(cx - a, cy + a, cx - a + l, cy + a, t, colour);
                    _ui.Line(cx - a, cy + a, cx - a, cy + a - l, t, colour);
                    _ui.Line(cx + a, cy + a, cx + a - l, cy + a, t, colour);
                    _ui.Line(cx + a, cy + a, cx + a, cy + a - l, t, colour);
                    break;
                }
                case VT_MENU:
                    _ui.Rect(cx - s * 0.8f, cy - s * 0.6f - t * 0.5f, s * 1.6f, t, colour);
                    _ui.Rect(cx - s * 0.8f, cy - t * 0.5f, s * 1.6f, t, colour);
                    _ui.Rect(cx - s * 0.8f, cy + s * 0.6f - t * 0.5f, s * 1.6f, t, colour);
                    break;
            }
        }

        #endregion

        #region Viewer copy

        /// <summary>
        /// Pause menu → SAVE VIEWER COPY…: writes a copy of this walkthrough (geometry, comments, views, sun, cut, billboards…)
        /// that opens in the simple viewer. The open file stays as it is. Reported in a message box (the pause menu shows
        /// no toasts).
        /// </summary>
        private void SaveViewerCopy()
        {
            if (!Save(saveAs: true, viewerCopy: true)) { return; }
            string home = Bookmarks?.Home != null
                ? "It opens at your home view."
                : $"It opens on the whole model ({Key(InputAction.SetHome)} sets a home view to open at instead, then save the copy again).";
            Utilities.Log_Utils.Write("Viewer copy written.");
            FileDialogs.ShowInfo(_window.Handle,
                "The viewer copy is saved. BimGo opens it in the simple viewer: orbit and walk, measure, section box, comments, views and the sun.\n\n" +
                $"{home}\n\nComments made in it are saved in that file: open it in BimGo to read them (its menu can switch to full BimGo).",
                "BimGo viewer copy");
        }

        #endregion
    }
}
