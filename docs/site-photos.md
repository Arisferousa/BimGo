# Site photos

← [Docs home](README.md)

Photographs of the site, linked to the model: each one stands where it was taken, its thumbnail floats over the model
while you orbit or walk, and a click looks through it, the photo over the model. The photos are kept **inside the
`.bimgo`**, so whoever opens the file sees them without the originals.

## Importing

Esc → **SITE PHOTOS**:

- **IMPORT PHOTOS…** picks JPEG files; **IMPORT FOLDER…** takes every JPEG in a folder and its sub-folders. Each
  sub-folder becomes a **group** (one per building, for example), which the simple viewer lists.
- Each photo is turned upright and kept at up to 2048 px (about 0.4 MB) with a small thumbnail: 100 to 500 photos add
  roughly 40 to 200 MB to the file. Photos already in the model are skipped.
- From the photo's EXIF: the **GPS position**, the **compass heading**, the lens (the 35 mm equivalent focal length, for
  the field of view) and when it was taken. HEIC photos (iPhone) need converting to JPEG first.

![The SITE PHOTOS panel](images/site-photos-panel.png)

## Lining them up with the model

GPS says where on Earth a photo was taken; the model has its own coordinates. Pinning a photo by hand ties the two
together:

1. In SITE PHOTOS, **PLACE** (or OPEN) a photo with GPS. The photo shows over the model, see-through.
2. Fly with the move keys (Space / Ctrl up and down), right-drag to look and scroll to set the lens, until the model lines
   up with the photo (the edges of buildings, the kerbs). Use the crosshair in the middle.
3. **PIN HERE**.

With **one pin**, every other photo with GPS is placed using the model's true north. **Pin a second photo far from the
first** (across the site) to fit the turn as well; more pins average out GPS error, and the panel says how well they
agree. Photos placed from GPS stand on the first floor or ground below them, 1.6 m up, facing their compass heading.
The pins also correct the compass: phones often record magnetic north, and that offset is learnt from them.

A photo without GPS is placed the same way, by hand. **ADJUST POSITION** on any photo moves it; **UNPIN** lets it be
placed from GPS again.

![Lining a photo up](images/site-photo-view.png)

## Looking through a photo

Click a thumbnail (in the simple viewer, with the cursor; in the walkthrough, with the crosshair and no gun out). The
camera goes to where the photo was taken, with its lens, and the photo covers the model:

- the **mouse wheel** or the slider fades between the photo and the model;
- **‹ PREVIOUS** and **NEXT ›** step through the photos (within a group when you browse one);
- **CLOSE** (or Esc) leaves you standing there.

In the simple viewer, the **site photos** button on the toolbar shows or hides the photos, BROWSE PHOTOS starts at the
nearest one, and the groups step through one building's photos. See [Simple viewer](simple-viewer.md).

## A newer model

When the IFC is exported again, Esc → **RELOAD FROM IFC…** puts the new model into the open `.bimgo` and keeps
everything reviewed on it: site photos, comments, views, measurements, the sun, billboards, camera paths and the
section box. Edits made in the walkthrough (moves, demolitions) belong to the old geometry and are left out. The file
is saved and reopens where you stand.

## Good to know

- GPS from a phone is good to a few metres (worse between tall buildings): a photo that lands on a roof or inside a
  building can be pinned by hand.
- In a live session from Revit, the photos are kept in the model's folder (`photos.json` and `photos-images`), and a
  SAVE VIEWER COPY… takes them into the file.
