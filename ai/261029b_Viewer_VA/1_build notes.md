# V-A build notes: simple viewer

**Built and tested here**: the .NET 8 SDK (Ubuntu package) ran BimGo.Core and its tests (`dotnet test
tests/BimGo.Core.Tests`: all pass except `SunStudyTests.Studies_WriteListReadDelete`, which fails on Linux only because
':' is a legal file name character there; unchanged by this round). BimGo.App was compile-checked with a scratch project
against the `Microsoft.WindowsDesktop.App.Ref` 8.0.0 reference assemblies (0 errors, no new warnings); it was **not run**
(no Windows / GPU). The orbit maths (the pivot stays on the same pixel while yaw and pitch turn about it) was checked
numerically. Nothing in BimGo.Revit changed.

## Files
- Core `Format/FileModels.cs`: `ManifestDto.Viewer`, `ViewerDto` (`open`, `start` orbit / walk; `Copy` reads unknown
  starts as orbit), `ViewerStart`. `BimGoDocument.Viewer`; the writer writes it only when `open`, the reader keeps it only
  when `open`. Format version unchanged (additive, optional).
- Core `Scene/InputCatalog.cs`: `SIT_VIEWER` (move, run, jump, look, drag look, home, save, fullscreen, screenshot,
  click; no gamepad menu click: A jumps there). No new action, so `DEFAULTS_VERSION` stays.
- App `Game/GameSession.Viewer.cs` (new): flag, enter / leave, the orbit camera (free pose turned about a per-press
  pivot), walk, mouse (press / drag / click / double-click), tools armed for the cursor, toolbar with drawn icons and
  tooltips, the tool panel (Measure modes and REMOVE LAST; Comments and ALL COMMENTS…), views and sun popovers, the
  viewer's menu, SAVE VIEWER COPY….
- App hooks: `GameSession.cs` (flag read, `EnterViewer` at the end of `Initialise`, no pause on focus loss,
  `UpdateViewer` after the editors / panels, player frozen while orbiting, no portals, orbit camera in `UpdateCamera`,
  cursor aim in `UpdateAim`, clicks in `UpdateGuns`, `CursorModeOpen`), `Render.cs` (`BuildViewerHud` instead of
  `BuildHud`), `Menu.cs` (`BuildViewerMenu` instead of the pause menu; SAVE VIEWER COPY… / SIMPLE VIEWER in the session
  stack), `Document.cs` (`Save(saveAs, viewerCopy)`: a copy leaves `DocumentPath` and the saved state alone; `viewer`
  kept on save), `Section.cs` (the editor yields to the viewer's mouse off the handles; no capture on close in the
  viewer), `Gamepad.cs` (`CurrentSituation`), `Guns/CommentGun.cs` (hover at `AimPixel`).
- Tests `ViewerTests.cs` (4): manifest round trip, left out of ordinary / switched-off files, unknown start, the
  situation's live keys. `ControlsTests` lists the new situation.
- Docs: `docs/simple-viewer.md` (new page), docs home, README features, DEVELOPING (walkthrough tools, format table,
  open build, limitations), HISTORY, two placeholder images (`viewer-orbit.png`, `viewer-copy.png`) and their rows.

## Decisions worth knowing
- **The player follows the orbit camera** (flying at the eye, physics frozen), so bookmarks, comment views, home, the
  tour, the room readout and saving the view all work unchanged. A move made by something else is detected by comparing
  the player's pose with the one last written: flying → orbit from it, walking → walk.
- **Orbit pivot = the point under the cursor at the press** (else the view centre's hit, else the model centre ahead):
  you orbit the building you grabbed. The pivot dot shows while dragging.
- **The cursor is never captured** in the viewer (Autodesk-style walk: drag to look), so the toolbar is always usable.
- **Clicks fire the armed tool in `UpdateGuns`** (after `UpdateAim` has followed the cursor), never through the UI's
  rectangles (last frame's). RMB never deletes a comment in the viewer.
- **Home**: the saved home when there is one (walking or flying as saved), else the whole model from the south-west.
- Sun popover: time in 5 minute steps, month (the day clamped to its length); moving either turns shadows on.

## To verify (Gavin)
See DEVELOPING → Open build. Also worth a look: orbiting a large site (5 km) and zooming near a facade (min range
0.4 m), panning speed at the pivot's depth, the toolbar at 150 % scaling on a 1366 px wide screen, and whether the
icons read well.

## Revision 2 (after testing on Windows)
- **V** (earlier revision): the fly key toggles orbit and walk. Bug found in testing: after V from orbit (falling), V
  wouldn't go back to orbit. Going back left the player walking for one physics tick; falling moved the feet, which the
  orbit camera read as a move made elsewhere and switched straight back to walking. `SetViewerWalk(false)` now flies the
  player at the eye at once (`OrbitToPlayer`).
- **Keys for the tools** (`InputContext.Viewer`, defaults version 10): M measure, X section box (closes the editor too),
  C comments, B views, O sun; tooltips show them.
- **Pass through walls** (G, a toolbar button in the walk group): walking with the player flying (no-clip); a flying
  bookmark while it's on stays walking; from orbiting it starts where the camera is.
- **Walking speed**: = / - (repeating) and the wheel while walking; `Player.SpeedScale` (0.25–4×), shown in the title.
- The views popover's buttons are full width, one under another (labels didn't fit side by side).
- The walk flag is reset on entering the viewer (it could stay set from an earlier visit), and the title's room label is
  made once per room instead of every frame.

## Revision 3
- **Small white marks under text** (all UI text, not only the viewer): accented capitals and tails (É, Å, Ç) reach
  past the font's line box, and the atlas drew them with `NoClip` into the glyphs of the row above, so those glyphs
  carried a stray dot at their bottom. Each glyph now has a padded cell (top 25 %, bottom 15 %, sides 10 % of the size)
  and is clipped to it; a dry run of the packer picks the smallest atlas (1024 → 8192) that holds everything.
