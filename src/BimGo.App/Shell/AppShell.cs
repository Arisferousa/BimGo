using BimGo.Format;
using BimGo.Game;
using BimGo.Live;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;
using BimGo.Sources;

// The class belongs to the Shell namespace
namespace BimGo.Shell
{
    /// <summary>
    /// The app: one window that alternates between the home screen and walkthroughs, all on the main thread.
    /// A walkthrough runs on a .bimgo file (edits go to its journal), on an IFC file imported as a new unsaved model (saved
    /// as a .bimgo), or on a live Revit session (edits go to Revit).
    ///
    /// Closing the window quits (after any unsaved-changes prompt); Close model returns to the home screen.
    /// While walking: dropped / double-clicked files are queued until the model is closed; a Go from Revit asks
    /// before switching; a new snapshot of the current live session reloads where the player stands.
    /// </summary>
    internal sealed class AppShell
    {
        #region Fields

        private readonly Queue<OpenTarget> _openQueue = new();
        private readonly RecentFiles _recent = RecentFiles.Load();
        private readonly AppInstance _instance;
        private GameWindow _window;
        private UiBatch _ui;
        private HomeScreen _home;

        // The walkthrough in progress
        private GameSession _current;
        private string _currentSessionId;
        private OpenTarget _next;

        // Updates from a changed IFC turned down this run (file + the IFC's save time), not asked again
        private readonly HashSet<string> _declinedUpdates = new(StringComparer.OrdinalIgnoreCase);

        #endregion

        /// <summary>
        /// Creates the shell.
        /// </summary>
        /// <param name="instance">The single-instance owner (inbox).</param>
        /// <param name="initial">A file or session to open straight away (command line), or null.</param>
        public AppShell(AppInstance instance, OpenTarget initial)
        {
            _instance = instance;
            if (initial != null) { _openQueue.Enqueue(initial); }
        }

        /// <summary>Who writes files saved by the app.</summary>
        public static WriterInfo Writer => new("BimGo", Program.Version);

        #region Loop

        /// <summary>
        /// Runs the app until the window closes.
        /// </summary>
        /// <returns>The process exit code.</returns>
        public int Run()
        {
            try
            {
                _window = new GameWindow("BimGo");
                _ui = new UiBatch();
                _ui.Initialise(_window.DpiScale);
                _home = new HomeScreen(_window, _ui, _recent, _instance);

                while (true)
                {
                    OpenTarget target = TakeNext() ?? _home.Run();
                    if (target == null) { return 0; }

                    SessionEndReason reason = target.IsLive ? RunLive(target) : RunFile(target.Path, target.Kiosk, target.Pose);
                    if (reason == SessionEndReason.WindowClosed) { return 0; }

                    // A switch whose save prompt was cancelled must not fire later
                    if (reason == SessionEndReason.EndedByUser) { _next = null; }
                }
            }
            finally
            {
                try { _ui?.Dispose(); }
                catch (Exception ex) { Utilities.Log_Utils.Write($"UI cleanup failed: {ex.Message}"); }
                try { _window?.Dispose(); }
                catch (Exception ex) { Utilities.Log_Utils.Write($"Window cleanup failed: {ex.Message}"); }
            }
        }

        /// <summary>
        /// The next thing to open without visiting the home screen: a switch / reload first, then queued files.
        /// </summary>
        private OpenTarget TakeNext()
        {
            if (_next != null)
            {
                OpenTarget next = _next;
                _next = null;
                return next;
            }
            return _openQueue.Count > 0 ? _openQueue.Dequeue() : null;
        }

        #endregion

        #region Files

