using System.Diagnostics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Round Z-E, first run: a welcome card over the first walkthrough on this PC: how to move and look, the guns, F1 and
    /// the menu, with ONLINE GUIDE (the docs). Tutorial round: it asks whether to take the tutorial (TAKE THE TUTORIAL /
    /// NO THANKS), once per PC for the full walkthrough (also to those who saw the old card) and, in the simple viewer,
    /// once per viewer copy per PC (<see cref="TutorialOffers"/>). Esc is NO THANKS. Not in kiosk mode or a safe start.
    /// The player stands still and the cursor is free while it shows.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>The user guide (the repo's docs folder).</summary>
        private const string DOCS_URL = "https://github.com/aussieBIMguru/BimGo/tree/main/docs";

        private bool _welcomeOpen;
        private bool _welcomeFirst;     // tutorial round: BimGo's first walkthrough on this PC (else the tutorial is new to them)
        private string _welcomeOffer;   // the question, with the step count (made when the card opens)
        private System.Numerics.Vector4 _welcomeRect; // the card as last drawn (clicks outside it are nobody's)

        private static readonly KeyHint HINT_WELCOME_MOVE = new(() =>
        {
            KeyMap m = Hotkeys.Map;
            return $"{m.DescribeJoined(" ", true, InputAction.MoveForward, InputAction.MoveLeft, InputAction.MoveBack, InputAction.MoveRight)}  move  ·  mouse  look  ·  {Key(InputAction.Run)}  run";
        });
        private static readonly KeyHint HINT_WELCOME_MORE = new(() =>
            $"{Key(InputAction.Jump)}  jump  ·  {Key(InputAction.Crouch)}  crouch  ·  {Key(InputAction.Fly)}  fly through walls");
        private static readonly KeyHint HINT_WELCOME_GUNS = new(() =>
            $"{Hotkeys.Map.DescribeRange(InputAction.Gun1, 9)}  tool guns (scan, measure, comment…)  ·  {Key(InputAction.GunPrimary)} / {Key(InputAction.GunSecondary)}  use them");
        private static readonly KeyHint HINT_WELCOME_HELP = new(() =>
            $"{Key(InputAction.Help)}  the keys for what you're doing  ·  Esc  menu, settings and CONTROLS");

        #endregion

        #region Open / close

        /// <summary>The viewer copy's id (tutorial round), or null.</summary>
        private string ViewerCopyId => IsFileMode ? _options.Document?.Viewer?.Id : null;

        /// <summary>
        /// End of <see cref="Initialise"/> (after the viewer opened, for a viewer file): shows the card when the tutorial
        /// hasn't been offered here yet.
        /// </summary>
        private void OfferTutorialAtStart()
        {
            if (LaunchSettings.SafeStart || _kiosk) { return; }
            LaunchSettings settings = LaunchSettings.LoadOrDefault();
            if (!TutorialOffers.ShouldOffer(settings, _viewer, ViewerCopyId)) { return; }
            _welcomeFirst = !settings.WelcomeShown;
            OpenWelcome();
        }

        /// <summary>Shows the card.</summary>
        private void OpenWelcome()
        {
            _welcomeOpen = true;
            int steps = Tutorials.StepCount(Tutorials.For(_viewer), TutorialHas);
            _welcomeOffer = _viewer
                ? $"Would you like a quick tutorial? {steps} short steps show you how to look around this model."
                : $"Would you like the tutorial? {steps} short steps in {Tutorials.FULL.Length} chapters show you around. Stop it whenever you like; " +
                  "menu → TUTORIAL brings it back, or plays one chapter.";
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
        }

        /// <summary>
        /// Closes the card for good (remembered in the settings). Taking the tutorial starts it with the cursor free (its
        /// first card has NEXT); NO THANKS captures the mouse to start walking.
        /// </summary>
        private void CloseWelcome(bool takeTutorial)
        {
            if (!_welcomeOpen) { return; }
            _welcomeOpen = false;
            Scene.Settings.WelcomeShown = true;
            LaunchSettings settings = LaunchSettings.LoadOrDefault();
            TutorialOffers.MarkOffered(settings, _viewer, ViewerCopyId);
            settings.Save();
            _window.Input.ReleaseAll();
            if (takeTutorial)
            {
                StartTutorial(Tutorials.For(_viewer));
                return;
            }
            Toast(_viewer ? "The menu's TUTORIAL shows it whenever you like" : "Esc → TUTORIAL shows it whenever you like", 4f);
            if (_window.IsActive && !_viewer) { _window.SetCaptured(true); }
        }

        /// <summary>Opens the user guide in the browser (never throws).</summary>
        private void OpenDocs()
        {
            try
            {
                Process.Start(new ProcessStartInfo(DOCS_URL) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"The guide could not be opened: {ex.Message}");
                Toast($"Open {DOCS_URL} in a browser", 6f, important: true);
            }
        }

        /// <summary>
        /// Per frame while the card shows (instead of the walkthrough's keys): Esc is NO THANKS; clicks outside the card
        /// are dropped (the HUD or the viewer's toolbar under it stays still).
        /// </summary>
        private void UpdateWelcome(InputState input)
        {
            _window.FocusLost = false;
            if (!Inside(_welcomeRect, input.MousePosition))
            {
                input.ConsumeClicks();
                input.ConsumeWheel();
            }
            if (input.Pressed(InputAction.Back))
            {
                input.ConsumeFrame();
                CloseWelcome(takeTutorial: false);
            }
        }

        #endregion

        #region Drawing

        /// <summary>The card, centred over the HUD (the viewer's: the question only).</summary>
        private void BuildWelcome(int width, int height)
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            string[] lines = _viewer ? Array.Empty<string>() : new[] { HINT_WELCOME_MOVE.Text, HINT_WELCOME_MORE.Text, HINT_WELCOME_GUNS.Text, HINT_WELCOME_HELP.Text };

            float textW = 0f;
            foreach (string line in lines) { textW = MathF.Max(textW, UiBatch.Measure(f.Body, line)); }
            float w = MathF.Min(MathF.Max(textW + S(64), S(540)), width - S(40));
            float lineH = S(26);
            float offerW = w - S(64);
            float offerH = _ui.TextWrapped(f.Body, 0f, 0f, offerW, _welcomeOffer, 0u, maxLines: 4, draw: false);
            float h = S(64) + lines.Length * lineH + (lines.Length > 0 ? S(14) : 0f) + offerH + S(24) + S(40) + S(28);
            float x = MathF.Round((width - w) * 0.5f), y = MathF.Round((height - h) * 0.5f);
            _welcomeRect = new System.Numerics.Vector4(x, y, w, h);

            _ui.Rect(0, 0, width, height, Rgba.Hex(0x000000, 0.35f));
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, Rgba.WithAlpha(UiTheme.ACCENT, 0.6f));
            string title = _viewer ? "WELCOME" : _welcomeFirst ? "WELCOME TO BIMGO" : "NEW: THE TUTORIAL";
            _ui.TextCentred(f.Title, width * 0.5f, y + S(18), title, UiTheme.TEXT, S(2));

            float ty = y + S(64);
            foreach (string line in lines)
            {
                _ui.TextCentred(f.Body, width * 0.5f, ty, line, UiTheme.TEXT_SOFT);
                ty += lineH;
            }
            if (lines.Length > 0) { ty += S(14); }
            _ui.TextWrapped(f.Body, x + S(32), ty, offerW, _welcomeOffer, UiTheme.TEXT, maxLines: 4);
            ty += offerH;

            // NO THANKS · TAKE THE TUTORIAL, and the online guide beside them (the full walkthrough)
            int buttons = _viewer ? 2 : 3;
            float gap = S(12);
            float buttonW = MathF.Min(S(200), (w - S(48) - gap * (buttons - 1)) / buttons);
            float by = ty + S(24);
            float bx = MathF.Round(width * 0.5f - (buttonW * buttons + gap * (buttons - 1)) * 0.5f);
            if (!_viewer)
            {
                if (MenuButton(f, bx, by, buttonW, "ONLINE GUIDE", false, false, height: S(40), compact: true)) { OpenDocs(); }
                bx += buttonW + gap;
            }
            if (MenuButton(f, bx, by, buttonW, "NO THANKS", false, false, height: S(40), compact: true))
            {
                input.ConsumeClicks();
                CloseWelcome(takeTutorial: false);
                return;
            }
            if (MenuButton(f, bx + buttonW + gap, by, buttonW, _viewer ? "SHOW ME" : "TAKE THE TUTORIAL", primary: true, danger: false, height: S(40), compact: true))
            {
                input.ConsumeClicks();
                CloseWelcome(takeTutorial: true);
            }
        }

        #endregion
    }
}
