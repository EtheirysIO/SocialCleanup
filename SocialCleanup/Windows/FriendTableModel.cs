using System;
using System.Collections.Generic;
using SocialCleanup.Friends;

namespace SocialCleanup.Windows;

/// <summary>Columns of the friend table. Values double as ImGui column user ids.</summary>
public enum FriendColumn : uint
{
    Selected = 0,
    Name = 1,
    HomeWorld = 2,
    DataCenter = 3,
    Status = 4,
    Group = 5,
    Pending = 6,
}

public enum FriendStatusFilter
{
    All,
    Online,
    Offline,

    /// <summary>RequestState 2 (request sent) or 3 (request received).</summary>
    PendingApproval,

    /// <summary>Rows the game sent without a name (shown as "(Unable to retrieve)").</summary>
    UnableToRetrieve,
}

/// <summary>One sort criterion (column + direction), in priority order.</summary>
public readonly record struct FriendSortKey(FriendColumn Column, bool Ascending);

/// <summary>
/// Selection, filtering and sorting state for the friend table. Contains no ImGui calls and touches only
/// managed snapshots, so it is unit-testable and safe to drive from the UI thread.
/// <para>
/// Selection is keyed by ContentId. The filtered + sorted <see cref="View"/> is rebuilt lazily, only when the
/// snapshot, a filter or the sort changes — never because the selection changed (so sorting by the checkbox
/// column reflects the selection at the time the sort/filter/snapshot last changed, and rows don't jump while clicking).
/// </para>
/// </summary>
public sealed class FriendTableModel
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    private readonly HashSet<ulong> selected = new();
    private readonly List<FriendEntry> view = new();
    private readonly List<FriendSortKey> sort = new();
    private readonly List<string> worldOptions = new();
    private readonly List<string> dataCenterOptions = new();
    private readonly List<FriendGroup> groupOptions = new();

    private FriendListSnapshot? snapshot;
    private bool viewDirty = true;
    private int visibleSelectedCount;
    private bool visibleSelectedDirty = true;
    private ulong? anchorContentId;

    private string searchText = string.Empty;
    private string searchNeedle = string.Empty;
    private string? homeWorldFilter;
    private string? dataCenterFilter;
    private FriendStatusFilter statusFilter = FriendStatusFilter.All;
    private FriendGroup? groupFilter;

    /// <summary>Current snapshot the model is built from (null = none).</summary>
    public FriendListSnapshot? Snapshot => snapshot;

    /// <summary>
    /// While true (a removal batch is Running/Halted) every user-initiated selection change is ignored.
    /// Snapshot-driven pruning and character-switch clearing still happen.
    /// </summary>
    public bool SelectionLocked { get; set; }

    /// <summary>Incremented on every change to the selected set (user or snapshot driven).</summary>
    public int SelectionVersion { get; private set; }

    /// <summary>Incremented every time <see cref="View"/> is rebuilt.</summary>
    public int ViewVersion { get; private set; }

    public int SelectedCount => selected.Count;

    public IReadOnlyCollection<ulong> SelectedIds => selected;

    public IReadOnlyList<string> WorldOptions => worldOptions;

    public IReadOnlyList<string> DataCenterOptions => dataCenterOptions;

    public IReadOnlyList<FriendGroup> GroupOptions => groupOptions;

    public IReadOnlyList<FriendSortKey> Sort => sort;

    // ---------------------------------------------------------------- filters

    public string SearchText
    {
        get => searchText;
        set
        {
            value ??= string.Empty;
            if (value == searchText)
                return;
            searchText = value;
            var needle = value.Trim();
            if (needle == searchNeedle)
                return;
            searchNeedle = needle;
            MarkViewDirty();
        }
    }

    /// <summary>Exact home world name, or null for all.</summary>
    public string? HomeWorldFilter
    {
        get => homeWorldFilter;
        set
        {
            if (value == homeWorldFilter)
                return;
            homeWorldFilter = value;
            MarkViewDirty();
        }
    }

    /// <summary>Exact data center name, or null for all.</summary>
    public string? DataCenterFilter
    {
        get => dataCenterFilter;
        set
        {
            if (value == dataCenterFilter)
                return;
            dataCenterFilter = value;
            MarkViewDirty();
        }
    }

    public FriendStatusFilter StatusFilter
    {
        get => statusFilter;
        set
        {
            if (value == statusFilter)
                return;
            statusFilter = value;
            MarkViewDirty();
        }
    }

    /// <summary>Exact group, or null for all.</summary>
    public FriendGroup? GroupFilter
    {
        get => groupFilter;
        set
        {
            if (value == groupFilter)
                return;
            groupFilter = value;
            MarkViewDirty();
        }
    }

    public bool HasActiveFilter =>
        searchNeedle.Length > 0 || homeWorldFilter != null || dataCenterFilter != null
        || statusFilter != FriendStatusFilter.All || groupFilter != null;

    public void ResetFilters()
    {
        SearchText = string.Empty;
        HomeWorldFilter = null;
        DataCenterFilter = null;
        StatusFilter = FriendStatusFilter.All;
        GroupFilter = null;
    }

    // ---------------------------------------------------------------- sort

    /// <summary>Replace the sort criteria (priority order). An empty list means native list order.</summary>
    public void SetSort(IEnumerable<FriendSortKey> keys)
    {
        sort.Clear();
        sort.AddRange(keys);
        MarkViewDirty();
    }

    /// <summary>Allocation-free alternative to <see cref="SetSort"/>: clear, then <see cref="AddSortKey"/> each criterion.</summary>
    public void BeginSortUpdate()
    {
        sort.Clear();
        MarkViewDirty();
    }

    public void AddSortKey(FriendSortKey key)
    {
        sort.Add(key);
        MarkViewDirty();
    }

    // ---------------------------------------------------------------- snapshot

    /// <summary>
    /// Feed the reader's current snapshot. Cheap when unchanged (reference/generation check).
    /// A null snapshot or a different <see cref="FriendListSnapshot.LocalContentId"/> clears the selection;
    /// otherwise ContentIds missing from the new snapshot are dropped from it.
    /// </summary>
    public void SetSnapshot(FriendListSnapshot? newSnapshot)
    {
        if (ReferenceEquals(newSnapshot, snapshot))
            return;
        if (newSnapshot != null && snapshot != null
            && newSnapshot.Generation == snapshot.Generation
            && newSnapshot.LocalContentId == snapshot.LocalContentId)
        {
            // Same logical snapshot (should not happen with the real reader, but be cheap about it).
            snapshot = newSnapshot;
            return;
        }

        var previous = snapshot;
        snapshot = newSnapshot;

        if (newSnapshot == null || previous == null || previous.LocalContentId != newSnapshot.LocalContentId)
        {
            // Logout, character switch, or first snapshot after having none: start from a clean slate.
            ClearSelectionInternal();
        }
        else if (selected.Count > 0)
        {
            // Rows that are gone, or are no longer selectable (e.g. turned into a pending request), drop out.
            var present = new HashSet<ulong>();
            foreach (var e in newSnapshot.Entries)
            {
                if (IsSelectable(e))
                    present.Add(e.ContentId);
            }
            if (selected.RemoveWhere(id => !present.Contains(id)) > 0)
                OnSelectionChanged();
            if (anchorContentId is { } a && !present.Contains(a))
                anchorContentId = null;
        }

        RebuildOptions();
        MarkViewDirty();
    }

    private void RebuildOptions()
    {
        worldOptions.Clear();
        dataCenterOptions.Clear();
        groupOptions.Clear();
        if (snapshot != null)
        {
            var worlds = new HashSet<string>(Cmp);
            var dcs = new HashSet<string>(Cmp);
            var groups = new HashSet<FriendGroup>();
            foreach (var e in snapshot.Entries)
            {
                if (worlds.Add(e.HomeWorldName))
                    worldOptions.Add(e.HomeWorldName);
                if (e.DataCenterName.Length > 0 && dcs.Add(e.DataCenterName))
                    dataCenterOptions.Add(e.DataCenterName);
                if (groups.Add(e.Group))
                    groupOptions.Add(e.Group);
            }

            worldOptions.Sort(Cmp);
            dataCenterOptions.Sort(Cmp);
            groupOptions.Sort();
        }

        // Drop filter values that no longer exist, so a refresh can't leave the table silently empty.
        if (homeWorldFilter != null && !worldOptions.Contains(homeWorldFilter))
            homeWorldFilter = null;
        if (dataCenterFilter != null && !dataCenterOptions.Contains(dataCenterFilter))
            dataCenterFilter = null;
        if (groupFilter is { } g && !groupOptions.Contains(g))
            groupFilter = null;
    }

    // ---------------------------------------------------------------- view

    /// <summary>Filtered + sorted rows of the current snapshot. Rebuilt on access only if an input changed.</summary>
    public IReadOnlyList<FriendEntry> View
    {
        get
        {
            EnsureView();
            return view;
        }
    }

    /// <summary>Selected rows that are in the current <see cref="View"/>.</summary>
    public int VisibleSelectedCount
    {
        get
        {
            EnsureView();
            if (visibleSelectedDirty)
            {
                var n = 0;
                if (selected.Count > 0)
                {
                    foreach (var e in view)
                    {
                        if (selected.Contains(e.ContentId))
                            n++;
                    }
                }

                visibleSelectedCount = n;
                visibleSelectedDirty = false;
            }

            return visibleSelectedCount;
        }
    }

    public void EnsureView()
    {
        if (!viewDirty)
            return;
        viewDirty = false;
        visibleSelectedDirty = true;
        view.Clear();
        if (snapshot != null)
        {
            foreach (var e in snapshot.Entries)
            {
                if (Matches(e))
                    view.Add(e);
            }

            view.Sort(Compare);
        }

        ViewVersion++;
    }

    public bool Matches(FriendEntry e)
    {
        if (homeWorldFilter != null && !Cmp.Equals(e.HomeWorldName, homeWorldFilter))
            return false;
        if (dataCenterFilter != null && !Cmp.Equals(e.DataCenterName, dataCenterFilter))
            return false;
        if (groupFilter is { } g && e.Group != g)
            return false;

        switch (statusFilter)
        {
            case FriendStatusFilter.Online when !e.IsOnline:
            case FriendStatusFilter.Offline when e.IsOnline:
            case FriendStatusFilter.PendingApproval when !e.IsPendingApproval:
            case FriendStatusFilter.UnableToRetrieve when !e.IsUnableToRetrieve:
                return false;
        }

        if (searchNeedle.Length > 0)
        {
            var s = searchNeedle;
            if (s.Length > 0
                && !e.DisplayName.Contains(s, StringComparison.OrdinalIgnoreCase)
                && !e.HomeWorldName.Contains(s, StringComparison.OrdinalIgnoreCase)
                && !e.CurrentWorldName.Contains(s, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>Comparison implementing the current sort; ties fall back to native order, then ContentId (total order).</summary>
    public int Compare(FriendEntry a, FriendEntry b)
    {
        foreach (var key in sort)
        {
            var c = CompareBy(key.Column, a, b);
            if (c != 0)
                return key.Ascending ? c : -c;
        }

        var li = a.ListIndex.CompareTo(b.ListIndex);
        return li != 0 ? li : a.ContentId.CompareTo(b.ContentId);
    }

    private int CompareBy(FriendColumn column, FriendEntry a, FriendEntry b) => column switch
    {
        // Selected first when ascending.
        FriendColumn.Selected => selected.Contains(b.ContentId).CompareTo(selected.Contains(a.ContentId)),
        FriendColumn.Name => Cmp.Compare(a.DisplayName, b.DisplayName),
        FriendColumn.HomeWorld => Cmp.Compare(a.HomeWorldName, b.HomeWorldName),
        FriendColumn.DataCenter => Cmp.Compare(a.DataCenterName, b.DataCenterName),
        // Online first when ascending, then by status text.
        FriendColumn.Status => b.IsOnline.CompareTo(a.IsOnline) is var o && o != 0 ? o : Cmp.Compare(a.StatusText, b.StatusText),
        FriendColumn.Group => ((sbyte)a.Group).CompareTo((sbyte)b.Group),
        // Pending first when ascending.
        FriendColumn.Pending => b.IsPendingApproval.CompareTo(a.IsPendingApproval) is var pc && pc != 0 ? pc : a.RequestState.CompareTo(b.RequestState),
        _ => 0,
    };

    private void MarkViewDirty()
    {
        viewDirty = true;
        visibleSelectedDirty = true;
    }

    // ---------------------------------------------------------------- selection

    public bool IsSelected(ulong contentId) => selected.Contains(contentId);

    /// <summary>
    /// Rows that can be ticked for removal: accepted friends only (RequestState 1). Pending friend requests (2 = sent,
    /// 3 = received) and unrecognised states cannot be selected; the game uses different actions for those.
    /// </summary>
    public static bool IsSelectable(FriendEntry e) => e.RequestState == 1;

    /// <summary>
    /// Checkbox click on <paramref name="contentId"/>. Without shift: toggle it. With shift and an anchor that is in the
    /// current view: set every row between the anchor and the clicked row (inclusive, in CURRENT view order) to the
    /// clicked row's new state. The clicked row becomes the new anchor. Returns false if ignored (locked / unknown row).
    /// </summary>
    public bool Click(ulong contentId, bool shift)
    {
        if (SelectionLocked)
            return false;
        EnsureView();
        var clicked = IndexInView(contentId);
        if (clicked < 0)
            return false;

        var newState = !selected.Contains(contentId);
        if (newState && !IsSelectable(view[clicked]))
            return false;
        var changed = false;
        var anchor = shift && anchorContentId is { } a ? IndexInView(a) : -1;
        if (anchor >= 0)
        {
            // Range: unselectable rows (pending requests) are skipped when selecting.
            var (lo, hi) = anchor <= clicked ? (anchor, clicked) : (clicked, anchor);
            for (var i = lo; i <= hi; i++)
            {
                if (!newState || IsSelectable(view[i]))
                    changed |= SetSelected(view[i].ContentId, newState);
            }
        }
        else
        {
            changed = SetSelected(contentId, newState);
        }

        anchorContentId = contentId;
        if (changed)
            OnSelectionChanged();
        return true;
    }

    public void SelectAllFiltered()
    {
        if (SelectionLocked)
            return;
        EnsureView();
        var changed = false;
        foreach (var e in view)
        {
            if (IsSelectable(e))
                changed |= selected.Add(e.ContentId);
        }

        if (changed)
            OnSelectionChanged();
    }

    public void DeselectFiltered()
    {
        if (SelectionLocked)
            return;
        EnsureView();
        var changed = false;
        foreach (var e in view)
            changed |= selected.Remove(e.ContentId);
        if (changed)
            OnSelectionChanged();
    }

    public void InvertFiltered()
    {
        if (SelectionLocked)
            return;
        EnsureView();
        if (view.Count == 0)
            return;
        foreach (var e in view)
        {
            if (!selected.Remove(e.ContentId) && IsSelectable(e))
                selected.Add(e.ContentId);
        }

        OnSelectionChanged();
    }

    public void ClearSelection()
    {
        if (SelectionLocked)
            return;
        ClearSelectionInternal();
    }

    /// <summary>
    /// Every selected row of the current snapshot (including rows hidden by the filter), in current sort order.
    /// Allocates; call on demand (e.g. when opening the confirmation), not per frame.
    /// </summary>
    public List<FriendEntry> GetSelectedEntries()
    {
        var result = new List<FriendEntry>(selected.Count);
        if (snapshot == null || selected.Count == 0)
            return result;
        foreach (var e in snapshot.Entries)
        {
            if (selected.Contains(e.ContentId))
                result.Add(e);
        }

        result.Sort(Compare);
        return result;
    }

    private bool SetSelected(ulong id, bool value) => value ? selected.Add(id) : selected.Remove(id);

    private int IndexInView(ulong contentId)
    {
        for (var i = 0; i < view.Count; i++)
        {
            if (view[i].ContentId == contentId)
                return i;
        }

        return -1;
    }

    private void ClearSelectionInternal()
    {
        anchorContentId = null;
        if (selected.Count == 0)
            return;
        selected.Clear();
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        SelectionVersion++;
        visibleSelectedDirty = true;
    }
}
