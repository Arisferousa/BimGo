using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Site photos' groups (site photos round): a site with many buildings, one folder of photos each, made easy to find.
    /// <list type="bullet">
    /// <item><b>Colours</b>: each group has its own colour (in name order, hues a golden angle apart), used by its pins,
    /// thumbnails, map marks and list rows, so neighbouring buildings' photos tell apart at a glance.</item>
    /// <item><b>Search</b>: one search box (the photo browser, SITE PHOTOS) matches groups by name and photos by name,
    /// group or date.</item>
    /// <item><b>Only one group</b> (<see cref="_siteFocusGroup"/>): the model shows only that group's pins and thumbnails,
    /// and the maps show the others faint. Picked in the browser, SITE PHOTOS or the viewer's site photos dropdown; not
    /// saved.</item>
    /// </list>
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private const int SITE_SEARCH_MAX = 48;

        /// <summary>A group: its colour, how many photos (placed), where its placed photos are, its dropdown label.</summary>
        private sealed class SiteGroupInfo
        {
            public string Name;
            public uint Colour;
            public int Count, Placed;
            public Vector2 Min = new(float.MaxValue), Max = new(float.MinValue);
            public string Label;
            public int Matches; // photos matching the search (all of them with no search)
        }

        // The groups in name order (natural: "Block 2" before "Block 10"), rebuilt when the photos change
        private readonly List<SiteGroupInfo> _siteGroupInfos = new();
        private readonly Dictionary<string, SiteGroupInfo> _siteGroupByName = new(StringComparer.OrdinalIgnoreCase);
        private int _siteGroupInfoRevision = -1;
        private int _siteUngrouped;

        // The search (typed into while the browser or SITE PHOTOS is open)
        private readonly char[] _siteSearch = new char[SITE_SEARCH_MAX];
        private int _siteSearchLength;
        private int _siteSearchPause;
        private string _siteSearchText = string.Empty;
        private int _siteSearchVersion;
        private int _siteMatchesFor = -1, _siteMatchesRevision = -1;
        private int _siteAllMatches; // every photo matching the search (grouped or not)

        // Only this group on the model (null: all), and the group picked in the browser / SITE PHOTOS (null: all)
        private string _siteFocusGroup;
        private string _siteBrowseGroup;

        // The dropdowns' slots (owner: the group list)
        private const int DROP_SITE_BROWSE = 1, DROP_SITE_FOCUS = 2;

        #endregion

        #region Groups

        /// <summary>The groups in name order, with colours and counts (rebuilt when the photos change).</summary>
        private List<SiteGroupInfo> SiteGroupInfos()
        {
            if (SitePhotos == null) { return _siteGroupInfos; }
            if (_siteGroupInfoRevision == SitePhotos.Revision) { return _siteGroupInfos; }
            _siteGroupInfoRevision = SitePhotos.Revision;
            _siteGroupInfos.Clear();
            _siteGroupByName.Clear();
            _siteUngrouped = 0;
            foreach (SitePhoto photo in SitePhotos.Photos)
            {
                if (string.IsNullOrEmpty(photo.Group))
                {
                    _siteUngrouped++;
                    continue;
                }
                if (!_siteGroupByName.TryGetValue(photo.Group, out SiteGroupInfo info))
                {
                    info = new SiteGroupInfo { Name = photo.Group };
                    _siteGroupByName[photo.Group] = info;
                    _siteGroupInfos.Add(info);
                }
                info.Count++;
                if (!photo.IsPlaced) { continue; }
                info.Placed++;
                info.Min = Vector2.Min(info.Min, new Vector2(photo.Local.X, photo.Local.Y));
                info.Max = Vector2.Max(info.Max, new Vector2(photo.Local.X, photo.Local.Y));
            }
            _siteGroupInfos.Sort((a, b) => NaturalCompare(a.Name, b.Name));
            for (int i = 0; i < _siteGroupInfos.Count; i++)
            {
                SiteGroupInfo info = _siteGroupInfos[i];
                info.Colour = GroupColour(i);
                info.Label = $"{info.Name}  ({info.Count})";
            }
            if (_siteFocusGroup != null && !_siteGroupByName.ContainsKey(_siteFocusGroup)) { _siteFocusGroup = null; }
            if (_siteBrowseGroup != null && !_siteGroupByName.ContainsKey(_siteBrowseGroup)) { _siteBrowseGroup = null; }
            _siteMatchesRevision = -1;
            return _siteGroupInfos;
        }

        /// <summary>The colour of the i-th group: hues a golden angle apart, three steps of strength so neighbours differ.</summary>
        private static uint GroupColour(int index)
        {
            float hue = (0.53f + index * 0.381966f) % 1f;
            float saturation = (index % 3) switch { 0 => 0.62f, 1 => 0.45f, _ => 0.78f };
            float value = (index % 2) == 0 ? 0.98f : 0.86f;
            HsvToRgb(hue, saturation, value, out float r, out float g, out float b);
            return Rgba.FromFloat(r, g, b, 1f);
        }

        private static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
        {
            float i = MathF.Floor(h * 6f), f = h * 6f - i;
            float p = v * (1f - s), q = v * (1f - f * s), t = v * (1f - (1f - f) * s);
            switch ((int)i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
        }

        /// <summary>Compares names with their numbers as numbers ("Block 2" before "Block 10"), ignoring case.</summary>
        private static int NaturalCompare(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) { i++; }
                    while (j < b.Length && char.IsDigit(b[j])) { j++; }
                    ReadOnlySpan<char> na = a.AsSpan(si, i - si).TrimStart('0'), nb = b.AsSpan(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) { return na.Length.CompareTo(nb.Length); }
                    int digits = na.CompareTo(nb, StringComparison.Ordinal);
                    if (digits != 0) { return digits; }
                    continue;
                }
                int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) { return c; }
                i++;
                j++;
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }

        /// <summary>A photo's colour: its group's (no group: the site photos' own).</summary>
        private uint SitePhotoColour(SitePhoto photo)
        {
            if (string.IsNullOrEmpty(photo.Group)) { return UiTheme.BOOKMARK; }
            SiteGroupInfos();
            return _siteGroupByName.TryGetValue(photo.Group, out SiteGroupInfo info) ? info.Colour : UiTheme.BOOKMARK;
        }

        /// <summary>A group's colour by name (no group: the site photos' own).</summary>
        private uint SiteGroupColour(string group)
        {
            if (string.IsNullOrEmpty(group)) { return UiTheme.BOOKMARK; }
            SiteGroupInfos();
            return _siteGroupByName.TryGetValue(group, out SiteGroupInfo info) ? info.Colour : UiTheme.BOOKMARK;
        }

        /// <summary>Whether the model shows this photo's pin and thumbnail (all, or only the focused group's).</summary>
        private bool SitePhotoInFocus(SitePhoto photo) =>
            _siteFocusGroup == null || string.Equals(photo.Group, _siteFocusGroup, StringComparison.OrdinalIgnoreCase);

        /// <summary>Only this group on the model (null: all again), said in a toast.</summary>
        private void SetSiteFocusGroup(string group)
        {
            _siteFocusGroup = group;
            Sound.Play(SoundId.UiClick);
            Toast(group == null ? "All site photo groups shown" : $"Only “{group}” shown on the model (the others are faint on the maps)", 3f);
        }

        /// <summary>The site map fitted on a group's placed photos.</summary>
        private void ShowSiteGroupOnMap(string group)
        {
            SiteGroupInfos();
            if (group == null || !_siteGroupByName.TryGetValue(group, out SiteGroupInfo info) || info.Placed == 0)
            {
                Toast("None of this group's photos stand in the model yet", 3f);
                return;
            }
            Vector2 middle = (info.Min + info.Max) * 0.5f;
            Vector2 size = info.Max - info.Min;
            float z = Scene.Levels.Length > 0 ? _player.Feet.Z + SITE_PHOTO_EYE : Camera.Position.Z;
            foreach (SitePhoto photo in SitePhotos.Photos)
            {
                if (photo.IsPlaced && string.Equals(photo.Group, group, StringComparison.OrdinalIgnoreCase))
                {
                    z = photo.Local.Z; // the map on the group's ground
                    break;
                }
            }
            if (_paused) { SetPaused(false); }
            OpenSiteMap(new Vector3(middle, z), MathF.Max(MathF.Max(size.X, size.Y) * 1.5f, 0f) + 60f);
        }

        #endregion

        #region Search

        /// <summary>The search box: typed characters go in (Backspace deletes, Ctrl+Backspace clears), × clears.</summary>
        /// <returns>The box's height.</returns>
        private float SiteSearchBox(FontAtlas f, InputState input, float x, float y, float w, string placeholder)
        {
            if (_siteSearchPause > 0) { _siteSearchPause--; }
            else { TypeIntoSiteSearch(input); }

            float h = S(34);
            _ui.Panel(x, y, w, h, UiTheme.CONTROL, _siteSearchLength > 0 ? UiTheme.ACCENT : UiTheme.CONTROL_BORDER);
            float textY = y + h * 0.5f - f.Body.LineHeight * 0.5f;
            if (_siteSearchLength == 0) { _ui.TextWrapped(f.Body, x + S(10), textY, w - S(20), placeholder, UiTheme.TEXT_FAINT, maxLines: 1); }
            else
            {
                _ui.PushClip(x + S(4), y, w - S(40), h);
                float used = _ui.Text(f.Body, x + S(10), textY, _siteSearch.AsSpan(0, _siteSearchLength), UiTheme.TEXT);
                if (((int)(_clock * 2f) & 1) == 0) { _ui.Rect(x + S(11) + used, y + S(8), S(2), h - S(16), UiTheme.TEXT_SOFT); }
                _ui.PopClip();
                bool hover = Hover(input, x + w - S(34), y, S(34), h);
                _ui.TextCentred(f.Bold, x + w - S(17), textY - S(1), "×", hover ? UiTheme.ACCENT : UiTheme.TEXT_MUTED);
                if (hover && input.LeftPressed)
                {
                    input.ConsumeClicks();
                    SetSiteSearch(0);
                }
            }
            return h;
        }

        private void TypeIntoSiteSearch(InputState input)
        {
            int length = _siteSearchLength;
            foreach (char c in input.Chars)
            {
                if (c == '\b')
                {
                    if (input.IsDown(KeyCodes.CTRL)) { length = 0; }
                    else if (length > 0) { length--; }
                }
                else if (c >= ' ' && c != 127 && length < SITE_SEARCH_MAX)
                {
                    _siteSearch[length++] = c;
                }
            }
            if (length != _siteSearchLength || input.Chars.Length > 0) { SetSiteSearch(length); }
        }

        /// <summary>The search's length changed (or its text): the trimmed text made once, the matches redone.</summary>
        private void SetSiteSearch(int length)
        {
            _siteSearchLength = length;
            string text = new string(_siteSearch, 0, length).Trim();
            if (text == _siteSearchText) { return; }
            _siteSearchText = text;
            _siteSearchVersion++;
            _siteGalleryScroll.Reset();
            _siteGroupScroll.Reset();
            _sitePhotoScroll.Reset();
        }

        /// <summary>Whether a photo matches the search (its name, group or date; everything when it's empty).</summary>
        private bool SiteSearchMatches(SitePhoto photo)
        {
            if (_siteSearchText.Length == 0) { return true; }
            return Contains(photo.Name) || Contains(photo.Group) || Contains(photo.Taken);

            bool Contains(string value) => value != null && value.Contains(_siteSearchText, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Counts each group's photos matching the search (when the search or the photos change).</summary>
        private void RefreshSiteMatches()
        {
            List<SiteGroupInfo> groups = SiteGroupInfos();
            if (_siteMatchesFor == _siteSearchVersion && _siteMatchesRevision == SitePhotos.Revision) { return; }
            _siteMatchesFor = _siteSearchVersion;
            _siteMatchesRevision = SitePhotos.Revision;
            foreach (SiteGroupInfo info in groups)
            {
                info.Matches = 0;
            }
            _siteAllMatches = 0;
            foreach (SitePhoto photo in SitePhotos.Photos)
            {
                if (!SiteSearchMatches(photo)) { continue; }
                _siteAllMatches++;
                if (!string.IsNullOrEmpty(photo.Group) && _siteGroupByName.TryGetValue(photo.Group, out SiteGroupInfo info)) { info.Matches++; }
            }
        }

        /// <summary>A group shown in a list: it matches the search by name or has matching photos.</summary>
        private bool SiteGroupListed(SiteGroupInfo info) =>
            _siteSearchText.Length == 0 || info.Matches > 0 || info.Name.Contains(_siteSearchText, StringComparison.OrdinalIgnoreCase);

        #endregion

        #region Dropdowns

        /// <summary>
        /// A group dropdown: "All groups" then each group with its count (in name order). <paramref name="slot"/> picks
        /// what it sets: the browser / SITE PHOTOS group, or the group shown on the model.
        /// </summary>
        private void SiteGroupDropdown(FontAtlas f, InputState input, float x, float y, float w, float h, int slot)
        {
            List<SiteGroupInfo> groups = SiteGroupInfos();
            string current = slot == DROP_SITE_FOCUS ? _siteFocusGroup : _siteBrowseGroup;
            string label = current ?? "All groups";
            if (current != null && _siteGroupByName.TryGetValue(current, out SiteGroupInfo picked))
            {
                _ui.Rect(x - S(6), y + S(4), S(3), h - S(8), picked.Colour); // the group's colour beside it
            }
            if (!DropdownButton(f, input, x, y, w, h, label, IsDropdownOpen(_siteGroupInfos, slot))) { return; }
            if (IsDropdownOpen(_siteGroupInfos, slot))
            {
                CloseOverlay();
                return;
            }
            var labels = new List<string>(groups.Count + 1) { "All groups" };
            int selected = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                labels.Add(groups[i].Label);
                if (current != null && string.Equals(groups[i].Name, current, StringComparison.OrdinalIgnoreCase)) { selected = i + 1; }
            }
            OpenDropdown(_siteGroupInfos, slot, x, y, w, h, labels, selected, k =>
            {
                string group = k <= 0 || k > _siteGroupInfos.Count ? null : _siteGroupInfos[k - 1].Name;
                if (slot == DROP_SITE_FOCUS) { SetSiteFocusGroup(group); }
                else
                {
                    _siteBrowseGroup = group;
                    _siteGalleryScroll.Reset();
                    _sitePhotoScroll.Reset();
                }
            });
        }

        #endregion
    }
}
