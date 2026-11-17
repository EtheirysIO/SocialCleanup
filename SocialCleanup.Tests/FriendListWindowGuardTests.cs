using System.Linq;
using SocialCleanup.Friends;
using SocialCleanup.Removal;
using SocialCleanup.Windows;
using Xunit;

namespace SocialCleanup.Tests;

/// <summary>
/// The in-game Friend List window guard: nothing starts while it is open (any mode), a running batch pauses (not halts)
/// before its next send, the in-flight item settles normally, and paused time does not count against any timeout.
/// </summary>
public sealed class FriendListWindowGuardTests
{
    [Fact]
    public void TryStart_RefusedInEveryModeWhileTheWindowIsOpen()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Session.IsFriendListWindowOpen = true;

        Assert.False(r.Queue.TryStart(r.Batch(false, ids), out var err));
        Assert.Contains("Friend List window", err);
        Assert.False(r.Queue.TryStart(r.Batch(false, ids) with { FastMode = true }, out err));
        Assert.Contains("Friend List window", err);
        Assert.False(r.Queue.TryStart(r.Batch(true, ids), out err));
        Assert.Contains("Friend List window", err);

        r.Tick(3);
        Assert.Equal(RemovalQueueState.Idle, r.Queue.State);
        Assert.Empty(r.Audit.Phase(AuditPhase.BatchStart));
        Assert.Empty(r.Remover.RealCalls);
        Assert.Empty(r.Remover.DryCalls);

        r.Session.IsFriendListWindowOpen = false;
        r.Start(false, ids);
    }

    [Fact]
    public void Standard_OpenedMidBatch_InFlightSettles_PausesWithoutSending_ContinuesWhenClosed()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Start(false, ids);
        r.Tick();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(0).Status);

        // Opened while item 0 is in flight: it still settles normally.
        r.Session.IsFriendListWindowOpen = true;
        r.ServerRemoves(ids[0]);
        Assert.Equal(RemovalItemStatus.Removed, r.Item(0).Status);

        // Opening the window makes the game download the list; far longer than every timeout passes.
        r.Reader.StartGameDownload();
        for (var i = 0; i < 24; i++)
        {
            r.Advance(5);
            r.Tick();
        }

        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.True(r.Queue.IsPausedForFriendListWindow);
        Assert.Equal(RemovalQueueCore.FriendListWindowPauseText, r.Queue.Activity);
        Assert.Equal(0, r.Remover.Real(ids[1]));
        Assert.Equal(1, r.Remover.TotalNativeCalls);

        // The download finishes while the window is still open: still nothing sent.
        r.Republish(newRequest: true);
        r.Advance(5);
        r.Tick();
        Assert.True(r.Queue.IsPausedForFriendListWindow);
        Assert.Equal(0, r.Remover.Real(ids[1]));

        // Closed: continues by itself on the next tick (the list is Ready).
        r.Session.IsFriendListWindowOpen = false;
        r.Tick();
        Assert.False(r.Queue.IsPausedForFriendListWindow);
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(1).Status);
        Assert.NotEqual(RemovalQueueCore.FriendListWindowPauseText, r.Queue.Activity);

        r.ServerRemoves(ids[1]);
        r.AfterCooldown();
        r.ServerRemoves(ids[2]);
        r.Tick();
        Assert.Equal(RemovalQueueState.Completed, r.Queue.State);
        Assert.Equal(3, r.Queue.Counts.Removed);
        Assert.DoesNotContain(r.Log, l => l.Contains("HALTED"));
        Assert.Contains(r.Log, l => l.Contains("Paused before"));
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void Fast_OpenedMidBatch_InFlightSettles_PausesWithoutSending_ContinuesAfterTheDownloadSettles()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.StartFast(ids);
        r.Tick();
        Assert.Equal(ids[0], r.Suppressor.Armed);

        r.Session.IsFriendListWindowOpen = true;
        Assert.True(r.FastReply(ids[0]));
        Assert.Equal(RemovalItemStatus.Removed, r.Item(0).Status);

        for (var i = 0; i < 30; i++)
        {
            r.Advance(5);
            r.Tick();
        }

        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.True(r.Queue.IsPausedForFriendListWindow);
        Assert.Equal(0, r.Remover.Real(ids[1]));
        Assert.Equal(0ul, r.Suppressor.Armed);
        Assert.True(r.Suppressor.FastBatchActive);

        // The open window made the game start a real download; it is still in flight when the window closes.
        r.Suppressor.IsDownloadInFlight = true;
        r.Reader.StartGameDownload();
        r.Advance(2);
        r.Tick();
        r.Session.IsFriendListWindowOpen = false;
        r.Tick();
        Assert.False(r.Queue.IsPausedForFriendListWindow);
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Equal(0, r.Remover.Real(ids[1]));
        Assert.Contains("download", r.Queue.Activity);

        // Download done → Ready → the next send.
        r.Advance(3);
        r.Republish(newRequest: true);
        r.Tick();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(1).Status);
        Assert.Equal(ids[1], r.Suppressor.Armed);

        Assert.True(r.FastReply(ids[1]));
        r.Advance(0.6);
        r.Tick();
        Assert.True(r.FastReply(ids[2]));
        r.Tick();
        Assert.Equal(RemovalQueueState.Completed, r.Queue.State);
        Assert.Equal(3, r.Queue.Counts.Removed);
        Assert.Equal(3, r.Remover.TotalNativeCalls);
        Assert.DoesNotContain(r.Log, l => l.Contains("HALTED"));
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void PausedTime_DoesNotCountAgainstTheReadyWait_ButTheBoundStillApplies()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.ServerRemoves(ids[0]);

        // The list starts loading and never finishes: the ready-wait (30 s) starts counting.
        r.Reader.StartGameDownload();
        r.AfterCooldown();
        r.Advance(20);
        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);

        // 60 s paused.
        r.Session.IsFriendListWindowOpen = true;
        r.Tick();
        r.Advance(60);
        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        r.Session.IsFriendListWindowOpen = false;
        r.Tick();

        // 20 s + 9 s of real waiting: still waiting. 20 + 11 s: halts, nothing sent.
        r.Advance(9);
        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Contains("Waiting for the friend list", r.Queue.Activity);
        r.Advance(2);
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Contains("usable friend list", r.Queue.HaltReason);
        Assert.Equal(0, r.Remover.Real(ids[1]));
    }

    [Fact]
    public void PausedTime_DoesNotCountAgainstTheTransientRefusalBudget()
    {
        using var r = new Rig();
        var ids = r.Ids(1);
        r.Remover.RequestIdsMoveBeforeCall = true;
        r.Start(false, ids);
        r.Tick();
        Assert.Equal(RemovalItemStatus.Pending, r.Item(0).Status);
        Assert.Equal(0, r.Remover.Native(ids[0]));

        r.Advance(20);
        r.Tick();
        r.Session.IsFriendListWindowOpen = true;
        r.Tick();
        r.Advance(60);
        r.Tick();
        r.Session.IsFriendListWindowOpen = false;
        r.Tick();

        // 20 s of real transient refusals (< 30 s): the send goes ahead once the list is fresh.
        r.Remover.RequestIdsMoveBeforeCall = false;
        r.Republish(newRequest: true);
        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(0).Status);
        Assert.Equal(1, r.Remover.Native(ids[0]));
    }

    [Fact]
    public void CancelWhilePaused_StopsWithoutSending()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Start(false, ids);
        r.Tick();
        r.ServerRemoves(ids[0]);
        r.Session.IsFriendListWindowOpen = true;
        r.AfterCooldown();
        Assert.True(r.Queue.IsPausedForFriendListWindow);

        r.Queue.Cancel();
        Assert.Equal(RemovalQueueState.Cancelled, r.Queue.State);
        Assert.False(r.Queue.IsPausedForFriendListWindow);
        r.Session.IsFriendListWindowOpen = false;
        r.Tick(3);
        Assert.Equal(1, r.Remover.TotalNativeCalls);
        Assert.Equal(2, r.Queue.Counts.Cancelled);
    }

    [Fact]
    public void RemovalGate_RefusesWhileTheWindowIsOpen()
    {
        Assert.False(RemovalGate.CanStartRemoval(true, RemovalQueueState.Idle, RemoverAvailability.Available(), FriendListState.Ready, true, true, 3,
            out var reason, friendListWindowOpen: true));
        Assert.Contains("Friend List window", reason);
        Assert.True(RemovalGate.CanStartRemoval(true, RemovalQueueState.Idle, RemoverAvailability.Available(), FriendListState.Ready, true, true, 3,
            out reason, friendListWindowOpen: false), reason);
    }
}

