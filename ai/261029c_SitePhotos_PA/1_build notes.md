# P-A build notes: site photos

**Built and tested here**: the .NET 8 SDK (Ubuntu package) ran BimGo.Core and its tests (`dotnet test
tests/BimGo.Core.Tests`: all pass except `SunStudyTests.Studies_WriteListReadDelete`, Linux only, unchanged). The EXIF
reader was also run against a JPEG written by Pillow (GPS south / east, altitude, magnetic heading, 35 mm focal length,
orientation 6, date): every value read back. BimGo.App was compile-checked with a scratch project against the
`Microsoft.WindowsDesktop.App.Ref` 8.0.0 reference assemblies (0 errors); it was **not run** (no Windows / GPU), so the
JPEG decode / rotate / scale path (System.Drawing) and everything drawn are untested. Nothing in BimGo.Revit changed.

## Files
- Core `Format/ExifReader.cs` (new): `ExifInfo`, `ExifReader.Read` (APP1 → TIFF, both byte orders, IFD0 → Exif / GPS
  IFDs; bounded reads, never throws; 0 / 0 GPS treated as no fix).
- Core `Format/PhotoModels.cs` (new): `PhotoPlacements`, `SitePhoto` (GPS, heading, size, lens, placement, eye pose in
  internal metres; `Clean`), `PhotoDocument` (`showPins`, ≤ 2000 photos, safe ids / names), `PhotoFiles` (the model
  folder's `photos.json` + `photos-images`, or `<model>.bimgo-photos.json` beside the comments; not in the
  `ModelFolders.SIDECARS` mirror list, since the mirror would copy the JSON without its pictures).
- Core `Scene/PhotoGeo.cs` (new): `PhotoAnchor`, `PhotoGeoFit` (`Apply`, `YawOf`), `PhotoGeo.ToLocal`, `TryFit`,
  `HeadingCorrection`, `FieldOfView`.
- Core format: `BimGoFormat` (`ENTRY_PHOTOS`, `PHOTOS_FOLDER`, `MAX_PHOTO_IMAGE_BYTES`, `PHOTOS_SIDECAR_SUFFIX`),
  `BimGoDocument.Photos` / `PhotoImages`, writer (pictures stored, not deflated), reader. Format version unchanged.
- Core `InputCatalog`: `SIT_SITE_PHOTO` (no new action).
- App `Game/PhotoStore.cs` (new), `Game/GameSession.SitePhotos.cs` (new: import, alignment, pins, thumbnails, photo
  view, the panel, textures), `Platform/FileDialogs.ShowOpenMany`.
- App hooks: `GameSession.cs` (setup after the sun, the photo view before the editors, player frozen while viewing,
  its camera and lens in `UpdateCamera`, no firing in it, the no-gun crosshair click, `CursorModeOpen`, cleanup),
  `Render.cs` (the photo view's 2D layer, pins with the markers, thumbnails in the HUD, no minimap), `Menu.cs` (SITE
  PHOTOS in the review stack, RELOAD FROM IFC… in the session stack, the panel), `PanelShell.cs` (back / any panel),
  `Document.cs` (dirty state; `DocumentForSave` / `FinishSave` split out of `Save`; `ReloadFromIfc`), `Gamepad.cs`
  (situation, editor cursor), `Viewer.cs` (site photos button and popover, thumbnails, `ViewerOverUi`; a reopened
  file keeps the pose), `SessionOptions.OnReopen`, `Shell/AppShell.cs` (reopen with a pose).
- Tests `SitePhotoTests.cs` (11): EXIF in both byte orders from a TIFF written in the test, truncated / garbage input,
  local metres, the fit from three anchors (turn, shift, a fourth point, headings), one anchor and true north (and two
  anchors on top of each other), the heading correction (and its wrap at ±180°), the field of view, a .bimgo round trip
  (and none written without photos), `Clean`, the model folder file and pictures, the situation's keys.
- Docs: `docs/site-photos.md` (new), simple viewer page, docs home, README, DEVELOPING (site photos, format table,
  model folders, open build, limitations), HISTORY, two placeholder images.

## Decisions worth knowing
- **Anchors are just pinned photos with GPS**: nothing else is stored, so unpinning or deleting one re-fits at once.
  The fit is rigid (no scale): GPS metres are metres, and a scale fitted to two noisy points would distort the site.
- **One pin uses the true north** (`SolarPosition.NorthAngle`, as the sun); two or more ignore it. Anchors within 1 m of
  their mean count as one.
- **Heading correction**: one circular-mean offset from the pins that recorded a heading, added to every GPS-placed
  photo's compass yaw (takes up magnetic declination: about 12° in Sydney).
- **Placed eye**: the first upward-facing surface under the GPS point from above the model, + 1.6 m. A roof catches a
  photo that GPS put on a building; pinning fixes it. Altitude isn't used (phone altitude is worse than position).