        /// <summary>
        /// Opens and walks a .bimgo file, or imports and walks an IFC file.
        /// </summary>
        /// <param name="path">The file.</param>
        /// <param name="kiosk">Round Z-C: start in kiosk mode (desktop kiosk shortcut).</param>
        /// <param name="pose">Where to put the player (a file reopened after a reload from IFC), or null.</param>
        private SessionEndReason RunFile(string path, bool kiosk = false, SessionPose pose = null)
        {
            BimGoDocument document = LoadFile(path, kiosk, out string notice);
            if (document == null) { return SessionEndReason.EndedByUser; }

            var options = new SessionOptions
            {
                Source = new FileEditSource(document.Scene, Path.GetFileName(document.Path ?? path)),
                Document = document,
                InApp = true,
                Writer = Writer,
                OnOpenRequest = QueueFile,
                OnSaved = saved => _recent.Touch(saved),
                OnReopen = (reopen, at) => _next = new OpenTarget { Path = reopen, Pose = at }, // reload from IFC
                Pose = pose,
                PollNotices = PollInbox,
                Kiosk = kiosk,
                StartNotice = notice
            };
            return RunSession(document.Scene, options, out _);
        }

        /// <summary>
        /// Reads a file (an IFC file is imported), showing a loading frame; on failure the reason goes to the home screen.
        /// Keeps the review across new IFC exports: an IFC that a .bimgo was made from offers to update that .bimgo, and a
        /// .bimgo whose IFC has been saved again since offers to update itself (comments, views, site photos… kept).
        /// </summary>
        /// <param name="path">The file.</param>
        /// <param name="kiosk">A kiosk launch (never asks).</param>
        /// <param name="notice">For an import, what came in and the reminder to save (the start notice), else null.</param>
        private BimGoDocument LoadFile(string path, bool kiosk, out string notice)
        {
            notice = null;
            if (IfcImporter.HasExtension(path))
            {
                string reviewed = kiosk ? null : FindModelMadeFrom(path);
                if (reviewed == null) { return ImportIfc(path, out notice); }
                FileDialogs.Answer answer = FileDialogs.AskYesNoCancel(_window.Handle,
                    $"{Path.GetFileName(reviewed)} was made from this IFC and holds its review: comments, views, site photos, measurements.\n\n" +
                    $"Update {Path.GetFileName(reviewed)} with {Path.GetFileName(path)}? Everything reviewed is kept and the file is saved with the new model " +
                    "(edits made in the walkthrough, such as moves and demolitions, belong to the old model and are left out).\n\n" +
                    "Yes: update it and open it.\nNo: open the IFC on its own, as a new model.",
                    "BimGo update from IFC");
                _window.Input.ReleaseAll();
                if (answer == FileDialogs.Answer.Cancel) { return null; }
                if (answer == FileDialogs.Answer.No) { return ImportIfc(path, out notice); }
                if (!UpdateFromIfc(reviewed, null, path, out string failed))
                {
                    _home.SetMessage(failed, error: !failed.EndsWith("nothing changed.", StringComparison.Ordinal));
                    return null;
                }
                path = reviewed;
            }
            string name = Path.GetFileName(path);
            if (!BimGoFormat.HasExtension(path))
            {
                _home.SetMessage($"{name} is not a .bimgo or .ifc file.", error: true);
                return null;
            }

            // Read on a worker thread with a progress bar (Esc cancels)
            var progress = new Utilities.OperationProgress();
            progress.Begin("Reading the model", 0.0, 1.0);
            LaunchSettings settings = LaunchSettings.LoadOrDefault();
            string error = null;
            BimGoDocument document = ProgressScreen.Run(_window, _ui, $"Opening {name}", progress,
                () => BimGoReader.Read(path, settings, out error, progress));
            if (document == null)
            {
                bool cancelled = progress.CancelRequested;
                _home.SetMessage(cancelled ? $"Opening {name} was cancelled." : $"{name} could not be opened: {error}", error: !cancelled);
                return null;
            }

            // Its IFC has been saved again since it was imported: offer the new model, the review kept
            if (!kiosk && ModelUpdate.SourceChanged(document.Scene?.Provenance, out string ifc, out DateTime ifcSaved)
                && !_declinedUpdates.Contains($"{path}|{ifcSaved.Ticks}"))
            {
                bool update = FileDialogs.AskYesNo(_window.Handle,
                    $"{Path.GetFileName(ifc)} has been saved again ({ifcSaved.ToLocalTime():d MMM yyyy HH:mm}) since {name} was imported from it.\n\n" +
                    $"Update {name} with the new IFC? Comments, views, site photos, measurements and the rest of the review are kept, and the file is saved with the new model.\n\n" +
                    "No: open it as it is (UPDATE FROM IFC… in the menu does it later).",
                    "BimGo update from IFC");
                _window.Input.ReleaseAll();
                if (!update) { _declinedUpdates.Add($"{path}|{ifcSaved.Ticks}"); }
                else if (!UpdateFromIfc(path, document, ifc, out string failed)) { notice = failed + " It opens as it was."; } // said when it opens
                else
                {
                    progress = new Utilities.OperationProgress();
                    progress.Begin("Reading the model", 0.0, 1.0);
                    BimGoDocument updated = ProgressScreen.Run(_window, _ui, $"Opening {name}", progress,
                        () => BimGoReader.Read(path, settings, out error, progress));
                    if (updated == null)
                    {
                        _home.SetMessage($"{name} could not be opened: {error}", error: true);
                        return null;
                    }
                    document = updated;
                }
            }

            _recent.Touch(path);
            _home.SetMessage(null, error: false);
            return document;
        }

