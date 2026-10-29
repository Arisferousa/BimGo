# P-A brief: site photos

Asked 2026-10-29 (the same user as V-A): the project is a master plan from Revit / IFC, with many photographs of each
building on the site. They want to see the photos in 3D, linked to the model, walk or orbit around the site and see the
real picture, and the manager they send the file to shouldn't have to download the photos separately.

## Decisions (asked and answered before building, see V-A)
- **Inside the .bimgo**, not the IFC: IFC has no standard way to carry pictures, and only BimGo would read them. The
  file grows by about 0.4 MB a photo (2048 px JPEG + thumbnail); 100–500 photos per file.
- Ground-level **phone photos with GPS**; the model may **not be georeferenced**, so placement is **GPS + a manual
  alignment**: pin one photo by hand (with the model's true north) or two or more (a fitted turn), and the rest follow.
- **Photo pins** that open the photo at its viewpoint, plus **floating thumbnails** visible while orbiting and walking
  (the user's own addition).
- **Reload from IFC** keeping photos, comments and views, for when the model is exported again.
- PR 2 of 2 after the simple viewer.
