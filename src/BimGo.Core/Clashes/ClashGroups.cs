using System.Text.Json.Serialization;

// The class belongs to the Clashes namespace
namespace BimGo.Clashes
{
    /// <summary>
    /// How a group was made (Round C-C): null by hand, or the automatic method that made it.
    /// </summary>
    public static class ClashGroupAuto
    {
        /// <summary>AUTO GROUP → by element: one element hitting several clashes.</summary>
        public const string ELEMENT = "element";
    }

    /// <summary>
    /// A clash group (Round C, build C-C1; Navisworks style): clashes of one test reviewed together, with its own
    /// status, assignee and notes (setting the group's status sets every member's), and one comment / BCF topic for
    /// them all. A clash is in at most one group. Members a later run no longer finds stay in the group, resolved; a
    /// group is removed only when it has no members left (one member is fine). New clashes stay ungrouped.
    /// <para><see cref="ClashIds"/> is the single source of truth for membership (<see cref="ClashGroups.Index"/> builds
    /// the clash → group lookup).</para>
    /// </summary>
    public sealed class ClashGroup
    {
        /// <summary>Stable id (a group comment's <c>CommentRecord.ClashId</c> holds it).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>The test's id (a group belongs to one test).</summary>
        public string TestId { get; set; }

        /// <summary>
        /// The name people gave it, or null: then it reads from <see cref="KeyElement"/> and the member count
        /// ("Ducts · Rect 600×300 (123456) × 7 clashes"; <see cref="ClashGroups.DisplayName"/>).
        /// </summary>
        public string Name { get; set; }

        /// <summary>The group's own status (<see cref="ClashStatus"/>); members that differ show it as mixed.</summary>
        public string Status { get; set; } = ClashStatus.NEW;

        /// <summary>Who it is assigned to (free text), or null.</summary>
        public string AssignedTo { get; set; }

        /// <summary>Notes (free text), or null.</summary>
        public string Notes { get; set; }

        /// <summary>The group's comment (one for all its members), or null.</summary>
        public string CommentId { get; set; }

        /// <summary>The member clashes' ids, in the order they joined.</summary>
        public List<string> ClashIds { get; set; } = new();

        /// <summary>How it was made: null by hand, else <see cref="ClashGroupAuto"/>.</summary>
        public string Auto { get; set; }

        /// <summary>The element every member shares (by-element groups and GROUP ALL ON), or null.</summary>
        public ClashElementRef KeyElement { get; set; }

        /// <summary>When it was made (UTC).</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>When a person last changed its name, status, assignee or notes (UTC), or null; imports keep the newer.</summary>
        public DateTime? Updated { get; set; }

        /// <summary>Who made that change.</summary>
        public string UpdatedBy { get; set; }

        /// <summary>Number of members.</summary>
        [JsonIgnore]
        public int Count => ClashIds?.Count ?? 0;
    }

    /// <summary>
    /// A group's members by status, worked out for display (not stored).
    /// </summary>
    public readonly record struct ClashGroupSummary(int Members, int Found, bool Mixed, int[] ByStatus);

    /// <summary>
    /// Clash group operations (Round C-C1). Every change keeps one group per clash: a clash joining a group leaves the
    /// one it was in, and a group left with no members is removed. Callers stamp, save and refresh.
    /// </summary>
    public static class ClashGroups
    {
        #region Lookups

        /// <summary>Clash id → its group (built after any change; thousands of clashes, no list searches).</summary>
        public static Dictionary<string, ClashGroup> Index(ClashDocument document)
        {
            var index = new Dictionary<string, ClashGroup>(StringComparer.Ordinal);
            if (document?.Groups == null) { return index; }
            foreach (ClashGroup group in document.Groups)
            {
                foreach (string id in group.ClashIds) { index.TryAdd(id, group); }
            }
            return index;
        }

