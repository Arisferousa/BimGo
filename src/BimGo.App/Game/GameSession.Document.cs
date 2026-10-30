using BimGo.Audio;
using BimGo.Edits;
using BimGo.Format;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Sources;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// The edit journal and the document side of a session: replaying a file's edits, recording new ones, undo / redo,
    /// saving as .bimgo, unsaved-change tracking and the window title.
    ///
    /// The journal is the source of truth for edits: the geometry is never changed, so the walkthrough state can
    /// always be rebuilt by resetting all edits and replaying the journal (which is exactly how undo works).
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private readonly EditJournal _journal;
        private int _savedJournalRevision;
        private int _savedCommentRevision;
        private int _savedBookmarkRevision;
        private int _savedPathRevision; // Round K-A
        private int _savedEntourageRevision; // Round X-B
        private int _savedLightProfileRevision; // Round X-D1
        private float _noticeTimer;
        private string _lastTitle;

        #endregion

        #region State

        /// <summary>True for a standalone .bimgo file (edits go to the journal and must be saved).</summary>
        public bool IsFileMode => !Source.IsRevit;

        /// <summary>The journal of committed edits.</summary>
        public EditJournal Journal => _journal;

        /// <summary>The open file's path (standalone), or null.</summary>
        public string DocumentPath { get; private set; }

        /// <summary>True if a standalone file has edits, comments or bookmarks that are not saved yet.</summary>
        public bool IsDirty => IsFileMode && Comments != null && Bookmarks != null &&
            (_journal.Revision != _savedJournalRevision || Comments.Revision != _savedCommentRevision || Bookmarks.Revision != _savedBookmarkRevision || SunDirty || VisibilityDirty
             || _materialsRevision != _savedMaterialsRevision || ClashesDirty || (CameraPaths != null && CameraPaths.Revision != _savedPathRevision)
             || (Entourage != null && Entourage.Revision != _savedEntourageRevision) || _lightProfileRevision != _savedLightProfileRevision
             || MeasurementsDirty || SitePhotosDirty);

        /// <summary>The document's display name (file name, or the Revit model title).</summary>
        public string DocumentName => IsFileMode && !string.IsNullOrEmpty(DocumentPath) ? Path.GetFileName(DocumentPath) : Scene.ModelTitle;

        /// <summary>
        /// Remembers the current state as saved.
        /// </summary>
        private void MarkSaved()
        {
            _savedJournalRevision = _journal.Revision;
            _savedMaterialsRevision = _materialsRevision;
            _savedCommentRevision = Comments?.Revision ?? 0;
            _savedBookmarkRevision = Bookmarks?.Revision ?? 0;
            _savedPathRevision = CameraPaths?.Revision ?? 0;
            _savedEntourageRevision = Entourage?.Revision ?? 0;
            _savedLightProfileRevision = _lightProfileRevision;
            _savedMeasurementRevision = _measurementRevision; // Round Y-A
            _savedSitePhotoRevision = SitePhotos?.Revision ?? 0; // site photos round
            _savedSunRevision = _sunRevision;
            _savedVisibilityRevision = _visibilityRevision;
            _savedClashRevision = _clashRevision;
            UpdateTitle();
        }

        /// <summary>
        /// Updates the window title: name, unsaved marker and where edits go.
        /// </summary>
        private void UpdateTitle()
        {
            string title = IsFileMode
                ? $"BimGo — {DocumentName}{(IsDirty ? " *" : string.Empty)}"
                : $"BimGo — {Scene.ModelTitle} · Revit";
            if (title == _lastTitle) { return; }
            _lastTitle = title;
            _window.SetTitle(title);
        }

        #endregion

        #region Journal: record, replay, undo, redo

        /// <summary>
        /// Records an accepted edit. Targets are stored by stable identity (UniqueId, or clone key for clones made
        /// in a walkthrough) so the entry can be replayed on a later load or pushed into Revit.
        /// </summary>
        private void RecordEdit(EditRequest request, EditResult result)
        {
            DescribeTarget(request.ElementId, request.TargetCloneKey, out int cloneKey, out string uniqueId, out long elementId);

            var entry = new JournalEntry
            {
                Op = request.Op switch
                {
                    EditOp.Transform => JournalOps.TRANSFORM,
                    EditOp.Copy => JournalOps.CLONE,
                    EditOp.Place => JournalOps.PLACE,
                    _ => JournalOps.HIDE
                },
                Mode = request.Op switch
                {
                    EditOp.Delete => JournalOps.MODE_DELETE,
                    EditOp.PhaseDemolish => JournalOps.MODE_DEMOLISH,
                    _ => null
                },
                ElementId = elementId,
                UniqueId = uniqueId,
                TargetCloneKey = cloneKey,
                NewCloneKey = request.NewCloneKey,
                TypeUniqueId = request.Op == EditOp.Place ? request.TypeUniqueId : null,
                TypeId = request.Op == EditOp.Place ? request.TypeId : 0,
                Pivot = request.Pivot,
                Offset = request.Translation,
                Angle = request.Angle,
                Label = request.Label ?? string.Empty,
                Utc = DateTime.UtcNow,
                User = Environment.UserName,
                AppliedToRevit = Source.IsRevit,
                RevitElementId = (request.Op == EditOp.Copy || request.Op == EditOp.Place) && Source.IsRevit ? result.NewElementId : 0
            };
            _journal.Add(entry);
            UpdateTitle();
        }

        /// <summary>
        /// Identifies a request's target for the journal: a walkthrough clone by key (even once Revit has given it
        /// an id), else a model element by UniqueId and ElementId.
        /// </summary>
        private void DescribeTarget(long requestId, int requestCloneKey, out int cloneKey, out string uniqueId, out long elementId)
        {
            cloneKey = 0;
            uniqueId = string.Empty;
            elementId = 0;

            if (requestCloneKey != 0)
            {
                cloneKey = requestCloneKey;
                return;
            }

            if (requestId > 0)
            {
                foreach (DynamicInstance instance in Dynamics.Instances)
                {
                    if (instance.IsClone && instance.RevitId == requestId)
                    {
                        cloneKey = instance.CloneKey;
                        return;
                    }
                }
            }

            elementId = requestId;
            if (_elementIndexById.TryGetValue(requestId, out int element)) { uniqueId = Scene.Elements[element].UniqueId ?? string.Empty; }
        }

        /// <summary>
        /// Finds a journal target in the current walkthrough.
        /// </summary>
        /// <param name="cloneKey">Clone key (0 for a model element).</param>
        /// <param name="uniqueId">Model element UniqueId (preferred).</param>
        /// <param name="elementId">Model element id (fallback).</param>
        /// <param name="element">The source element index.</param>
        /// <param name="clone">The clone, when the target is one; else null.</param>
        /// <returns>False if the target is not in this walkthrough.</returns>
        private bool ResolveTarget(int cloneKey, string uniqueId, long elementId, out int element, out DynamicInstance clone)
        {
            element = -1;
            clone = null;

            if (cloneKey != 0)
            {
                foreach (DynamicInstance instance in Dynamics.Instances)
                {
                    if (instance.IsClone && instance.CloneKey == cloneKey)
                    {
                        clone = instance;
                        element = instance.Element;
                        return true;
                    }
                }
                return false;
            }

            if (!string.IsNullOrEmpty(uniqueId) && _elementIndexByUniqueId.TryGetValue(uniqueId, out element)) { return true; }
            if (elementId > 0 && _elementIndexById.TryGetValue(elementId, out element)) { return true; }
            element = -1;
            return false;
        }

        /// <summary>
        /// Applies every journal entry in order (on load, and after an undo).
        /// </summary>
        /// <returns>The number of entries that could not be applied (their targets are missing).</returns>
        private int ReplayJournal()
        {
            int failures = 0;
            foreach (JournalEntry entry in _journal.Entries)
            {
                bool applied;
                try
                {
                    applied = ApplyEntry(entry);
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Journal entry {entry.Seq} ({entry.Op}) failed: {ex.Message}");
                    applied = false;
                }
                if (!applied) { failures++; }
            }

            _nextCloneKey = Math.Max(_nextCloneKey, _journal.MaxCloneKey());
            if (failures > 0) { Utilities.Log_Utils.Write($"Journal replay: {failures} of {_journal.Count} entries skipped."); }
            return failures;
        }

        /// <summary>
        /// Applies one entry to the walkthrough (no source involved: the edit was accepted when it was made).
        /// </summary>
        private bool ApplyEntry(JournalEntry entry)
        {
            // A family library placement has no target: it clones the type's template
            if (entry.Op == JournalOps.PLACE) { return ApplyPlaceEntry(entry); }

            if (!ResolveTarget(entry.TargetCloneKey, entry.UniqueId, entry.ElementId, out int element, out DynamicInstance clone)) { return false; }

            switch (entry.Op)
            {
                case JournalOps.HIDE:
                    if (clone != null)
                    {
                        clone.Hidden = true;
                        return true;
                    }
                    DynamicInstance moved = Dynamics.FindOriginal(element);
                    if (moved != null) { moved.Hidden = true; }
                    else { SetStaticHidden(element, true); }
                    ApplyRemovals(CollectHosted(Scene.Elements[element].ElementId));
                    return true;

                case JournalOps.TRANSFORM:
                {
                    DynamicInstance target = clone ?? MakeDynamic(element);
                    Dynamics.SetTransform(target, target.Offset + entry.Offset, target.Angle + entry.Angle);
                    return true;
                }

                case JournalOps.CLONE:
                {
                    DynamicInstance source = clone ?? Dynamics.FindOriginal(element);
                    DynamicInstance copy = CreateClone(element, source, entry.NewCloneKey);
                    Dynamics.SetTransform(copy, copy.Offset + entry.Offset, copy.Angle + entry.Angle);
                    copy.Committed = true;
                    copy.RevitId = entry.RevitElementId;
                    return true;
                }

                default:
                    Utilities.Log_Utils.Write($"Journal entry {entry.Seq}: unknown op '{entry.Op}'.");
                    return false;
            }
        }

        /// <summary>
        /// Everything hosted by an element, recursively (doors in a wall...), excluding the element itself.
        /// </summary>
        private long[] CollectHosted(long hostId)
        {
            if (!_hostedBy.ContainsKey(hostId)) { return Array.Empty<long>(); }
            var result = new List<long>();
            var queue = new Queue<long>();
            queue.Enqueue(hostId);
            while (queue.Count > 0 && result.Count < 10_000)
            {
                if (!_hostedBy.TryGetValue(queue.Dequeue(), out List<long> hosted)) { continue; }
                foreach (long id in hosted)
                {
                    result.Add(id);
                    queue.Enqueue(id);
                }
            }
            return result.ToArray();
        }

        /// <summary>
        /// Puts the walkthrough back to the unedited snapshot (all elements shown, no moved or cloned instances).
        /// </summary>
        private void ResetEdits()
        {
            for (int i = Dynamics.Instances.Count - 1; i >= 0; i--)
            {
                Dynamics.Remove(Dynamics.Instances[i]);
            }
            for (int e = 0; e < _hidden.Length; e++)
            {
                if (_hidden[e]) { SetStaticHidden(e, false); } // library templates stay hidden (SetStaticHidden ignores them)
            }
            _nextCloneKey = 0;
        }

        /// <summary>
        /// Ctrl+Z: removes the last journal entry and rebuilds the walkthrough from the rest (standalone files only;
        /// in a Revit session, undo in Revit).
        /// </summary>
        private void Undo()
        {
            if (!IsFileMode)
            {
                Toast($"Edits are in Revit: undo them there, then press {Key(InputAction.Refresh)} to refresh");
                return;
            }
            if (_guns[_activeGun].CapturesInput || IsPushPanelOpen) { return; }

            // An edit already pushed into Revit stays: undoing only the file's copy would make them disagree
            if (_journal.Count > 0 && _journal.Entries[^1].AppliedToRevit)
            {
                Toast("The last edit is already in Revit: undo it there (one Ctrl+Z undoes a whole push)", 4f);
                return;
            }

            JournalEntry last = _journal.RemoveLast();
            if (last == null)
            {
                Toast("Nothing to undo");
                return;
            }

            ResetEdits();
            ReplayJournal();
            UpdateTitle();
            Sound.Play(SoundId.Remove);
            Toast(string.IsNullOrEmpty(last.Label) ? $"Undone ({Key(InputAction.Redo)} redoes)" : $"Undone: {last.Label} ({Key(InputAction.Redo)} redoes)");
        }

        /// <summary>
        /// Ctrl+Y / Ctrl+Shift+Z: puts the last undone edit back (standalone files only). Replaying a single entry on top
        /// of the current state gives the same walkthrough as a full replay, because undo rebuilt it from the entries
        /// before this one. The redo history ends as soon as a new edit is made.
        /// </summary>
        private void Redo()
        {
            if (!IsFileMode)
            {
                Toast($"Edits are in Revit: redo them there, then press {Key(InputAction.Refresh)} to refresh");
                return;
            }
            if (_guns[_activeGun].CapturesInput || IsPushPanelOpen) { return; }

            JournalEntry entry = _journal.Redo();
            if (entry == null)
            {
                Toast("Nothing to redo");
                return;
            }

            bool applied;
            try
            {
                applied = ApplyEntry(entry);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Redo of entry {entry.Seq} ({entry.Op}) failed: {ex.Message}");
                applied = false;
            }
            _nextCloneKey = Math.Max(_nextCloneKey, _journal.MaxCloneKey());
            UpdateTitle();

            if (!applied)
            {
                Sound.Play(SoundId.Error);
                Toast("Redone in the file, but its element is not in this walkthrough", 4f, important: true);
                return;
            }
            Sound.Play(SoundId.Commit);
            string more = _journal.RedoCount > 0 ? $" ({_journal.RedoCount} more)" : string.Empty;
            Toast((string.IsNullOrEmpty(entry.Label) ? "Redone" : $"Redone: {entry.Label}") + more);
        }

        #endregion

        #region Save

        /// <summary>
        /// Saves the walkthrough as a .bimgo: the snapshot, the comments and the journal. In a standalone file
        /// <paramref name="saveAs"/> = false writes back to the open file; in a Revit session it always asks for a
        /// new file (the model itself is already up to date in Revit).
        /// </summary>
        /// <param name="saveAs">Ask for a file (always in a Revit session).</param>
        /// <param name="viewerCopy">Viewer round: write a copy that opens in the simple viewer; the open file (path, unsaved
        /// changes) stays as it is.</param>
        /// <returns>True if saved.</returns>
        public bool Save(bool saveAs, bool viewerCopy = false)
        {
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();

            string path = IsFileMode && !saveAs && !viewerCopy ? DocumentPath : null;
            if (string.IsNullOrEmpty(path))
            {
                string suggestedName = (IsFileMode ? Path.GetFileNameWithoutExtension(DocumentName) : SafeFileName(Scene.ModelTitle)) + (viewerCopy ? " (viewer)" : string.Empty) + BimGoFormat.EXTENSION;
                string title = viewerCopy ? "Save a copy for the simple viewer" : IsFileMode ? "Save BimGo model as" : "Save walkthrough as a BimGo model";
                path = FileDialogs.ShowSave(_window.Handle, title, BimGoFormat.DIALOG_FILTER, SuggestedFolder(), suggestedName, BimGoFormat.EXTENSION);
                if (path == null) { return false; }
                if (!BimGoFormat.HasExtension(path)) { path += BimGoFormat.EXTENSION; }
            }

            // The start screen's picture of this view (before the progress screen draws over the window)
            byte[] thumbnail = CaptureFileThumbnail() ?? _options.Document?.Thumbnail;

            BimGoDocument document = DocumentForSave(path, thumbnail, viewerCopy);
            string kind = IsFileMode ? FileKinds.SAVE : FileKinds.SESSION_SAVE;

            // Written on a worker thread with a progress bar; a cancelled save leaves the file on disk untouched
            var progress = new Utilities.OperationProgress();
            progress.Begin("Writing the .bimgo file", 0.0, 1.0);
            string error = null;
            bool written = ProgressScreen.Run(_window, _ui, $"Saving {Path.GetFileName(path)}", progress,
                () => BimGoWriter.Write(path, document, _options.Writer, kind, out error, progress: progress), cancelOnClose: false);
            return FinishSave(path, written, error, progress, viewerCopy);
        }

        /// <summary>
        /// What a save writes: the snapshot and everything reviewed on it.
        /// </summary>
        /// <param name="path">Where it goes.</param>
        /// <param name="thumbnail">The start screen's picture.</param>
        /// <param name="viewerCopy">Viewer round: mark it for the simple viewer.</param>
        private BimGoDocument DocumentForSave(string path, byte[] thumbnail, bool viewerCopy)
        {
            return new BimGoDocument
            {
                Thumbnail = thumbnail,
                Scene = Scene,
                Comments = Comments.ToDocument(),
                Journal = _journal,
                Bookmarks = Bookmarks.ToDocument(),
                Paths = CameraPaths?.ToDocument(), // Round K-A
                Entourage = Entourage?.ToDocument(), // Round X-B
                EntourageImages = Entourage?.ImagesForSave(),
                LightProfiles = LightProfilesForSave(), // Round X-D1
                Measurements = MeasurementsForSave(), // Round Y-A
                Photos = SitePhotos?.ToDocument(), // site photos round
                PhotoImages = SitePhotos?.ImagesForSave(),
                Sun = _sun?.Copy(),
                Visibility = ToVisibilitySettings(),
                Materials = MaterialsForSave(), // Textures panel: picked images are embedded; materials round: the matches applied
                MaterialMatches = MatchesForSave(),
                MatchImages = MatchImagesForSave(),
                MaterialBaseVertices = MatchBaseVerticesForSave(),
                Clashes = ClashBytesForSave(), // Round C: as held when clash detection was never switched on
                Viewer = viewerCopy || _viewerFile ? new ViewerDto { Start = viewerCopy ? ViewerStart.ORBIT : _viewerStart } : null, // viewer round: a viewer file stays one
                CreatedUtc = _options.Document?.CreatedUtc ?? Scene.Provenance?.ExtractedUtc ?? DateTime.UtcNow,
                Path = path
            };
        }

        /// <summary>After the write: the outcome said, the open file's path and saved state updated (not for a copy).</summary>
        private bool FinishSave(string path, bool written, string error, Utilities.OperationProgress progress, bool viewerCopy)
        {
            _window.Input.ReleaseAll();
            if (!written)
            {
                bool cancelled = progress.CancelRequested;
                Sound.Play(cancelled ? SoundId.UiClick : SoundId.Error);
                Toast(cancelled ? "Save cancelled: the file on disk was not changed." : $"The model could not be saved: {error}", 5f, important: !cancelled);
                return false;
            }

            if (IsFileMode && !viewerCopy)
            {
                DocumentPath = path;
                if (Source is FileEditSource fileSource) { fileSource.DisplayName = Path.GetFileName(path); }
                MarkSaved();
            }
            _options.OnSaved?.Invoke(path);

            Sound.Play(SoundId.Commit);
            if (viewerCopy) { return true; } // the caller says what was written
            string pending = EditsPending > 0 ? $" ({EditsPending} edit{(EditsPending == 1 ? " was" : "s were")} still waiting for Revit and not included)" : string.Empty;
            Toast($"Saved {Path.GetFileName(path)}{pending}", pending.Length > 0 ? 5f : 2.6f);
            return true;
        }

        /// <summary>
        /// Site photos round: menu → UPDATE FROM IFC… (the pause menu and the viewer's): imports a newer IFC of the same
        /// model and saves it into this file in place of the old geometry, keeping everything reviewed on it
        /// (<see cref="ModelUpdate.WithNewModel"/>: positions are in the model's own coordinates, so they stay put).
        /// Edits made in the walkthrough and picked textures belong to the old geometry and are left out. Offers the
        /// IFC the model came from first. The file then reopens where you stand.
        /// </summary>
        private void ReloadFromIfc()
        {
            if (!IsFileMode) { return; }
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();

            if (string.IsNullOrEmpty(DocumentPath))
            {
                bool save = FileDialogs.AskYesNo(_window.Handle,
                    "Updating from IFC saves into this model's .bimgo file, and it has none yet.\n\nSave it as a .bimgo first?", "BimGo update from IFC");
                if (!save || !Save(saveAs: true)) { return; }
            }
            // The IFC it came from (if it's still there), else one picked
            string ifc = ModelUpdate.SourceIfc(Scene.Provenance);
            if (ifc != null && File.Exists(ifc))
            {
                DateTime saved = File.GetLastWriteTimeUtc(ifc);
                string when = ModelUpdate.SourceChanged(Scene.Provenance, out _, out _) ? $"saved again {saved.ToLocalTime():d MMM yyyy HH:mm}" : "not changed since it was imported";
                FileDialogs.Answer answer = FileDialogs.AskYesNoCancel(_window.Handle,
                    $"Update from {Path.GetFileName(ifc)} ({when})?\n\nYes: that file.\nNo: choose another IFC file.", "BimGo update from IFC");
                if (answer == FileDialogs.Answer.Cancel) { return; }
                if (answer == FileDialogs.Answer.No) { ifc = null; }
            }
            else
            {
                ifc = null;
            }
            if (ifc == null)
            {
                string folder = Path.GetDirectoryName(ModelUpdate.SourceIfc(Scene.Provenance) ?? DocumentPath);
                if (!Directory.Exists(folder)) { folder = Path.GetDirectoryName(DocumentPath); }
                ifc = FileDialogs.ShowOpen(_window.Handle, "Update the model from an IFC file", "IFC model (*.ifc;*.ifczip)|*.ifc;*.ifczip|All files (*.*)|*.*", folder);
                if (ifc == null) { return; }
            }
            if (!IfcImporter.HasExtension(ifc))
            {
                FileDialogs.ShowError(_window.Handle, $"{Path.GetFileName(ifc)} is not an .ifc or .ifczip file.", "BimGo update from IFC");
                return;
            }
            string dropped = _journal.Count > 0 ? $"\n\nThe {_journal.Count} edit{(_journal.Count == 1 ? "" : "s")} made in the walkthrough (moves, demolitions…) belong to the old geometry and are left out." : string.Empty;
            bool go = FileDialogs.AskYesNo(_window.Handle,
                $"Replace the model in {DocumentName} with {Path.GetFileName(ifc)}?\n\nComments, views, site photos, measurements, the sun, billboards, camera paths and the section box are kept. The file is saved and reopens where you stand.{dropped}",
                "BimGo update from IFC");
            if (!go) { return; }

            // Import (Esc cancels; nothing changed then)
            var progress = new Utilities.OperationProgress();
            progress.Begin("Reading the IFC file", 0.0, 0.85);
            LaunchSettings settings = LaunchSettings.LoadOrDefault();
            string error = null;
            BimGoDocument imported = null;
            try
            {
                imported = ProgressScreen.Run(_window, _ui, $"Importing {Path.GetFileName(ifc)}", progress,
                    () => IfcImporter.Import(ifc, settings, out error, out _, progress), cancelOnClose: false);
            }
            catch (OperationCanceledException)
            {
                imported = null;
            }
            _window.Input.ReleaseAll();
            if (imported?.Scene == null)
            {
                bool cancelled = progress.CancelRequested;
                Sound.Play(cancelled ? SoundId.UiClick : SoundId.Error);
                Toast(cancelled ? "Update cancelled: nothing changed." : $"{Path.GetFileName(ifc)} could not be imported: {error}", 6f, important: !cancelled);
                return;
            }

            // Saved into this file with the review kept, then reopened from it
            string path = DocumentPath;
            BimGoDocument document = ModelUpdate.WithNewModel(DocumentForSave(path, _options.Document?.Thumbnail, viewerCopy: false), imported.Scene);
            progress.Begin("Writing the .bimgo file", 0.85, 1.0);
            bool written = ProgressScreen.Run(_window, _ui, $"Saving {Path.GetFileName(path)}", progress,
                () => BimGoWriter.Write(path, document, _options.Writer, FileKinds.SAVE, out error, progress: progress), cancelOnClose: false);
            if (!FinishSave(path, written, error, progress, viewerCopy: false)) { return; }

            Utilities.Log_Utils.Write($"Reloaded {path} from {ifc} ({imported.Scene.Elements.Length} elements; {_journal.Count} walkthrough edits left out).");
            if (_options.OnReopen == null)
            {
                Toast("Updated and saved: close and open the file again to see the new model", 6f, important: true);
                return;
            }
            _options.OnReopen(path, CapturePose());
            RequestEnd(SessionEndReason.Switch);
        }

        /// <summary>
        /// Before closing: if there are unsaved changes, asks Save / Don't save / Cancel.
        /// </summary>
        /// <returns>True to go ahead and close.</returns>
        private bool ConfirmDiscardOrSave()
        {
            if (!IsDirty) { return true; }
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();

            FileDialogs.Answer answer = FileDialogs.AskYesNoCancel(_window.Handle,
                $"Save changes to {DocumentName}?\n\n{_journal.Count} edit(s), {Comments.Comments.Count} comment(s), {Bookmarks.Bookmarks.Count} bookmark(s).", "BimGo");
            return answer switch
            {
                FileDialogs.Answer.Yes => Save(saveAs: false),
                FileDialogs.Answer.No => true,
                _ => false
            };
        }

        /// <summary>
        /// The folder offered by Save dialogs: the open file's, else the Revit model's, else Documents.
        /// </summary>
        private string SuggestedFolder()
        {
            try
            {
                string folder = !string.IsNullOrEmpty(DocumentPath) ? Path.GetDirectoryName(DocumentPath) : null;
                if (string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(Scene.Provenance?.ModelPath) && Path.IsPathRooted(Scene.Provenance.ModelPath))
                {
                    folder = Path.GetDirectoryName(Scene.Provenance.ModelPath);
                }
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) { return folder; }
            }
            catch
            {
                // Fall through
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        private static string SafeFileName(string name) => Utilities.FileNames.Safe(name);

        #endregion

        #region Host messages

        /// <summary>
        /// Files dropped on the window and notices from the host (e.g. the app's open-request inbox).
        /// </summary>
        private void PollHost(float dt)
        {
            UpdateLive();
            UpdateRevitViews(); // Round X-E: views Revit's ribbon asked for
            UpdateCommentMarkers(); // Round Z-B: comments → Revit markers
            UpdatePush(dt);
            UpdateSun(dt);
            UpdateScreenshot();
            UpdateVisibilitySidecar(dt);
            UpdateSunStudy();

            while (_window.TryTakeDroppedFile(out string dropped))
            {
                if (_kiosk) { continue; } // Round Z-C: kiosk visitors can't open files (drops are dropped)
                if (_options.OnOpenRequest != null)
                {
                    string message = _options.OnOpenRequest(dropped);
                    if (message != null) { Toast(message, 4f); }
                }
                else
                {
                    Toast("Open .bimgo files in the BimGo app", 3f);
                }
            }

            _noticeTimer += dt;
            // Round Z-C: kiosk mode leaves other launches' requests in the inbox (they open after leaving kiosk mode)
            if (_noticeTimer < 1f || _options.PollNotices == null || _kiosk) { return; }
            _noticeTimer = 0f;
            string notice = _options.PollNotices();
            if (notice != null) { Toast(notice, 4f); }
        }

        #endregion
    }
}
