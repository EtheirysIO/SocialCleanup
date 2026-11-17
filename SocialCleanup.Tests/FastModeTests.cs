using System;
using System.Linq;
using SocialCleanup.Friends;
using SocialCleanup.Removal;
using Xunit;

namespace SocialCleanup.Tests;

public sealed class FastModeTests
{
    [Fact]
    public void HappyPath_ThreeItems_OneSendEach_NoReloadBetween_ExactlyOneFinalReload()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.StartFast(ids);

        for (var i = 0; i < 3; i++)
        {
            r.Tick();
            Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(i).Status);
            Assert.Equal(ids[i], r.Suppressor.Armed);
            Assert.Equal(1, r.Remover.Native(ids[i]));

            Assert.True(r.FastReply(ids[i]));
            Assert.Equal(RemovalItemStatus.Removed, r.Item(i).Status);
            Assert.Equal(0ul, r.Suppressor.Armed);
            Assert.Null(r.Remover.Unresolved); // released by the local snapshot
            Assert.Equal(FriendListState.Ready, r.Reader.State);

            // Fast cooldown (500 ms): not on the very next tick. After the last item that tick completes the batch.
            r.Tick();
            if (i + 1 < 3)
            {
                Assert.Equal(0, r.Remover.Real(ids[i + 1]));
                r.Advance(0.6);
            }
        }

        Assert.Equal(RemovalQueueState.Completed, r.Queue.State);
        Assert.False(r.Suppressor.FastBatchActive);
        Assert.Equal(FinalVerificationStatus.Pending, r.Queue.FinalVerification!.Status);
        Assert.Equal(0, r.Reader.RefreshRequests);

        r.Tick();
        Assert.Equal(FinalVerificationStatus.Requested, r.Queue.FinalVerification!.Status);
        Assert.Equal(1, r.Reader.RefreshRequests);
        r.Tick(5);
        Assert.Equal(1, r.Reader.RefreshRequests);

        r.CompleteReload();
        Assert.Equal(FinalVerificationStatus.Passed, r.Queue.FinalVerification!.Status);
        r.Tick();
        Assert.Equal("Passed", Assert.Single(r.Audit.Phase(AuditPhase.Verify)).Result);

        Assert.Equal(1, r.Reader.RefreshRequests);   // exactly one real reload, at the end
        Assert.Equal(0, r.Suppressor.PassedReloads); // none from the reply handler
        Assert.Equal(3, r.Suppressor.Suppressed);
        Assert.Equal(new FastModeStats(3, 3, 3, 0), r.Queue.FastStats);
        Assert.Equal(3, r.Audit.Phase(AuditPhase.Verdict).Count(v => v.Result == "Removed"));
        Assert.Equal(3, r.Audit.Phase(AuditPhase.Intent).Count());
        Assert.Contains("fastMode=True", Assert.Single(r.Audit.Phase(AuditPhase.BatchStart)).Detail);
        Assert.Equal(TimeSpan.FromSeconds(0.2), r.Queue.AverageItemDuration);
        r.AssertAtMostOneRealSendPerItem();

        r.Queue.Clear();
        Assert.Equal(RemovalQueueState.Idle, r.Queue.State);
    }

    [Fact]
    public void NonZeroReply_FailsItem_ReleasesLatch_Halts_FinalReload_NeverResends()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.StartFast(ids);
        r.Tick();

        Assert.False(r.FastReply(ids[0], result: 340));
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Failed, r.Item(0).Status);
        Assert.Contains("LogMessage#340", r.Item(0).Detail);
        Assert.Contains("result 340", r.Queue.HaltReason);
        Assert.Null(r.Remover.Unresolved);
        Assert.Contains(r.Audit.Phase(AuditPhase.Released), x => x.Result == "ServerRejected");
        Assert.False(r.Suppressor.FastBatchActive);
        Assert.Equal(0, r.Suppressor.PassedReloads); // the game does not reload on a rejection

        r.Tick();
        Assert.Contains(r.Audit.Phase(AuditPhase.Verdict), v => v.Result == "ServerRejected");
        Assert.Equal(1, r.Reader.RefreshRequests); // final reload issued on halt
        Assert.False(r.Queue.Resume(out var err));  // not while the final verification runs
        Assert.Contains("verification", err);

        r.CompleteReload();
        Assert.Equal(FinalVerificationStatus.Passed, r.Queue.FinalVerification!.Status);

        Assert.True(r.Queue.Resume(out err), err);
        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Failed, r.Item(0).Status); // sent + rejected: never back to Pending
        Assert.True(r.Suppressor.FastBatchActive);
        r.Advance(1);
        r.Tick();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(1).Status);
        Assert.Equal(1, r.Remover.Real(ids[0]));
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void NoReply_TimesOutIndeterminate_ThenResumeReconcilesAgainstFullReload()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.StartFast(ids);
        r.Tick();

        r.Advance(9.9);
        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        r.Advance(0.2);
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Contains("No server reply", r.Item(0).Detail);
        Assert.Equal(0ul, r.Suppressor.Armed);
        Assert.False(r.Suppressor.FastBatchActive);
        Assert.NotNull(r.Remover.Unresolved);

        r.Tick();
        Assert.Equal(1, r.Reader.RefreshRequests);
        r.CompleteReload(); // still listed in a post-send download → latch released
        Assert.Null(r.Remover.Unresolved);
        Assert.Equal(FinalVerificationStatus.Passed, r.Queue.FinalVerification!.Status);

        Assert.True(r.Queue.Resume(out var err), err);
        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Failed, r.Item(0).Status);
        Assert.Equal(1, r.Remover.Real(ids[0]));
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void ReplyForADifferentContentId_IsIgnored_AndNeverSuppresses()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.StartFast(ids);
        r.Tick();

        // A rejection for someone else: ignored, nothing changes.
        Assert.False(r.FastReply(0xDEAD, result: 340));
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(0).Status);
        Assert.Equal(ids[0], r.Suppressor.Armed);

        // A success for someone else (not in the list): not suppressed; the game starts a real download.
        Assert.False(r.FastReply(0xBEEF));
        Assert.Equal(0, r.Suppressor.Suppressed);
        Assert.Equal(1, r.Suppressor.PassedReloads);
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(0).Status);
        Assert.Contains(r.Log, l => l.Contains("Reply ignored") && l.Contains("000000000000BEEF"));

        // The real reply arrives while that download is in flight: still not suppressed (hazard rule).
        Assert.False(r.FastReply(ids[0]));
        Assert.Equal(0, r.Suppressor.Suppressed);
        Assert.Contains("reload NOT suppressed", r.Item(0).Detail);

        // The full download then decides it.
        r.CompleteReload(3);
        Assert.Equal(RemovalItemStatus.Removed, r.Item(0).Status);
        Assert.Contains("full download", r.Item(0).Detail);
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
    }

    [Fact]
    public void DownloadInFlight_BlocksSending_UntilItFinishes()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.StartFast(ids);
        r.Suppressor.IsDownloadInFlight = true; // proxy+0x3ADD != 0
        for (var i = 0; i < 5; i++)
        {
            r.Advance(1);
            r.Tick();
        }

        Assert.Empty(r.Remover.RealCalls);
        Assert.Contains("download", r.Queue.Activity);

        r.Suppressor.IsDownloadInFlight = false;
        r.Tick();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(0).Status);
        Assert.True(r.FastReply(ids[0]));

        // A game-initiated download (user opened the friend list): the reader goes Loading; wait for it.
        r.Reader.StartGameDownload();
        r.Advance(1);
        r.Tick(3);
        Assert.Equal(0, r.Remover.Real(ids[1]));
        r.CompleteReload(5);
        r.Tick();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(1).Status);
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void UnexpectedExtraMissingContentId_InLocalUpdate_HaltsIndeterminate()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.StartFast(ids);
        r.Tick();

        // Something else changed under us: Friend 5 vanished and a new friend appeared (count still -1).
        r.List.RemoveAll(e => e.ContentId == 0x1005);
        r.List.Add(Rig.Entry(0x3001, "New Friend", index: 9));
        Assert.True(r.FastReply(ids[0]));

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Contains("Friend 5", r.Item(0).Detail);
        Assert.Equal(0, r.Remover.Real(ids[1]));
    }

    [Fact]
    public void LocalUpdateWithWrongCount_HaltsIndeterminate()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.StartFast(ids);
        r.Tick();
        r.Advance(0.2);
        r.Reader.PublishLocal(r.List.Where(e => e.ContentId != ids[0]).ToList(), Rig.Me, r.RequestId, nativeCount: 6);

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Contains("no result-0 server reply", r.Item(0).Detail);
        Assert.Contains("expected 5", r.Item(0).Detail);
    }

    [Fact]
    public void FinalVerificationMismatch_IsReportedAndAudited()
    {
        using var r = new Rig();
        var ids = r.Ids(1);
        r.StartFast(ids);
        r.Tick();
        Assert.True(r.FastReply(ids[0]));
        r.Advance(0.6);
        r.Tick(); // Completed
        r.Tick(); // final RequestData

        // The full list disagrees: the "removed" friend is back, and an unrelated friend is gone.
        r.List.Add(Rig.Entry(ids[0], "Friend 1", index: 0));
        r.List.RemoveAll(e => e.ContentId == 0x1004);
        r.CompleteReload();

        var v = r.Queue.FinalVerification!;
        Assert.Equal(FinalVerificationStatus.Mismatch, v.Status);
        Assert.Contains("STILL LISTED", v.Detail);
        Assert.Contains("Friend 4", v.Detail);
        Assert.Contains("NOT in the batch", v.Detail);
        r.Tick();
        Assert.Equal("Mismatch", Assert.Single(r.Audit.Phase(AuditPhase.Verify)).Result);
        Assert.Contains(r.Log, l => l.Contains("FINAL VERIFICATION Mismatch"));
    }

    [Fact]
    public void FinalReload_IsIssuedOnCancel()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.StartFast(ids);
        r.Tick();
        r.Queue.Cancel();
        Assert.True(r.FastReply(ids[0]));
        r.Advance(1);
        r.Tick();
        Assert.Equal(RemovalQueueState.Cancelled, r.Queue.State);
        Assert.False(r.Suppressor.FastBatchActive);
        r.Tick();
        Assert.Equal(1, r.Reader.RefreshRequests);
        r.CompleteReload();
        Assert.Equal(FinalVerificationStatus.Passed, r.Queue.FinalVerification!.Status);
        Assert.Equal(0, r.Remover.Real(ids[1]));
    }

    [Fact]
    public void FinalReload_IsIssuedOnCancelWhileHalted_Once()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.StartFast(ids);
        r.Remover.Override = (t, dry) => new RemovalSendOutcome(RemovalSendResult.TargetMismatch, "renamed", false);
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        r.Queue.Cancel(); // before the final reload was even issued
        Assert.Equal(RemovalQueueState.Cancelled, r.Queue.State);
        r.Tick(3);
        Assert.Equal(1, r.Reader.RefreshRequests);
        r.CompleteReload();
        Assert.Equal(FinalVerificationStatus.Passed, r.Queue.FinalVerification!.Status);
        Assert.Equal(1, r.Reader.RefreshRequests);
    }

    [Fact]
    public void AbortOnLogout_SkipsTheFinalReload()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.StartFast(ids);
        r.Tick();
        r.Session.IsLoggedIn = false;
        r.Tick();
        Assert.Equal(RemovalQueueState.Cancelled, r.Queue.State);
        Assert.False(r.Suppressor.FastBatchActive);
        Assert.Equal(0ul, r.Suppressor.Armed);
        r.Tick();
        Assert.Equal(FinalVerificationStatus.Skipped, r.Queue.FinalVerification!.Status);
        Assert.Equal(0, r.Reader.RefreshRequests);
    }

    [Fact]
    public void FinalReloadNeverCompletes_TimesOut()
    {
        using var r = new Rig();
        var ids = r.Ids(1);
        r.StartFast(ids);
        r.Tick();
        r.FastReply(ids[0]);
        r.Advance(1);
        r.Tick(2);
        Assert.Equal(FinalVerificationStatus.Requested, r.Queue.FinalVerification!.Status);
        r.Advance(31);
        r.Tick();
        Assert.Equal(FinalVerificationStatus.TimedOut, r.Queue.FinalVerification!.Status);
    }

    [Fact]
    public void TryStart_FastModeRefusals()
    {
        using var r = new Rig();
        var ids = r.Ids(2);

        Assert.False(r.Queue.TryStart(r.Batch(true, ids) with { FastMode = true }, out var err));
        Assert.Contains("real batches only", err);

        r.Suppressor.Availability = FastModeAvailability.Unavailable("self-check pending");
        Assert.False(r.Queue.TryStart(r.Batch(false, ids) with { FastMode = true }, out err));
        Assert.Contains("self-check pending", err);
        r.Suppressor.Availability = FastModeAvailability.Available();

        r.Session.IsFriendListWindowOpen = true;
        Assert.False(r.Queue.TryStart(r.Batch(false, ids) with { FastMode = true }, out err));
        Assert.Contains("Friend List window", err);
        r.Session.IsFriendListWindowOpen = false; // (the window refuses standard batches too: FriendListWindowGuardTests)

        // A standard batch is not affected by the fast-mode refusals.
        Assert.True(r.Queue.TryStart(r.Batch(false, ids), out err), err);
        Assert.DoesNotContain(r.Audit.Phase(AuditPhase.BatchStart), b => b.Detail.Contains("fastMode=True"));
    }

    [Fact]
    public void TryStart_RefusedWhileThePreviousFinalVerificationRuns()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.StartFast(ids[0]);
        r.Tick();
        r.FastReply(ids[0]);
        r.Advance(1);
        r.Tick(2);
        Assert.Equal(RemovalQueueState.Completed, r.Queue.State);
        Assert.False(r.Queue.TryStart(r.Batch(false, ids[1]), out var err));
        Assert.Contains("verification", err);
        r.Queue.Clear();
        Assert.True(r.Queue.HasBatch); // Clear refused too
    }

    [Fact]
    public void FastModeBecomesUnavailableMidBatch_HaltsWithNothingSent()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.StartFast(ids);
        r.Tick();
        r.FastReply(ids[0]);
        r.Suppressor.Availability = FastModeAvailability.Unavailable("reply handler observed off the framework thread");
        r.Advance(1);
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Contains("off the framework thread", r.Queue.HaltReason);
        Assert.Equal(0, r.Remover.Real(ids[1]));
        r.Tick();
        r.CompleteReload();
        Assert.Equal(FinalVerificationStatus.Passed, r.Queue.FinalVerification!.Status);
        Assert.False(r.Queue.Resume(out var err));
        Assert.Contains("fast mode is unavailable", err);
    }

    [Fact]
    public void StandardBatch_NeverArmsOrActivatesSuppression()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        Assert.False(r.Suppressor.FastBatchActive);
        Assert.Equal(0ul, r.Suppressor.Armed);

        Assert.False(r.FastReply(ids[0]));
        Assert.Equal(1, r.Suppressor.PassedReloads);
        Assert.Contains("server reply result 0", r.Item(0).Detail);
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(0).Status);
        r.CompleteReload();
        Assert.Equal(RemovalItemStatus.Removed, r.Item(0).Status);
        Assert.Null(r.Queue.FinalVerification);
        Assert.Equal(new FastModeStats(0, 0, 0, 0), r.Queue.FastStats);
    }

    [Fact]
    public void SuppressionPolicy_IsAClosedListOfConditions()
    {
        const ulong t = 0x1001;
        string? Why(bool disposing = false, bool avail = true, bool active = true, ulong armed = t, ulong reply = t, uint result = 0,
            bool fw = true, bool proxy = true, byte dl = 0, byte c8 = 1, bool ready = true, bool present = true) =>
            SuppressionPolicy.WhyNotSuppress(disposing, avail, active, armed, reply, result, fw, proxy, dl, c8, ready, present);

        Assert.Null(Why());
        Assert.NotNull(Why(disposing: true));
        Assert.NotNull(Why(avail: false));
        Assert.NotNull(Why(active: false));
        Assert.NotNull(Why(armed: 0));
        Assert.NotNull(Why(reply: 0x1002));
        Assert.NotNull(Why(result: 1));
        Assert.NotNull(Why(fw: false));
        Assert.NotNull(Why(proxy: false));
        Assert.NotNull(Why(dl: 1));
        Assert.NotNull(Why(c8: 0));
        Assert.NotNull(Why(c8: 2));
        Assert.NotNull(Why(ready: false));
        Assert.NotNull(Why(present: false));
    }
}