        /// <summary>Clash id → clash.</summary>
        public static Dictionary<string, ClashRecord> ClashesById(ClashDocument document)
        {
            var byId = new Dictionary<string, ClashRecord>(document?.Clashes?.Count ?? 0, StringComparer.Ordinal);
            if (document?.Clashes == null) { return byId; }
            foreach (ClashRecord clash in document.Clashes) { byId.TryAdd(clash.Id, clash); }
            return byId;
        }

        /// <summary>The group with an id, or null.</summary>
        public static ClashGroup Find(ClashDocument document, string id)
        {
            if (document?.Groups == null || string.IsNullOrEmpty(id)) { return null; }
            foreach (ClashGroup group in document.Groups)
            {
                if (group.Id == id) { return group; }
            }
            return null;
        }

        /// <summary>A test's groups (none: empty).</summary>
        public static IEnumerable<ClashGroup> OfTest(ClashDocument document, string testId) =>
            document?.Groups == null ? Enumerable.Empty<ClashGroup>() : document.Groups.Where(g => g.TestId == testId);

        /// <summary>A group's members that are still in the document, in member order.</summary>
        public static List<ClashRecord> Members(ClashGroup group, IReadOnlyDictionary<string, ClashRecord> byId)
        {
            var members = new List<ClashRecord>(group?.Count ?? 0);
            if (group == null) { return members; }
            foreach (string id in group.ClashIds)
            {
                if (byId.TryGetValue(id, out ClashRecord clash)) { members.Add(clash); }
            }
            return members;
        }

        /// <summary>Members per status and whether any differs from the group's own status.</summary>
        public static ClashGroupSummary Summarise(ClashGroup group, IReadOnlyDictionary<string, ClashRecord> byId)
        {
            int[] counts = new int[ClashStatus.All.Length];
            int found = 0;
            bool mixed = false;
            string status = ClashStatus.Normalise(group.Status);
            foreach (string id in group.ClashIds)
            {
                if (!byId.TryGetValue(id, out ClashRecord clash)) { continue; }
                found++;
                counts[ClashStatus.IndexOf(clash.Status)]++;
                if (clash.Status != status) { mixed = true; }
            }
            return new ClashGroupSummary(group.Count, found, mixed, counts);
        }

        #endregion

        #region Names

        /// <summary>
        /// The name a group shows: its own, else "&lt;type&gt; (&lt;id&gt;) × N clashes" from its key element, else "Group".
        /// </summary>
        public static string DisplayName(ClashGroup group)
        {
            if (group == null) { return string.Empty; }
            if (!string.IsNullOrWhiteSpace(group.Name)) { return group.Name; }
            if (group.KeyElement == null) { return "Group"; }
            int count = group.Count;
            return $"{ElementName(group.KeyElement)} × {count} clash{(count == 1 ? string.Empty : "es")}";
        }

        /// <summary>"Rectangular Duct: 600x300 (123456)": the family / type (else the category) and the element id.</summary>
        public static string ElementName(ClashElementRef element)
        {
            if (element == null) { return "Element"; }
            string name = !string.IsNullOrWhiteSpace(element.FamilyType) ? element.FamilyType.Trim()
                : !string.IsNullOrWhiteSpace(element.Category) ? element.Category.Trim() : "Element";
            string id = element.CloneKey > 0 ? "clone " + element.CloneKey
                : element.ElementId > 0 ? element.ElementId.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return id == null ? name : $"{name} ({id})";
        }

