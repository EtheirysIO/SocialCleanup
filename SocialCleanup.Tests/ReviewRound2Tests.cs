using System;
using System.Linq;
using SocialCleanup.Friends;
using SocialCleanup.Removal;
using Xunit;

namespace SocialCleanup.Tests;

/// <summary>Second review round: resume refusal, "who else went missing", standard-batch verification, nits.</summary>
public sealed class ReviewRound2Tests
{
    private const ulong NotInBatch = 0x1005; // "Friend 5"

    // ------------------------------------------------------------------ 1: no Resume after Mismatch / TimedOut

    [Fact]
    public void FastHalt_FinalVerificationMismatch_RefusesResume_NamesEveryone_CancelAndClearStillWork()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.StartFast(ids);
        r.Tick();
        r.Advance(10.1);
        r.Tick(); // no server reply → Indeterminate, halted
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        r.Tick(); // final reload requested
        Assert.Equal(FinalVerificationStatus.Requested, r.Queue.FinalVerification!.Status);

        r.List.RemoveAll(e => e.ContentId == NotInBatch);
        r.CompleteReload();

        var v = r.Queue.FinalVerification!;
        Assert.Equal(FinalVerificationStatus.Mismatch, v.Status);
        Assert.True(v.BlocksResume);
        Assert.Contains(v.MissingNotInBatch, n => n.Contains("Friend 5") && n.Contains("0000000000001005"));
        Assert.NotNull(r.Queue.ResumeBlockedReason);
        Assert.Contains("cannot be resumed", r.Queue.HaltReason);

        Assert.False(r.Queue.Resume(out var err));
        Assert.Contains("Mismatch", err);
        Assert.False(r.Queue.IsResumePending);
        r.Tick(3);
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(0, r.Remover.Real(ids[1]));

        // Cancel keeps the mismatch visible (no second reload overwrites it).
        r.Queue.Cancel();
        Assert.Equal(RemovalQueueState.Cancelled, r.Queue.State);
        r.Tick(3);
        Assert.Equal(FinalVerificationStatus.Mismatch, r.Queue.FinalVerification!.Status);
        Assert.Equal(1, r.Reader.RefreshRequests);

        r.Queue.Clear();
        Assert.Equal(RemovalQueueState.Idle, r.Queue.State);
        Assert.Null(r.Queue.ResumeBlockedReason);
        Assert.Empty(r.Queue.MissingOutsideBatch);

        // A new batch from the fresh snapshot is allowed.
        r.Remover.Unresolved = null;
        r.Start(false, ids[1]);
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void FastHalt_FinalVerificationTimedOut_RefusesResume()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.StartFast(ids);
        r.Tick();
        r.Advance(10.1);
        r.Tick();
        r.Tick();
        Assert.Equal(FinalVerificationStatus.Requested, r.Queue.FinalVerification!.Status);
        r.Advance(31);
        r.Tick();
        Assert.Equal(FinalVerificationStatus.TimedOut, r.Queue.FinalVerification!.Status);