/// <summary>Pending friend requests cannot be ticked; the confirmation never dry-runs unless asked to.</summary>
public sealed class FriendTableSelectionTests
{
    private static (Rig Rig, FriendTableModel Model) Setup()
    {
        var r = new Rig(friends: 3, extra: [Rig.Entry(0x2001, "Sent Request", requestState: 2, index: 1), Rig.Entry(0x2002, "Got Request", requestState: 3, index: 4)]);
        var m = new FriendTableModel();
        m.SetSnapshot(r.Reader.Snapshot);
        return (r, m);
    }

    [Fact]
    public void SelectAllAndInvert_SkipPendingRequests()
    {
        var (r, m) = Setup();
        using var _ = r;
        m.SelectAllFiltered();
        Assert.Equal(3, m.SelectedCount);
        Assert.False(m.IsSelected(0x2001));
        Assert.False(m.IsSelected(0x2002));

        m.ClearSelection();
        m.InvertFiltered();
        Assert.Equal(3, m.SelectedCount);
        Assert.False(m.IsSelected(0x2001));
    }

    [Fact]
    public void Click_OnPendingRequest_IsIgnored_AndShiftRangeSkipsIt()
    {
        var (r, m) = Setup();
        using var _ = r;
        Assert.False(m.Click(0x2001, shift: false));
        Assert.Equal(0, m.SelectedCount);

        // Range Friend 1 .. Friend 3 spans the pending "Sent Request" row (same native index as Friend 2): it is skipped.
        Assert.True(m.Click(r.List[0].ContentId, shift: false));
        Assert.False(m.Click(0x2002, shift: true)); // clicking the pending row itself is ignored
        Assert.True(m.Click(r.List[2].ContentId, shift: true));
        Assert.Equal(3, m.SelectedCount);
        Assert.DoesNotContain(m.SelectedIds, id => id is 0x2001 or 0x2002);
    }

    [Fact]
    public void Confirmation_UsesTheGivenDryRunFlag_AndExcludesNothingSelectable()
    {
        var (r, m) = Setup();
        using var _ = r;
        m.SelectAllFiltered();
        var c = RemovalConfirmation.Create(m, dryRunDefault: false, fastModeDefault: true)!;
        Assert.False(c.DryRun);
        Assert.True(c.FastMode);
        Assert.Equal(3, c.Targets.Count);
        Assert.Empty(c.Excluded);
        Assert.True(c.TryBuildBatch(r.Reader.Snapshot, r.Clock.UtcNow, out var batch, out var err), err);
        Assert.False(batch!.DryRun);
        Assert.True(batch.FastMode);
        Assert.All(c.Targets, t => Assert.True(r.List.Single(e => e.ContentId == t.ContentId).RequestState == 1));
    }
}
