# T-A build notes: tutorial and tips

**Built and tested here**: BimGo.Core and its tests (all pass except the Linux-only
`SunStudyTests.Studies_WriteListReadDelete`, as before). BimGo.App compile-checked against the Windows Desktop
reference assemblies. **Not run** (no Windows / GPU here).

## Files
- Core `Scene/Tutorial.cs` (new): `TutorialGoal`, `TutorialSpot`, `TutorialNeed`, `TutorialStep`, `TutorialChapter`;
  `Tutorials` (`FULL`: six chapters, 32 steps with every need met; `VIEWER`: one chapter, 10 steps), `Format` (keys
  from the bindings), `StepCount`; `TutorialRun` (the steps whose needs are met, next / back, chapters finished);
  `TutorialTips` (37 tips); `TutorialOffers` (when to offer). Test: `TutorialTests` (7).
- Core `Scene/LaunchSettings.cs`: `TutorialOffered`, `ViewerTutorialOffered`, `TutorialCopies`,
  `TutorialChaptersDone`, `Tips`, `TipsSeen` (cleaned in `Sanitise`).
- Core `Format/FileModels.cs`: `ViewerDto.Id` (manifest `viewer.id`; additive, older files read null), `NewId`.
- Core `Scene/InputCatalog.cs`: `InputAction.TutorialNext` (Enter), context `Tutorial` (walking, holding, viewer),
  defaults version 12; `docs/controls.md` regenerated.
- App `Game/GameSession.Tutorial.cs` (new): the run, goal polling, the step card, spots, the menu's TUTORIAL card,
  tips. `GameSession.Welcome.cs`: the offer (both views), clicks outside the card dropped.
- Hooks: `GameSession.cs` (settings, `OfferTutorialAtStart`, `UpdateTutorial` / `UpdateTutorialPicker` in
  `UpdateFrame`, gun tips in `SelectGun`), `Render.cs` (the overlay last; spots for the crosshair, gun bar, status,
  minimap), `Interface.cs` (HELP section: TUTORIAL (N STEPS), ONLINE GUIDE; spots; the tips checkbox; icons),
  `Viewer.cs` (the card's clicks, home, tool tips, spots, the menu's TUTORIAL, the copy note), `SiteMap.cs`,
  `Document.cs` (a new id per viewer copy), and one `ShowTip` line in each feature's opener.

## Decisions worth knowing
- **Goals are polled, not evented** (mostly): each step remembers what it compares with when it starts (the gun, a
  count, a flag, the level), amounts add up per frame (a teleport isn't a walk: at most 0.5 m or 0.5 rad a frame),
  round trips need the thing opened then closed. Only RETURN HOME and the viewer's home report an event.
- **Enter is the only new key**, in its own context so it never meets photo mode's, the markup editor's, clash mode's
  or the path editor's Enter. While walking the mouse is taken, so the card shows the keys; Esc opens the menu, where
  the card's buttons work (the tutorial waits there).
- **Clicks on the cards are theirs**: taken at the start of the frame from last frame's rectangles, so nothing under a
  card reacts (the immediate-mode panels under it see no click). The menu's TUTORIAL card is modal (it takes the whole
  frame's input).
- **The welcome card** now asks about the tutorial; it is offered once more to people who saw the old card. The
  viewer, which never showed it, offers its own tutorial once per copy per PC.
- Tips save the settings when shown (once per feature).

## Revision 2: a portable zip
- `build/publish.ps1 -PortableOnly [-Include <files>]` (tests, the app, the zip; only the .NET 8 SDK needed) or
  `-Portable` (the zip as well as the setup): `artifacts\BimGo-Portable-<version>.zip` with the app folder,
  "Open .bimgo files with BimGo.cmd" (`--register`), "Remove BimGo from this PC.cmd" (`--unregister`),
  "READ ME FIRST.txt" and the included files (a viewer copy). Documented in `docs/releasing.md`; the simple viewer
  page points to it. Not run here (no PowerShell / Windows).