        Assert.False(r.Queue.Resume(out var err));
        Assert.Contains("TimedOut", err);
        Assert.NotNull(r.Queue.ResumeBlockedReason);
    }

    [Fact]
    public void Mismatch_ListsRemovedItemsThatAreStillListed()
    {
        using var r = new Rig();
        var ids = r.Ids(1);
        r.StartFast(ids);
        r.Tick();
        Assert.True(r.FastReply(ids[0]));
        r.Advance(0.6);
        r.Tick(2);
        r.List.Add(Rig.Entry(ids[0], "Friend 1", index: 0));
        r.CompleteReload();

        var v = r.Queue.FinalVerification!;
        Assert.Equal(FinalVerificationStatus.Mismatch, v.Status);
        Assert.Contains(v.StillListed, n => n.Contains("Friend 1") && n.Contains("Removed"));
        Assert.Empty(v.MissingNotInBatch);
    }

    // ------------------------------------------------------------------ 3: who else went missing, every verdict path

    [Theory]
    [InlineData("stillPresent")]
    [InlineData("preSendData")]
    [InlineData("removed")]
    [InlineData("wrongCount")]
    public void Standard_AnyVerdictSnapshot_MissingAFriendOutsideTheBatch_HaltsAndBlocksResume(string kind)
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.Advance(7);
        r.List.RemoveAll(e => e.ContentId == NotInBatch);
        switch (kind)
        {
            case "stillPresent":
                r.Republish(newRequest: true);
                break;
            case "preSendData":
                r.Republish(newRequest: false);
                break;
            case "removed":
                r.List.RemoveAll(e => e.ContentId == ids[0]);
                r.List.Add(Rig.Entry(0x3001, "New Friend", index: 9)); // keeps the count at -1
                r.Republish(newRequest: true);
                break;
            default:
                r.List.RemoveAll(e => e.ContentId == ids[0]);
                r.Republish(newRequest: true);
                break;
        }

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Contains("Friend 5", r.Queue.HaltReason);
        Assert.Contains("NOT in this batch", r.Queue.HaltReason);
        Assert.Contains(r.Queue.MissingOutsideBatch, n => n.Contains("0000000000001005"));
        Assert.NotNull(r.Queue.ResumeBlockedReason);
        Assert.Contains(r.Log, l => l.Contains("HALTED") && l.Contains("Friend 5"));

        r.Tick();
        Assert.Contains(r.Audit.Phase(AuditPhase.UnexpectedMissing), a => a.Detail.Contains("Friend 5") && a.Target.ContentId == 0);
        Assert.Contains(r.Audit.Phase(AuditPhase.Verdict), a => a.Result == "Unexpected" && a.Detail.Contains("Friend 5"));

        Assert.False(r.Queue.Resume(out var err));
        Assert.Contains("cannot be resumed", err);
        r.AfterCooldown();
        Assert.Equal(0, r.Remover.Real(ids[1]));
    }

    [Fact]
    public void Fast_LocalSnapshotMissingAFriendOutsideTheBatch_HaltsAndBlocksResume()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.StartFast(ids);
        r.Tick();
        r.List.RemoveAll(e => e.ContentId == NotInBatch);
        Assert.True(r.FastReply(ids[0]));

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.NotNull(r.Queue.ResumeBlockedReason);
        r.Tick(); // final reload
        r.CompleteReload();
        Assert.Equal(FinalVerificationStatus.Mismatch, r.Queue.FinalVerification!.Status);
        Assert.False(r.Queue.Resume(out _));
    }

    [Fact]
    public void Fast_FullDownloadMissingAFriendOutsideTheBatch_HaltsAndBlocksResume()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.StartFast(ids);
        r.Tick();
        r.Suppressor.IsDownloadInFlight = true; // the reply's reload is not suppressed → a full download decides
        Assert.False(r.FastReply(ids[0]));
        r.List.RemoveAll(e => e.ContentId == NotInBatch);
        r.CompleteReload(3);

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Contains("Friend 5", r.Queue.HaltReason);
        Assert.NotNull(r.Queue.ResumeBlockedReason);
    }

    [Fact]
    public void TimeoutReconcile_MissingAFriendOutsideTheBatch_StaysHalted_AndBlocksResume()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.Advance(31);
        r.Tick();
        Assert.True(r.Queue.Resume(out var err), err);
        r.Tick();

        r.List.RemoveAll(e => e.ContentId == ids[0] || e.ContentId == NotInBatch);
        r.CompleteReload(1);
        r.Tick();

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Contains("Friend 5", r.Queue.HaltReason);
        Assert.NotNull(r.Queue.ResumeBlockedReason);
        Assert.False(r.Queue.Resume(out _));
        r.AfterCooldown();
        Assert.Equal(0, r.Remover.Real(ids[1]));
    }

    [Fact]
    public void AlreadyAbsent_WithAFriendOutsideTheBatchMissing_Halts()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.List.RemoveAll(e => e.ContentId == ids[0] || e.ContentId == NotInBatch);
        r.Republish(newRequest: true);
        r.Tick();

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Failed, r.Item(0).Status);
        Assert.Contains("nothing sent", r.Item(0).Detail);
        Assert.NotNull(r.Queue.ResumeBlockedReason);
        Assert.Equal(0, r.Remover.TotalNativeCalls);
        Assert.False(r.Queue.Resume(out _));
    }

    [Fact]
    public void ABatchItemGoingMissing_IsNotAnOutsideBatchHalt()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Start(false, ids);
        r.Tick();
        // Item 0 removed by us, item 2 (pending, in the batch) removed elsewhere at the same time: the per-item check halts,
        // but this is not "a friend outside the batch went missing", so Resume stays possible.
        r.List.RemoveAll(e => e.ContentId == ids[2]);
        r.List.Add(Rig.Entry(0x3001, "New Friend", index: 9));
        r.ServerRemoves(ids[0]);
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Null(r.Queue.ResumeBlockedReason);
        Assert.Empty(r.Queue.MissingOutsideBatch);
    }

    // ------------------------------------------------------------------ 3/7: standard end-of-batch verification

    [Fact]
    public void StandardBatch_Completed_IsVerifiedAgainstTheLastFullSnapshot_WithoutAnExtraReload()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.ServerRemoves(ids[0]);
        r.AfterCooldown();
        var last = r.ServerRemoves(ids[1]);
        r.AfterCooldown();

        Assert.Equal(RemovalQueueState.Completed, r.Queue.State);
        var v = r.Queue.FinalVerification!;
        Assert.Equal(FinalVerificationStatus.Passed, v.Status);
        Assert.Equal(last.Generation, v.Generation);
        Assert.Equal(0, r.Reader.RefreshRequests);
        Assert.Equal("Passed", Assert.Single(r.Audit.Phase(AuditPhase.Verify)).Result);
    }

    [Fact]
    public void StandardBatch_AlreadyAbsentItemListedAgainAtTheEnd_IsAMismatch()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.List.RemoveAll(e => e.ContentId == ids[0]);
        r.Republish(newRequest: true);
        r.Tick();
        Assert.Equal(RemovalItemStatus.AlreadyAbsent, r.Item(0).Status);
        r.AfterCooldown();
        r.ServerRemoves(ids[1]);

        // Before the completing tick, a game-initiated download lists the "already absent" friend again.
        r.List.Add(Rig.Entry(ids[0], "Friend 1", index: 0));
        r.Republish(newRequest: true);
        r.AfterCooldown();

        Assert.Equal(RemovalQueueState.Completed, r.Queue.State);
        var v = r.Queue.FinalVerification!;
        Assert.Equal(FinalVerificationStatus.Mismatch, v.Status);
        Assert.Contains(v.StillListed, n => n.Contains("Friend 1") && n.Contains("AlreadyAbsent"));
        Assert.Equal(0, r.Reader.RefreshRequests);
    }

    [Fact]
    public void FastBatch_FinalVerificationReChecksAlreadyAbsentItems()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.StartFast(ids);
        r.List.RemoveAll(e => e.ContentId == ids[0]);
        r.Republish(newRequest: true);
        r.Tick();
        Assert.Equal(RemovalItemStatus.AlreadyAbsent, r.Item(0).Status);
        r.Advance(0.6);
        r.Tick();
        Assert.True(r.FastReply(ids[1]));
        r.Advance(0.6);
        r.Tick(2);
        Assert.Equal(FinalVerificationStatus.Requested, r.Queue.FinalVerification!.Status);

        r.List.Add(Rig.Entry(ids[0], "Friend 1", index: 0));
        r.CompleteReload();
        var v = r.Queue.FinalVerification!;
        Assert.Equal(FinalVerificationStatus.Mismatch, v.Status);
        Assert.Contains(v.StillListed, n => n.Contains("AlreadyAbsent"));
    }

    [Fact]
    public void DryRunBatch_HasNoVerification()
    {
        using var r = new Rig();
        r.Start(true, r.Ids(1));
        r.Tick(2);
        Assert.Equal(RemovalQueueState.Completed, r.Queue.State);
        Assert.Null(r.Queue.FinalVerification);
    }

    // ------------------------------------------------------------------ 5: local snapshots are never post-send downloads

    [Fact]
    public void SendClassifier_LocalSnapshot_IsNeverAPostSendDownload()
    {
        var e = Rig.Entry(0x1001, "A");
        var other = Rig.Entry(0x1002, "B");
        var local = new FriendListSnapshot(new[] { e, other }, 2, Rig.Me, DateTime.UtcNow, 2, 0, 0, false, 20, 21, 20)
        {
            Source = SnapshotSource.LocalAfterRemoval,
        };

        // Request id differs from the one at send time: a full download would be "post-send"; a local one is not.
        Assert.Equal(SendObservation.PresentInPreSendData, SendClassifier.Classify(local, 0x1001, Rig.Me, 1, 19));
        Assert.Equal(SnapshotVerdict.KeepWaiting, SendClassifier.Evaluate(local, 0x1001, Rig.Me, 1, 19, 2).Verdict);
        Assert.Equal(SendObservation.PresentInPostSendDownload,
            SendClassifier.Classify(local with { Source = SnapshotSource.FullDownload }, 0x1001, Rig.Me, 1, 19));

        var absent = local with { Entries = new[] { other } };
        Assert.Equal(SendObservation.TargetAbsent, SendClassifier.Classify(absent, 0x1001, Rig.Me, 1, 19));
    }

    // ------------------------------------------------------------------ 6: reader timeout floor

    [Theory]
    [InlineData(15, null, 15)]
    [InlineData(15, 30, 30)]
    [InlineData(15, 10, 30)]
    [InlineData(15, 60, 60)]
    [InlineData(90, 30, 90)]
    [InlineData(0, null, 1)]
    public void GameDownloadTimeout_IsNeverShorterThanMaxOfItemTimeoutAnd30s_WhileRemoving(int configured, int? itemSeconds, int expected)
    {
        TimeSpan? item = itemSeconds is { } s ? TimeSpan.FromSeconds(s) : null;
        Assert.Equal(TimeSpan.FromSeconds(expected), FriendListTimeouts.GameDownloadTimeout(configured, item));
    }

    // ------------------------------------------------------------------ 10: nits

    [Fact]
    public void TransientRefreshFlag_IsResetWithTransientSince_SoALaterTransientAsksAgain()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Remover.LiveEntryCount = 0;
        r.Tick();
        r.Advance(2.1);
        r.Tick();
        Assert.Equal(1, r.Reader.RefreshRequests);

        r.Remover.LiveEntryCount = null;
        r.CompleteReload(1);
        r.Tick();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(0).Status);
        r.ServerRemoves(ids[0]);

        r.Remover.LiveEntryCount = 0;
        r.AfterCooldown();
        Assert.Equal(RemovalItemStatus.Pending, r.Item(1).Status);
        r.Advance(2.1);
        r.Tick();
        Assert.Equal(2, r.Reader.RefreshRequests);
        r.AssertAtMostOneNativeCallPerItem();
    }

    [Fact]
    public void Standard_NonZeroServerReply_HaltsImmediatelyAsFailed_ViaTheNarrowLatchRelease()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        Assert.NotNull(r.Remover.Unresolved);

        Assert.False(r.FastReply(ids[0], result: 340));
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Failed, r.Item(0).Status);
        Assert.Contains("LogMessage#340", r.Item(0).Detail);
        Assert.Contains("result 340", r.Queue.HaltReason);
        Assert.Null(r.Remover.Unresolved);
        Assert.Equal(1, r.Remover.Releases);
        Assert.Contains(r.Audit.Phase(AuditPhase.Released), a => a.Result == "ServerRejected");
        Assert.Equal(new FastModeStats(0, 0, 0, 0), r.Queue.FastStats);
        r.Tick();
        Assert.Contains(r.Audit.Phase(AuditPhase.Verdict), a => a.Result == "ServerRejected");

        // A reply for someone else (or a second one) changes nothing.
        Assert.False(r.FastReply(ids[1], result: 340));
        Assert.Equal(1, r.Remover.Releases);

        Assert.True(r.Queue.Resume(out var err), err);
        r.Tick();
        r.CompleteReload(1);
        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Failed, r.Item(0).Status); // sent + rejected: never back to Pending
        r.AfterCooldown();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(1).Status);
        Assert.Equal(1, r.Remover.Real(ids[0]));
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void Standard_NonZeroReply_LatchForAnotherTarget_IsNotReleased()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        var latch = r.Remover.Unresolved!;
        r.Remover.Unresolved = latch with { Target = RemovalTarget.From(r.List[3]) };

        r.FastReply(ids[0], result: 340);
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.NotNull(r.Remover.Unresolved);
        Assert.Contains("latch is still set", r.Queue.HaltReason);
    }

    [Fact]
    public void HaltingItselfThrows_TheFinalReloadIsStillScheduled()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.StartFast(ids);
        r.Tick();
        r.Suppressor.ThrowOnDisarm = true;
        r.Advance(10.1);
        r.Tick(); // no reply → Indeterminate → Disarm throws → exception → halting throws too

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.False(r.Suppressor.FastBatchActive);
        Assert.Equal(FinalVerificationStatus.Pending, r.Queue.FinalVerification!.Status);
        Assert.Contains(r.Log, l => l.Contains("Halting after an exception failed too"));

        r.Suppressor.ThrowOnDisarm = false;
        r.Tick();
        Assert.Equal(FinalVerificationStatus.Requested, r.Queue.FinalVerification!.Status);
        Assert.Equal(1, r.Reader.RefreshRequests);
    }
}
