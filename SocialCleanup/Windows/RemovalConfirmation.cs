using System;
using System.Collections.Generic;
using SocialCleanup.Friends;
using SocialCleanup.Removal;

namespace SocialCleanup.Windows;

/// <summary>
/// The set of friends shown in the confirmation dialog, frozen when the dialog opens. Confirming builds a
/// <see cref="RemovalBatch"/> from exactly these targets, and only if the reader's snapshot is still the one the
/// list was frozen from (same generation and owner) — otherwise the user must review the refreshed list again.
/// No ImGui; testable.
/// </summary>
public sealed class RemovalConfirmation
{
    private RemovalConfirmation(
        IReadOnlyList<FriendEntry> entries,
        IReadOnlyList<RemovalTarget> targets,
        IReadOnlyList<FriendEntry> excluded,
        long generation,
        ulong localContentId,
        int hiddenByFilter,
        bool dryRun,
        bool fastMode)
    {
        Entries = entries;
        Targets = targets;
        Excluded = excluded;
        SnapshotGeneration = generation;
        LocalContentId = localContentId;
        HiddenByFilterCount = hiddenByFilter;
        DryRun = dryRun;
        FastMode = fastMode;
    }

    /// <summary>Frozen rows that WILL be removed, for display (name, world).</summary>
    public IReadOnlyList<FriendEntry> Entries { get; }

    /// <summary>
    /// Selected rows left out because they are not accepted friends (RequestState 2 = request sent, 3 = request
    /// received, or anything other than 1). The game uses different packets for those; the queue refuses them.
    /// </summary>
    public IReadOnlyList<FriendEntry> Excluded { get; }

    /// <summary>True when <paramref name="e"/> can go into a removal batch (accepted friend, RequestState 1).</summary>
    public static bool IsRemovable(FriendEntry e) => e.RequestState == 1;

    public IReadOnlyList<RemovalTarget> Targets { get; }

    public long SnapshotGeneration { get; }

    public ulong LocalContentId { get; }

    /// <summary>How many of the targets are not visible under the current filter (so the user knows they're included).</summary>
    public int HiddenByFilterCount { get; }

    public bool DryRun { get; set; }

    /// <summary>Fast mode requested (only applies to real batches; ignored for dry runs).</summary>
    public bool FastMode { get; set; }

    /// <summary>Freeze the model's current selection. Returns null when there is no snapshot or nothing is selected.</summary>
    public static RemovalConfirmation? Create(FriendTableModel model, bool dryRunDefault, bool fastModeDefault = false)
    {
        var snap = model.Snapshot;
        if (snap == null || model.SelectedCount == 0)
            return null;

        var selected = model.GetSelectedEntries();
        if (selected.Count == 0)
            return null;

        var entries = new List<FriendEntry>(selected.Count);
        var excluded = new List<FriendEntry>();
        var targets = new List<RemovalTarget>(selected.Count);
        var hidden = 0;
        foreach (var e in selected)
        {
            if (!IsRemovable(e))
            {
                excluded.Add(e);
                continue;
            }

            entries.Add(e);
            targets.Add(RemovalTarget.From(e));
            if (!model.Matches(e))
                hidden++;
        }

        return new RemovalConfirmation(entries, targets, excluded, snap.Generation, snap.LocalContentId, hidden, dryRunDefault, fastModeDefault);
    }

    /// <summary>True when <paramref name="current"/> is still the snapshot this confirmation was frozen from.</summary>
    public bool IsCurrent(FriendListSnapshot? current) =>
        current != null && current.Generation == SnapshotGeneration && current.LocalContentId == LocalContentId;

    /// <summary>Build the batch to hand to <see cref="IRemovalQueue.TryStart"/>; fails closed if the snapshot moved on.</summary>
    public bool TryBuildBatch(FriendListSnapshot? current, DateTime nowUtc, out RemovalBatch? batch, out string? error)
    {
        batch = null;
        if (current == null)
        {
            error = "There is no friend-list snapshot any more (logged out or character changed).";
            return false;
        }

        if (current.LocalContentId != LocalContentId)
        {
            error = "The logged-in character changed since this list was built.";
            return false;
        }

        if (current.Generation != SnapshotGeneration)
        {
            error = "The friend list was refreshed while this dialog was open. Close it and review the selection again.";
            return false;
        }

        if (Targets.Count == 0)
        {
            error = Excluded.Count > 0 ? "Nothing to remove: every selected entry is a pending friend request." : "Nothing to remove.";
            return false;
        }

        batch = new RemovalBatch(Targets, LocalContentId, SnapshotGeneration, nowUtc, DryRun) { FastMode = FastMode && !DryRun };
        error = null;
        return true;
    }
}

/// <summary>Decides whether the "Remove N selected" action is allowed, with a user-facing reason when it is not.</summary>
public static class RemovalGate
{
    public static bool IsBatchActive(RemovalQueueState? state) =>
        state is RemovalQueueState.Running or RemovalQueueState.Halted;

    /// <param name="hasQueue">An <see cref="IRemovalQueue"/> was supplied.</param>
    /// <param name="queueState">Its state (ignored when <paramref name="hasQueue"/> is false).</param>
    /// <param name="removerAvailability">The remover's availability, or null when no remover was supplied.</param>
    /// <param name="readerState">Friend-list reader state.</param>
    /// <param name="hookActive">Reader completion hook installed.</param>
    /// <param name="hasSnapshot">A complete snapshot exists.</param>
    /// <param name="selectedCount">Selected rows.</param>
    /// <param name="reason">Why it is disabled, or null when enabled.</param>
    public static bool CanStartRemoval(
        bool hasQueue,
        RemovalQueueState queueState,
        RemoverAvailability? removerAvailability,
        FriendListState readerState,
        bool hookActive,
        bool hasSnapshot,
        int selectedCount,
        out string? reason,
        bool singleTestInFlight = false,
        bool friendListWindowOpen = false)
    {
        reason = null;
        if (!hasQueue)
            reason = "Removal engine is not available in this build.";
        else if (removerAvailability is not { } avail)
            reason = "No friend remover was supplied; availability unknown.";
        else if (!avail.IsAvailable)
            reason = $"Removal is unavailable: {avail.Reason}";
        else if (friendListWindowOpen)
            reason = "Close the in-game Friend List window first.";
        else if (IsBatchActive(queueState))
            reason = queueState == RemovalQueueState.Halted ? "A removal batch is halted; resume or cancel it first." : "A removal batch is running.";
        else if (singleTestInFlight)
            reason = "A single-friend removal test is in flight.";
        else if (!hookActive)
            reason = "The friend list cannot be read (completion hook not active).";
        else if (!hasSnapshot)
            reason = "The friend list is not loaded yet.";
        else if (readerState != FriendListState.Ready)
            reason = readerState == FriendListState.Loading ? "Waiting for the friend list to finish loading." : $"The friend list is {readerState}; press Refresh.";
        else if (selectedCount == 0)
            reason = "Nothing selected.";

        return reason == null;
    }
}
