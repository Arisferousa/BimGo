using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// The photo browser (site photos round): made for a site of many buildings, one group of photos each.
    /// <list type="bullet">
    /// <item><b>Left</b>: the search box and the groups in name order, each in its colour with its photo count (only those
    /// matching the search). A click picks one; "All groups" shows every photo under a heading per group.</item>
    /// <item><b>Right</b>: the picked group's name and actions (SHOW ON MAP, BROWSE ›, only this group on the model), then
    /// its photos as tiles (GRID) or rows (LIST). A click looks through a photo from its camera (the photo view); in the
    /// full walkthrough a photo not in the model yet opens to be placed.</item>
    /// </list>
    /// From SITE PHOTOS → BROWSER, the simple viewer's site photos popover and Shift+P.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private const int GALLERY_UPLOADS = 6;

        private bool _siteGalleryOpen;
        private bool _siteBrowseList;
        private readonly ScrollRegion _siteGalleryScroll = new();
        private readonly ScrollRegion _siteGroupScroll = new();

        // The photos shown (indices into the photos; a heading where a group starts), rebuilt when they, the search or the
        // picked group change
        private readonly List<int> _galleryOrder = new();
        private readonly List<(int Start, string Heading, uint Colour)> _galleryHeadings = new();
        private int _galleryRevision = -1, _gallerySearch = -1;
        private string _galleryGroup;

        // The groups listed on the left (indices into the groups; -1: "All groups"), each frame
        private readonly List<int> _siteListedGroups = new();

        #endregion

        /// <summary>True while the browser replaces the pause menu.</summary>
        private bool IsSiteGalleryOpen => _siteGalleryOpen;

        private void OpenSiteGallery()
        {
            _siteGalleryOpen = true;
            _siteGalleryScroll.Reset();
            _siteGroupScroll.Reset();
            _siteSearchPause = 2; // the key that opened it isn't typed into the search
        }

        /// <summary>One level back: the browser closes.</summary>
        /// <returns>True if it was open.</returns>
        private bool CloseSiteGallery()
        {
            if (!_siteGalleryOpen) { return false; }
            _siteGalleryOpen = false;
            return true;
        }

        /// <summary>The simple viewer's PHOTO BROWSER… (and Shift+P): the browser over the model (closing it goes back to the model).</summary>
        private void ViewerOpenGallery()
        {
            _viewerPopup = VP_NONE;
            ViewerCancelDrag();
            SetPaused(true);
            _viewerFromBar = true;
            OpenSiteGallery();
        }

        /// <summary>
        /// The photos shown: the picked group's (or every group's, in name order with headings, ungrouped last) that match
        /// the search.
        /// </summary>
        private void RefreshGalleryOrder()
        {
            List<SiteGroupInfo> groups = SiteGroupInfos();
            if (_galleryRevision == SitePhotos.Revision && _gallerySearch == _siteSearchVersion && string.Equals(_galleryGroup, _siteBrowseGroup, StringComparison.Ordinal)) { return; }
            _galleryRevision = SitePhotos.Revision;
            _gallerySearch = _siteSearchVersion;
            _galleryGroup = _siteBrowseGroup;
            _galleryOrder.Clear();
            _galleryHeadings.Clear();
            List<SitePhoto> photos = SitePhotos.Photos;
            foreach (SiteGroupInfo group in groups)
            {
                if (_siteBrowseGroup != null && !string.Equals(group.Name, _siteBrowseGroup, StringComparison.OrdinalIgnoreCase)) { continue; }
                int start = _galleryOrder.Count;
                for (int i = 0; i < photos.Count; i++)
                {
                    if (string.Equals(photos[i].Group, group.Name, StringComparison.OrdinalIgnoreCase) && SiteSearchMatches(photos[i])) { _galleryOrder.Add(i); }
                }
                if (_galleryOrder.Count > start && _siteBrowseGroup == null) { _galleryHeadings.Add((start, group.Name, group.Colour)); }
            }
            if (_siteBrowseGroup != null) { return; }
            int rest = _galleryOrder.Count;
            for (int i = 0; i < photos.Count; i++)
            {
                if (string.IsNullOrEmpty(photos[i].Group) && SiteSearchMatches(photos[i])) { _galleryOrder.Add(i); }
            }
            if (_galleryOrder.Count > rest) { _galleryHeadings.Add((rest, groups.Count > 0 ? "Other photos" : "All photos", UiTheme.BOOKMARK)); }
        }

        /// <summary>The browser panel: search and groups on the left, the picked group's actions and photos on the right.</summary>
        private void BuildSiteGalleryPanel()
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            List<SiteGroupInfo> groups = SiteGroupInfos();
            RefreshSiteMatches();
            RefreshGalleryOrder();
            int count = SitePhotos.Photos.Count;
            Text.Clear().AppendGrouped(count).Append(count == 1 ? " photo" : " photos");
            if (groups.Count > 0) { Text.Append(" in ").Append(groups.Count).Append(groups.Count == 1 ? " group" : " groups"); }
            const string LIST = "LIST VIEW", GRID = "GRID VIEW";
            PanelFrame frame = BeginPanel(PanelSize.Wide, "PHOTO BROWSER", UiTheme.BOOKMARK_LABEL, Text.Span, headerControls: ShellButtonWidth(f, LIST));
            if (HeaderButton(ref frame, _siteBrowseList ? GRID : LIST))
            {
                _siteBrowseList = !_siteBrowseList;
                _siteGalleryScroll.Reset();
            }

            SitePhoto open = null;
            int action = 0; // 1 show on map, 2 browse
            float gap = S(UiTheme.GAP);
            float y = frame.BodyTop;
            if (count == 0)
            {
                _ui.TextWrapped(f.Body, frame.Ix, y + S(8), frame.Iw, "No site photos in this model yet.", UiTheme.TEXT_MUTED, maxLines: 2);
            }
            else
            {
                bool sidebar = groups.Count > 0;
                float sideW = sidebar ? MathF.Min(S(300), frame.Iw * 0.32f) : 0f;
                float boxH = SiteSearchBox(f, input, frame.Ix, y, sidebar ? sideW : MathF.Min(S(420), frame.Iw), "Search: building, photo name or date");
                float listTop = y + boxH + S(UiTheme.GAP_SMALL);
                float rx = frame.Ix, rw = frame.Iw;
                if (sidebar)
                {
                    BuildSiteGroupList(f, input, frame.Ix, listTop, sideW, frame.BodyBottom);
                    rx += sideW + gap;
                    rw -= sideW + gap;
                    action = BuildSiteGroupStrip(f, input, rx, y, rw, boxH);
                }
                if (_galleryOrder.Count == 0)
                {
                    _ui.TextWrapped(f.Body, rx, listTop + S(8), rw, _siteSearchText.Length > 0 ? "No photos match the search." : "No photos here.", UiTheme.TEXT_MUTED, maxLines: 2);
                }
                else if (_siteBrowseList) { open = BuildSiteGalleryList(f, input, rx, listTop, rw, frame.BodyBottom); }
                else { open = BuildSiteGalleryGrid(f, input, rx, listTop, rw, frame.BodyBottom); }
            }

            if (EndPanel(frame, null, _viewer ? "Click a photo to look through it from where it was taken." : "Click a photo to look through it (one not in the model yet opens to be placed).") == FOOTER_LEFT)
            {
                return; // BACK or CLOSE left the panel
            }
            string group = _siteBrowseGroup;
            if (open != null)
            {
                CloseSiteGallery();
                CloseSitePhotosPanel();
                OpenSitePhoto(open, adjust: !open.IsPlaced);
                _siteGroupFilter = group; // ← / → stay in the group picked (or go through them all)
            }
            else if (action == 1)
            {
                CloseSiteGallery();
                CloseSitePhotosPanel();
                ShowSiteGroupOnMap(group);
            }
            else if (action == 2)
            {
                CloseSiteGallery();
                CloseSitePhotosPanel();
                BrowseSitePhotos(group);
            }
        }

        /// <summary>The groups (and "All groups") matching the search, each in its colour with its count; a click picks one.</summary>
        private void BuildSiteGroupList(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            List<SiteGroupInfo> groups = _siteGroupInfos;
            _siteListedGroups.Clear();
            _siteListedGroups.Add(-1);
            for (int i = 0; i < groups.Count; i++)
            {
                if (SiteGroupListed(groups[i])) { _siteListedGroups.Add(i); }
            }

            float rowH = S(40);
            int n = _siteListedGroups.Count;
            float top = BeginScroll(_siteGroupScroll, input, x, y, w, bottom - y, out float cw);
            VisibleRows(_siteGroupScroll, rowH, n, out int first, out int last);
            for (int r = first; r < last; r++)
            {
                int index = _siteListedGroups[r];
                SiteGroupInfo info = index >= 0 ? groups[index] : null;
                float ry = top + r * rowH;
                bool selected = info == null ? _siteBrowseGroup == null : string.Equals(info.Name, _siteBrowseGroup, StringComparison.OrdinalIgnoreCase);
                bool hover = Hover(input, x, ry, cw, rowH - S(4));
                _ui.Rect(x, ry, cw, rowH - S(4), selected ? Rgba.Hex(0x22D3EE, 0.14f) : hover ? UiTheme.HOVER : UiTheme.CONTROL);
                if (selected) { _ui.Rect(x, ry, S(3), rowH - S(4), UiTheme.ACCENT); }

                uint colour = info?.Colour ?? UiTheme.TEXT_MUTED;
                bool focused = info != null && string.Equals(info.Name, _siteFocusGroup, StringComparison.OrdinalIgnoreCase);
                _ui.Rect(x + S(12), ry + S(9), S(12), rowH - S(22), colour);
                if (focused) { _ui.Outline(x + S(9), ry + S(6), S(18), rowH - S(16), MathF.Max(1.5f, S(1.5f)), UiTheme.TEXT); }

                int shown = info == null ? _siteAllMatches : _siteSearchText.Length > 0 ? info.Matches : info.Count;
                Text.Clear().Append(shown);
                float countW = UiBatch.Measure(f.Small, Text.Span) + S(10);
                _ui.TextRight(f.Small, x + cw - S(10), ry + S(12), Text.Span, UiTheme.TEXT_MUTED);
                _ui.TextWrapped(f.Body, x + S(34), ry + S(9), cw - S(44) - countW, info?.Name ?? "All groups", selected ? UiTheme.ACCENT : UiTheme.TEXT, maxLines: 1);

                if (hover && input.LeftPressed)
                {
                    input.ConsumeClicks();
                    Sound.Play(SoundId.UiClick);
                    _siteBrowseGroup = info?.Name;
                    _siteGalleryScroll.Reset();
                }
            }
            EndScroll(_siteGroupScroll, input, top + n * rowH);
        }

        /// <summary>
        /// Over the photos: the picked group's name, colour and counts, and SHOW ON MAP, BROWSE ›, and only it on the model
        /// (or, for all groups, the way back to showing them all).
        /// </summary>
        /// <returns>1: show on map, 2: browse, else 0.</returns>
        private int BuildSiteGroupStrip(FontAtlas f, InputState input, float x, float y, float w, float h)
        {
            float bh = S(30), by = y + (h - bh) * 0.5f, gap = S(UiTheme.GAP_SMALL);
            float right = x + w;
            if (_siteBrowseGroup == null || !_siteGroupByName.TryGetValue(_siteBrowseGroup, out SiteGroupInfo info))
            {
                _ui.Text(f.Bold, x, y + S(7), "All groups", UiTheme.TEXT);
                if (_siteFocusGroup != null)
                {
                    const string ALL = "SHOW ALL ON THE MODEL";
                    float aw = UiBatch.Measure(f.Body, ALL) + S(28);
                    if (MenuButton(f, right - aw, by, aw, ALL, false, false, height: bh, compact: true)) { SetSiteFocusGroup(null); }
                }
                return 0;
            }

            bool focused = string.Equals(_siteFocusGroup, info.Name, StringComparison.OrdinalIgnoreCase);
            string focus = focused ? "SHOW ALL ON THE MODEL" : "ONLY THIS ON THE MODEL";
            const string MAP = "SHOW ON MAP", BROWSE = "BROWSE ›";
            float fw = UiBatch.Measure(f.Body, focus) + S(28), mw = UiBatch.Measure(f.Body, MAP) + S(28), bw = UiBatch.Measure(f.Body, BROWSE) + S(28);
            int result = 0;
            float bx = right - fw;
            if (MenuButton(f, bx, by, fw, focus, focused, false, height: bh, compact: true)) { SetSiteFocusGroup(focused ? null : info.Name); }
            bx -= gap + bw;
            if (MenuButton(f, bx, by, bw, BROWSE, primary: true, danger: false, enabled: info.Placed > 0, height: bh, compact: true)) { result = 2; }
            bx -= gap + mw;
            if (MenuButton(f, bx, by, mw, MAP, false, false, enabled: info.Placed > 0, height: bh, compact: true)) { result = 1; }

            float textW = bx - x - S(30);
            _ui.Rect(x, y + S(6), S(12), h - S(12), info.Colour);
            _ui.TextWrapped(f.Bold, x + S(20), y + S(2), textW, info.Name, info.Colour, maxLines: 1);
            Text.Clear().Append(info.Count).Append(info.Count == 1 ? " photo" : " photos");
            if (info.Placed < info.Count) { Text.Append(" · ").Append(info.Placed).Append(" in the model"); }
            _ui.TextWrapped(f.Small, x + S(20), y + S(20), textW, Text.Span, UiTheme.TEXT_MUTED, maxLines: 1);
            return result;
        }

        /// <summary>The photos as tiles under a heading per group.</summary>
        /// <returns>The photo clicked, or null.</returns>
        private SitePhoto BuildSiteGalleryGrid(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            SitePhoto open = null;
            float top = BeginScroll(_siteGalleryScroll, input, x, y, w, bottom - y, out float cw);
            float gap = S(UiTheme.GAP), tileW = S(208), headingH = S(34);
            int columns = Math.Max(1, (int)((cw + gap) / (tileW + gap)));
            tileW = (cw - gap * (columns - 1)) / columns;
            float thumbH = tileW * 0.65f;
            float tileH = thumbH + S(46);
            float viewTop = _siteGalleryScroll.Y, viewBottom = _siteGalleryScroll.Y + _siteGalleryScroll.H;
            int uploads = GALLERY_UPLOADS;

            // One section per heading (just one, untitled, for a picked group)
            int sections = Math.Max(_galleryHeadings.Count, 1);
            float cy = top;
            for (int h = 0; h < sections; h++)
            {
                int start = _galleryHeadings.Count > 0 ? _galleryHeadings[h].Start : 0;
                int end = h + 1 < _galleryHeadings.Count ? _galleryHeadings[h + 1].Start : _galleryOrder.Count;
                if (_galleryHeadings.Count > 0)
                {
                    (_, string heading, uint colour) = _galleryHeadings[h];
                    if (cy + headingH > viewTop && cy < viewBottom)
                    {
                        _ui.Rect(x, cy + S(10), S(12), S(18), colour);
                        _ui.Text(f.Bold, x + S(20), cy + S(8), heading, colour);
                        Text.Clear().Append(end - start).Append(end - start == 1 ? " photo" : " photos");
                        _ui.TextRight(f.Small, x + cw, cy + S(11), Text.Span, UiTheme.TEXT_FAINT);
                    }
                    cy += headingH;
                }
                int rows = (end - start + columns - 1) / columns;
                for (int r = 0; r < rows; r++)
                {
                    float ty = cy + r * (tileH + gap);
                    if (ty + tileH < viewTop || ty > viewBottom) { continue; } // off screen
                    for (int c = 0; c < columns; c++)
                    {
                        int slot = start + r * columns + c;
                        if (slot >= end) { break; }
                        SitePhoto photo = SitePhotos.Photos[_galleryOrder[slot]];
                        if (GalleryTile(f, input, photo, x + c * (tileW + gap), ty, tileW, thumbH, tileH, ref uploads)) { open = photo; }
                    }
                }
                cy += rows * (tileH + gap) + S(UiTheme.GAP_SMALL);
            }
            EndScroll(_siteGalleryScroll, input, cy);
            return open;
        }

        /// <summary>The photos as rows: thumbnail, name, group (in its colour), time and how it is placed.</summary>
        /// <returns>The photo clicked, or null.</returns>
        private SitePhoto BuildSiteGalleryList(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            SitePhoto open = null;
            float rowH = S(64);
            int n = _galleryOrder.Count;
            float top = BeginScroll(_siteGalleryScroll, input, x, y, w, bottom - y, out float cw);
            VisibleRows(_siteGalleryScroll, rowH, n, out int first, out int last);
            int uploads = GALLERY_UPLOADS;
            for (int i = first; i < last; i++)
            {
                SitePhoto photo = SitePhotos.Photos[_galleryOrder[i]];
                float ry = top + i * rowH;
                bool usable = photo.IsPlaced || !_viewer;
                bool hover = usable && Hover(input, x, ry, cw, rowH - S(4));
                _ui.Rect(x, ry, cw, rowH - S(4), hover ? UiTheme.HOVER : (i & 1) == 0 ? UiTheme.ROW_STRIPE : UiTheme.CONTROL);
                uint colour = SitePhotoColour(photo);
                _ui.Rect(x, ry, S(4), rowH - S(4), colour);

                float thumbH = rowH - S(14), thumbW = MathF.Min(S(96), thumbH * Math.Clamp(photo.Aspect, 0.5f, 2f));
                uint thumb = SiteThumbTexture(photo, ref uploads);
                if (thumb != 0) { _ui.Image(thumb, x + S(12), ry + S(5), thumbW, thumbH, _window.Width, _window.Height, usable ? 0xFFFFFFFFu : Rgba.Hex(0xFFFFFF, 0.4f)); }

                float tx = x + S(120), tw = cw - S(130);
                _ui.TextWrapped(f.Bold, tx, ry + S(8), tw, photo.Name, usable ? UiTheme.TEXT : UiTheme.TEXT_FAINT, maxLines: 1);
                float gx = tx;
                if (!string.IsNullOrEmpty(photo.Group))
                {
                    gx += _ui.Text(f.Small, tx, ry + S(32), photo.Group, colour) + S(4);
                }
                TextBuffer text = Text.Clear();
                if (!string.IsNullOrEmpty(photo.Group)) { text.Append("· "); }
                if (!string.IsNullOrEmpty(photo.Taken)) { text.Append(photo.Taken).Append(" · "); }
                text.Append(photo.Placement == PhotoPlacements.PINNED ? "pinned" : photo.Placement == PhotoPlacements.GPS ? "placed from GPS" : "not in the model yet");
                _ui.TextWrapped(f.Small, gx, ry + S(32), tw - (gx - tx), text.Span, photo.IsPlaced ? UiTheme.TEXT_MUTED : UiTheme.MEASURE_TEXT, maxLines: 1);

                if (hover && input.LeftPressed)
                {
                    input.ConsumeClicks();
                    Sound.Play(SoundId.UiClick);
                    open = photo;
                }
            }
            EndScroll(_siteGalleryScroll, input, top + n * rowH);
            return open;
        }

        /// <summary>One tile: the thumbnail (cropped to the tile), the name, the time and how it is placed; the group's colour along the top.</summary>
        /// <returns>True when clicked (and it can be opened here).</returns>
        private bool GalleryTile(FontAtlas f, InputState input, SitePhoto photo, float x, float y, float w, float thumbH, float h, ref int uploads)
        {
            bool usable = photo.IsPlaced || !_viewer;
            bool hover = usable && Hover(input, x, y, w, h);
            _ui.Rect(x, y, w, h, hover ? UiTheme.HOVER : UiTheme.CONTROL);
            _ui.Outline(x, y, w, h, MathF.Max(1f, UiScale), hover ? UiTheme.ACCENT : UiTheme.CONTROL_BORDER);
            _ui.Rect(x, y, w, S(3), SitePhotoColour(photo));

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
