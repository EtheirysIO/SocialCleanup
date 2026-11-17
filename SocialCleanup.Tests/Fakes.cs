using System;
using System.Collections.Generic;
using System.Linq;
using SocialCleanup.Friends;
using SocialCleanup.Removal;

namespace SocialCleanup.Tests;

/// <summary>Wall clock and monotonic clock advance together (tests can also skew the wall clock alone).</summary>
public sealed class FakeClock : IQueueClock
{
    public DateTime UtcNow { get; set; } = new(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);

    public TimeSpan Monotonic { get; set; } = TimeSpan.FromHours(5);

    public void Advance(TimeSpan by)
    {
        UtcNow += by;
        Monotonic += by;
    }
}

public sealed class FakeSession : IGameSession
{
    public bool IsLoggedIn { get; set; } = true;

    public ulong LocalContentId { get; set; }

    public bool IsBetweenAreas { get; set; }

    public int LogoutCount { get; set; }

    public bool IsFriendListWindowOpen { get; set; }
}

public sealed record AuditRow(ulong Local, RemovalTarget Target, bool DryRun, string Phase, string Result, string Detail);

public sealed class FakeAudit : IRemovalAudit
{
    public List<AuditRow> Rows { get; } = new();

    /// <summary>Writes of this phase fail.</summary>
    public HashSet<string> FailPhases { get; } = new();

    public string? LastError { get; private set; }

    public bool TryWrite(ulong localContentId, RemovalTarget target, bool dryRun, string phase, string result, string detail)
    {
        if (FailPhases.Contains(phase))
        {
            LastError = $"simulated {phase} write failure";
            return false;
        }

        LastError = null;
        Rows.Add(new AuditRow(localContentId, target, dryRun, phase, result, detail));
        return true;
    }

    public IEnumerable<AuditRow> Phase(string phase) => Rows.Where(r => r.Phase == phase);
}

public sealed class FakeReader : IFriendListReader
{
    private long generation;

    public FriendListState State { get; set; } = FriendListState.Unloaded;

    public string? StateReason { get; set; }

    public FriendListSnapshot? Snapshot { get; private set; }

    public IReadOnlyList<FriendEntry> Entries => Snapshot?.Entries ?? Array.Empty<FriendEntry>();

    public long Generation => Snapshot?.Generation ?? 0;

    public ulong SnapshotLocalContentId => Snapshot?.LocalContentId ?? 0;

    public bool IsCompletionHookActive { get; set; } = true;

    /// <summary>RequestRefresh calls that were accepted (our own RequestData).</summary>
    public int RefreshRequests { get; private set; }

    public bool AcceptRefresh { get; set; } = true;

    public event Action<FriendListSnapshot>? SnapshotCompleted;

    public event Action<FriendListState>? StateChanged;

    public bool RequestRefresh()
    {
        if (!AcceptRefresh || State == FriendListState.Loading)
            return false;
        RefreshRequests++;
        State = FriendListState.Loading;
        StateChanged?.Invoke(State);
        return true;
    }

    /// <summary>A game-initiated download started (seen through the RequestData hook).</summary>
    public void StartGameDownload()
    {
        State = FriendListState.Loading;
        StateChanged?.Invoke(State);
    }

    /// <summary>Publish a complete snapshot (Ready) and raise SnapshotCompleted, like EndRequest does.</summary>
    public FriendListSnapshot Publish(IReadOnlyList<FriendEntry> entries, ulong owner, byte requestId, uint? nativeCount = null, bool stableRequestId = true) =>
        PublishRaw(new FriendListSnapshot(
            entries.ToList().AsReadOnly(), generation + 1, owner, DateTime.UtcNow, nativeCount ?? (uint)entries.Count, 0, 0, false,
            CurrentRequestId: requestId, NextRequestId: (byte)(requestId + 1),
            CurrentRequestIdAfterEnd: stableRequestId ? requestId : (byte)(requestId + 1)));