        /// <summary>
        /// The .bimgo made from an IFC file (beside it or in the recent files), or null.
        /// </summary>
        private string FindModelMadeFrom(string ifcPath)
        {
            try
            {
                return ModelUpdate.FindModelFor(ifcPath, _recent.Entries.Select(e => e.Path));
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Looking for a model made from {ifcPath} failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Updates a .bimgo with a newer IFC of its model, keeping its review (<see cref="ModelUpdate.WithNewModel"/>),
        /// with progress (Esc cancels the import; the file is untouched then).
        /// </summary>
        /// <param name="bimgoPath">The .bimgo to update.</param>
        /// <param name="reviewed">Its document if already read, else null (read here).</param>
        /// <param name="ifcPath">The IFC file.</param>
        /// <param name="failed">Why not, in plain words (when false).</param>
        /// <returns>True if the file was written.</returns>
        private bool UpdateFromIfc(string bimgoPath, BimGoDocument reviewed, string ifcPath, out string failed)
        {
            failed = null;
            string name = Path.GetFileName(bimgoPath), ifcName = Path.GetFileName(ifcPath);
            var progress = new Utilities.OperationProgress();
            progress.Begin("Reading the IFC file", 0.0, reviewed == null ? 0.6 : 0.85);
            LaunchSettings settings = LaunchSettings.LoadOrDefault();
            string error = null;
            BimGoDocument imported = ProgressScreen.Run(_window, _ui, $"Importing {ifcName}", progress,
                () => IfcImporter.Import(ifcPath, settings, out error, out _, progress));
            if (imported?.Scene == null)
            {
                failed = progress.CancelRequested ? $"Updating {name} was cancelled: nothing changed." : $"{ifcName} could not be imported: {error}";
                return false;
            }

            if (reviewed == null)
            {
                progress.Begin("Reading the model", 0.6, 0.85);
                reviewed = ProgressScreen.Run(_window, _ui, $"Opening {name}", progress,
                    () => BimGoReader.Read(bimgoPath, settings, out error, progress));
                if (reviewed == null)
                {
                    failed = progress.CancelRequested ? $"Updating {name} was cancelled: nothing changed." : $"{name} could not be opened: {error}";
                    return false;
                }
            }

            BimGoDocument updated = ModelUpdate.WithNewModel(reviewed, imported.Scene);
            updated.Path = bimgoPath;
            progress.Begin("Writing the .bimgo file", 0.85, 1.0);
            bool written = ProgressScreen.Run(_window, _ui, $"Saving {name}", progress,
                () => BimGoWriter.Write(bimgoPath, updated, Writer, FileKinds.SAVE, out error, progress: progress), cancelOnClose: false);
            _window.Input.ReleaseAll();
            if (!written)
            {
                failed = $"{name} could not be saved with the new model: {error}";
                return false;
            }
            Utilities.Log_Utils.Write($"Updated {bimgoPath} from {ifcPath} ({imported.Scene.Elements.Length} elements; {reviewed.Journal?.Count ?? 0} walkthrough edits left out).");
            return true;
        }

        /// <summary>
        /// Imports an IFC file (Revit LT's way in: File → Export → IFC) as a new, unsaved model on a worker thread with a
        /// progress bar (Esc cancels). Saving it writes a .bimgo; the IFC itself is never changed.
        /// </summary>
        private BimGoDocument ImportIfc(string path, out string notice)
        {
            notice = null;
            string name = Path.GetFileName(path);
            var progress = new Utilities.OperationProgress();
            progress.Begin("Reading the IFC file", 0.0, 1.0);
            LaunchSettings settings = LaunchSettings.LoadOrDefault();
            string error = null;
            IfcImportSummary summary = null;
            BimGoDocument document = ProgressScreen.Run(_window, _ui, $"Importing {name}", progress,
                () => IfcImporter.Import(path, settings, out error, out summary, progress));
            if (document == null)
            {
                bool cancelled = progress.CancelRequested;
                _home.SetMessage(cancelled ? $"Importing {name} was cancelled." : $"{name} could not be imported: {error}", error: !cancelled);
                return null;
            }

            _recent.Touch(path);
            _home.SetMessage(null, error: false);
            notice = summary?.Notice();
            return document;
        }

        #endregion

        #region Live sessions

        /// <summary>
        /// Joins a live Revit session and walks its latest snapshot. A reload (newer snapshot) comes straight back
        /// here with the player's pose.
        /// </summary>
        private SessionEndReason RunLive(OpenTarget target)
        {
            string sessionId = target.SessionId;
            SessionInfo info = LiveSessions.ReadInfo(sessionId);
            if (info == null || !info.IsAlive())
            {
                _home.SetMessage("That Revit session is no longer running. Press Go in Revit to start a new one.", error: true);
                return SessionEndReason.EndedByUser;
            }
            if (string.IsNullOrEmpty(info.LatestSnapshot) || !File.Exists(info.LatestSnapshot))
            {
                _home.SetMessage($"{info.DocTitle} has no snapshot yet. Press Go in Revit.", error: true);
                return SessionEndReason.EndedByUser;
            }

            string title = string.IsNullOrWhiteSpace(info.DocTitle) ? "Revit model" : info.DocTitle;
            // Attach first so nothing Revit sends from now on is missed, then read the snapshot (Esc cancels)
            var source = new LiveSessionSource(info, Program.Version);
            var progress = new Utilities.OperationProgress();
            progress.Begin(target.Pose == null ? "Reading the Revit snapshot" : "Reading the new snapshot", 0.0, 1.0);
            LaunchSettings settings = LaunchSettings.LoadOrDefault();
            string snapshot = info.LatestSnapshot;
            string error = null;
            BimGoDocument document = ProgressScreen.Run(_window, _ui, target.Pose == null ? $"Joining {title}" : $"Reloading {title}", progress,
                () => BimGoReader.Read(snapshot, settings, out error, progress));
            if (document == null)
            {
                source.Close(sayGoodbye: true);
                bool cancelled = progress.CancelRequested;
                _home.SetMessage(cancelled ? $"Joining {title} was cancelled. Join it again from the home screen." : $"The snapshot of {title} could not be read: {error}", error: !cancelled);
                return SessionEndReason.EndedByUser;
            }
            _home.SetMessage(null, error: false);
            if (target.Pose == null) { LastLiveSession.Save(info); } // the start screen's "last live session" card

            var options = new SessionOptions
            {
                Source = source,
                Document = null,
                InApp = true,
                Writer = Writer,
                Pose = target.Pose,
                OnOpenRequest = QueueFile,
                OnSaved = saved => _recent.Touch(saved),
                PollNotices = PollInbox,
                StartNotice = target.Pose == null && !VersionCheck.SameRelease(info.AddinVersion, Program.Version)
                    ? VersionCheck.Mismatch(info.AddinVersion, Program.Version)
                    : null // Round Z-E: once per session, not on every reload
            };

            _currentSessionId = sessionId;
            SessionEndReason reason = RunSession(document.Scene, options, out SessionPose pose);
            _currentSessionId = null;

            source.Close(sayGoodbye: reason != SessionEndReason.Reload);
            if (reason == SessionEndReason.Reload) { _next = OpenTarget.Live(sessionId, pose); }
            return reason;
        }

        #endregion

        #region Sessions

        /// <summary>
        /// Runs a walkthrough. A crash ends the walkthrough (logged and reported) but not the app.
        /// </summary>
        /// <param name="scene">The scene.</param>
        /// <param name="options">The session options.</param>
        /// <param name="pose">Where the player stood at the end (for reloads), or null.</param>
        private SessionEndReason RunSession(SceneData scene, SessionOptions options, out SessionPose pose)
        {
            pose = null;
            GameSession session = null;
            try
            {
                Utilities.Log_Utils.Write($"Session starting: {options.Source.DisplayName}");
                session = new GameSession(_window, scene, options);
                _current = session;
                SessionEndReason reason = session.Run();
                pose = session.CapturePose();
                return reason;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Session crashed: {ex}");
                _window.SetCaptured(false);
                FileDialogs.ShowError(_window.Handle, $"The walkthrough stopped unexpectedly:\n\n{ex.Message}\n\nDetails were written to:\n{Utilities.Log_Utils.LogPath}");
                _home.SetMessage("The last walkthrough stopped unexpectedly. Unsaved changes were lost.", error: true);
                return _window.Handle == 0 ? SessionEndReason.WindowClosed : SessionEndReason.EndedByUser;
            }
            finally
            {
                _current = null;
                try { session?.Dispose(); }
                catch (Exception ex) { Utilities.Log_Utils.Write($"Session cleanup failed: {ex.Message}"); }
                Utilities.Log_Utils.Write("Session ended.");
            }
        }

        /// <summary>
        /// A file dropped on the window during a walkthrough: queued until the current model is closed.
        /// </summary>
        private string QueueFile(string path)
        {
            if (!OpenTarget.CanOpen(path)) { return $"{Path.GetFileName(path)} is not a .bimgo or .ifc file"; }
            _openQueue.Enqueue(OpenTarget.File(path));
            return $"{Path.GetFileName(path)} will open when you close this model (Esc → CLOSE MODEL)";
        }

        /// <summary>
        /// Requests from other instances / Revit during a walkthrough. Files are queued; a Go from Revit for another
        /// model asks before switching (a Go for the current session needs nothing: its new snapshot reloads).
        /// </summary>
        private string PollInbox()
        {
            OpenTarget request = _instance?.TakeRequest();
            if (request == null) { return null; }
            if (!request.IsLive) { return QueueFile(request.Path); }
            if (request.SessionId == _currentSessionId) { return null; }

            SessionInfo info = LiveSessions.ReadInfo(request.SessionId);
            string title = string.IsNullOrWhiteSpace(info?.DocTitle) ? "another Revit model" : info.DocTitle;

            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            // The model this file came from: staying lets the file's edits be pushed into it
            string pushHint = _current?.HasPushableEditsFor(info?.ModelKey) == true
                ? "\n\nChoose No to stay in this file and push its edits into that model (Esc → PUSH TO REVIT)."
                : string.Empty;
            bool switchNow = FileDialogs.AskYesNo(_window.Handle,
                $"Revit wants to open {title} in BimGo.\n\nSwitch now? (You'll be asked to save first if this model has unsaved changes.){pushHint}",
                "BimGo");
            if (!switchNow) { return $"{title} was not opened. Join it later from the home screen."; }

            _next = request;
            _current?.RequestEnd(SessionEndReason.Switch);
            return $"Switching to {title}…";
        }

        #endregion
    }
}
