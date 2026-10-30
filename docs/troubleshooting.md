# Troubleshooting

← [Docs home](README.md)

## BimGo doesn't start, or starts black

- Update the graphics driver from the GPU maker's site (BimGo needs OpenGL 3.3 or later).
- **Safe start:** hold **Shift** while starting BimGo. It runs with default settings and the Basic quality profile, and
  saves nothing, so a bad setting can't stop it. If that works, lower the quality profile or reset settings in the menu.
- On a laptop with two GPUs, set BimGo to use the high-performance GPU (Windows Settings → Display → Graphics).

## It runs slowly

Try the Medium or Basic quality profile, a lower light quality (Display tab), or switch off artificial lights (K).
The menu's DISPLAY tab → *Performance details* shows where the time goes.

## Revit doesn't show the BimGo tab

- Re-run the setup with **BimGo for Revit** ticked.
- If Revit asked about an unverified publisher and you chose not to load it, restart Revit and choose **Always Load**.
- "Duplicate add-in" warnings: an older copy is in `%AppData%\Autodesk\Revit\Addins\<year>\BimGo`. The setup removes
  it; delete it by hand if it came back.

## "BimGo versions differ"

BimGo for Revit and the BimGo app come from different releases. Run the latest setup so both match.

## An IFC file is missing geometry, or won't open

- The start notice says when some geometry was left out; the log (`BimGo.App.log`) lists which IFC types. Curved
  surfaces in *IFC4 Design Transfer View* exports aren't read: export again with **IFC2x3 Coordination View 2.0** or
  **IFC4 Reference View**.
- Reinforcement (rebar) isn't imported, and nor are spaces as solids (they become rooms).
- "has no 3D geometry BimGo can show": the export had no 3D elements (check the export setup's view and phase).
- Large files take a while to import; Esc cancels. Save the result as a `.bimgo`: it opens much faster than the IFC.

## Where are the logs?

`%LocalAppData%\BimGo\Logs\BimGo.App.log` (the app) and `BimGo.Revit.log` (the add-in). Include them when reporting a
problem.
