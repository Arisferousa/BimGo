using System.Numerics;
using BimGo.Audio;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Tutorial round: the tutorial and first-use tips.
    /// <list type="bullet">
    /// <item><b>The tutorial</b> (<see cref="Tutorials"/>): a card at the bottom of the screen, one step at a time, with
    /// STEP 3 OF 28, the chapter, a progress bar and BACK / NEXT (SKIP while a step waits for you) / END. A step that asks
    /// for something (look around, walk, open the menu…) is watched every frame and moves on by itself a moment after
    /// it's done; Enter (<see cref="InputAction.TutorialNext"/>) skips any step. The part of the screen a step is about
    /// is ringed (and the rest dimmed when there's nothing to do). The full walkthrough's tutorial has chapters; the
    /// simple viewer's is one short chapter.</item>
    /// <item><b>The menu's TUTORIAL</b> (pause menu → HELP): play it all or one chapter (done ones ticked), tips on / off and
    /// SHOW TIPS AGAIN. The simple viewer's menu starts its tutorial straight away.</item>
    /// <item><b>Tips</b> (<see cref="TutorialTips"/>): a small card at the bottom left the first time a feature is used
    /// (once per PC; SETTINGS → INTERFACE turns them off). Not during the tutorial.</item>
    /// </list>
    /// Clicks on the cards are theirs: taken at the start of the frame (from last frame's rectangles) so nothing under
    /// them reacts too. Progress (chapters done, tips seen, tips on) is kept in the settings.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>A step that's done shows DONE this long before the next one.</summary>
        private const float TUTORIAL_DONE_SECONDS = 0.9f;

        /// <summary>A tip shows this long (s).</summary>
        private const float TIP_SECONDS = 12f;

        /// <summary>The first-use tip of each gun, in the guns' order.</summary>
        private static readonly string[] GUN_TIPS = { "gun-scan", "gun-measure", "gun-portal", "gun-comment", "gun-teleport", "gun-demolish", "gun-gizmo", "gun-clone", "gun-place" };

        private TutorialRun _tutorial;
        private bool _tutorialViewer;          // the run is the simple viewer's
        private float _tutorialAmount;         // progress toward the step's goal (metres, seconds, radians, pixels, notches)
        private bool _tutorialSawOpen;         // a round-trip goal: the thing has been open
        private float _tutorialDoneAt = -1f;   // when the goal was met (clock), or -1
        private bool _tutorialHomeEvent;       // RETURN HOME / the viewer's home this frame
        private int _tutorialStartGun, _tutorialStartCount;
        private bool _tutorialStartFlag;
        private string _tutorialStartLevel;
        private Vector3 _tutorialLastFeet;
        private float _tutorialLastYaw, _tutorialLastPitch, _tutorialLastOrbitYaw, _tutorialLastOrbitPitch;
        private Vector2 _tutorialLastMouse;

        // The step's text with the keys filled in (made when the step or the keys change)
        private TutorialStep _tutorialTextFor;
        private int _tutorialTextVersion = int.MinValue;
        private string _tutorialText, _tutorialHeader, _tutorialCount, _tutorialSkipLabel, _tutorialKeysHint;

        // The menu's TUTORIAL card
        private bool _tutorialPickerOpen;
        private readonly HashSet<string> _tutorialChaptersDone = new(StringComparer.Ordinal);
        private string _tutorialMenuLabel, _tutorialPlayAllLabel;
        private int _tutorialMenuLabelFor = -1, _tutorialPlayAllFor = -1;

        // Tips
        private bool _tipsOn = true;
        private bool _tipsArmed;               // set once the walkthrough is up (no tips for what Initialise selects)
        private readonly HashSet<string> _tipsSeen = new(StringComparer.Ordinal);
        private TutorialTip _tip;
        private string _tipText;
        private float _tipFrom;

        // Clicks on the cards (their rectangles as last drawn; the click taken at the start of the frame)
        private Vector4 _tutorialCardRect, _tipRect;
        private bool _tutorialClick;

        // Where each spot was drawn this frame (valid when its frame number is this frame's)
        private readonly Vector4[] _spotRects = new Vector4[16];
        private readonly int[] _spotFrames = new int[16];
        private int _tutorialFrame = 1;

        #endregion

        #region Settings

        private void ReadTutorialSettings(LaunchSettings settings)
        {
            _tipsOn = settings.Tips;
            _tipsSeen.Clear();
            foreach (string id in settings.TipsSeen ?? new List<string>()) { _tipsSeen.Add(id); }
            _tutorialChaptersDone.Clear();
            foreach (string id in settings.TutorialChaptersDone ?? new List<string>()) { _tutorialChaptersDone.Add(id); }
        }

        private void WriteTutorialSettings(LaunchSettings settings)
        {
            settings.Tips = _tipsOn;
            settings.TipsSeen = _tipsSeen.ToList();
            settings.TutorialChaptersDone = _tutorialChaptersDone.ToList();
        }

        /// <summary>Which steps the model has what for (more than one level, rooms, site photos on the model).</summary>
        private bool TutorialHas(TutorialNeed need) => need switch
        {
            TutorialNeed.Levels => Scene.Levels.Length > 1,
            TutorialNeed.Rooms => Scene.Rooms.Length > 0,
            TutorialNeed.SitePhotos => HasPlacedSitePhotos,
            _ => true
        };

        /// <summary>"TUTORIAL (28 STEPS)" for the menus (made again only when the count changes).</summary>
        private string TutorialMenuLabel()
        {
            int steps = Tutorials.StepCount(Tutorials.For(_viewer), TutorialHas);
            if (_tutorialMenuLabel == null || steps != _tutorialMenuLabelFor)
            {
                _tutorialMenuLabelFor = steps;
                _tutorialMenuLabel = $"TUTORIAL ({steps} STEPS)";
            }
            return _tutorialMenuLabel;
        }

        #endregion

        #region Running

        /// <summary>Starts the tutorial (all of it or some chapters) from the first step; the menu closes.</summary>
        private void StartTutorial(IEnumerable<TutorialChapter> chapters)
        {
            _tutorial = new TutorialRun(chapters, TutorialHas);
            _tutorialViewer = _viewer;
            _tutorialPickerOpen = false;
            _tip = null;
            if (_tutorial.Finished)
            {
                _tutorial = null;
                return;
            }
            if (_paused) { SetPaused(false); }
            if (!_viewer) { _window.SetCaptured(false); } // the first card's NEXT can be clicked (a click on the view takes the mouse)
            BeginTutorialStep();
            Utilities.Log_Utils.Write($"Tutorial started: {_tutorial.Count} steps ({(_viewer ? "simple viewer" : "walkthrough")}).");
        }

        /// <summary>A step starts: its progress and what it compares against.</summary>
        private void BeginTutorialStep()
        {
            TutorialStep step = _tutorial?.Step;
            if (step == null) { return; }
            _tutorialAmount = 0f;
            _tutorialSawOpen = false;
            _tutorialDoneAt = -1f;
            _tutorialHomeEvent = false;
            _tutorialStartGun = _unarmed ? -1 : _activeGun;
            _tutorialStartCount = step.Goal switch
            {
                TutorialGoal.Measure => _measureGun.Count,
                TutorialGoal.Bookmark => Bookmarks?.Bookmarks.Count ?? 0,
                _ => 0
            };
            _tutorialStartFlag = step.Goal switch
            {
                TutorialGoal.Minimap => _showMap,
                TutorialGoal.Shadows => ShadowsOn,
                _ => false
            };
            _tutorialStartLevel = CurrentLevelName;
            _tutorialLastFeet = _player.Feet;
            _tutorialLastYaw = _player.Yaw;
            _tutorialLastPitch = _player.Pitch;
            _tutorialLastOrbitYaw = _orbitYaw;
            _tutorialLastOrbitPitch = _orbitPitch;
            _tutorialLastMouse = _window.Input.MousePosition;
        }

        /// <summary>NEXT / SKIP / Enter, or a goal met: the next step (or the end).</summary>
        private void NextTutorialStep()
        {
            if (_tutorial == null) { return; }
            bool finished = _tutorial.Next();
            RememberTutorialChapters();
            if (finished)
            {
                EndTutorial(finished: true);
                return;
            }
            BeginTutorialStep();
        }

        private void BackTutorialStep()
        {
            if (_tutorial == null || _tutorial.IsFirst) { return; }
            _tutorial.Back();
            BeginTutorialStep();
        }

        /// <summary>Stops the tutorial (finished, END, or the view it was made for has gone).</summary>
        private void EndTutorial(bool finished, bool quiet = false)
        {
            if (_tutorial == null) { return; }
            RememberTutorialChapters();
            _tutorial = null;
            _tutorialCardRect = Vector4.Zero;
            if (quiet) { return; }
            string again = _tutorialViewer ? "the menu's TUTORIAL" : "Esc → TUTORIAL";
            Toast(finished ? $"Tutorial finished: {again} plays it again" : $"Tutorial closed: {again} plays it again", 4f);
            if (finished) { Sound.Play(SoundId.Commit); }
        }

        /// <summary>Chapters played to the end are ticked in the menu's TUTORIAL (saved).</summary>
        private void RememberTutorialChapters()
        {
            if (_tutorial == null) { return; }
            bool changed = false;
            foreach (string id in _tutorial.FinishedChapters) { changed |= _tutorialChaptersDone.Add(id); }
            if (changed) { SaveSettings(); }
        }

        /// <summary>RETURN HOME (menu) and the viewer's home: the Home step counts them.</summary>
        private void TutorialSawHome() => _tutorialHomeEvent = true;

        /// <summary>True while Enter can skip a step (walking or the viewer, nothing else having the keys).</summary>
        private bool TutorialKeysLive => !_paused && !IsEditingComment && !_markupOpen && !IsCapturingKey && !IsMappingPad && !_wheelOpen
            && (_viewer ? !_siteMapOpen && !SitePhotoViewOpen : !CursorModeOpen);

        /// <summary>
        /// Per frame, before the rest of the frame's input: clicks on the step card or a tip are theirs, the tip times out,
        /// Enter skips the step, and the step's goal is checked.
        /// </summary>
        private void UpdateTutorial(InputState input, float dt)
        {
            _tutorialClick = false;
            if (!_window.IsCaptured && (input.LeftPressed || input.RightPressed || input.Wheel != 0))
            {
                Vector2 p = input.MousePosition;
                if ((_tutorial != null && Inside(_tutorialCardRect, p)) || (_tip != null && Inside(_tipRect, p)))
                {
                    _tutorialClick = input.LeftPressed;
                    input.ConsumeClicks();
                    input.ConsumeWheel();
                }
            }
            if (_tip != null && _clock - _tipFrom > TIP_SECONDS) { _tip = null; }

            if (_tutorial == null) { return; }
            if (_kiosk || _tutorialViewer != _viewer)
            {
                EndTutorial(finished: false, quiet: true); // kiosk, or the viewer left / entered: that tutorial no longer fits
                return;
            }
            if (_tourActive || _video != null) { return; } // a bookmark tour or a recording plays: the tutorial waits

            TutorialStep step = _tutorial.Step;
            if (step == null) { return; }
            if (_tutorialDoneAt >= 0f)
            {
                if (_clock - _tutorialDoneAt >= TUTORIAL_DONE_SECONDS) { NextTutorialStep(); }
                return;
            }
            if (TutorialKeysLive && input.Pressed(InputAction.TutorialNext))
            {
                Sound.Play(SoundId.UiClick);
                NextTutorialStep();
                return;
            }
            if (TutorialGoalMet(step, input, dt))
            {
                _tutorialDoneAt = _clock;
                Sound.Play(SoundId.Commit);
            }
            _tutorialHomeEvent = false;
        }

        /// <summary>A round-trip goal: done when the thing closes after having been open.</summary>
        private bool TutorialRoundTrip(bool open)
        {
            if (open)
            {
                _tutorialSawOpen = true;
                return false;
            }
            return _tutorialSawOpen;
        }

        /// <summary>Whether the step's goal has been met (polled at the start of each frame; amounts add up).</summary>
        private bool TutorialGoalMet(TutorialStep step, InputState input, float dt)
        {
            // This frame's movement, turning and mouse (each step starts from where it began)
            Vector3 feet = _player.Feet;
            float walked = MathF.Min(new Vector2(feet.X - _tutorialLastFeet.X, feet.Y - _tutorialLastFeet.Y).Length(), 0.5f); // a teleport isn't a walk
            _tutorialLastFeet = feet;
            float turned = MathF.Min(MathF.Abs(WrapAngle(_player.Yaw - _tutorialLastYaw)) + MathF.Abs(_player.Pitch - _tutorialLastPitch), 0.5f);
            _tutorialLastYaw = _player.Yaw;
            _tutorialLastPitch = _player.Pitch;
            float orbited = MathF.Min(MathF.Abs(WrapAngle(_orbitYaw - _tutorialLastOrbitYaw)) + MathF.Abs(_orbitPitch - _tutorialLastOrbitPitch), 0.5f);
            _tutorialLastOrbitYaw = _orbitYaw;
            _tutorialLastOrbitPitch = _orbitPitch;
            Vector2 mouse = input.MousePosition;
            float mouseMoved = Vector2.Distance(mouse, _tutorialLastMouse) / MathF.Max(UiScale, 0.1f);
            _tutorialLastMouse = mouse;

            bool walking = !_paused && !_viewer;
            bool gunFree = !_guns[_activeGun].CapturesInput;
            switch (step.Goal)
            {
                case TutorialGoal.Look:
                    if (walking) { _tutorialAmount += turned; }
                    return _tutorialAmount >= step.Amount;
                case TutorialGoal.Move:
                    if (walking) { _tutorialAmount += walked; }
                    return _tutorialAmount >= step.Amount;
                case TutorialGoal.Run:
                    if (walking && walked > 0.001f && input.Down(InputAction.Run)) { _tutorialAmount += dt; }
                    return _tutorialAmount >= step.Amount;
                case TutorialGoal.Jump:
                    return walking && gunFree && !_player.Flying && input.Pressed(InputAction.Jump);
                case TutorialGoal.Fly:
                    return TutorialRoundTrip(_player.Flying);
                case TutorialGoal.Level:
                    return !string.Equals(CurrentLevelName, _tutorialStartLevel, StringComparison.Ordinal);
                case TutorialGoal.GoHome:
                    return _tutorialHomeEvent || (!_paused && gunFree && input.Pressed(InputAction.GoHome));
                case TutorialGoal.PickGun:
                    return !_unarmed && _activeGun != _tutorialStartGun;
                case TutorialGoal.UseScan:
                    return walking && !_unarmed && _activeGun == 0 && _window.IsCaptured && input.Pressed(InputAction.GunPrimary);
                case TutorialGoal.Measure:
                    return _measureGun.Count > _tutorialStartCount;
                case TutorialGoal.PickComment:
                    return !_unarmed && _guns[_activeGun] == _commentGun;
                case TutorialGoal.Minimap:
                    return _showMap != _tutorialStartFlag;
                case TutorialGoal.SiteMap:
                    return TutorialRoundTrip(_siteMapOpen);
                case TutorialGoal.Help:
                    return _showHelp;
                case TutorialGoal.HideUi:
                    return TutorialRoundTrip(_uiHidden);
                case TutorialGoal.Pause:
                    return _paused;
                case TutorialGoal.MenuSearch:
                    return _paused && _menuSearchLength > 0;
                case TutorialGoal.InterfaceTab:
                    return _paused && !AnyBigPanelOpen && _rightTab == Array.IndexOf(RIGHT_TABS, "INTERFACE");
                case TutorialGoal.Resume:
                    return !_paused;
                case TutorialGoal.Bookmark:
                    return (Bookmarks?.Bookmarks.Count ?? 0) > _tutorialStartCount;
                case TutorialGoal.SectionBox:
                    return TutorialRoundTrip(_sectionOpen);
                case TutorialGoal.Shadows:
                    return ShadowsOn != _tutorialStartFlag;
                case TutorialGoal.FindRoom:
                    return _roomsOpen;
                case TutorialGoal.PhotoMode:
                    return TutorialRoundTrip(_photoOpen);
                case TutorialGoal.Orbit:
                    if (_viewer && !_viewerWalk && _viewerDrag == 1 && _viewerDragged) { _tutorialAmount += orbited; }
                    return _tutorialAmount >= step.Amount;
                case TutorialGoal.Pan:
                    if (_viewer && !_viewerWalk && _viewerDrag == 2 && _viewerDragged) { _tutorialAmount += mouseMoved; }
                    return _tutorialAmount >= step.Amount;
                case TutorialGoal.Zoom:
                    if (_viewer && !_viewerWalk && !_paused && input.Wheel != 0 && !ViewerOverUi(mouse)) { _tutorialAmount += Math.Abs(input.Wheel); }
                    return _tutorialAmount >= step.Amount;
                case TutorialGoal.ViewerWalk:
                    return _viewer && TutorialRoundTrip(_viewerWalk);
                case TutorialGoal.ViewerTool:
                    return _viewer && (!_unarmed || _sectionOpen || _viewerPopup != VP_NONE || _siteMapOpen);
                default:
                    return false;
            }
        }

        #endregion

        #region The menu's TUTORIAL card

        /// <summary>Pause menu → TUTORIAL: the card over the menu.</summary>
        private void OpenTutorialPicker()
        {
            _tutorialPickerOpen = true;
            CloseOverlay();
        }

        /// <summary>
        /// Per frame while the card shows: it has every key and click (Esc closes it; the menu under it sees nothing).
        /// </summary>
        /// <returns>False when it has closed (the menu carries on as usual this frame).</returns>
        private bool UpdateTutorialPicker(InputState input)
        {
            if (!_paused || _viewer)
            {
                _tutorialPickerOpen = false;
                return false;
            }
            _window.FocusLost = false;
            if (input.Pressed(InputAction.Back) || input.Pressed(InputAction.PadMenuBack))
            {
                _tutorialPickerOpen = false;
                input.ConsumeFrame();
                return true;
            }
            _tutorialClick = input.LeftPressed;
            input.ConsumeFrame(); // typing must not go into the menu's search under the card
            return true;
        }

        /// <summary>The card: play it all, or one chapter (done ones ticked); tips; the guide; CLOSE.</summary>
        private void BuildTutorialPicker(int width, int height)
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            TutorialChapter[] chapters = Tutorials.FULL;
            int total = Tutorials.StepCount(chapters, TutorialHas);

            float w = MathF.Min(S(580), width - S(32)), pad = S(22);
            float fixedH = pad * 2f + S(34) + S(44) + S(52) + S(14) + S(14) + S(34) + S(44);
            float rowH = Math.Clamp((height - S(40) - fixedH) / chapters.Length, S(34), S(56));
            bool blurbs = rowH >= S(50);
            float h = fixedH + rowH * chapters.Length;
            float x = MathF.Round((width - w) * 0.5f), y = MathF.Round(MathF.Max(S(16), (height - h) * 0.5f));
            float ix = x + pad, iw = w - pad * 2f;

            _ui.Rect(0, 0, width, height, Rgba.Hex(0x000000, 0.45f));
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.Accent(0.5f));
            float cy = y + pad;
            _ui.Text(f.Bold, ix, cy, "TUTORIAL", UiTheme.TEXT, S(1.5f));
            DrawMenuIcon(MI_TUTORIAL, ix + iw - S(10), cy + S(10), S(9), UiTheme.ACCENT);
            cy += S(34);
            _ui.TextWrapped(f.Small, ix, cy, iw, "Do what each step asks and it moves on by itself, or press Enter (or NEXT) to skip it. Play it all, or just one chapter.",
                UiTheme.TEXT_MUTED, maxLines: 2);
            cy += S(44);

            bool allDone = chapters.All(c => _tutorialChaptersDone.Contains(c.Id));
            int labelKey = total * 2 + (allDone ? 1 : 0);
            if (_tutorialPlayAllLabel == null || labelKey != _tutorialPlayAllFor)
            {
                _tutorialPlayAllFor = labelKey;
                _tutorialPlayAllLabel = $"{(allDone ? "PLAY IT ALL AGAIN" : "PLAY IT ALL")} ({total} STEPS)";
            }
            if (TutorialButton(f, input, ix, cy, iw, S(40), _tutorialPlayAllLabel, primary: true))
            {
                StartTutorial(chapters);
                return;
            }
            cy += S(52);

            // The chapters
            _ui.Rect(ix, cy, iw, MathF.Max(1f, UiScale), UiTheme.DIVIDER);
            cy += S(14);
            for (int i = 0; i < chapters.Length; i++)
            {
                TutorialChapter chapter = chapters[i];
                int steps = Tutorials.StepCount(new[] { chapter }, TutorialHas);
                bool done = _tutorialChaptersDone.Contains(chapter.Id);
                float rh = rowH - S(4);
                bool hover = Hover(input, ix, cy, iw, rh);
                if (hover) { _ui.RoundRect(ix, cy, iw, rh, S(UiTheme.RADIUS_SMALL), UiTheme.Ink(0.08f)); }

                // A tick when done, else the chapter's number
                float bx = ix + S(18), by = cy + rh * 0.5f;
                if (done)
                {
                    _ui.Circle(bx, by, S(11), UiTheme.Accent(0.9f), 18);
                    DrawTick(bx, by, S(6), UiTheme.ON_ACCENT);
                }
                else
                {
                    _ui.Ring(bx, by, S(11), MathF.Max(1f, S(1.5f)), UiTheme.CONTROL_BORDER, 18);
                    _ui.TextCentred(f.Small, bx, by - f.Small.LineHeight * 0.5f, NUMBERS[i % NUMBERS.Length], UiTheme.TEXT_MUTED);
                }
                float tx = ix + S(42), tw = iw - S(42) - S(90);
                float titleY = blurbs ? cy + S(8) : by - f.Body.LineHeight * 0.5f;
                _ui.Text(f.Body, tx, titleY, chapter.Title, hover ? UiTheme.ACCENT : UiTheme.TEXT);
                if (blurbs) { _ui.TextWrapped(f.Small, tx, cy + S(28), tw, chapter.Blurb, UiTheme.TEXT_FAINT, maxLines: 1); }
                _ui.TextRight(f.Small, ix + iw - S(12), by - f.Small.LineHeight * 0.5f, Text.Clear().Append(steps).Append(steps == 1 ? " STEP" : " STEPS").Span, UiTheme.TEXT_MUTED, S(0.8f));
                if (hover && _tutorialClick)
                {
                    _tutorialClick = false;
                    Sound.Play(SoundId.UiClick);
                    StartTutorial(new[] { chapter });
                    return;
                }
                cy += rowH;
            }

            // Tips
            cy += S(14);
            _ui.Rect(ix, cy - S(8), iw, MathF.Max(1f, UiScale), UiTheme.DIVIDER);
            cy += S(4);
            if (TutorialCheckbox(f, input, ix, cy, iw * 0.62f, "Tips the first time I use a feature", _tipsOn))
            {
                _tipsOn = !_tipsOn;
                SaveSettings();
            }
            if (_tipsSeen.Count > 0 && _tipsOn && TutorialButton(f, input, ix + iw - S(170), cy - S(6), S(170), S(28), "SHOW TIPS AGAIN", primary: false))
            {
                _tipsSeen.Clear();
                SaveSettings();
                Toast("Tips will show again the first time you use each feature", 3f);
            }
            cy += S(34);

            // The guide · CLOSE
            float bw = (iw - S(UiTheme.GAP_SMALL)) * 0.5f;
            if (TutorialButton(f, input, ix, cy + S(4), bw, S(36), "ONLINE GUIDE", primary: false)) { OpenDocs(); }
            if (TutorialButton(f, input, ix + bw + S(UiTheme.GAP_SMALL), cy + S(4), bw, S(36), "CLOSE", primary: false)) { _tutorialPickerOpen = false; }
        }

        private static readonly string[] NUMBERS = { "1", "2", "3", "4", "5", "6", "7", "8", "9" };

        #endregion

        #region Tips

        /// <summary>
        /// The first time a feature is used: its tip (once per PC; not with tips off, during the tutorial or the welcome
        /// card, in kiosk mode, or while recording).
        /// </summary>
        private void ShowTip(string id)
        {
            if (!_tipsArmed || !_tipsOn || _kiosk || _tutorial != null || _welcomeOpen || _video != null || _tipsSeen.Contains(id)) { return; }
            TutorialTip tip = TutorialTips.Find(id);
            if (tip == null) { return; }
            _tip = tip;
            _tipText = Tutorials.Format(tip.Text, Key);
            _tipFrom = _clock;
            _tipsSeen.Add(id);
            SaveSettings();
        }

        /// <summary>The selected gun's tip (the walkthrough's guns; the viewer has its own tool tips).</summary>
        private void ShowGunTip(int index)
        {
            if (!_viewer && index >= 0 && index < GUN_TIPS.Length) { ShowTip(GUN_TIPS[index]); }
        }

        /// <summary>A viewer toolbar tool's tip.</summary>
        private void ShowViewerTip(int tool)
        {
            string id = tool switch
            {
                VT_MEASURE => "viewer-measure",
                VT_SECTION => "viewer-section",
                VT_COMMENT => "viewer-comment",
                VT_VIEWS => "viewer-views",
                VT_SUN => "viewer-sun",
                VT_PHOTOS => "viewer-photos",
                VT_MAP => "viewer-map",
                _ => null
            };
            if (id != null) { ShowTip(id); }
        }

        /// <summary>The tip card, bottom left: TIP, the title, the text; GOT IT and NO MORE TIPS when the cursor is free.</summary>
        private void BuildTip(int width, int height)
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            float shown = _clock - _tipFrom;
            float alpha = Math.Clamp((TIP_SECONDS - shown) / 0.5f, 0f, 1f) * Math.Clamp(shown / 0.2f, 0f, 1f);
            if (alpha <= 0f)
            {
                _tipRect = Vector4.Zero;
                return;
            }
            bool free = !_window.IsCaptured;
            float w = MathF.Min(S(380), width - S(32)), pad = S(16), iw = w - pad * 2f;
            float textH = _ui.TextWrapped(f.Body, 0f, 0f, iw, _tipText, 0u, maxLines: 4, draw: false);
            float h = pad + S(22) + textH + S(10) + (free ? S(34) : S(4)) + pad;
            float x = S(UiTheme.COLUMN_MARGIN), y = height - h - S(UiTheme.COLUMN_MARGIN);
            _tipRect = new Vector4(x, y, w, h);

            _ui.Opacity = alpha;
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.Accent(0.5f));
            float tx = _ui.Text(f.Small, x + pad, y + pad + S(2), "TIP", UiTheme.ACCENT, S(1.5f));
            _ui.Text(f.Bold, x + pad + tx + S(10), y + pad - S(1), _tip.Title, UiTheme.TEXT);
            float cy = y + pad + S(22);
            _ui.TextWrapped(f.Body, x + pad, cy, iw, _tipText, UiTheme.TEXT_SOFT, maxLines: 4);
            cy += textH + S(10);
            if (free)
            {
                float bw = (iw - S(UiTheme.GAP_SMALL)) * 0.5f;
                if (TutorialButton(f, input, x + pad, cy, bw, S(28), "GOT IT", primary: true)) { _tip = null; }
                if (TutorialButton(f, input, x + pad + bw + S(UiTheme.GAP_SMALL), cy, bw, S(28), "NO MORE TIPS", primary: false))
                {
                    _tip = null;
                    _tipsOn = false;
                    SaveSettings();
                    Toast("Tips are off (SETTINGS → INTERFACE turns them back on)", 3.5f);
                }
            }
            else
            {
                // Walking (the mouse is taken): how long it stays
                float left = 1f - shown / TIP_SECONDS;
                _ui.Rect(x + pad, cy, iw * left, MathF.Max(1f, S(2)), UiTheme.Accent(0.6f));
            }
            _ui.Opacity = 1f;
        }

        #endregion

        #region Drawing

        /// <summary>A spot's rectangle as drawn this frame (x, y, w, h), for the step pointing at it.</summary>
        private void MarkSpot(TutorialSpot spot, float x, float y, float w, float h)
        {
            if (_tutorial == null) { return; }
            int i = (int)spot;
            _spotRects[i] = new Vector4(x, y, w, h);
            _spotFrames[i] = _tutorialFrame;
        }

        /// <summary>
        /// Last in the frame's UI (over the HUD, the viewer and the menu): the welcome card, the menu's TUTORIAL card, the
        /// step card and the tip.
        /// </summary>
        private void BuildTutorialOverlay(int width, int height)
        {
            _ui.Opacity = 1f;
            if (_kiosk || _video != null)
            {
                _tutorialCardRect = _tipRect = Vector4.Zero;
            }
            else if (_welcomeOpen)
            {
                BuildWelcome(width, height);
                _tutorialCardRect = _tipRect = Vector4.Zero;
            }
            else if (_tutorialPickerOpen && _paused)
            {
                BuildTutorialPicker(width, height);
                _tutorialCardRect = _tipRect = Vector4.Zero;
            }
            else
            {
                if (_tutorial != null && !_tourActive) { BuildTutorialCard(width, height); }
                else { _tutorialCardRect = Vector4.Zero; }
                if (_tip != null && !_uiHidden) { BuildTip(width, height); }
                else { _tipRect = Vector4.Zero; }
            }
            _tutorialFrame++;
        }

        /// <summary>The step's texts with the keys filled in (again when the step or a key changes).</summary>
        private void RefreshTutorialText(TutorialStep step, bool keysLive)
        {
            int version = Hotkeys.Version * 4 + (keysLive ? 2 : 0) + (_window.IsCaptured ? 1 : 0);
            if (ReferenceEquals(step, _tutorialTextFor) && version == _tutorialTextVersion) { return; }
            _tutorialTextFor = step;
            _tutorialTextVersion = version;
            _tutorialText = Tutorials.Format(step.Text, Key);
            _tutorialHeader = $"TUTORIAL · {_tutorial.Chapter.Title.ToUpperInvariant()}";
            _tutorialCount = $"STEP {_tutorial.Index + 1} OF {_tutorial.Count}";
            string label = step.Goal == TutorialGoal.None ? (_tutorial.IsLast ? "FINISH" : "NEXT") : "SKIP";
            _tutorialSkipLabel = keysLive ? $"{label} ({KeyUpper(InputAction.TutorialNext)})" : label;
            _tutorialKeysHint = $"{Key(InputAction.TutorialNext)}: {(step.Goal == TutorialGoal.None ? "next" : "skip this step")} · Esc: the menu, where these buttons work";
        }

        /// <summary>
        /// The step card (bottom centre; at the top when the spot is low on the screen; bottom right over the menu), and
        /// the ring around the spot (with the rest dimmed when the step only explains).
        /// </summary>
        private void BuildTutorialCard(int width, int height)
        {
            TutorialStep step = _tutorial.Step;
            if (step == null) { return; }
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            bool keysLive = TutorialKeysLive;
            RefreshTutorialText(step, keysLive);
            bool done = _tutorialDoneAt >= 0f;
            bool free = !_window.IsCaptured;

            // The spot: a pulsing ring (and the rest dimmed for a step with nothing to do)
            int si = (int)step.Spot;
            bool spotShown = step.Spot != TutorialSpot.None && _spotFrames[si] == _tutorialFrame;
            Vector4 spot = _spotRects[si];
            if (spotShown)
            {
                float g = S(6);
                float sx = spot.X - g, sy = spot.Y - g, sw = spot.Z + g * 2f, sh = spot.W + g * 2f;
                if (step.Goal == TutorialGoal.None)
                {
                    uint dim = Rgba.Hex(0x000000, 0.35f);
                    _ui.Rect(0, 0, width, MathF.Max(0f, sy), dim);
                    _ui.Rect(0, sy + sh, width, MathF.Max(0f, height - sy - sh), dim);
                    _ui.Rect(0, sy, MathF.Max(0f, sx), sh, dim);
                    _ui.Rect(sx + sw, sy, MathF.Max(0f, width - sx - sw), sh, dim);
                }
                float pulse = 0.5f + 0.5f * MathF.Sin(_clock * 4f);
                _ui.RoundOutline(sx, sy, sw, sh, S(UiTheme.RADIUS), MathF.Max(2f, S(2.5f)), UiTheme.Accent(0.55f + 0.45f * pulse));
            }

            // Size
            float w = MathF.Min(S(560), width - S(32)), pad = S(18), iw = w - pad * 2f;
            float textH = _ui.TextWrapped(f.Body, 0f, 0f, iw, _tutorialText, 0u, maxLines: 5, draw: false);
            bool goal = step.Goal != TutorialGoal.None;
            float h = pad + S(18) + S(12) + S(26) + textH + (goal ? S(8) + S(20) : 0f) + S(12) + (free ? S(34) : S(20)) + pad;

            // Place: bottom centre (above the gun bar or the toolbar), at the top when the spot is in the lower half;
            // over the menu, bottom right (the sidebar is on the left, the viewer's menu in the middle)
            float x = MathF.Round((width - w) * 0.5f);
            float y = height - h - (_viewer ? S(104) : S(120));
            if (spotShown && spot.Y + spot.W * 0.5f > height * 0.5f) { y = S(64); }
            if (_paused)
            {
                x = width - w - S(24);
                y = height - h - S(24);
            }
            y = MathF.Max(S(8), y);
            _tutorialCardRect = new Vector4(x, y, w, h);

            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.Accent(0.55f));
            float cy = y + pad;

            // TUTORIAL · CHAPTER and STEP 3 OF 28, then the progress
            _ui.Text(f.Small, x + pad, cy, _tutorialHeader, UiTheme.ACCENT, S(1.2f));
            _ui.TextRight(f.Small, x + w - pad, cy, _tutorialCount, UiTheme.TEXT_MUTED, S(1f));
            cy += S(18);
            float bar = MathF.Max(2f, S(3));
            _ui.RoundRect(x + pad, cy, iw, bar, bar * 0.5f, UiTheme.Ink(0.12f));
            _ui.RoundRect(x + pad, cy, iw * (_tutorial.Index + (done ? 1f : 0.5f)) / _tutorial.Count, bar, bar * 0.5f, UiTheme.ACCENT);
            cy += S(12);

            // The step
            _ui.Text(f.Bold, x + pad, cy, step.Title, UiTheme.TEXT);
            cy += S(26);
            _ui.TextWrapped(f.Body, x + pad, cy, iw, _tutorialText, UiTheme.TEXT_SOFT, maxLines: 5);
            cy += textH;

            // What's asked: DONE (a tick), or YOUR TURN with how far along it is
            if (goal)
            {
                cy += S(8);
                if (done)
                {
                    _ui.Circle(x + pad + S(8), cy + S(9), S(8), UiTheme.GOOD, 16);
                    DrawTick(x + pad + S(8), cy + S(9), S(4.5f), UiTheme.PANEL_STRONG);
                    _ui.Text(f.Body, x + pad + S(24), cy, "Done", UiTheme.GOOD);
                }
                else
                {
                    float turn = _ui.Text(f.Small, x + pad, cy + S(2), "YOUR TURN", UiTheme.ACCENT, S(1.2f));
                    float fraction = step.Amount > 1f || step.Goal is TutorialGoal.Look or TutorialGoal.Move or TutorialGoal.Run or TutorialGoal.Orbit
                        ? Math.Clamp(_tutorialAmount / step.Amount, 0f, 1f) : -1f;
                    if (fraction >= 0f)
                    {
                        float px = x + pad + turn + S(12), pw = MathF.Min(S(160), iw - turn - S(12));
                        _ui.RoundRect(px, cy + S(8), pw, bar, bar * 0.5f, UiTheme.Ink(0.12f));
                        _ui.RoundRect(px, cy + S(8), pw * fraction, bar, bar * 0.5f, UiTheme.Accent(0.8f));
                    }
                }
                cy += S(20);
            }
            cy += S(12);

            // The buttons (cursor free), else how to skip with the keys
            if (!free)
            {
                _ui.TextWrapped(f.Small, x + pad, cy, iw, _tutorialKeysHint, UiTheme.TEXT_FAINT, maxLines: 1);
                return;
            }
            float bh = S(32), gap = S(UiTheme.GAP_SMALL);
            float nextW = MathF.Min(S(170), iw * 0.4f), backW = MathF.Min(S(90), iw * 0.2f), endW = MathF.Min(S(140), iw * 0.3f);
            if (TutorialButton(f, input, x + pad, cy, endW, bh, "END TUTORIAL", primary: false))
            {
                EndTutorial(finished: false);
                return;
            }
            if (!_tutorial.IsFirst && TutorialButton(f, input, x + w - pad - nextW - gap - backW, cy, backW, bh, "BACK", primary: false))
            {
                BackTutorialStep();
                return;
            }
            if (TutorialButton(f, input, x + w - pad - nextW, cy, nextW, bh, _tutorialSkipLabel, primary: true)) { NextTutorialStep(); }
        }

        /// <summary>A button on the tutorial's cards: clicked with the click taken at the start of the frame.</summary>
        private bool TutorialButton(FontAtlas f, InputState input, float x, float y, float w, float h, string label, bool primary)
        {
            bool hover = !_window.IsCaptured && Hover(input, x, y, w, h);
            float r = S(UiTheme.RADIUS_SMALL);
            if (primary) { _ui.RoundRect(x, y, w, h, r, hover ? UiTheme.ACCENT : UiTheme.Accent(0.85f)); }
            else
            {
                _ui.RoundRect(x, y, w, h, r, hover ? UiTheme.Ink(0.14f) : UiTheme.Ink(0.06f));
                _ui.RoundOutline(x, y, w, h, r, MathF.Max(1f, UiScale), UiTheme.CONTROL_BORDER);
            }
            _ui.TextCentred(f.Body, x + w * 0.5f, y + h * 0.5f - f.Body.LineHeight * 0.5f, label, primary ? UiTheme.ON_ACCENT : hover ? UiTheme.TEXT : UiTheme.TEXT_SOFT);
            if (!hover || !_tutorialClick) { return false; }
            _tutorialClick = false;
            Sound.Play(SoundId.UiClick);
            return true;
        }

        /// <summary>A checkbox on the tutorial's cards (true when clicked: the caller flips the value).</summary>
        private bool TutorialCheckbox(FontAtlas f, InputState input, float x, float y, float w, string label, bool value)
        {
            float box = S(16);
            bool hover = Hover(input, x, y - S(3), w, S(24));
            _ui.RoundRect(x, y, box, box, S(4), value ? UiTheme.ACCENT : UiTheme.CONTROL);
            _ui.RoundOutline(x, y, box, box, S(4), MathF.Max(1f, UiScale), value ? UiTheme.ACCENT : UiTheme.CONTROL_BORDER);
            if (value) { DrawTick(x + box * 0.5f, y + box * 0.5f, box * 0.32f, UiTheme.ON_ACCENT); }
            _ui.Text(f.Body, x + box + S(10), y - S(1), label, hover ? UiTheme.ACCENT : UiTheme.TEXT);
            if (!hover || !_tutorialClick) { return false; }
            _tutorialClick = false;
            Sound.Play(SoundId.UiClick);
            return true;
        }

        /// <summary>A tick mark centred on (cx, cy), about <paramref name="r"/> from the centre to its ends.</summary>
        private void DrawTick(float cx, float cy, float r, uint colour)
        {
            float t = MathF.Max(1.5f, S(2f));
            _ui.Line(cx - r, cy, cx - r * 0.3f, cy + r * 0.7f, t, colour);
            _ui.Line(cx - r * 0.3f, cy + r * 0.7f, cx + r, cy - r * 0.7f, t, colour);
        }

        #endregion
    }
}
