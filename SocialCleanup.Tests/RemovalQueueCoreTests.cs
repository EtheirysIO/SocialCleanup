using System;
using System.Linq;
using SocialCleanup.Removal;
using Xunit;

namespace SocialCleanup.Tests;

public sealed class RemovalQueueCoreTests
{
    // ------------------------------------------------------------------ happy path

    [Fact]
    public void HappyPath_ThreeItems_OneSendEach_WaitsForReloadAndCooldown()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Start(false, ids);

        for (var i = 0; i < 3; i++)
        {
            r.Tick();
            Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(i).Status);
            Assert.Equal(1, r.Remover.Real(ids[i]));
            Assert.Equal(ids[i], r.Queue.InFlight?.Target.ContentId);

            // While waiting for the reload nothing else is sent.
            r.Advance(3);
            r.Tick(5);
            Assert.Equal(1, r.Remover.Real(ids[i]));
            if (i + 1 < 3)
                Assert.Equal(0, r.Remover.Real(ids[i + 1]));

            r.ServerRemoves(ids[i], afterSeconds: 4.4);
            Assert.Equal(RemovalItemStatus.Removed, r.Item(i).Status);
            Assert.Null(r.Queue.InFlight);

            // Cooldown: the next item is not sent on the very next tick.
            r.Tick();
            if (i + 1 < 3)
            {
                Assert.Equal(RemovalItemStatus.Pending, r.Item(i + 1).Status);
                Assert.Equal(0, r.Remover.Real(ids[i + 1]));
                r.Advance(1.1);
            }
        }

        r.Tick();
        Assert.Equal(RemovalQueueState.Completed, r.Queue.State);
        Assert.Null(r.Queue.HaltReason);
        var c = r.Queue.Counts;
        Assert.Equal(3, c.Removed);
        Assert.Equal(3, c.Done);
        Assert.All(ids, id => Assert.Equal(1, r.Remover.Real(id)));
        Assert.All(ids, id => Assert.Equal(1, r.Remover.Native(id)));
        Assert.Equal(TimeSpan.FromSeconds(7.4), r.Queue.AverageItemDuration);
        Assert.All(r.Queue.Items, it => Assert.Equal(TimeSpan.FromSeconds(7.4), it.ElapsedAt(r.Clock.UtcNow)));

        Assert.Single(r.Audit.Phase(AuditPhase.BatchStart));
        Assert.Equal(3, r.Audit.Phase(AuditPhase.Intent).Count());
        Assert.Equal(3, r.Audit.Phase(AuditPhase.Verdict).Count(v => v.Result == "Removed"));
        var end = Assert.Single(r.Audit.Phase(AuditPhase.BatchEnd));
        Assert.Equal("Completed", end.Result);
        Assert.Contains("removed=3", end.Detail);
        Assert.Equal(0ul, end.Target.ContentId);
        r.AssertAtMostOneRealSendPerItem();

        r.Queue.Clear();
        Assert.Equal(RemovalQueueState.Idle, r.Queue.State);
        Assert.False(r.Queue.HasBatch);
    }

    [Fact]
    public void Eta_UsesFallbackThenMeasuredAverage()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Start(false, ids);
        // 3 pending × (7.5 fallback + 1.0 cooldown)
        Assert.Equal(TimeSpan.FromSeconds(25.5), r.Queue.EstimatedRemaining);
        r.Tick();
        r.ServerRemoves(ids[0], afterSeconds: 6.0);
        Assert.Equal(TimeSpan.FromSeconds(6), r.Queue.AverageItemDuration);
        // 2 pending × (6 + 1) + cooldown left (1 s)
        Assert.Equal(TimeSpan.FromSeconds(15), r.Queue.EstimatedRemaining);
    }

    // ------------------------------------------------------------------ classification

    [Fact]
    public void PreSendSnapshotArrivingAfterSend_DoesNotProduceAVerdict()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();

        r.Advance(0.5);
        r.Republish(newRequest: false); // same request id: requested before the send, still lists the target
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(0).Status);
        Assert.Contains("pre-send", r.Item(0).Detail);
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);

        r.Advance(2);
        r.Tick(3);
        Assert.Equal(0, r.Remover.Real(ids[1]));

        r.ServerRemoves(ids[0]);
        Assert.Equal(RemovalItemStatus.Removed, r.Item(0).Status);
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void StillPresent_Halts_AndNothingElseIsSent()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Start(false, ids);
        r.Tick();

        r.Advance(7);
        r.Republish(newRequest: true); // post-send download, target still there
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Contains("still present", r.Queue.HaltReason, StringComparison.OrdinalIgnoreCase);

        // Rows produced inside the snapshot callback are written on the next tick.
        Assert.DoesNotContain(r.Audit.Phase(AuditPhase.Verdict), v => v.Result == "StillPresent");
        r.Tick();
        Assert.Contains(r.Audit.Phase(AuditPhase.Verdict), v => v.Result == "StillPresent");

        for (var i = 0; i < 10; i++)
            r.AfterCooldown();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(0, r.Remover.Real(ids[1]));
        Assert.Equal(1, r.Remover.Real(ids[0]));
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void UnexpectedCountDelta_Halts()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();

        r.Advance(7);
        r.List.RemoveAll(e => e.ContentId == ids[0]);
        r.RequestId++;
        r.Reader.Publish(r.List, Rig.Me, r.RequestId, nativeCount: (uint)r.List.Count - 1); // two fewer than before

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Contains("Unexpected", r.Item(0).Detail);
        Assert.Equal(0, r.Remover.Real(ids[1]));
    }

    [Fact]
    public void NativeReturnedZero_HaltsAsIndeterminate()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Remover.NativeReturn = 0;
        r.Start(false, ids);
        r.Tick();

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Contains("NotQueued", r.Item(0).Detail);
        Assert.Contains(r.Audit.Phase(AuditPhase.Verdict), v => v.Result == "NotQueued");
        r.AfterCooldown();
        Assert.Equal(0, r.Remover.Real(ids[1]));
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void ReaderInvalidWhileInFlight_HaltsAsIndeterminate()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.Reader.State = Friends.FriendListState.Invalid;
        r.Reader.StateReason = "boom";
        r.Tick();

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Contains("Invalid", r.Queue.HaltReason);
    }

    [Fact]
    public void ReaderInvalidBeforeSend_HaltsWithNothingSent()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Reader.State = Friends.FriendListState.Invalid;
        r.Tick();

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Pending, r.Item(0).Status);
        Assert.Equal(0, r.Remover.Real(ids[0]));
    }

    [Fact]
    public void RemoverRefusal_HaltsAsFailed()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Remover.Override = (t, dry) => new RemovalSendOutcome(RemovalSendResult.TargetMismatch, "name changed", false);
        r.Tick();

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Failed, r.Item(0).Status);
        Assert.Contains("TargetMismatch", r.Queue.HaltReason);
        Assert.Equal(0, r.Remover.TotalNativeCalls);
    }

    // ------------------------------------------------------------------ timeout + resume

    [Fact]
    public void Timeout_HaltsIndeterminate_ResumeReconcilesAbsentAsRemoved_WithoutResending()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Start(false, ids);
        r.Tick();

        r.Advance(30.5);
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Contains("timed out", r.Queue.HaltReason);
        Assert.Contains(r.Audit.Phase(AuditPhase.Verdict), v => v.Result == "Indeterminate");

        Assert.True(r.Queue.Resume(out var err), err);
        Assert.True(r.Queue.IsResumePending);
        r.Tick();
        Assert.Equal(1, r.Reader.RefreshRequests); // asked for a fresh list
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.False(r.Queue.Resume(out _)); // already pending

        r.ServerRemoves(ids[0], afterSeconds: 1); // the fresh post-halt list lacks it (latch released by it too)
        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Removed, r.Item(0).Status);
        Assert.Contains("reconciled", r.Item(0).Detail);
        Assert.Contains(r.Audit.Phase(AuditPhase.Verdict), v => v.Result == "Removed" && v.Detail.Contains("reconciled"));

        r.AfterCooldown();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(1).Status);
        r.ServerRemoves(ids[1]);
        r.AfterCooldown();
        r.ServerRemoves(ids[2]);
        r.Tick();
        Assert.Equal(RemovalQueueState.Completed, r.Queue.State);
        Assert.All(ids, id => Assert.Equal(1, r.Remover.Real(id)));
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void Resume_TargetStillPresent_MarksFailed_DoesNotResend_ContinuesWithNext()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.Advance(31);
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);

        Assert.True(r.Queue.Resume(out _));
        r.Tick();
        r.Advance(2);
        r.Republish(newRequest: true); // post-halt, post-send download: still a friend
        r.Tick();

        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Failed, r.Item(0).Status);
        Assert.Contains("NOT re-sent", r.Item(0).Detail);

        r.AfterCooldown();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(1).Status);
        Assert.Equal(1, r.Remover.Real(ids[0]));
        Assert.Equal(1, r.Remover.Real(ids[1]));
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void Resume_PreSendDataStillListsTarget_LatchStillSet_StaysHalted()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.Advance(31);
        r.Tick();

        Assert.True(r.Queue.Resume(out _));
        r.Tick();
        r.Republish(newRequest: false); // newer generation, same request id: may be pre-send data; remover keeps its latch
        r.Tick();

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.False(r.Queue.IsResumePending);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Contains("latch", r.Queue.HaltReason);
        Assert.NotNull(r.Remover.Unresolved);

        // A later resume with a genuinely post-send download settles it, still without re-sending.
        Assert.True(r.Queue.Resume(out _));
        r.Republish(newRequest: true);
        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Failed, r.Item(0).Status);
        r.AfterCooldown();
        Assert.Equal(1, r.Remover.Real(ids[0]));
        Assert.Equal(1, r.Remover.Real(ids[1]));
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void Resume_NoFreshSnapshot_TimesOutAndStaysHalted()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.Advance(31);
        r.Tick();
        r.Reader.AcceptRefresh = false;

        Assert.True(r.Queue.Resume(out _));
        r.Tick();
        r.Advance(31);
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.False(r.Queue.IsResumePending);
        Assert.Contains("no fresh friend-list snapshot", r.Queue.HaltReason);
        Assert.Equal(0, r.Remover.Real(ids[1]));
    }

    [Fact]
    public void TrySendThrows_Indeterminate_NotResentAfterResume()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Remover.Override = (t, dry) => throw new InvalidOperationException("kaboom");
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);

        r.Remover.Override = null;
        Assert.True(r.Queue.Resume(out _));
        r.Tick();
        r.Republish(newRequest: true); // still listed; unknown send parameters → cannot be reconciled
        r.Tick();

        // Fix A6: a possibly-sent item that cannot be reconciled keeps the queue Halted.
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Contains("cannot be reconciled", r.Queue.HaltReason);
        r.AfterCooldown();
        Assert.Equal(0, r.Remover.Real(ids[1]));

        // Once a post-halt snapshot shows it absent it reconciles as Removed and the queue continues — still no re-send.
        Assert.True(r.Queue.Resume(out _));
        r.Tick();
        r.ServerRemoves(ids[0], afterSeconds: 1);
        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Removed, r.Item(0).Status);
        r.AfterCooldown();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(1).Status);
        Assert.Equal(1, r.Remover.Real(ids[0]));
        r.AssertAtMostOneRealSendPerItem();
    }

    // ------------------------------------------------------------------ cancel / abort

    [Fact]
    public void CancelMidFlight_LetsItemSettle_ThenCancelsRest()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Start(false, ids);
        r.Tick();

        r.Queue.Cancel();
        Assert.True(r.Queue.IsCancelRequested);
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        r.Advance(2);
        r.Tick(3);
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(0).Status);

        r.ServerRemoves(ids[0]);
        Assert.Equal(RemovalItemStatus.Removed, r.Item(0).Status);
        r.AfterCooldown();

        Assert.Equal(RemovalQueueState.Cancelled, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Cancelled, r.Item(1).Status);
        Assert.Equal(RemovalItemStatus.Cancelled, r.Item(2).Status);
        Assert.Equal(0, r.Remover.Real(ids[1]));
        Assert.Equal(0, r.Remover.Real(ids[2]));
        Assert.Equal("Cancelled", Assert.Single(r.Audit.Phase(AuditPhase.BatchEnd)).Result);
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void CancelMidFlight_ThenTimeout_EndsCancelledNotHalted()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.Queue.Cancel();
        r.Advance(31);
        r.Tick();

        Assert.Equal(RemovalQueueState.Cancelled, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Equal(RemovalItemStatus.Cancelled, r.Item(1).Status);
    }

    [Fact]
    public void CancelWhileHalted_Cancels()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.Advance(31);
        r.Tick();
        r.Queue.Clear(); // no-op while halted
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);

        r.Queue.Cancel();
        Assert.Equal(RemovalQueueState.Cancelled, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Cancelled, r.Item(1).Status);
    }

    [Fact]
    public void CharacterChangeMidBatch_AbortsBatch()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Start(false, ids);
        r.Tick();

        r.Session.LocalContentId = Rig.SomeoneElse;
        r.Tick();

        Assert.Equal(RemovalQueueState.Cancelled, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Equal(RemovalItemStatus.Cancelled, r.Item(1).Status);
        Assert.Equal(RemovalItemStatus.Cancelled, r.Item(2).Status);
        Assert.Contains("character changed", r.Queue.HaltReason);
        Assert.Equal("Aborted", Assert.Single(r.Audit.Phase(AuditPhase.BatchEnd)).Result);

        r.Session.LocalContentId = Rig.Me;
        r.AfterCooldown();
        Assert.Equal(0, r.Remover.Real(ids[1]));
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void LogoutBetweenItems_AbortsBatch_EvenIfLoggedBackIn()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Start(false, ids);
        r.Tick();
        r.ServerRemoves(ids[0]);

        r.Session.LogoutCount++; // logged out and back in between two ticks
        r.AfterCooldown();

        Assert.Equal(RemovalQueueState.Cancelled, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Removed, r.Item(0).Status);
        Assert.Equal(RemovalItemStatus.Cancelled, r.Item(1).Status);
        Assert.Contains("logged out", r.Queue.HaltReason);
        Assert.Equal(0, r.Remover.Real(ids[1]));
    }

    // ------------------------------------------------------------------ zoning

    [Fact]
    public void Zoning_DoesNotSend_AndDoesNotHalt()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Session.IsBetweenAreas = true;
        for (var i = 0; i < 10; i++)
        {
            r.Advance(10);
            r.Tick();
        }

        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Equal(0, r.Remover.Real(ids[0]));
        Assert.Contains("zoning", r.Queue.Activity);

        r.Session.IsBetweenAreas = false;
        r.Tick();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(0).Status);
    }

    [Fact]
    public void ZoningWhileInFlight_KeepsWaiting_TimeoutStillApplies()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.Session.IsBetweenAreas = true;
        r.Advance(10);
        r.Tick();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(0).Status);
        r.Advance(21);
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
    }

    // ------------------------------------------------------------------ already absent / audit

    [Fact]
    public void AlreadyAbsent_Continues()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Start(false, ids);

        // Removed elsewhere (e.g. in game) after confirmation: a game-initiated refresh lacks it.
        r.List.RemoveAll(e => e.ContentId == ids[0]);
        r.Republish(newRequest: true);

        r.Tick();
        Assert.Equal(RemovalItemStatus.AlreadyAbsent, r.Item(0).Status);
        Assert.Equal(1, r.Remover.Real(ids[0]));   // asked once...
        Assert.Equal(0, r.Remover.Native(ids[0])); // ...nothing sent
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Contains(r.Audit.Phase(AuditPhase.Verdict), v => v.Result == "AlreadyAbsent");

        r.AfterCooldown();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(1).Status);
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void NotInLiveListButSnapshotStillListsTarget_Halts()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Remover.Override = (t, dry) => new RemovalSendOutcome(RemovalSendResult.NotInLiveList, "no live row", false);
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Failed, r.Item(0).Status);
    }

    [Fact]
    public void AuditIntentFailure_HaltsWithNothingSent()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Audit.FailPhases.Add(AuditPhase.Intent);
        r.Start(false, ids);
        r.Tick();

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Failed, r.Item(0).Status);
        Assert.Contains("INTENT", r.Queue.HaltReason);
        Assert.Equal(0, r.Remover.TotalNativeCalls);
        Assert.Null(r.Remover.Unresolved);

        for (var i = 0; i < 5; i++)
            r.AfterCooldown();
        Assert.Equal(0, r.Remover.Real(ids[1]));
        Assert.Equal(0, r.Remover.TotalNativeCalls);
    }

    [Fact]
    public void AuditVerdictFailure_HaltsAfterRemoval()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Audit.FailPhases.Add(AuditPhase.Verdict);
        r.Start(false, ids);
        r.Tick();
        r.ServerRemoves(ids[0]);
        Assert.Equal(RemovalItemStatus.Removed, r.Item(0).Status);

        // The VERDICT row is written (and fails) on the next tick, before anything else can be sent.
        r.Advance(5);
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Contains("VERDICT", r.Queue.HaltReason);
        Assert.Equal(0, r.Remover.Real(ids[1]));
        r.AfterCooldown();
        Assert.Equal(0, r.Remover.Real(ids[1]));
    }

    // ------------------------------------------------------------------ dry run

    [Fact]
    public void DryRunBatch_SendsNothing_AndPacesItems()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Start(true, ids);

        r.Tick();
        Assert.Equal(RemovalItemStatus.DryRunOk, r.Item(0).Status);
        r.Tick();
        Assert.Equal(RemovalItemStatus.Pending, r.Item(1).Status); // small delay between items
        for (var i = 0; i < 6; i++)
        {
            r.Advance(0.5);
            r.Tick();
        }

        Assert.Equal(RemovalQueueState.Completed, r.Queue.State);
        Assert.Equal(3, r.Queue.Counts.DryRunOk);
        Assert.Empty(r.Remover.RealCalls);
        Assert.Equal(0, r.Remover.TotalNativeCalls);
        Assert.Empty(r.Audit.Phase(AuditPhase.Intent));
        Assert.All(ids, id => Assert.Equal(1, r.Remover.DryCalls[id]));
        Assert.Equal(0, r.Reader.RefreshRequests);
        Assert.All(r.Audit.Rows, row => Assert.True(row.DryRun));
    }

    [Fact]
    public void DryRunBatch_HaltsOnFirstRefusal()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Start(true, ids);
        r.Remover.Override = (t, dry) => t.ContentId == ids[1]
            ? new RemovalSendOutcome(RemovalSendResult.TargetMismatch, "renamed", false)
            : null;

        for (var i = 0; i < 6; i++)
        {
            r.Advance(0.5);
            r.Tick();
        }

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.DryRunOk, r.Item(0).Status);
        Assert.Equal(RemovalItemStatus.Failed, r.Item(1).Status);
        Assert.Equal(RemovalItemStatus.Pending, r.Item(2).Status);
        Assert.Equal(0, r.Remover.TotalNativeCalls);
    }

    // ------------------------------------------------------------------ TryStart validation

    [Fact]
    public void TryStart_RejectsWhileRunningOrHalted()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(true, ids);
        Assert.False(r.Queue.TryStart(r.Batch(true, ids), out var err));
        Assert.Contains("Running", err);

        r.Remover.Override = (t, dry) => new RemovalSendOutcome(RemovalSendResult.TargetMismatch, "x", false);
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.False(r.Queue.TryStart(r.Batch(true, ids), out err));
        Assert.Contains("Halted", err);
    }

    [Fact]
    public void TryStart_RejectsOtherCharacter()
    {
        using var r = new Rig();
        var b = r.Batch(false, r.Ids(1)) with { LocalContentId = Rig.SomeoneElse };
        Assert.False(r.Queue.TryStart(b, out var err));
        Assert.Contains("character", err);

        r.Session.LocalContentId = Rig.SomeoneElse;
        Assert.False(r.Queue.TryStart(r.Batch(false, r.Ids(1)), out err));
        Assert.Contains("logged in", err);
    }

    [Fact]
    public void TryStart_RejectsStaleGeneration()
    {
        using var r = new Rig();
        var b = r.Batch(false, r.Ids(2));
        r.Republish(newRequest: true);
        Assert.False(r.Queue.TryStart(b, out var err));
        Assert.Contains("changed since confirmation", err);
        Assert.Equal(RemovalQueueState.Idle, r.Queue.State);
    }

    [Fact]
    public void TryStart_RejectsZeroAndDuplicateContentIds()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        var dup = r.Batch(false, ids[0], ids[1], ids[0]);
        Assert.False(r.Queue.TryStart(dup, out var err));
        Assert.Contains("more than once", err);

        var zero = r.Batch(false, ids) with { Targets = new[] { new RemovalTarget(0, "Nobody", 73, "Adamantoise") } };
        Assert.False(r.Queue.TryStart(zero, out err));
        Assert.Contains("ContentId 0", err);

        var empty = r.Batch(false);
        Assert.False(r.Queue.TryStart(empty, out err));
    }

    [Fact]
    public void TryStart_RejectsRemoverUnavailableOrLatched()
    {
        using var r = new Rig();
        var ids = r.Ids(1);
        r.Remover.Availability = RemoverAvailability.Unavailable("sig not found");
        Assert.False(r.Queue.TryStart(r.Batch(false, ids), out var err));
        Assert.Contains("sig not found", err);

        r.Remover.Availability = RemoverAvailability.Available();
        r.Remover.StartupBlockReason = "unresolved INTENT";
        Assert.False(r.Queue.TryStart(r.Batch(false, ids), out err));
        Assert.Contains("startup", err);

        r.Remover.StartupBlockReason = null;
        r.Remover.Unresolved = new UnresolvedSend(new RemovalTarget(0x1001, "Friend 1", 73, "Adamantoise"), Rig.Me, 1, DateTime.UtcNow, 1, 19, 20);
        Assert.False(r.Queue.TryStart(r.Batch(false, ids), out err));
        Assert.Contains("unresolved", err);
    }

    [Fact]
    public void TryStart_RejectsPendingRequests_NamingThem()
    {
        using var r = new Rig(3, new[]
        {
            Rig.Entry(0x2001, "Outgoing Person", requestState: 2),
            Rig.Entry(0x2002, "Incoming Person", requestState: 3),
        });
        Assert.False(r.Queue.TryStart(r.Batch(false, 0x1001, 0x2001, 0x2002), out var err));
        Assert.Contains("Outgoing Person", err);
        Assert.Contains("Incoming Person", err);
        Assert.Contains("pending friend request", err);
        Assert.Equal(0, r.Remover.TotalNativeCalls);
        Assert.Empty(r.Audit.Rows);

        // Excluding them (as the confirmation dialog does) works.
        Assert.True(r.Queue.TryStart(r.Batch(false, 0x1001), out err), err);
    }

    [Fact]
    public void TryStart_RejectsReaderNotReady_ExternalBusy_AndBatchStartAuditFailure()
    {
        using var r = new Rig();
        var ids = r.Ids(1);

        r.Reader.State = Friends.FriendListState.Loading;
        Assert.False(r.Queue.TryStart(r.Batch(false, ids), out var err));
        Assert.Contains("Loading", err);
        r.Reader.State = Friends.FriendListState.Ready;

        r.ExternalBusy = "A single-friend removal test is in flight.";
        Assert.False(r.Queue.TryStart(r.Batch(false, ids), out err));
        Assert.Contains("single-friend", err);
        r.ExternalBusy = null;

        r.Audit.FailPhases.Add(AuditPhase.BatchStart);
        Assert.False(r.Queue.TryStart(r.Batch(false, ids), out err));
        Assert.Contains("BATCH_START", err);
        Assert.Equal(RemovalQueueState.Idle, r.Queue.State);
        r.Tick();
        Assert.Empty(r.Remover.RealCalls);
    }

    [Fact]
    public void TryStart_AllowedAgainAfterCompletion()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(true, ids[0]);
        r.Tick();
        r.Tick();
        Assert.Equal(RemovalQueueState.Completed, r.Queue.State);
        r.Start(true, ids[1]);
    }

    // ------------------------------------------------------------------ shared classifier

    [Fact]
    public void SendClassifier_MatchesSingleTestRules()
    {
        var e = Rig.Entry(0x1001, "A");
        var other = Rig.Entry(0x1002, "B");
        Friends.FriendListSnapshot Snap(long gen, byte req, uint native, params Friends.FriendEntry[] entries) =>
            new(entries, gen, Rig.Me, DateTime.UtcNow, native, 0, 0, false, req, (byte)(req + 1), req);

        Assert.Equal(SnapshotVerdict.Ignore, SendClassifier.Evaluate(Snap(1, 19, 2, e, other), 0x1001, Rig.Me, 1, 19, 2).Verdict);
        Assert.Equal(SnapshotVerdict.KeepWaiting, SendClassifier.Evaluate(Snap(2, 19, 2, e, other), 0x1001, Rig.Me, 1, 19, 2).Verdict);
        Assert.Equal(SnapshotVerdict.StillPresent, SendClassifier.Evaluate(Snap(2, 20, 2, e, other), 0x1001, Rig.Me, 1, 19, 2).Verdict);
        Assert.Equal(SnapshotVerdict.Removed, SendClassifier.Evaluate(Snap(2, 20, 1, other), 0x1001, Rig.Me, 1, 19, 2).Verdict);
        Assert.Equal(SnapshotVerdict.Removed, SendClassifier.Evaluate(Snap(2, 19, 1, other), 0x1001, Rig.Me, 1, 19, 2).Verdict);
        Assert.Equal(SnapshotVerdict.Unexpected, SendClassifier.Evaluate(Snap(2, 20, 0, other), 0x1001, Rig.Me, 1, 19, 2).Verdict);
        var foreign = Snap(2, 20, 1, other) with { LocalContentId = Rig.SomeoneElse };
        Assert.Equal(SnapshotVerdict.Unexpected, SendClassifier.Evaluate(foreign, 0x1001, Rig.Me, 1, 19, 2).Verdict);
        var unstable = Snap(2, 20, 2, e, other) with { CurrentRequestIdAfterEnd = 21 };
        Assert.Equal(SnapshotVerdict.KeepWaiting, SendClassifier.Evaluate(unstable, 0x1001, Rig.Me, 1, 19, 2).Verdict);
    }
}
