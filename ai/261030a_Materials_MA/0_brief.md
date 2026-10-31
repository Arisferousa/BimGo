# M-A brief: materials for IFC models

Asked 2026-10-30 (the same user as V-A and P-A): a third PR, for the full BimGo. Revit LT can only export IFC, and
IFC carries no textures, so their models come in with plain colours. They want to connect each of the IFC's materials
by hand to proper materials (an image, bump, cutout and the like, "not too detailed, enough for now"), as the existing
grass and water features do, and asked first whether Revit LT can keep materials in the export at all.

## Finding (told to the user)
Revit's IFC exporter (Revit LT's too) writes material names and surface colours (with transparency), which BimGo
already read; it never writes textures, bump or cutouts (IFC's texture styles aren't written, and most readers ignore
them). Revit LT runs no add-ins and has no FBX export, so matching inside BimGo is the way. Advice: clear material
names, Graphics → Use Render Appearance, IFC2x3 if IFC4 loses colours.

## Decisions (asked and answered)
- **Stacked on site photos** (PR 2): a new branch `ifc-materials`, so updating from a newer IFC keeps the matches.
- **A reusable library, but the viewer copy self-contained**: matches are kept in a library between projects, and
  everything (images included) is inside the .bimgo, so the boss needs nothing else.
- **Bump and cutout with separate images too**: from the colour image, or an image / mask of their own.
- **Extras: all three**: overrides by element and category (not only by material), an Autodesk Material Library
  browser, a before / after toggle.