        /// <summary>"Group N" with the next free number in the test.</summary>
        public static string NextName(ClashDocument document, string testId)
        {
            int next = 1;
            foreach (ClashGroup group in OfTest(document, testId))
            {
                string name = group.Name;
                if (name != null && name.StartsWith("Group ", StringComparison.Ordinal)
                    && int.TryParse(name.AsSpan(6), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n))
                {
                    next = Math.Max(next, n + 1);
                }
            }
            return "Group " + next.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The element every clash shares (A or B side; the first such element in the first clash), or null.
        /// </summary>
        public static ClashElementRef CommonElement(IReadOnlyList<ClashRecord> clashes)
        {
            if (clashes == null || clashes.Count == 0) { return null; }
            foreach (ClashElementRef candidate in new[] { clashes[0].A, clashes[0].B })
            {
                string key = candidate.Key;
                bool all = true;
                for (int i = 1; i < clashes.Count && all; i++)
                {
                    all = clashes[i].A.Key == key || clashes[i].B.Key == key;
                }
                if (all) { return candidate; }
            }
            return null;
        }

        #endregion

        #region Changes

        /// <summary>
        /// Makes a group of clashes (only those of <paramref name="testId"/>; each leaves the group it was in). Its
        /// status starts as the least advanced of the members' (new → resolved), its assignee as theirs when they all
        /// share one. A null <paramref name="name"/> reads from <paramref name="keyElement"/> (or "Group N" without one).
        /// </summary>
        /// <param name="moved">How many came out of other groups.</param>
        /// <returns>The group, or null when no clash was given.</returns>
        public static ClashGroup Create(ClashDocument document, string testId, IEnumerable<ClashRecord> clashes, string name, ClashElementRef keyElement,
            string auto, string user, DateTime utc, out int moved)
        {
            moved = 0;
            List<ClashRecord> members = Distinct(clashes, testId);
            if (document == null || members.Count == 0) { return null; }

            moved = Detach(document, members);
            int least = ClashStatus.All.Length - 1;
            string assignee = members[0].AssignedTo;
            foreach (ClashRecord clash in members)
            {
                least = Math.Min(least, ClashStatus.IndexOf(clash.Status));
                if (!string.Equals(clash.AssignedTo, assignee, StringComparison.Ordinal)) { assignee = null; }
            }

            var group = new ClashGroup
            {
                TestId = testId,
                Name = string.IsNullOrWhiteSpace(name) ? (keyElement == null ? NextName(document, testId) : null) : name.Trim(),
                Status = ClashStatus.All[least],
                AssignedTo = string.IsNullOrWhiteSpace(assignee) ? null : assignee,
                Auto = auto,
                KeyElement = keyElement,
                CreatedUtc = utc,
                ClashIds = members.Select(c => c.Id).ToList()
            };
            if (auto == null) { Stamp(group, user, utc); }
            (document.Groups ??= new List<ClashGroup>()).Add(group);
            return group;
        }

        /// <summary>Adds clashes (of the group's test) to a group; each leaves the group it was in.</summary>
        /// <param name="moved">How many came out of other groups.</param>
        /// <returns>How many joined (already members don't count).</returns>
        public static int AddTo(ClashDocument document, ClashGroup group, IEnumerable<ClashRecord> clashes, string user, DateTime utc, out int moved)
        {
            moved = 0;
            if (document == null || group == null) { return 0; }
            var have = new HashSet<string>(group.ClashIds, StringComparer.Ordinal);
            List<ClashRecord> joining = Distinct(clashes, group.TestId).Where(c => !have.Contains(c.Id)).ToList();
            if (joining.Count == 0) { return 0; }
            moved = Detach(document, joining, keep: group);
            foreach (ClashRecord clash in joining) { group.ClashIds.Add(clash.Id); }
            Stamp(group, user, utc);
            return joining.Count;
        }

        /// <summary>Takes clashes out of whatever group they are in (groups left empty are removed).</summary>
        /// <returns>How many left a group.</returns>
        public static int Remove(ClashDocument document, IEnumerable<ClashRecord> clashes) =>
            document == null ? 0 : Detach(document, Distinct(clashes, null));

        /// <summary>UNGROUP: the group goes; its clashes stay, ungrouped.</summary>
        public static bool Ungroup(ClashDocument document, ClashGroup group)
        {
            if (document?.Groups == null || group == null || !document.Groups.Remove(group)) { return false; }
            if (document.Groups.Count == 0) { document.Groups = null; }
            return true;
        }

        /// <summary>
        /// MERGE GROUPS: the first group takes every other group's members and keeps its own name, status, assignee and
        /// notes; the others' notes are added under their names; the others go. An automatic name is frozen as it
        /// reads after the merge (the members no longer all share its element).
        /// </summary>
        /// <returns>The merged group, or null when fewer than two groups of one test were given.</returns>
        public static ClashGroup Merge(ClashDocument document, IReadOnlyList<ClashGroup> groups, string user, DateTime utc)
        {
            if (document?.Groups == null || groups == null || groups.Count < 2) { return null; }
            ClashGroup first = groups[0];
            var have = new HashSet<string>(first.ClashIds, StringComparer.Ordinal);
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(first.Notes)) { notes.Add(first.Notes.Trim()); }
            bool otherElement = false;
            for (int i = 1; i < groups.Count; i++)
            {
                ClashGroup other = groups[i];
                if (other == null || other == first || other.TestId != first.TestId) { continue; }
                foreach (string id in other.ClashIds)
                {
                    if (have.Add(id)) { first.ClashIds.Add(id); }
                }
                if (!string.IsNullOrWhiteSpace(other.Notes)) { notes.Add($"{DisplayName(other)}: {other.Notes.Trim()}"); }
                otherElement |= other.KeyElement == null || first.KeyElement == null || other.KeyElement.Key != first.KeyElement.Key;
                document.Groups.Remove(other);
            }
            if (first.Name == null && otherElement)
            {
                first.Name = DisplayName(first);
                first.KeyElement = null;
            }
            first.Auto = null;
            first.Notes = notes.Count == 0 ? null : string.Join("\n", notes);
            Stamp(first, user, utc);
            return first;
        }