- **The photo view's lens**: the photo is letterboxed into the window and the window's horizontal field of view set so
  the photo's lens spans the photo's rectangle exactly (the model outside it is the true continuation).
- **Pictures in memory**: the store holds every JPEG (≈ 0.4 MB each), as entourage pictures; textures are made on demand
  (thumbnails 3 a frame, mipmapped; one full picture at a time).
- **Reload from IFC** writes the open file and reopens it rather than swapping the scene in place: everything that
  depends on the geometry (batches, BVH, lights, probes) is built the normal way.

## To verify (Gavin)
See DEVELOPING → Open build. Also worth a look: import time for 500 phone photos (≈ 12 MP), memory with 500 photos,
thumbnail clutter on a dense site (40 nearest), the photo view on an ultrawide screen, and whether 1.6 m and the first
surface below suit the site's photos.

## Revision 2 (after testing on Windows: "it runs quite good")
Asked: the heading tick as a shorter arrow; bigger thumbnails as a setting; V in the simple viewer (orbit → walk,
falling); a map in the photo view with the photo points and arrows, expandable to the whole site, with room names;
importing several folders; a gallery of every photo. Answers to the follow-up questions: the map in the photo view and
on a viewer toolbar button (and the walkthrough's minimap); aerial by default with a plan toggle; the thumbnail size kept
in the file; V falls with gravity.

- **V** (PR 1, `export-external`, merged into `site-photos`): `UpdateViewer` toggles with the fly key: orbit → walk
  keeping the pose (gravity drops the player), walk → orbit. `SIT_VIEWER` lists it; tooltips and the title hint name it.
- **Pins**: a 0.75 m arrow (shaft + two barbs) instead of the 1.2 m tick.
- **Thumbnail size**: `PhotoDocument.ThumbScale` (0.5–2.5, default 1; `Clean` clamps it); the base size went from
  30–92 px (2400 / distance) to 46–130 px (3200 / distance). Slider in SITE PHOTOS and the viewer's popover; saved on
  release (a live session isn't rewritten every frame of a drag).
- **Folders**: `Native/FolderPicker.cs` (IFileOpenDialog, vtable slots from shobjidl_core.idl: Show 3, SetOptions 9,
  GetOptions 10, SetFolder 12, SetTitle 17, GetResults 27; IShellItemArray GetCount 7, GetItemAt 8; IShellItem
  GetDisplayName 5); null → the WinForms single picker. Groups: the first sub-folder, else the folder picked.
- **Map**: `GameSession.SiteMap.cs` (see DEVELOPING). `SIT_SITE_MAP`.
- **Gallery**: `GameSession.SiteGallery.cs`.
- Tests: `ThumbScale` round trip and clamping. Core tests as before (all pass but the Linux-only one); the app
  compile-checked, not run.

To verify additionally: the multi-folder picker (Ctrl / Shift several folders; cancel), V from a high orbit (the fall,
then V back), arrows, the size slider in both places (and that a viewer copy keeps it), the small map (centred, the
current photo ringed, a click on another photo, EXPAND), the big map (aerial / plan, levels, pan, zoom at the cursor,
FIT SITE, the scale bar, hovering a photo's thumbnail, a click opening it, a double-click going there in the viewer),
room names when zoomed in, the minimap's photo arrows, the gallery (groups, tiles, scrolling 500 photos, a click).

## Revision 3 (after testing revision 2)
Asked: (1) V back to orbit failed after falling (fixed in the viewer's PR: the player now flies at the eye at once);
(2) the map in the simple viewer too; (3) closing a photo should put the camera back where it was, not leave it at the
photo; (4) the photos popover's buttons overflowed; (5) photos on the map as cameras, unlike the player; (6) keys for the
viewer's tools; (7) pass through walls and (8) the walking speed (both in the viewer's PR); (9) far thumbnails hidden.

- The viewer's corner map (`BuildViewerMiniMap`): bottom right, Tab hides it, EXPAND / a click opens the site map.
- `RememberSiteReturn` / `CloseSitePhoto`: the player and the viewer's orbit / walk / pass-through put back; looking at
  a photo no longer moves the player (only adjusting does).
- The popover's buttons are full width, one under another; a distance slider under the size slider.
- `DrawMapCamera` (body, viewfinder, lens, a wedge as wide as the lens) for photos on the corner, small, big and the
  walkthrough's maps.
- `PhotoDocument.ThumbDistance` (default 150 m) with a fade over the last fifth.
- Keys (defaults version 11): P site photos popover, Shift+P gallery, Tab corner map, Shift+Tab site map; ← / → step
  photos in the photo view.
- Tests: `ThumbDistance` round trip and clamping; the viewer situation's keys (in the viewer's PR).
