using System.Text;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Tutorial round: what a step asks the user to do. The app watches for it and moves on by itself; every step can
    /// also be skipped (<see cref="InputAction.TutorialNext"/> or the card's NEXT). "Round trip" goals (open something,
    /// then close it) are done when the thing closes again.
    /// </summary>
    public enum TutorialGoal : byte
    {
        /// <summary>Nothing to do: read it and press NEXT.</summary>
        None,

        // Walking
        Look, Move, Run, Jump, Fly, Level, GoHome,

        // Guns
        PickGun, UseScan, Measure, PickComment,

        // On screen
        Minimap, SiteMap, Help, HideUi,

        // The menu
        Pause, MenuSearch, InterfaceTab, Resume,

        // Review and media
        Bookmark, SectionBox, Shadows, FindRoom, PhotoMode,

        // The simple viewer
        Orbit, Pan, Zoom, ViewerWalk, ViewerTool
    }

    /// <summary>Tutorial round: the part of the screen a step points at (highlighted around it), or none.</summary>
    public enum TutorialSpot : byte
    {
        None,
        Crosshair, GunBar, Status, Minimap,
        Sidebar, MenuSearch, SettingsTabs,
        ViewerBar, ViewerMap, ViewerHome, ViewerPhotos
    }

    /// <summary>Tutorial round: what a step needs to make sense (steps whose needs aren't met are left out).</summary>
    [Flags]
    public enum TutorialNeed : byte
    {
        None = 0,

        /// <summary>The model has more than one level.</summary>
        Levels = 1,

        /// <summary>The model has rooms.</summary>
        Rooms = 2,

        /// <summary>Site photos are placed on the model.</summary>
        SitePhotos = 4
    }

    /// <summary>
    /// One step: a title, the text (key names in braces, "{Fly}", as <see cref="Tutorials.Format"/> fills them in), what
    /// to do and where to look. Amount: how much of the goal is enough (metres walked, seconds run, radians looked or
    /// orbited, pixels panned, wheel notches).
    /// </summary>
    public sealed record TutorialStep(string Id, string Title, string Text, TutorialGoal Goal = TutorialGoal.None, TutorialSpot Spot = TutorialSpot.None,
        TutorialNeed Need = TutorialNeed.None, float Amount = 1f);

    /// <summary>A chapter: a few steps on one subject (the menu's TUTORIAL can play one on its own).</summary>
    public sealed record TutorialChapter(string Id, string Title, string Blurb, TutorialStep[] Steps);

    /// <summary>
    /// Tutorial round: the two tutorials. The full walkthrough's is long, in chapters; the simple viewer's (a viewer copy
    /// sent to someone) is one short chapter.
    /// </summary>
    public static class Tutorials
    {
        #region The full walkthrough

        public static readonly TutorialChapter[] FULL =
        {
            new("moving", "Moving around", "Look, walk, run, fly and get back home.", new TutorialStep[]
            {
                new("welcome", "Welcome to the tutorial",
                    "It shows BimGo one step at a time. Do what a step asks and it moves on by itself, or press {TutorialNext} to skip it. " +
                    "Esc opens the menu: the tutorial waits there, with its buttons."),
                new("look", "Look around", "Click the view to take the mouse, then move the mouse to look around.", TutorialGoal.Look, TutorialSpot.Crosshair, Amount: 2f),
                new("walk", "Walk", "Walk with {MoveForward} {MoveLeft} {MoveBack} {MoveRight} (or the arrow keys).", TutorialGoal.Move, Amount: 3f),
                new("run", "Run", "Hold {Run} while you walk to run.", TutorialGoal.Run, Amount: 1f),
                new("jump", "Jump and crouch", "{Jump} jumps and {Crouch} crouches. Try a jump.", TutorialGoal.Jump),
                new("fly", "Fly through walls",
                    "{Fly} flies, through walls and floors: {Jump} goes up, {Crouch} down. Press {Fly} again to walk (you drop to the floor).", TutorialGoal.Fly),
                new("level", "Change level", "{LevelUp} and {LevelDown} take you to the level above or below.", TutorialGoal.Level, TutorialSpot.Status, TutorialNeed.Levels),
                new("home", "Home", "{GoHome} takes you back to where you started. {SetHome} makes where you stand the new home.", TutorialGoal.GoHome)
            }),
            new("guns", "Tool guns", "Scan, measure, comment and more, on the number keys.", new TutorialStep[]
            {
                new("guns", "Your tools are guns",
                    "Each tool is a gun on a number key, {Gun1} to {Gun9}. Hold {GunWheel} for a wheel of them; {Unarmed} puts the gun away. Pick another gun now.",
                    TutorialGoal.PickGun, TutorialSpot.GunBar),
                new("scan", "Scan", "{Gun1} is Scan: aim at anything to see what it is (category, family, parameters). Pick it and {GunPrimary} locks on to what you aim at.",
                    TutorialGoal.UseScan, TutorialSpot.GunBar),
                new("measure", "Measure",
                    "{Gun2} is Measure: click one point, then another, for a distance. {Gun2} again gives area, angle and clearance; {ClearMarkers} clears. Measure something.",
                    TutorialGoal.Measure, TutorialSpot.GunBar),
                new("comment", "Comments",
                    "{Gun4} is Comment: aim at a problem and click. It keeps a picture of the view you can draw on, and your note. Pick it now.",
                    TutorialGoal.PickComment, TutorialSpot.GunBar),
                new("more-guns", "More guns",
                    "{Gun3} Portal, {Gun5} Teleport, {Gun6} Demolish, {Gun7} Gizmo (move things), {Gun8} Clone and {Gun9} Place (furniture, people, trees). " +
                    "Each shows its keys in its own panel, and {Help} lists them.", Spot: TutorialSpot.GunBar)
            }),
            new("screen", "On screen", "The status, the minimap, the site map, help and a clean view.", new TutorialStep[]
            {
                new("status", "Where you are", "Top left: walking or flying, the level and the room you're in.", Spot: TutorialSpot.Status),
                new("minimap", "The minimap", "Top right: the plan around you. {Minimap} hides or shows it.", TutorialGoal.Minimap, TutorialSpot.Minimap),
                new("site-map", "The site map",
                    "{SiteMap} opens the big site map: drag to pan, scroll to zoom, double-click to go there. {SiteMap} again closes it.", TutorialGoal.SiteMap, TutorialSpot.Minimap),
                new("help", "Help", "{Help} shows the keys for whatever you're doing. Try it.", TutorialGoal.Help),
                new("hide-ui", "A clean view", "{HideUi} hides everything for a clean view. Press it again (or Esc) to bring it back.", TutorialGoal.HideUi)
            }),
            new("menu", "The menu", "Everything else, the settings, and saving.", new TutorialStep[]
            {
                new("pause", "Open the menu", "Press Esc. The walkthrough pauses and the menu opens.", TutorialGoal.Pause),
                new("sidebar", "Everything is here",
                    "In sections: MODEL (saving, home, materials), REVIEW (comments, bookmarks, clashes, rooms), MEDIA (pictures, video, people and trees, " +
                    "site photos) and SETTINGS.", Spot: TutorialSpot.Sidebar),
                new("search", "Find anything", "Just type to search the menu. Try \"sun\".", TutorialGoal.MenuSearch, TutorialSpot.MenuSearch),
                new("interface", "Make it yours",
                    "SETTINGS → INTERFACE: a light or dark theme, the accent colour, sizes, fonts and how much shows while you walk. Open it.",
                    TutorialGoal.InterfaceTab, TutorialSpot.SettingsTabs),
                new("save", "Saving",
                    "{Save} saves. The file keeps your comments, bookmarks, measurements, site photos and materials. " +
                    "SAVE VIEWER COPY… makes a simple version to send to someone who doesn't use BimGo.", Spot: TutorialSpot.Sidebar),
                new("resume", "Back to the model", "Press Esc (or RESUME) to carry on.", TutorialGoal.Resume)
            }),
            new("review", "Review tools", "Bookmarks, cutting the model open, the sun and rooms.", new TutorialStep[]
            {
                new("bookmark", "Bookmarks",
                    "{AddBookmark} saves this view as a bookmark, and {Bookmark1} to {Bookmark9} jump back. Menu → BOOKMARKS plays them as a tour. Add one.",
                    TutorialGoal.Bookmark),
                new("section", "Section box",
                    "{SectionBox} opens the section box: drag its handles to cut the model open. {SectionBox} or Esc closes it and the cut stays " +
                    "({SectionClear} clears it). Try it.", TutorialGoal.SectionBox),
                new("sun", "Sun and shadows",
                    "{Shadows} turns the sun's shadows on or off; {SunEarlier} and {SunLater} change the time. {SunPanel} opens the sun panel for the date and place.",
                    TutorialGoal.Shadows),
                new("rooms", "Find a room", "{FindRoom} lists the rooms: pick one to go there.", TutorialGoal.FindRoom, Need: TutorialNeed.Rooms),
                new("site-photos", "Site photos",
                    "Menu → SITE PHOTOS imports photos from site: they pin to where they were taken. Click a pin to look through the photo; the site map shows them all.")
            }),
            new("media", "Pictures and video", "Photo mode, the media panel, and where to find help.", new TutorialStep[]
            {
                new("photo", "Photo mode",
                    "{PhotoMode} opens photo mode: a clean frame, the wheel zooms and {PhotoTake} takes the picture. {PhotoMode} or Esc closes it. Try it.",
                    TutorialGoal.PhotoMode),
                new("media", "Media", "{MediaPanel} opens MEDIA: pictures, the bookmark tour, camera paths (flythroughs) and video."),
                new("done", "That's the tutorial",
                    "Menu → TUTORIAL plays it, or any chapter, again. A tip shows the first time you use a feature (SETTINGS → INTERFACE turns them off), " +
                    "and {Help} lists the keys at any time.")
            })
        };

        #endregion

        #region The simple viewer

        public static readonly TutorialChapter[] VIEWER =
        {
            new("viewer", "The simple viewer", "Look around the model, walk inside, and the toolbar.", new TutorialStep[]
            {
                new("welcome", "Welcome",
                    "This is a 3D model to look around. A few short steps show you how: do what a step asks and it moves on by itself, " +
                    "or press {TutorialNext} (or NEXT) to skip it."),
                new("orbit", "Turn around the model", "Drag with the left mouse button to turn around the model.", TutorialGoal.Orbit, Amount: 1.2f),
                new("pan", "Slide the view", "Drag with the right mouse button to slide the view.", TutorialGoal.Pan, Amount: 150f),
                new("zoom", "Zoom", "Scroll to zoom in and out, toward the mouse. Double-click something to fly closer to it.", TutorialGoal.Zoom, Amount: 3f),
                new("walk", "Walk inside",
                    "{Fly} switches to walking: {MoveForward} {MoveLeft} {MoveBack} {MoveRight} walk and dragging looks around. Press {Fly} again to go back to " +
                    "turning around the model.", TutorialGoal.ViewerWalk),
                new("toolbar", "The toolbar",
                    "At the bottom: measure, section box, comments, views, the map and the sun. Hover a button for its name and key, then try one.",
                    TutorialGoal.ViewerTool, TutorialSpot.ViewerBar),
                new("photos", "Site photos",
                    "This button ({ViewerPhotos}) shows the photos taken on site, pinned where they were taken: click one to look through it.",
                    Spot: TutorialSpot.ViewerPhotos, Need: TutorialNeed.SitePhotos),
                new("map", "The map", "Top right, the map of where you are: scroll it to zoom. {SiteMap} opens the big map.", Spot: TutorialSpot.ViewerMap),
                new("home", "Back to the start", "{GoHome} (or the house button) takes you back to the start view.", TutorialGoal.GoHome, TutorialSpot.ViewerHome),
                new("done", "That's it", "The menu (Esc, or the last button) has the views, the comments and TUTORIAL to see this again. Enjoy the model.")
            })
        };

        #endregion

        #region Helpers

        /// <summary>The full walkthrough's chapters, or the simple viewer's.</summary>
        public static TutorialChapter[] For(bool viewer) => viewer ? VIEWER : FULL;

        /// <summary>A chapter by id (null when unknown).</summary>
        public static TutorialChapter Chapter(bool viewer, string id) =>
            For(viewer).FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

        /// <summary>How many of the chapters' steps apply (needs met).</summary>
        public static int StepCount(IEnumerable<TutorialChapter> chapters, Func<TutorialNeed, bool> has) =>
            chapters.Sum(c => c.Steps.Count(s => Applies(s, has)));

        /// <summary>True when a step's needs are met.</summary>
        public static bool Applies(TutorialStep step, Func<TutorialNeed, bool> has)
        {
            foreach (TutorialNeed need in new[] { TutorialNeed.Levels, TutorialNeed.Rooms, TutorialNeed.SitePhotos })
            {
                if ((step.Need & need) != 0 && (has == null || !has(need))) { return false; }
            }
            return true;
        }

        /// <summary>
        /// A step's text with the keys filled in: "{Fly}" becomes the action's key as <paramref name="key"/> names it ("V").
        /// Braces around anything that isn't an action stay as they are.
        /// </summary>
        public static string Format(string text, Func<InputAction, string> key)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) { return text ?? string.Empty; }
            var sb = new StringBuilder(text.Length + 16);
            int i = 0;
            while (i < text.Length)
            {
                int open = text.IndexOf('{', i);
                int close = open < 0 ? -1 : text.IndexOf('}', open + 1);
                if (close < 0)
                {
                    sb.Append(text, i, text.Length - i);
                    break;
                }
                sb.Append(text, i, open - i);
                InputAction action = InputCatalog.FromId(text.Substring(open + 1, close - open - 1));
                if (action == InputAction.None) { sb.Append(text, open, close - open + 1); }
                else
                {
                    string name = key?.Invoke(action);
                    sb.Append(string.IsNullOrEmpty(name) ? "(no key)" : name);
                }
                i = close + 1;
            }
            return sb.ToString();
        }

        /// <summary>The names in braces in a text ("{Fly}" → "Fly"), for checking they are all actions.</summary>
        public static IEnumerable<string> Placeholders(string text)
        {
            int i = 0;
            while (text != null && i < text.Length)
            {
                int open = text.IndexOf('{', i);
                if (open < 0) { yield break; }
                int close = text.IndexOf('}', open + 1);
                if (close < 0) { yield break; }
                yield return text.Substring(open + 1, close - open - 1);
                i = close + 1;
            }
        }

        #endregion
    }

    /// <summary>
    /// Tutorial round: one run through the tutorial (the whole of it or one chapter): the steps whose needs are met, in
    /// order, and where it is. Chapters passed to the end are reported as done.
    /// </summary>
    public sealed class TutorialRun
    {
        private readonly List<(TutorialChapter Chapter, TutorialStep Step)> _steps = new();
        private readonly List<string> _finished = new();

        /// <param name="chapters">The chapters to play, in order.</param>
        /// <param name="has">Which needs the model meets (null: none).</param>
        public TutorialRun(IEnumerable<TutorialChapter> chapters, Func<TutorialNeed, bool> has)
        {
            foreach (TutorialChapter chapter in chapters)
            {
                foreach (TutorialStep step in chapter.Steps)
                {
                    if (Tutorials.Applies(step, has)) { _steps.Add((chapter, step)); }
                }
            }
        }

        /// <summary>How many steps (1 to <see cref="Count"/> as shown).</summary>
        public int Count => _steps.Count;

        /// <summary>The step showing (0-based).</summary>
        public int Index { get; private set; }

        /// <summary>True when there are no steps, or the last one has been passed.</summary>
        public bool Finished => Index >= _steps.Count;

        /// <summary>The step showing (null when finished).</summary>
        public TutorialStep Step => Finished ? null : _steps[Index].Step;

        /// <summary>Its chapter (null when finished).</summary>
        public TutorialChapter Chapter => Finished ? null : _steps[Index].Chapter;

        /// <summary>True on the last step.</summary>
        public bool IsLast => Index == _steps.Count - 1;

        /// <summary>True on the first step.</summary>
        public bool IsFirst => Index == 0;

        /// <summary>The chapters played to their last step in this run (ids, in order).</summary>
        public IReadOnlyList<string> FinishedChapters => _finished;

        /// <summary>Goes on to the next step (a chapter whose last step this was is done).</summary>
        /// <returns>True when the run has finished.</returns>
        public bool Next()
        {
            if (Finished) { return true; }
            TutorialChapter chapter = _steps[Index].Chapter;
            Index++;
            if ((Finished || !ReferenceEquals(_steps[Index].Chapter, chapter)) && !_finished.Contains(chapter.Id)) { _finished.Add(chapter.Id); }
            return Finished;
        }

        /// <summary>Back to the step before (none before the first).</summary>
        public void Back()
        {
            if (Index > 0) { Index = Math.Min(Index, _steps.Count) - 1; }
        }
    }

    /// <summary>
    /// Tutorial round: a tip shown the first time a feature is used (once per PC, unless tips are turned off or shown
    /// again from the menu's TUTORIAL). Text as <see cref="TutorialStep.Text"/>, keys in braces.
    /// </summary>
    public sealed record TutorialTip(string Id, string Title, string Text);

    /// <summary>Tutorial round: the first-use tips, by feature.</summary>
    public static class TutorialTips
    {
        public static readonly TutorialTip[] ALL =
        {
            // The guns
            new("gun-scan", "Scan", "Aim at anything to see what it is. {GunPrimary} locks on so you can read it; {GunSecondary} clears."),
            new("gun-measure", "Measure", "Click two points for a distance. {Gun2} again: area, angle, clearance. {MeasureShowAll} shows or hides the saved ones."),
            new("gun-portal", "Portal", "{GunPrimary} places the blue portal, {GunSecondary} the red. Walk into one to come out of the other."),
            new("gun-comment", "Comment", "Click a problem: a picture of the view opens to draw on, then type the note. Menu → COMMENTS lists them all."),
            new("gun-teleport", "Teleport", "Aim and {GunPrimary} to blink there; {GunSecondary} goes back."),
            new("gun-demolish", "Demolish", "{GunPrimary} primes an element, {GunPrimary} again demolishes it; {GunSecondary} unprimes. {Undo} undoes."),
            new("gun-gizmo", "Gizmo", "{GunPrimary} locks on to an element, the move keys move it, {GunSecondary} commits and Esc cancels."),
            new("gun-clone", "Clone", "{GunPrimary} copies the aimed element; move it with the move keys and {GunSecondary} to place it."),
            new("gun-place", "Place", "{GunPrimary} opens the library of families and entourage (people, trees, cars) to place in front of you."),

            // Walking
            new("site-map", "The site map", "Drag to pan, scroll to zoom. Double-click a place to go there, or a photo to look through it. {SiteMap} or Esc closes it."),
            new("section-box", "Section box", "Drag a handle along its arrow to cut the model. The cut stays when this closes; {SectionClear} clears it."),
            new("sun-panel", "Sun panel", "Set the date, the time and the place. {SunEarlier} and {SunLater} step the time while you walk."),
            new("photo-mode", "Photo mode", "Right-drag looks, the wheel zooms, {PhotoTake} takes the picture. The settings here are the media panel's too."),
            new("sun-hours", "Sun hours study", "The room you stand in is selected; click surfaces to add or remove them, then RUN: sun hours, daylight % or lux on each one."),
            new("clash-mode", "Clash mode", "The list of clashes between categories: click one to fly there. Set each one's status as you review them."),
            new("path-editor", "Camera paths", "Fly to where the path starts, add a key; fly on and add more. The path curves through them; preview it with {PathPreview}."),
            new("grids", "Grids", "{GridsCycle} switches the grids off, on your level, or on every level."),

            // The menu's panels
            new("comments", "Comments", "Every comment, with its picture. Click one to go to where it was made; set its status as you work through them."),
            new("bookmarks", "Bookmarks", "Your saved views: go to, rename, update, reorder or delete them. Tick the ones for the tour and PLAY TOUR."),
            new("clashes", "Clashes", "Clash tests between categories: set one up and run it, then clash mode ({ClashMode}) takes you through the results."),
            new("rooms", "Find room", "Type part of a room's name or number and pick it to go there."),
            new("media", "Media", "PHOTO takes stills, TOUR plays the bookmarks, PATHS makes flythroughs and VIDEO records them."),
            new("entourage", "People, trees and cars", "Pick one to place it with the Place gun ({Gun9}); it stands where you aim."),
            new("site-photos", "Site photos", "Import a folder of photos: ones with GPS pin themselves; place the others by hand. Each folder gets its own colour."),
            new("site-gallery", "Photo browser", "Every site photo by folder: search, switch GRID / LIST, and SHOW ON MAP or look through one."),
            new("materials", "Materials", "Match each material from the IFC to an image, with bump and cutouts. PICK ON MODEL selects one by clicking it."),
            new("textures", "Textures", "Pick an image for a material that has none, or swap one. SAVE puts the images in the file."),
            new("library", "Family library", "Families to place: pick one and it appears in front of you on the Place gun."),
            new("geometry", "Categories and links", "Show or hide categories, linked models and hidden elements."),
            new("controls", "Controls", "Click a key to change it; Esc cancels. Keys that clash are shown in red."),

            // The simple viewer
            new("viewer-measure", "Measure", "Click one point, then another. The panel switches to area, angle and clearance."),
            new("viewer-section", "Section box", "Drag the handles to cut the model open. The button again closes it; the cut stays."),
            new("viewer-comment", "Comments", "Click the model to leave a comment there. Hover a marker to read it."),
            new("viewer-views", "Views", "Saved views: click one to go there, or play them all as a tour."),
            new("viewer-sun", "Sun", "Drag the time and the month to see the shadows move."),
            new("viewer-photos", "Site photos", "Click a photo to look through it from where it was taken; Esc comes back."),
            new("viewer-map", "The map", "Drag to pan, scroll to zoom, double-click to go there. Esc closes it.")
        };

        /// <summary>A tip by id (null when unknown).</summary>
        public static TutorialTip Find(string id) => ALL.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
    }

    /// <summary>
    /// Tutorial round: when to offer the tutorial. The full walkthrough's once per PC; the simple viewer's once per viewer
    /// copy per PC (each copy has its own id), so a copy sent to someone always offers it on their PC, and on yours too.
    /// </summary>
    public static class TutorialOffers
    {
        /// <summary>Most viewer copies remembered (the oldest are forgotten first).</summary>
        public const int MAX_COPIES = 200;

        /// <summary>
        /// True when the tutorial should be offered (copyId: the viewer copy's id; null for files without one, or the full
        /// walkthrough).
        /// </summary>
        public static bool ShouldOffer(LaunchSettings settings, bool viewer, string copyId)
        {
            if (settings == null) { return false; }
            if (!viewer) { return !settings.TutorialOffered; }
            return string.IsNullOrWhiteSpace(copyId) ? !settings.ViewerTutorialOffered : !settings.TutorialCopies.Contains(copyId.Trim());
        }

        /// <summary>Remembers that it was offered (then <see cref="ShouldOffer"/> is false).</summary>
        public static void MarkOffered(LaunchSettings settings, bool viewer, string copyId)
        {
            if (settings == null) { return; }
            if (!viewer)
            {
                settings.TutorialOffered = true;
                settings.WelcomeShown = true;
                return;
            }
            settings.ViewerTutorialOffered = true;
            if (string.IsNullOrWhiteSpace(copyId)) { return; }
            string id = copyId.Trim();
            settings.TutorialCopies.Remove(id);
            settings.TutorialCopies.Add(id);
            while (settings.TutorialCopies.Count > MAX_COPIES) { settings.TutorialCopies.RemoveAt(0); }
        }
    }
}