        /// <summary>RENAME…: an empty name goes back to the automatic one (or "Group N" without a key element).</summary>
        public static void Rename(ClashDocument document, ClashGroup group, string name, string user, DateTime utc)
        {
            if (group == null) { return; }
            group.Name = string.IsNullOrWhiteSpace(name) ? (group.KeyElement == null ? NextName(document, group.TestId) : null) : name.Trim();
            Stamp(group, user, utc);
        }

        /// <summary>The group's status, and every member's (stamped).</summary>
        /// <returns>The members whose status changed (their comments follow).</returns>
        public static List<ClashRecord> SetStatus(ClashGroup group, string status, IReadOnlyDictionary<string, ClashRecord> byId, string user, DateTime utc)
        {
            var changed = new List<ClashRecord>();
            if (group == null) { return changed; }
            status = ClashStatus.Normalise(status);
            group.Status = status;
            Stamp(group, user, utc);
            foreach (ClashRecord clash in Members(group, byId))
            {
                if (clash.Status == status) { continue; }
                ClashMerge.SetStatus(clash, status, user, utc);
                changed.Add(clash);
            }
            return changed;
        }

        /// <summary>ASSIGN… on a group: the group's and every member's assignee (empty clears).</summary>
        /// <returns>The members (their comments follow).</returns>
        public static List<ClashRecord> SetAssignee(ClashGroup group, string assignedTo, IReadOnlyDictionary<string, ClashRecord> byId, string user, DateTime utc)
        {
            if (group == null) { return new List<ClashRecord>(); }
            assignedTo ??= string.Empty;
            group.AssignedTo = assignedTo.Trim().Length == 0 ? null : assignedTo.Trim();
            Stamp(group, user, utc);
            List<ClashRecord> members = Members(group, byId);
            foreach (ClashRecord clash in members) { ClashMerge.SetDetails(clash, group.AssignedTo ?? string.Empty, null, user, utc); }
            return members;
        }

        /// <summary>NOTES… on a group: the group's own notes (members keep theirs; empty clears).</summary>
        public static void SetNotes(ClashGroup group, string notes, string user, DateTime utc)
        {
            if (group == null) { return; }
            group.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            Stamp(group, user, utc);
        }

