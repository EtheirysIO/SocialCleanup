using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SocialCleanup.Friends;
using SocialCleanup.Removal;
using Xunit;

namespace SocialCleanup.Tests;

/// <summary>Review fixes (A1, A4, A6, A7) and the reviewer's edge-case list.</summary>
public sealed class ReviewFixTests
{
    // ------------------------------------------------------------------ A1: transient refusals

    [Theory]
    [InlineData("count")]
    [InlineData("index")]
    [InlineData("requestIds")]
    public void TransientRefusal_KeepsItemPending_ThenSendsOnceTheListSettles(string kind)
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        switch (kind)
        {
            case "count": r.Remover.LiveEntryCount = 0; break;      // download in progress: EntryCount zeroed
            case "index": r.Remover.LiveIndexShift = 1; break;      // list reshuffled under us
            default: r.Remover.RequestIdsMoveBeforeCall = true; break; // counters moved between scan and call (after INTENT)
        }

        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Pending, r.Item(0).Status);
        Assert.Contains("nothing sent", r.Item(0).Detail);
        Assert.Equal(0, r.Remover.TotalNativeCalls);

        // No newer snapshot yet: it waits instead of hammering the remover.
        r.Advance(0.5);
        r.Tick(3);
        Assert.Equal(1, r.Remover.Real(ids[0]));

        // The download completes (new snapshot), the cause is gone: sent exactly once.
        r.Remover.LiveEntryCount = null;
        r.Remover.LiveIndexShift = 0;
        r.Remover.RequestIdsMoveBeforeCall = false;
        r.Republish(newRequest: true);
        r.Tick();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(0).Status);
        Assert.Equal(1, r.Remover.Native(ids[0]));
        r.ServerRemoves(ids[0]);
        Assert.Equal(RemovalItemStatus.Removed, r.Item(0).Status);
        r.AssertAtMostOneNativeCallPerItem();

        if (kind == "requestIds")
        {
            // INTENT then REFUSED with no OUTCOME between: the startup latch treats it as "nothing sent".
            var rows = r.Audit.Rows.Where(x => x.Target.ContentId == ids[0]).Select(x => x.Phase).ToList();
            Assert.Equal(new[] { AuditPhase.Intent, AuditPhase.Refused, AuditPhase.Intent, AuditPhase.Outcome }, rows.Take(4));
        }
    }

    [Fact]
    public void TransientRefusal_NeverSettles_HaltsAfterReadyWait_WithNothingSent()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Remover.LiveEntryCount = 0;
        r.Tick();

        for (var i = 0; i < 40; i++)
        {
            r.Advance(1);
            r.Tick();
        }

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Pending, r.Item(0).Status);
        Assert.Contains("Nothing sent", r.Queue.HaltReason);
        Assert.Equal(1, r.Reader.RefreshRequests); // asked once for a newer list after the refusal
        Assert.Equal(0, r.Remover.TotalNativeCalls);
    }

    [Fact]
    public void Resume_PutsNeverSentFailedItemsBackToPending()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Remover.Override = (t, dry) => new RemovalSendOutcome(RemovalSendResult.TargetMismatch, "renamed", false);
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Failed, r.Item(0).Status);

        r.Remover.Override = null;
        Assert.True(r.Queue.Resume(out var err), err);
        r.Tick();
        r.CompleteReload(1);
        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Pending, r.Item(0).Status);
        Assert.Contains("Back to Pending", r.Item(0).Detail);

        r.AfterCooldown();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(0).Status);
        Assert.Equal(1, r.Remover.Native(ids[0]));
        r.AssertAtMostOneNativeCallPerItem();
    }

    [Fact]
    public void Resume_NeverRePendsAFailedItemThatWasSent()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.Advance(31);
        r.Tick(); // Indeterminate (timeout)
        Assert.True(r.Queue.Resume(out _));
        r.Tick();
        r.CompleteReload(1); // post-send download still lists it → Failed (sent, not removed)
        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Failed, r.Item(0).Status);

        // A second halt + resume must not resurrect it.
        r.Remover.Override = (t, dry) => new RemovalSendOutcome(RemovalSendResult.TargetMismatch, "x", false);
        r.AfterCooldown();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        r.Remover.Override = null;
        Assert.True(r.Queue.Resume(out _));
        r.Tick();
        r.CompleteReload(1);
        r.Tick();
        Assert.Equal(RemovalItemStatus.Failed, r.Item(0).Status);
        Assert.Equal(RemovalItemStatus.Pending, r.Item(1).Status);
        r.AfterCooldown();
        Assert.Equal(1, r.Remover.Real(ids[0]));
        Assert.Equal(1, r.Remover.Native(ids[0]));
        r.AssertAtMostOneNativeCallPerItem();
    }

    // ------------------------------------------------------------------ A4: only the target may go missing

    [Fact]
    public void StandardVerdict_OtherFriendMissingToo_HaltsUnexpected_NamingIt()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();

        // Target gone and count -1, but only because Friend 5 vanished too and a new friend appeared.
        r.Advance(7);
        r.List.RemoveAll(e => e.ContentId == ids[0] || e.ContentId == 0x1005);
        r.List.Add(Rig.Entry(0x3001, "New Friend", index: 9));
        r.RequestId++;
        r.Reader.Publish(r.List, Rig.Me, r.RequestId);

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Contains("Friend 5", r.Item(0).Detail);
        Assert.Contains("0000000000001005", r.Queue.HaltReason);
        r.Tick();
        Assert.Contains(r.Audit.Phase(AuditPhase.Verdict), v => v.Result == "Unexpected" && v.Detail.Contains("Friend 5"));
        Assert.Equal(0, r.Remover.Real(ids[1]));
    }

    // ------------------------------------------------------------------ reviewer's list

    [Fact]
    public void SnapshotPublishedReentrantlyFromInsideTrySend_IsEvaluatedAfterTheSend()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Remover.DuringSend = t =>
        {
            r.Remover.DuringSend = null;
            r.List.RemoveAll(e => e.ContentId == t.ContentId);
            r.RequestId++;
            r.Reader.Publish(r.List, Rig.Me, r.RequestId);
        };

        r.Tick();
        Assert.Equal(RemovalItemStatus.Removed, r.Item(0).Status);
        Assert.Contains(r.Log, l => l.Contains("arrived while TrySend was running"));

        // The remover set its latch after that snapshot, so it is still set: the queue halts rather than send.
        r.AfterCooldown();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Contains("latch", r.Queue.HaltReason);
        Assert.Equal(0, r.Remover.Real(ids[1]));

        // A fresh list releases it; Resume continues without re-sending item 0.
        Assert.True(r.Queue.Resume(out _));
        r.Tick();
        r.CompleteReload(1);
        r.Tick();
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        r.AfterCooldown();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(1).Status);
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void LateVerdictSnapshotWhileHalted_ThenResume_ReconcilesWithoutNewRequest()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.Advance(31);
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);

        r.ServerRemoves(ids[0], afterSeconds: 2); // arrives late, while halted
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Null(r.Remover.Unresolved); // the remover's latch saw it

        Assert.True(r.Queue.Resume(out _));
        r.Tick();
        Assert.Equal(0, r.Reader.RefreshRequests); // the late snapshot is already newer than the halt
        Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Removed, r.Item(0).Status);
        r.AfterCooldown();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(1).Status);
        r.AssertAtMostOneRealSendPerItem();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RequestIdWrap_255To0_IsStillAPostSendDownload(bool removed)
    {
        using var r = new Rig(requestId: 255);
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        if (removed)
        {
            r.ServerRemoves(ids[0]);
            Assert.Equal(0, r.RequestId);
            Assert.Equal(RemovalItemStatus.Removed, r.Item(0).Status);
        }
        else
        {
            r.Advance(7);
            r.Republish(newRequest: true);
            Assert.Equal(0, r.RequestId);
            Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
            Assert.Contains("still present", r.Queue.HaltReason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void OtherCharacterSnapshotInFlight_HaltsIndeterminate()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.Advance(3);
        r.Reader.Publish(r.List.Skip(1).ToList(), Rig.SomeoneElse, (byte)(r.RequestId + 1));

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Contains("different character", r.Item(0).Detail);
        Assert.NotNull(r.Remover.Unresolved); // other-character data never releases the latch
    }

    [Fact]
    public void LogoutInFlight_AbortsWithIndeterminateItem()
    {
        using var r = new Rig();
        var ids = r.Ids(3);
        r.Start(false, ids);
        r.Tick();
        r.Session.IsLoggedIn = false;
        r.Tick();

        Assert.Equal(RemovalQueueState.Cancelled, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Equal(RemovalItemStatus.Cancelled, r.Item(1).Status);
        Assert.Contains(r.Audit.Phase(AuditPhase.Verdict), v => v.Result == "Indeterminate");
        Assert.Equal("Aborted", Assert.Single(r.Audit.Phase(AuditPhase.BatchEnd)).Result);
    }

    [Fact]
    public void ReadyWaitTimeout_HaltsWithNothingSent()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Reader.StartGameDownload(); // e.g. the user opened the in-game friend list; it never completes
        for (var i = 0; i < 32; i++)
        {
            r.Advance(1);
            r.Tick();
            if (i <= 30)
                Assert.Equal(RemovalQueueState.Running, r.Queue.State);
        }

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Contains("Waited 30s", r.Queue.HaltReason);
        Assert.Equal(RemovalItemStatus.Pending, r.Item(0).Status);
        Assert.Empty(r.Remover.RealCalls);
    }

    [Fact]
    public void ExceptionInSnapshotHandler_HaltsIndeterminate_NeverResends()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();

        // The remover enumerates once and the classifier once more; the queue's "only the target missing" check throws.
        r.Advance(7);
        var after = r.List.Where(e => e.ContentId != ids[0]).ToList();
        r.RequestId++;
        r.Reader.PublishRaw(new FriendListSnapshot(new ThrowingList(after, allowedEnumerations: 2), 0, Rig.Me, DateTime.UtcNow,
            (uint)after.Count, 0, 0, false, r.RequestId, (byte)(r.RequestId + 1), r.RequestId));

        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Contains("Snapshot handler exception", r.Item(0).Detail);
        r.AfterCooldown();
        Assert.Equal(0, r.Remover.Real(ids[1]));
        r.AssertAtMostOneRealSendPerItem();
    }

    [Fact]
    public void DryRunBatchWhileLatchSet_IsRefused()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Remover.Unresolved = new UnresolvedSend(RemovalTarget.From(r.List[5]), Rig.Me, 1, DateTime.UtcNow, 1, 19, 20);
        Assert.False(r.Queue.TryStart(r.Batch(true, ids), out var err));
        Assert.Contains("unresolved", err);
        Assert.Empty(r.Audit.Rows);
        Assert.Empty(r.Remover.DryCalls);
    }

    [Fact]
    public void ResumeWhileRemoverUnavailable_IsRefused()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.Advance(31);
        r.Tick();
        r.Remover.Availability = RemoverAvailability.Unavailable("sig gone");
        Assert.False(r.Queue.Resume(out var err));
        Assert.Contains("sig gone", err);
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
        Assert.False(r.Queue.IsResumePending);
    }

    [Fact]
    public void DisposeMidFlight_WritesIndeterminateVerdictAndBatchEnd()
    {
        var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();
        r.Dispose();

        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Contains(r.Audit.Phase(AuditPhase.Verdict), v => v.Result == "Indeterminate" && v.Target.ContentId == ids[0]);
        Assert.Equal("Disposed", Assert.Single(r.Audit.Phase(AuditPhase.BatchEnd)).Result);

        // Nothing happens after dispose.
        r.ServerRemoves(ids[0]);
        r.AfterCooldown();
        Assert.Equal(RemovalItemStatus.Indeterminate, r.Item(0).Status);
        Assert.Equal(0, r.Remover.Real(ids[1]));
    }

    [Fact]
    public void Timers_UseTheMonotonicClock_NotTheWallClock()
    {
        using var r = new Rig();
        var ids = r.Ids(2);
        r.Start(false, ids);
        r.Tick();

        // Wall clock jumps an hour (NTP / DST / manual change): nothing times out.
        r.Clock.UtcNow += TimeSpan.FromHours(1);
        r.Tick();
        Assert.Equal(RemovalItemStatus.AwaitingRefresh, r.Item(0).Status);

        // Wall clock jumps back: the timeout still fires on monotonic time.
        r.Clock.UtcNow -= TimeSpan.FromHours(2);
        r.Clock.Monotonic += TimeSpan.FromSeconds(31);
        r.Tick();
        Assert.Equal(RemovalQueueState.Halted, r.Queue.State);
    }

    /// <summary>An entry list whose enumerator throws after a number of enumerations.</summary>
    private sealed class ThrowingList(IReadOnlyList<FriendEntry> inner, int allowedEnumerations) : IReadOnlyList<FriendEntry>
    {
        private int enumerations;

        public int Count => inner.Count;

        public FriendEntry this[int index] => inner[index];

        public IEnumerator<FriendEntry> GetEnumerator()
        {
            if (++enumerations > allowedEnumerations)
                throw new InvalidOperationException("simulated corrupt snapshot");
            return inner.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
