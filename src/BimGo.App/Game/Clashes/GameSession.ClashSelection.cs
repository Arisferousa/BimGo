using System.Numerics;
using BimGo.Audio;
using BimGo.Clashes;
using BimGo.Physics;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Several clashes at once in clash mode (Round C, build C-B3). In the docked list a click goes to one clash (the
    /// selection is just that one); <b>Ctrl+click</b> adds or removes a clash, <b>Shift+click</b> selects the run from
    /// the last clicked one. With two or more selected the clash view shows every pair (A red, B green), the section
    /// box (when on) covers every selected clash's region, and the camera stands back far enough to see them all. The
    /// card shows the clicked clash; 1–5 set every selected clash's status, R shows them all in Revit, TO COMMENT makes
    /// a comment for each. `.` / `,`, a plain click, a test change or the filters hiding all but one go back to one.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        // The selected clashes in click order (empty or one: a single clash, shown as before)
        private readonly List<ClashRecord> _clashSelected = new();
        private ClashRecord _clashAnchor;

        // Every selected pair as walked (static elements and moved / cloned instances), drawn together
        private readonly List<int> _clashMultiA = new(), _clashMultiB = new();
        private readonly List<DynamicInstance> _clashMultiDynamicA = new(), _clashMultiDynamicB = new();

        // True while a single clash's picture renders (the selection is set aside)
        private bool _clashMultiSuspended;

        #endregion

        #region State

        /// <summary>True while two or more clashes are shown together.</summary>
        private bool ClashMultiActive => !_clashMultiSuspended && _clashSelected.Count > 1;

        /// <summary>True when a clash is part of the selection (two or more).</summary>
        private bool IsClashSelected(ClashRecord clash) => _clashSelected.Count > 1 && _clashSelected.Contains(clash);

        /// <summary>The clashes 1–5, R and TO COMMENT act on: the selection, or the current clash.</summary>
        private IReadOnlyList<ClashRecord> ClashTargets()
        {
            if (ClashMultiActive) { return _clashSelected; }
            return _clashCurrent == null ? Array.Empty<ClashRecord>() : new[] { _clashCurrent };
        }

        #endregion

        #region Picking

        /// <summary>
        /// A click on a list row: plain = that clash alone; Ctrl = add / remove it; Shift = the run from the last clicked
        /// (Ctrl+Shift adds the run to the selection).
        /// </summary>
        private void ClickClashRow(ClashRecord clash, bool ctrl, bool shift)
        {
            if (clash == null) { return; }
            if (!ctrl && !shift)
            {
                _clashAnchor = clash;
                GoToClash(clash, animate: !_clashInstant);
                return;
            }

            // Start from the clash on screen when nothing is selected yet
            if (_clashSelected.Count == 0 && _clashCurrent != null && _clashList.Contains(_clashCurrent)) { _clashSelected.Add(_clashCurrent); }

            if (shift)
            {
                ClashRecord anchor = _clashAnchor != null && _clashList.Contains(_clashAnchor) ? _clashAnchor : _clashCurrent;
                int from = anchor == null ? -1 : _clashList.IndexOf(anchor), to = _clashList.IndexOf(clash);
                if (from < 0) { from = to; }
                if (!ctrl) { _clashSelected.Clear(); }
                for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                {
                    if (!_clashSelected.Contains(_clashList[i])) { _clashSelected.Add(_clashList[i]); }
                }
                _clashCurrent = clash;
            }
            else
            {
                _clashAnchor = clash;
                if (_clashSelected.Remove(clash))
                {
                    if (_clashCurrent == clash) { _clashCurrent = _clashSelected.Count > 0 ? _clashSelected[^1] : null; }
                }
                else
                {
                    _clashSelected.Add(clash);
                    _clashCurrent = clash;
                }
            }

            Sound.Play(SoundId.UiClick);
            if (_clashSelected.Count == 0) { return; }
            if (_clashSelected.Count == 1)
            {
                ClashRecord only = _clashSelected[0];
                GoToClash(only, animate: !_clashInstant);
                return;
            }
            GoToSelectedClashes(animate: !_clashInstant);
            Toast($"{_clashSelected.Count} clashes together · Ctrl+click adds / removes · Shift+click a run · a plain click goes back to one", 2.5f);
        }

        /// <summary>Back to a single clash (the current one stays on screen).</summary>
        private void ClearClashSelection()
        {
            _clashSelected.Clear();
            _clashMultiA.Clear();
            _clashMultiB.Clear();
            _clashMultiDynamicA.Clear();
            _clashMultiDynamicB.Clear();
        }

        /// <summary>
        /// After the list changed: drops selected clashes it no longer shows (one left: back to a single clash) and
        /// finds the remaining pairs again (the camera stays).
        /// </summary>
        private void PruneClashSelection()
        {
            if (_clashSelected.Count == 0) { return; }
            int before = _clashSelected.Count;
            _clashSelected.RemoveAll(c => !_clashList.Contains(c));
            if (_clashSelected.Count < 2)
            {
                ClearClashSelection();
                return;
            }
            if (_clashSelected.Count != before || _clashMultiA.Count + _clashMultiDynamicA.Count + _clashMultiB.Count + _clashMultiDynamicB.Count == 0)
            {
                ResolveSelectedClashes(out _, out _, out _);
            }
            if (!_clashSelected.Contains(_clashCurrent)) { _clashCurrent = _clashSelected[^1]; }
        }

        #endregion

        #region GO

        /// <summary>
        /// Shows the selected clashes together: every pair found as walked, the section box over all their regions (when
        /// on), the camera far enough back to see them, the clicked clash on the card.
        /// </summary>
        private void GoToSelectedClashes(bool animate)
        {
            if (_clashSelected.Count < 2) { return; }
            if (_clashCurrent == null || !_clashSelected.Contains(_clashCurrent)) { _clashCurrent = _clashSelected[^1]; }
            ResolveClashElement(_clashCurrent.A, out _clashElementA, out _clashDynamicA);
            ResolveClashElement(_clashCurrent.B, out _clashElementB, out _clashDynamicB);

            ResolveSelectedClashes(out Aabb region, out Aabb boxesA, out Aabb boxesB);
            Vector3 centre = region.Center;
            float distance = ClashFraming.DistanceToSee(region, _fov, Camera.Aspect);
            ClashView view = ClashFraming.Frame(centre, boxesA, boxesB, region, _player.Yaw, distance: distance);
            _clashFrame = view;

            Vector3 origin = Scene.OriginOffset;
            _clashSection = new SectionCut
            {
                BoxOn = true,
                MinX = region.Min.X + origin.X, MinY = region.Min.Y + origin.Y, MinZ = region.Min.Z + origin.Z,
                MaxX = region.Max.X + origin.X, MaxY = region.Max.Y + origin.Y, MaxZ = region.Max.Z + origin.Z
            };
            if (_clashBoxOn) { ApplySection(_clashSection, announce: false); }

            var point = new Vector3((float)(_clashCurrent.X - origin.X), (float)(_clashCurrent.Y - origin.Y), (float)(_clashCurrent.Z - origin.Z));
            int room = FindRoom(point);
            _clashCurrentRoom = room >= 0 ? (string.IsNullOrWhiteSpace(Scene.Rooms[room].Number) || Scene.Rooms[room].Number == "—"
                ? Scene.Rooms[room].Name : $"{Scene.Rooms[room].Number} · {Scene.Rooms[room].Name}") : null;
            _clashScrollToCurrent = true;
            RefreshClashCard();
            FlyToClashView(view, animate);
        }

        /// <summary>
        /// Finds every selected pair as walked (into the multi lists) and the boxes they make together: the section
        /// regions, the A elements and the B elements.
        /// </summary>
        private void ResolveSelectedClashes(out Aabb region, out Aabb boxesA, out Aabb boxesB)
        {
            _clashMultiA.Clear();
            _clashMultiB.Clear();
            _clashMultiDynamicA.Clear();
            _clashMultiDynamicB.Clear();
            region = Aabb.Empty;
            boxesA = Aabb.Empty;
            boxesB = Aabb.Empty;
            Vector3 origin = Scene.OriginOffset;
            foreach (ClashRecord clash in _clashSelected)
            {
                ResolveClashElement(clash.A, out int a, out DynamicInstance da);
                ResolveClashElement(clash.B, out int b, out DynamicInstance db);
                if (a >= 0 && !_clashMultiA.Contains(a)) { _clashMultiA.Add(a); }
                if (da != null && !_clashMultiDynamicA.Contains(da)) { _clashMultiDynamicA.Add(da); }
                if (b >= 0 && !_clashMultiB.Contains(b)) { _clashMultiB.Add(b); }
                if (db != null && !_clashMultiDynamicB.Contains(db)) { _clashMultiDynamicB.Add(db); }

                var point = new Vector3((float)(clash.X - origin.X), (float)(clash.Y - origin.Y), (float)(clash.Z - origin.Z));
                Aabb boxA = ClashBoundsOf(a, da, point), boxB = ClashBoundsOf(b, db, point);
                Aabb section = ClashFraming.SectionFor(boxA, boxB, point);
                region.Include(section.Min);
                region.Include(section.Max);
                boxesA.Include(boxA.Min);
                boxesA.Include(boxA.Max);
                boxesB.Include(boxB.Min);
                boxesB.Include(boxB.Max);
            }
        }

        /// <summary>Every selected pair in red (A) and green (B).</summary>
        private void DrawSelectedClashPairs(in SceneDrawParams q)
        {
            foreach (int element in _clashMultiA) { _renderer.DrawElementHighlight(q, element, CLASH_A_TINT); }
            foreach (DynamicInstance instance in _clashMultiDynamicA) { _renderer.DrawDynamicHighlight(q, instance, CLASH_A_TINT); }
            foreach (int element in _clashMultiB) { _renderer.DrawElementHighlight(q, element, CLASH_B_TINT); }
            foreach (DynamicInstance instance in _clashMultiDynamicB) { _renderer.DrawDynamicHighlight(q, instance, CLASH_B_TINT); }
        }

        #endregion
    }
}