        private static void Stamp(ClashGroup group, string user, DateTime utc)
        {
            group.Updated = utc;
            group.UpdatedBy = user;
        }

        /// <summary>The clashes once each, of one test (null: any).</summary>
        private static List<ClashRecord> Distinct(IEnumerable<ClashRecord> clashes, string testId)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<ClashRecord>();
            if (clashes == null) { return list; }
            foreach (ClashRecord clash in clashes)
            {
                if (clash == null || (testId != null && clash.TestId != testId) || !seen.Add(clash.Id)) { continue; }
                list.Add(clash);
            }
            return list;
        }

        /// <summary>Takes clashes out of their groups (except <paramref name="keep"/>), removing groups left empty.</summary>
        /// <returns>How many left a group.</returns>
        private static int Detach(ClashDocument document, List<ClashRecord> clashes, ClashGroup keep = null)
        {
            if (document.Groups == null || clashes.Count == 0) { return 0; }
            var ids = new HashSet<string>(clashes.Select(c => c.Id), StringComparer.Ordinal);
            int removed = 0;
            foreach (ClashGroup group in document.Groups)
            {
                if (group == keep) { continue; }
                removed += group.ClashIds.RemoveAll(ids.Contains);
            }
            document.Groups.RemoveAll(g => g != keep && g.ClashIds.Count == 0);
            if (document.Groups.Count == 0) { document.Groups = null; }
            return removed;
        }

        #endregion

        #region Document upkeep

