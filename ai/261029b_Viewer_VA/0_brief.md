# V-A brief: simple viewer

Asked 2026-10-29: a much simpler app for a manager who only needs to open a model and look at it, with a clean look like
an online model viewer (Autodesk Viewer): orbit, measure, section box, comments; no wall of settings. The author keeps
the full BimGo, prepares the model and sends the file. The project is a master plan (a whole site from Revit / IFC).

## Decisions (asked and answered before building)
- **Viewer mode in the file**, not a second app: one codebase, one installer. SAVE VIEWER COPY… marks a copy; BimGo
  opens such a file in the simple viewer. A hidden *Open in full BimGo* in its menu switches to every tool.
- **Orbit by default, walk as a toggle** (a site overview first, street level on demand).
- Tools kept: measure, section box, **comments** (made in the viewer and sent back in the same file), **saved views and
  the tour**, **sun and shadows** (one popover: shadows, time, month). Left out: guns other than Measure / Comment,
  edits, settings, studies, clashes, media.
- **Delivered as two PRs**: this one (the viewer); the next one site photos (GPS-placed photo pins with floating
  thumbnails and a viewpoint overlay, stored inside the `.bimgo`, plus reloading the model from a newer IFC while keeping
  photos, comments and views).