    /// <summary>Fast mode: the local snapshot published after a suppressed reload (same request id, Source = LocalAfterRemoval).</summary>
    public FriendListSnapshot PublishLocal(IReadOnlyList<FriendEntry> entries, ulong owner, byte requestId, uint? nativeCount = null) =>
        PublishRaw(new FriendListSnapshot(
            entries.ToList().AsReadOnly(), generation + 1, owner, DateTime.UtcNow, nativeCount ?? (uint)entries.Count, 0, 0, false,
            CurrentRequestId: requestId, NextRequestId: (byte)(requestId + 1), CurrentRequestIdAfterEnd: requestId)
        {
            Source = SnapshotSource.LocalAfterRemoval,
        });

    /// <summary>Publish exactly this snapshot (its Generation is replaced by the next one).</summary>
    public FriendListSnapshot PublishRaw(FriendListSnapshot snap)
    {
        snap = snap with { Generation = ++generation };
        Snapshot = snap;
        State = FriendListState.Ready;
        SnapshotCompleted?.Invoke(snap);
        return snap;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Models the DirectFriendRemover contract: re-validates against the reader's snapshot and the (simulated) live list,
/// writes INTENT before a real "native call", sets the in-flight latch after it, and releases the latch with the same
/// classification rule (or the narrow server-rejection path).
/// </summary>
public sealed class FakeRemover : IFriendRemover
{
    private readonly FakeReader reader;
    private readonly FakeAudit audit;
    private readonly Func<ulong> localId;

    public FakeRemover(FakeReader reader, FakeAudit audit, Func<ulong> localId)
    {
        this.reader = reader;
        this.audit = audit;
        this.localId = localId;
        reader.SnapshotCompleted += OnSnapshot;
    }

    public RemoverAvailability Availability { get; set; } = RemoverAvailability.Available();

    public UnresolvedSend? Unresolved { get; set; }

    public string? StartupBlockReason { get; set; }

    public bool HasUnresolvedSend => Unresolved != null || StartupBlockReason != null;

    /// <summary>Optional per-call override: return non-null to replace the default behaviour.</summary>
    public Func<RemovalTarget, bool, RemovalSendOutcome?>? Override { get; set; }

    /// <summary>Runs inside TrySend right before the "native call" (e.g. to publish a snapshot re-entrantly).</summary>
    public Action<RemovalTarget>? DuringSend { get; set; }

    /// <summary>Native return value for real sends (1 = queued).</summary>
    public byte NativeReturn { get; set; } = 1;

    /// <summary>Simulated live EntryCount; differs from the snapshot while a download is in progress (null = same as snapshot).</summary>
    public uint? LiveEntryCount { get; set; }

    /// <summary>Simulated shift of the live raw index relative to the snapshot (list reshuffled by a reload).</summary>
    public int LiveIndexShift { get; set; }

    /// <summary>Simulated request-id change between the scan and the call (after INTENT): transient refusal.</summary>
    public bool RequestIdsMoveBeforeCall { get; set; }

    public Dictionary<ulong, int> RealCalls { get; } = new();

    public Dictionary<ulong, int> DryCalls { get; } = new();

    public Dictionary<ulong, int> NativeCalls { get; } = new();

    public int TotalNativeCalls => NativeCalls.Values.Sum();

    public int Releases { get; private set; }

    public int Real(ulong cid) => RealCalls.TryGetValue(cid, out var n) ? n : 0;

    public int Native(ulong cid) => NativeCalls.TryGetValue(cid, out var n) ? n : 0;

    public RemovalSendOutcome TrySend(RemovalTarget target, bool dryRun) => TrySend(target, dryRun, out _);

    public RemovalSendOutcome TrySend(RemovalTarget target, bool dryRun, out SendDiagnostics diag)
    {
        var calls = dryRun ? DryCalls : RealCalls;
        calls[target.ContentId] = (calls.TryGetValue(target.ContentId, out var c) ? c : 0) + 1;
        diag = SendDiagnostics.Empty(dryRun);

        if (Override?.Invoke(target, dryRun) is { } forced)
            return forced;

        if (!Availability.IsAvailable)
            return Refuse(RemovalSendResult.Unavailable, Availability.Reason);
        if (Unresolved != null)
            return Refuse(RemovalSendResult.Unavailable, "A previous send is unresolved.");
        if (StartupBlockReason != null)
            return Refuse(RemovalSendResult.Unavailable, "Startup block.");
        if (reader.State != FriendListState.Ready || reader.Snapshot is not { } snap)
            return Refuse(RemovalSendResult.Unavailable, $"Reader is {reader.State}, not Ready.", transient: reader.State == FriendListState.Loading);
        var entry = snap.Entries.FirstOrDefault(e => e.ContentId == target.ContentId);
        if (entry == null)
            return Refuse(RemovalSendResult.NotInLiveList, "Target is not in the current friend-list snapshot.");
        if (LiveEntryCount is { } live && live != snap.NativeEntryCount)
            return Refuse(RemovalSendResult.Unavailable, $"Live EntryCount {live} != snapshot NativeEntryCount {snap.NativeEntryCount} (list download in progress).", transient: true);
        if (LiveIndexShift != 0)
            return Refuse(RemovalSendResult.TargetMismatch, $"Live raw index {entry.ListIndex + LiveIndexShift} != snapshot raw index {entry.ListIndex} (list shifted).", transient: true);
        if (entry.Name != target.Name || entry.HomeWorldId != target.HomeWorldId)
            return Refuse(RemovalSendResult.TargetMismatch, "Name/world mismatch.");
        if (entry.RequestState != 1)
            return Refuse(RemovalSendResult.Failed, $"Request state {entry.RequestState}.");

        diag = diag with
        {
            LocalContentId = localId(),
            SnapshotGeneration = snap.Generation,
            EntryCountBefore = snap.NativeEntryCount,
            CurrentRequestIdBefore = snap.CurrentRequestId,
            NextRequestIdBefore = (byte)(snap.CurrentRequestId + 1),
            RawIndex = entry.ListIndex,
        };

        if (dryRun)
        {
            audit.TryWrite(localId(), target, true, AuditPhase.DryRun, "DryRunResolved", "ok");
            return new RemovalSendOutcome(RemovalSendResult.DryRunResolved, $"index={entry.ListIndex} (nothing sent)", false);
        }

        if (!audit.TryWrite(localId(), target, false, AuditPhase.Intent, "PENDING", "intent"))
            return Refuse(RemovalSendResult.Failed, $"Audit INTENT row could not be written ({audit.LastError}); nothing sent.");

        if (RequestIdsMoveBeforeCall)
            return Refuse(RemovalSendResult.Failed, "Friend proxy / CharData / EntryCount / request ids changed between scan and call.", transient: true);

        DuringSend?.Invoke(target);

        NativeCalls[target.ContentId] = Native(target.ContentId) + 1;
        var ret = NativeReturn;
        Unresolved = new UnresolvedSend(target, localId(), snap.Generation, DateTime.UtcNow, ret, snap.CurrentRequestId, (byte)(snap.CurrentRequestId + 1));
        diag = diag with
        {
            NativeCalled = true,
            NativeReturn = ret,
            EntryCountAfter = snap.NativeEntryCount,
            CurrentRequestIdAfter = snap.CurrentRequestId,
            NextRequestIdAfter = (byte)(snap.CurrentRequestId + 1),
        };
        var outcome = ret != 0
            ? new RemovalSendOutcome(RemovalSendResult.Sent, "Queued (NOT confirmed)", true)
            : new RemovalSendOutcome(RemovalSendResult.Failed, "Native RemoveFriend returned 0", true);
        audit.TryWrite(localId(), target, false, AuditPhase.Outcome, outcome.Result.ToString(), outcome.Detail);
        return outcome;

        RemovalSendOutcome Refuse(RemovalSendResult r, string detail, bool transient = false)
        {
            var o = new RemovalSendOutcome(r, detail, false) { Transient = transient };
            audit.TryWrite(localId(), target, dryRun, AuditPhase.Refused, r.ToString(), detail);
            return o;
        }
    }

    public bool TryReleaseAfterServerRejection(RemovalTarget target, uint result, string replyDetail, out string detail)
    {
        if (result == 0)
        {
            detail = "result 0 is not a rejection";
            return false;
        }

        if (Unresolved is not { } u)
        {
            detail = "no latch set";
            return false;
        }

        if (u.Target.ContentId != target.ContentId)
        {
            detail = $"latch is for {u.Target.ContentId:X16}";
            return false;
        }

        Unresolved = null;
        Releases++;
        audit.TryWrite(u.LocalContentId, u.Target, false, AuditPhase.Released, "ServerRejected", replyDetail);
        detail = "released";
        return true;
    }

    private void OnSnapshot(FriendListSnapshot snap)
    {
        if (Unresolved is not { } u)
            return;
        var obs = SendClassifier.Classify(snap, u.Target.ContentId, u.LocalContentId, u.GenerationAtSend, u.CurrentRequestIdAtSend);
        if (obs is SendObservation.TargetAbsent or SendObservation.PresentInPostSendDownload)
        {
            Unresolved = null;
            Releases++;
            audit.TryWrite(u.LocalContentId, u.Target, false, AuditPhase.Released, obs.ToString(), "released");
        }
    }
}

/// <summary>
/// Models the reply hook + RequestData hook. <see cref="Reply"/> plays one server reply the way the game's handler does:
/// result 0 → local removal + RequestData (suppressed per <see cref="SuppressionPolicy"/>, else a real download starts);
/// non-zero → nothing but the event.
/// </summary>
public sealed class FakeSuppressor : IReloadSuppressor
{
    public FastModeAvailability Availability { get; set; } = FastModeAvailability.Available();

    public bool IsDownloadInFlight { get; set; }

    public bool FastBatchActive { get; private set; }

    public ulong Armed { get; private set; }

    public int Suppressed { get; private set; }

    /// <summary>RequestData calls from the reply handler that were NOT suppressed (a real download started).</summary>
    public int PassedReloads { get; private set; }

    public List<ServerReply> Replies { get; } = new();

    public event Action<ServerReply>? ReplyReceived;

    public string DescribeProxyState() => $"3ADD={(IsDownloadInFlight ? 1 : 0)} C8={(IsDownloadInFlight ? 0 : 1)}";

    public void SetFastBatchActive(bool active) => FastBatchActive = active;

    public void Arm(ulong targetContentId) => Armed = targetContentId;

    /// <summary>Disarm throws (to provoke the queue's "halting itself threw" path).</summary>
    public bool ThrowOnDisarm { get; set; }

    public void Disarm()
    {
        if (ThrowOnDisarm)
            throw new InvalidOperationException("simulated Disarm failure");
        Armed = 0;
    }

    public string DescribeResult(uint result) => $"LogMessage#{result}";

    /// <summary>Play a server reply. Returns whether the reload was suppressed.</summary>
    public bool Reply(Rig rig, ulong cid, uint result = 0)
    {
        var present = rig.List.Any(e => e.ContentId == cid);
        var before = (uint)rig.List.Count;
        var why = SuppressionPolicy.WhyNotSuppress(false, Availability.IsAvailable, FastBatchActive, Armed, cid, result, true, true,
            (byte)(IsDownloadInFlight ? 1 : 0), (byte)(IsDownloadInFlight ? 0 : 1), rig.Reader.State == FriendListState.Ready, present);
        if (cid == Armed)
            Armed = 0;
        if (result == 0)
            rig.List.RemoveAll(e => e.ContentId == cid); // LocalRemove
        var suppressed = result == 0 && why == null;
        if (suppressed)
            Suppressed++;
        else if (result == 0)
            PassedReloads++;

        var reply = new ServerReply(cid, result, 0, "name", before, (uint)rig.List.Count, present, rig.List.Any(e => e.ContentId == cid),
            suppressed, suppressed ? null : why ?? "n/a", DateTime.UtcNow);
        Replies.Add(reply);
        ReplyReceived?.Invoke(reply);

        if (suppressed)
            rig.Reader.PublishLocal(rig.List, Rig.Me, rig.RequestId);
        else if (result == 0)
        {
            IsDownloadInFlight = true;
            rig.Reader.StartGameDownload();
        }

        return suppressed;
    }
}