        /// <summary>
        /// Repairs groups (called by <see cref="ClashDocument.Clean"/>): drops null groups, members that aren't clashes
        /// of the group's test, a clash's later memberships (the first group keeps it) and groups left with none; fixes
        /// missing / duplicate ids and unknown statuses. <see cref="ClashDocument.Groups"/> is null when none are left.
        /// </summary>
        public static void Clean(ClashDocument document)
        {
            if (document.Groups == null) { return; }
            var testOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ClashRecord clash in document.Clashes) { testOf.TryAdd(clash.Id, clash.TestId); }

            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var kept = new List<ClashGroup>(document.Groups.Count);
            foreach (ClashGroup group in document.Groups)
            {
                if (group == null || string.IsNullOrEmpty(group.TestId)) { continue; }
                if (string.IsNullOrWhiteSpace(group.Id) || !groupIds.Add(group.Id)) { group.Id = Guid.NewGuid().ToString("N"); groupIds.Add(group.Id); }
                group.Status = ClashStatus.Normalise(group.Status);
                if (string.IsNullOrWhiteSpace(group.Name)) { group.Name = null; }
                if (group.KeyElement != null)
                {
                    group.KeyElement.Link ??= string.Empty;
                    group.KeyElement.UniqueId ??= string.Empty;
                }
                var members = new List<string>(group.ClashIds?.Count ?? 0);
                foreach (string id in group.ClashIds ?? new List<string>())
                {
                    if (id == null || !testOf.TryGetValue(id, out string test) || test != group.TestId || !taken.Add(id)) { continue; }
                    members.Add(id);
                }
                group.ClashIds = members;
                if (members.Count > 0) { kept.Add(group); }
            }
            document.Groups = kept.Count == 0 ? null : kept;
        }

        /// <summary>
        /// After a run of a test (<see cref="ClashMerge.Apply"/>): a group whose members are all resolved becomes resolved;
        /// a resolved group with a member found again becomes active. Not stamped (the run did it, not a person).
        /// </summary>
        /// <returns>The groups whose status changed.</returns>
        public static List<ClashGroup> FollowRun(ClashDocument document, string testId)
        {
            var changed = new List<ClashGroup>();
            if (document?.Groups == null) { return changed; }
            Dictionary<string, ClashRecord> byId = null;
            foreach (ClashGroup group in document.Groups)
            {
                if (group.TestId != testId) { continue; }
                byId ??= ClashesById(document);
                bool any = false, allResolved = true;
                foreach (string id in group.ClashIds)
                {
                    if (!byId.TryGetValue(id, out ClashRecord clash)) { continue; }
                    any = true;
                    if (clash.Status != ClashStatus.RESOLVED) { allResolved = false; }
                }
                if (!any) { continue; }
                if (allResolved && group.Status != ClashStatus.RESOLVED)
                {
                    group.Status = ClashStatus.RESOLVED;
                    changed.Add(group);
                }
                else if (!allResolved && group.Status == ClashStatus.RESOLVED)
                {
                    group.Status = ClashStatus.ACTIVE;
                    changed.Add(group);
                }
            }
            return changed;
        }

        /// <summary>Removes a test's groups (reset or delete of the test).</summary>
        public static int RemoveTest(ClashDocument document, string testId)
        {
            if (document?.Groups == null) { return 0; }
            int removed = document.Groups.RemoveAll(g => g.TestId == testId);
            if (document.Groups.Count == 0) { document.Groups = null; }
            return removed;
        }

        /// <summary>
        /// Merges a report's groups (<see cref="ClashFiles.Merge"/>, after its clashes): a group we have (same id) takes
        /// the report's name, status, assignee and notes when its change is newer; members are matched by clash
        /// (test + element pair) and join only when not already in one of our groups; groups we don't have are added
        /// with their members that aren't grouped here.
        /// </summary>
        /// <param name="theirClashToOurs">Their clash id → our clash id (matched or added).</param>
        /// <returns>Groups added.</returns>
        internal static int MergeFrom(ClashDocument from, ClashDocument into, IReadOnlyDictionary<string, string> theirClashToOurs)
        {
            if (from?.Groups == null || from.Groups.Count == 0) { return 0; }
            Dictionary<string, ClashGroup> index = Index(into);
            int added = 0;
            foreach (ClashGroup theirs in from.Groups)
            {
                var ids = new List<string>();
                foreach (string id in theirs.ClashIds)
                {
                    if (theirClashToOurs.TryGetValue(id, out string ours) && !index.ContainsKey(ours)) { ids.Add(ours); }
                }

                ClashGroup mine = Find(into, theirs.Id);
                if (mine != null)
                {
                    bool newer = theirs.Updated.HasValue && (!mine.Updated.HasValue || theirs.Updated.Value > mine.Updated.Value);
                    if (newer)
                    {
                        mine.Name = theirs.Name;
                        mine.Status = theirs.Status;
                        mine.AssignedTo = theirs.AssignedTo;
                        mine.Notes = theirs.Notes;
                        mine.Updated = theirs.Updated;
                        mine.UpdatedBy = theirs.UpdatedBy;
                    }
                    mine.CommentId ??= theirs.CommentId;
                    foreach (string id in ids)
                    {
                        mine.ClashIds.Add(id);
                        index[id] = mine;
                    }
                    continue;
                }
                if (ids.Count == 0) { continue; }
                theirs.ClashIds = ids;
                (into.Groups ??= new List<ClashGroup>()).Add(theirs);
                foreach (string id in ids) { index[id] = theirs; }
                added++;
            }
            return added;
        }

        #endregion
    }

    /// <summary>
    /// Automatic and element grouping (Round C-C1).
    /// </summary>
    public static class ClashGrouping
    {
        /// <summary>The fewest clashes an automatic group takes.</summary>
        public const int MIN_SIZE = 2;

        /// <summary>
        /// GROUP ALL ON &lt;element&gt;: every clash of the test that has the element on either side (resolved ones only
        /// with <paramref name="includeResolved"/>), in document order.
        /// </summary>
        public static List<ClashRecord> ForElement(ClashDocument document, string testId, string elementKey, bool includeResolved = false)
        {
            var found = new List<ClashRecord>();
            if (document == null || string.IsNullOrEmpty(elementKey)) { return found; }
            foreach (ClashRecord clash in document.Clashes)
            {
                if (clash.TestId != testId || (!includeResolved && clash.Status == ClashStatus.RESOLVED)) { continue; }
                if (clash.A.Key == elementKey || clash.B.Key == elementKey) { found.Add(clash); }
            }
            return found;
        }

        /// <summary>
        /// AUTO GROUP → by element: among the test's ungrouped, unresolved clashes, the element in the most of them
        /// (A or B side) gets a group of those clashes; then the next, over the clashes left, until no element has
        /// <paramref name="minSize"/>. A clash joins one group only. Ties go to the smaller element key (ordinal), so the
        /// result doesn't depend on clash order. Already grouped clashes are never touched.
        /// </summary>
        /// <returns>The groups made, largest first.</returns>
        public static List<ClashGroup> ByElement(ClashDocument document, string testId, int minSize, string user, DateTime utc)
        {
            var made = new List<ClashGroup>();
            if (document == null) { return made; }
            minSize = Math.Max(1, minSize);
            Dictionary<string, ClashGroup> grouped = ClashGroups.Index(document);

            // Candidates and, per element key, the candidates it is in (and one reference to it for the group)
            var candidates = new List<ClashRecord>();
            var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var refOf = new Dictionary<string, ClashElementRef>(StringComparer.Ordinal);
            foreach (ClashRecord clash in document.Clashes)
            {
                if (clash.TestId != testId || clash.Status == ClashStatus.RESOLVED || grouped.ContainsKey(clash.Id)) { continue; }
                int index = candidates.Count;
                candidates.Add(clash);
                string a = clash.A.Key, b = clash.B.Key;
                AddKey(byKey, refOf, a, clash.A, index);
                if (b != a) { AddKey(byKey, refOf, b, clash.B, index); }
            }

            // The element with most untaken clashes first (count descending, key ordinal ascending)
            var counts = new Dictionary<string, int>(byKey.Count, StringComparer.Ordinal);
            var order = new SortedSet<(int Count, string Key)>(Comparer<(int Count, string Key)>.Create((p, q) =>
            {
                int c = q.Count.CompareTo(p.Count);
                return c != 0 ? c : string.CompareOrdinal(p.Key, q.Key);
            }));
            foreach (KeyValuePair<string, List<int>> pair in byKey)
            {
                counts[pair.Key] = pair.Value.Count;
                order.Add((pair.Value.Count, pair.Key));
            }

            bool[] taken = new bool[candidates.Count];
            while (order.Count > 0)
            {
                (int count, string key) = order.Min;
                if (count < minSize) { break; }
                order.Remove(order.Min);

                var members = new List<ClashRecord>(count);
                foreach (int index in byKey[key])
                {
                    if (taken[index]) { continue; }
                    taken[index] = true;
                    ClashRecord clash = candidates[index];
                    members.Add(clash);

                    // The clash's other element loses one
                    string other = clash.A.Key == key ? clash.B.Key : clash.A.Key;
                    if (other != key && counts.TryGetValue(other, out int otherCount) && order.Remove((otherCount, other)))
                    {
                        counts[other] = otherCount - 1;
                        order.Add((otherCount - 1, other));
                    }
                }
                counts[key] = 0;

                ClashGroup group = ClashGroups.Create(document, testId, members, null, refOf[key], ClashGroupAuto.ELEMENT, user, utc, out _);
                if (group != null) { made.Add(group); }
            }
            return made;
        }

        private static void AddKey(Dictionary<string, List<int>> byKey, Dictionary<string, ClashElementRef> refOf, string key, ClashElementRef element, int index)
        {
            if (!byKey.TryGetValue(key, out List<int> list))
            {
                list = new List<int>();
                byKey[key] = list;
                refOf[key] = element;
            }
            list.Add(index);
        }
    }
}
