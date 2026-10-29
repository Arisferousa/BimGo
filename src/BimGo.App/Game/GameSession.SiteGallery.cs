using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// The photo gallery (site photos round): every site photo as a tile, grouped (one heading per group, such as a
    /// building; ungrouped ones last). A click looks through the photo from its camera (the photo view); in the full
    /// walkthrough a photo not in the model yet opens to be placed. From SITE PHOTOS → GALLERY and the simple viewer's
    /// site photos popover.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private const int GALLERY_UPLOADS = 6;

        private bool _siteGalleryOpen;
        private readonly ScrollRegion _siteGalleryScroll = new();

        // The photos grouped (indices into the photos; a heading where a group starts), rebuilt when they change
        private readonly List<int> _galleryOrder = new();
        private readonly List<(int Start, string Heading)> _galleryHeadings = new();
        private int _galleryRevision = -1;

        #endregion

        /// <summary>True while the gallery replaces the pause menu.</summary>
        private bool IsSiteGalleryOpen => _siteGalleryOpen;

        private void OpenSiteGallery()
        {
            _siteGalleryOpen = true;
            _siteGalleryScroll.Reset();
        }

        /// <summary>One level back: the gallery closes.</summary>
        /// <returns>True if it was open.</returns>
        private bool CloseSiteGallery()
        {
            if (!_siteGalleryOpen) { return false; }
            _siteGalleryOpen = false;
            return true;
        }

        /// <summary>The simple viewer's GALLERY…: the gallery over the model (closing it goes back to the model).</summary>
        private void ViewerOpenGallery()
        {
            _viewerPopup = VP_NONE;
            ViewerCancelDrag();
            SetPaused(true);
            _viewerFromBar = true;
            OpenSiteGallery();
        }

        /// <summary>Groups the photos for the gallery (named groups in the order they first come, then the rest).</summary>
        private void RefreshGalleryOrder()
        {
            if (_galleryRevision == SitePhotos.Revision) { return; }
            _galleryRevision = SitePhotos.Revision;
            _galleryOrder.Clear();
            _galleryHeadings.Clear();
            List<SitePhoto> photos = SitePhotos.Photos;
            var groups = new List<string>();
            foreach (SitePhoto photo in photos)
            {
                if (!string.IsNullOrEmpty(photo.Group) && !groups.Exists(g => string.Equals(g, photo.Group, StringComparison.OrdinalIgnoreCase))) { groups.Add(photo.Group); }
            }
            foreach (string group in groups)
            {
                _galleryHeadings.Add((_galleryOrder.Count, group));
                for (int i = 0; i < photos.Count; i++)
                {
                    if (string.Equals(photos[i].Group, group, StringComparison.OrdinalIgnoreCase)) { _galleryOrder.Add(i); }
                }
            }
            int rest = _galleryOrder.Count;
            for (int i = 0; i < photos.Count; i++)
            {
                if (string.IsNullOrEmpty(photos[i].Group)) { _galleryOrder.Add(i); }
            }
            if (_galleryOrder.Count > rest) { _galleryHeadings.Add((rest, groups.Count > 0 ? "Other photos" : "All photos")); }
        }

        /// <summary>The gallery panel: headings and tiles (picture, name, time, how it is placed) in a scroll region.</summary>
        private void BuildSiteGalleryPanel()
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            RefreshGalleryOrder();
            int count = SitePhotos.Photos.Count;
            Text.Clear().AppendGrouped(count).Append(count == 1 ? " photo" : " photos");
            int groups = _galleryHeadings.Count;
            if (groups > 1) { Text.Append(" in ").Append(groups).Append(" groups"); }
            PanelFrame frame = BeginPanel(PanelSize.Wide, "PHOTO GALLERY", UiTheme.BOOKMARK_LABEL, Text.Span);

            SitePhoto open = null;
            if (count == 0)
            {
                _ui.TextWrapped(f.Body, frame.Ix, frame.BodyTop + S(8), frame.Iw, "No site photos in this model yet.", UiTheme.TEXT_MUTED, maxLines: 2);
            }
            else
            {
                float top = BeginScroll(_siteGalleryScroll, input, frame.Ix, frame.BodyTop, frame.Iw, frame.BodyBottom - frame.BodyTop, out float cw);
                float gap = S(UiTheme.GAP), tileW = S(208), thumbH = S(136), tileH = thumbH + S(46), headingH = S(34);
                int columns = Math.Max(1, (int)((cw + gap) / (tileW + gap)));
                tileW = (cw - gap * (columns - 1)) / columns;
                thumbH = tileW * 0.65f;
                tileH = thumbH + S(46);
                float viewTop = _siteGalleryScroll.Y, viewBottom = _siteGalleryScroll.Y + _siteGalleryScroll.H;
                int uploads = GALLERY_UPLOADS;

                float y = top;
                for (int h = 0; h < _galleryHeadings.Count; h++)
                {
                    (int start, string heading) = _galleryHeadings[h];
                    int end = h + 1 < _galleryHeadings.Count ? _galleryHeadings[h + 1].Start : _galleryOrder.Count;
                    if (y + headingH > viewTop && y < viewBottom)
                    {
                        _ui.Text(f.Bold, frame.Ix, y + S(8), heading, UiTheme.BOOKMARK_LABEL);
                        Text.Clear().Append(end - start).Append(end - start == 1 ? " photo" : " photos");
                        _ui.TextRight(f.Small, frame.Ix + cw, y + S(11), Text.Span, UiTheme.TEXT_FAINT);
                    }
                    y += headingH;
                    int rows = (end - start + columns - 1) / columns;
                    for (int r = 0; r < rows; r++)
                    {
                        float ty = y + r * (tileH + gap);
                        if (ty + tileH < viewTop || ty > viewBottom) { continue; } // off screen
                        for (int c = 0; c < columns; c++)
                        {
                            int slot = start + r * columns + c;
                            if (slot >= end) { break; }
                            SitePhoto photo = SitePhotos.Photos[_galleryOrder[slot]];
                            if (GalleryTile(f, input, photo, frame.Ix + c * (tileW + gap), ty, tileW, thumbH, tileH, ref uploads)) { open = photo; }
                        }
                    }
                    y += rows * (tileH + gap) + S(UiTheme.GAP_SMALL);
                }
                EndScroll(_siteGalleryScroll, input, y);
            }

            if (EndPanel(frame, null, _viewer ? "Click a photo to look through it from where it was taken." : "Click a photo to look through it (one not in the model yet opens to be placed).") == FOOTER_LEFT)
            {
                return; // BACK or CLOSE left the panel
            }
            if (open != null)
            {
                CloseSiteGallery();
                CloseSitePhotosPanel();
                OpenSitePhoto(open, adjust: !open.IsPlaced);
            }
        }

        /// <summary>One tile: the thumbnail (cropped to the tile), the name, the time and how it is placed.</summary>
        /// <returns>True when clicked (and it can be opened here).</returns>
        private bool GalleryTile(FontAtlas f, InputState input, SitePhoto photo, float x, float y, float w, float thumbH, float h, ref int uploads)
        {
            bool usable = photo.IsPlaced || !_viewer;
            bool hover = usable && Hover(input, x, y, w, h);
            _ui.Rect(x, y, w, h, hover ? UiTheme.HOVER : UiTheme.CONTROL);
            _ui.Outline(x, y, w, h, MathF.Max(1f, UiScale), hover ? UiTheme.ACCENT : UiTheme.CONTROL_BORDER);

            // The picture fitted inside the tile's picture area (letterboxed)
            uint thumb = SiteThumbTexture(photo, ref uploads);
            float aspect = Math.Clamp(photo.Aspect, 0.4f, 2.5f);
            float iw = w - S(8), ih = thumbH - S(4);
            float pw = MathF.Min(iw, ih * aspect), ph = pw / aspect;
            float px = x + (w - pw) * 0.5f, py = y + S(4) + (ih - ph) * 0.5f;
            if (thumb != 0) { _ui.Image(thumb, px, py, pw, ph, _window.Width, _window.Height, usable ? 0xFFFFFFFFu : Rgba.Hex(0xFFFFFF, 0.4f)); }

            _ui.TextWrapped(f.Body, x + S(8), y + thumbH + S(6), w - S(16), photo.Name, usable ? UiTheme.TEXT : UiTheme.TEXT_FAINT, maxLines: 1);
            string state = photo.Placement == PhotoPlacements.PINNED ? "pinned" : photo.Placement == PhotoPlacements.GPS ? "placed from GPS" : "not in the model yet";
            TextBuffer text = Text.Clear();
            if (!string.IsNullOrEmpty(photo.Taken)) { text.Append(photo.Taken).Append(" · "); }
            text.Append(state);
            _ui.TextWrapped(f.Small, x + S(8), y + thumbH + S(26), w - S(16), text.Span, photo.IsPlaced ? UiTheme.TEXT_MUTED : UiTheme.MEASURE_TEXT, maxLines: 1);

            if (hover && input.LeftPressed)
            {
                input.ConsumeClicks();
                Sound.Play(SoundId.UiClick);
                return true;
            }
            return false;
        }
    }
}
