using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SocialCleanup.Friends;

namespace SocialCleanup.Removal;

// Dalamud-free on purpose: linked into the test project. The Dalamud wiring is RemovalQueue.cs.

/// <summary>Clocks for the queue (faked in tests).</summary>
public interface IQueueClock
{
    /// <summary>Wall clock. Display and audit timestamps ONLY; never used for timers.</summary>
    DateTime UtcNow { get; }

    /// <summary>Monotonic time since an arbitrary origin. Every timeout, cooldown and duration uses this.</summary>
    TimeSpan Monotonic { get; }
}

public sealed class SystemQueueClock : IQueueClock
{
    public static readonly SystemQueueClock Instance = new();

    private static readonly long Origin = Stopwatch.GetTimestamp();

    public DateTime UtcNow => DateTime.UtcNow;

    public TimeSpan Monotonic => Stopwatch.GetElapsedTime(Origin);
}

/// <summary>Login / character / zoning state as last observed on the framework thread.</summary>
public interface IGameSession
{
    bool IsLoggedIn { get; }

    /// <summary>Logged-in character, or 0 when unknown (treated as "wait", not as a character change).</summary>
    ulong LocalContentId { get; }

    /// <summary>Zoning / loading screen / logging out: no sends.</summary>
    bool IsBetweenAreas { get; }

    /// <summary>Incremented on every logout event, so a logout + re-login between ticks is still seen.</summary>
    int LogoutCount { get; }

    /// <summary>
    /// The in-game Friend List window (addon "FriendList") is open (unknown counts as open). No batch starts while it is,
    /// and a running batch pauses before its next send until it is closed.
    /// </summary>
    bool IsFriendListWindowOpen { get; }
}

/// <param name="ItemTimeout">Max time from a real send to its verdict snapshot; also caps waiting for a usable friend list (zoning excluded).</param>
/// <param name="Cooldown">Pause between a verdict and the next send.</param>
/// <param name="CooldownJitterMax">Random 0..this added to every cooldown.</param>
/// <param name="DryRunDelay">Pause between dry-run items (so the UI is watchable).</param>
public sealed record RemovalQueueSettings(TimeSpan ItemTimeout, TimeSpan Cooldown, TimeSpan CooldownJitterMax, TimeSpan DryRunDelay)
{
    public static readonly RemovalQueueSettings Default = new(
        TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(400));

    /// <summary>Fallback per-item duration (measured in game: send → EndRequest ≈ 7.4 s).</summary>
    public static readonly TimeSpan FallbackItemDuration = TimeSpan.FromSeconds(7.5);

    /// <summary>Fallback per-item duration in fast mode (send → reply ≈ 0.2 s, unmeasured until the first item).</summary>
    public static readonly TimeSpan FallbackFastItemDuration = TimeSpan.FromSeconds(0.5);

    /// <summary>Fast mode: max time from the send to the server reply (and from a result-0 reply to the local update).</summary>
    public TimeSpan FastReplyTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Fast mode: pause between a verdict and the next send (clamped to at least 200 ms).</summary>
    public TimeSpan FastCooldown { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Fast mode: random 0..this added to every fast cooldown.</summary>
    public TimeSpan FastCooldownJitterMax { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// The removal queue state machine, with no Dalamud dependencies. Drive it by calling <see cref="Tick"/> from the
/// framework thread; snapshots arrive via <see cref="IFriendListReader.SnapshotCompleted"/> and fast-mode server
/// replies via <see cref="IReloadSuppressor.ReplyReceived"/>. UI calls are locked.
///
/// Rules: one item in flight, ever; at most one native send per item (a refusal with nothing sent may be retried only
/// when the remover marks it transient, or after Resume); Removed / AlreadyAbsent / DryRunOk continue; everything else
/// halts (no automatic retry or skip); Resume reconciles possibly-sent items against a post-halt snapshot and never
/// re-sends them. Audit rows produced inside game callbacks (snapshot / reply) are written on the next tick, before
/// anything else can be sent.
/// </summary>
public class RemovalQueueCore : IRemovalQueue
{
    /// <summary>Activity text while a running batch is paused because the in-game Friend List window is open.</summary>
    public const string FriendListWindowPauseText = "Paused — close the in-game Friend List window to continue";

    private static readonly TimeSpan TransientRefreshAfter = TimeSpan.FromSeconds(2);

    private readonly object gate = new();
    private readonly IFriendListReader reader;
    private readonly IFriendRemover remover;
    private readonly IRemovalAudit audit;
    private readonly IQueueClock clock;
    private readonly IGameSession session;
    private readonly Func<RemovalQueueSettings> settings;
    private readonly Func<string?>? externalBusy;
    private readonly Action<string>? log;
    private readonly Action<string>? detailLog;
    private readonly Random random;
    private readonly IReloadSuppressor? suppressor;

    private readonly List<ItemState> items = new();
    private readonly List<TimeSpan> durations = new();
    private readonly List<PendingAuditRow> deferredAudit = new();
    private int callbackDepth;

    private RemovalBatch? batch;
    private FriendListSnapshot? batchStartSnapshot;
    private RemovalQueueState state = RemovalQueueState.Idle;
    private string? haltReason;
    private string? activity;

    private int current = -1;
    private long requiredGeneration;
    private TimeSpan nextActionAt;
    private TimeSpan? waitingSince;
    private int logoutCountAtStart;
    private bool cancelRequested;

    // Transient refusals (nothing sent, list download in progress) for the next item.
    private TimeSpan? transientSince;
    private TimeSpan lastTransientAt;
    private bool transientRefreshRequested;

    // Paused (Running, between items) because the in-game Friend List window is open; null when not paused.
    private TimeSpan? pausedSince;

    // A snapshot raised re-entrantly while TrySend was running; evaluated once the send parameters are known.
    private FriendListSnapshot? deferredSnapshot;

    private bool resumePending;
    private TimeSpan resumeRequestedAt;
    private bool resumeRefreshRequested;
    private long haltGeneration;
    private string? originalHaltReason;

    // Fast mode.
    private int fastSends;
    private int fastReplies;
    private int fastSuppressed;
    private int fastRejected;
    private FinalVerification? verification;
    private TimeSpan verifyPhaseSince;
    private long verifyRequestedGeneration;

    // Sticky until Clear / a new batch: friends outside the batch went missing, or the final verification failed.
    private string? resumeBlockedReason;
    private IReadOnlyList<string> missingOutsideBatch = Array.Empty<string>();

    private int version;
    private int itemsCacheVersion = -1;
    private RemovalQueueItem[] itemsCache = Array.Empty<RemovalQueueItem>();
    private int raisedVersion;
    private bool disposed;

    public RemovalQueueCore(
        IFriendListReader reader,
        IFriendRemover remover,
        IRemovalAudit audit,
        IQueueClock clock,
        IGameSession session,
        Func<RemovalQueueSettings> settings,
        Func<string?>? externalBusy = null,
        Action<string>? log = null,
        Random? random = null,
        IReloadSuppressor? suppressor = null,
        Action<string>? detailLog = null)
    {
        this.reader = reader;
        this.remover = remover;
        this.audit = audit;
        this.clock = clock;
        this.session = session;
        this.settings = settings;
        this.externalBusy = externalBusy;
        this.log = log;
        this.detailLog = detailLog;
        this.random = random ?? new Random();
        this.suppressor = suppressor;
        reader.SnapshotCompleted += OnSnapshot;
        if (suppressor != null)
            suppressor.ReplyReceived += OnReply;
    }

    public event Action? Changed;

    // ------------------------------------------------------------------ read side (any thread)

    public RemovalQueueState State
    {
        get
        {
            lock (gate)
                return state;
        }
    }

    public string? HaltReason
    {
        get
        {
            lock (gate)
                return haltReason;
        }
    }

    public bool IsDryRun
    {
        get
        {
            lock (gate)
                return batch?.DryRun ?? false;
        }
    }

    public bool IsFastMode
    {
        get
        {
            lock (gate)
                return batch?.FastMode ?? false;
        }
    }

    public FastModeAvailability FastModeAvailability =>
        suppressor?.Availability ?? FastModeAvailability.Unavailable("Fast mode is not wired in this build.");

    public FastModeStats FastStats
    {
        get
        {
            lock (gate)
                return new FastModeStats(fastSends, fastReplies, fastSuppressed, fastRejected);
        }
    }

    public FinalVerification? FinalVerification
    {
        get
        {
            lock (gate)
                return verification;
        }
    }

    public RemovalBatch? Batch
    {
        get
        {
            lock (gate)
                return batch;
        }
    }

    public string? ResumeBlockedReason
    {
        get
        {
            lock (gate)
                return resumeBlockedReason;
        }
    }

    public IReadOnlyList<string> MissingOutsideBatch
    {
        get
        {
            lock (gate)
                return missingOutsideBatch;
        }
    }

    /// <summary>The (clamped) item timeout the queue applies right now.</summary>
    public TimeSpan EffectiveItemTimeout => Settings().ItemTimeout;

    public bool HasBatch
    {
        get
        {
            lock (gate)
                return batch != null;
        }
    }

    public RemoverAvailability Availability => remover.Availability;

    public IReadOnlyList<RemovalQueueItem> Items
    {
        get
        {
            lock (gate)
                return ItemsLocked();
        }
    }

    public RemovalQueueItem? InFlight
    {
        get
        {
            lock (gate)
            {
                if (current < 0 || current >= items.Count)
                    return null;
                var s = items[current].Status;
                return s is RemovalItemStatus.Sending or RemovalItemStatus.AwaitingRefresh ? ItemsLocked()[current] : null;
            }
        }
    }

    public RemovalQueueCounts Counts
    {
        get
        {
            lock (gate)
                return CountsLocked();
        }
    }

    public TimeSpan AverageItemDuration
    {
        get
        {
            lock (gate)
                return AverageLocked();
        }
    }

    public TimeSpan? EstimatedRemaining
    {
        get
        {
            lock (gate)
            {
                if (state != RemovalQueueState.Running || batch == null)
                    return null;
                var s = Settings();
                var c = CountsLocked();
                var now = clock.Monotonic;
                TimeSpan per;
                var inFlightLeft = TimeSpan.Zero;
                if (batch.DryRun)
                {
                    per = s.DryRunDelay;
                }
                else
                {
                    var avg = AverageLocked();
                    per = batch.FastMode
                        ? avg + s.FastCooldown + (s.FastCooldownJitterMax / 2)
                        : avg + s.Cooldown + (s.CooldownJitterMax / 2);
                    if (current >= 0 && items[current].SentAt is { } sent)
                    {
                        var left = avg - (now - sent);
                        inFlightLeft = left > TimeSpan.Zero ? left : TimeSpan.Zero;
                    }
                }

                var cooldownLeft = nextActionAt > now && current < 0 ? nextActionAt - now : TimeSpan.Zero;
                return (per * c.Pending) + inFlightLeft + cooldownLeft;
            }
        }
    }

    public string? Activity
    {
        get
        {
            lock (gate)
                return activity;
        }
    }

    public bool IsCancelRequested
    {
        get
        {
            lock (gate)
                return cancelRequested;
        }
    }

    public bool IsResumePending
    {
        get
        {
            lock (gate)
                return resumePending;
        }
    }

    public bool IsPausedForFriendListWindow
    {
        get
        {
            lock (gate)
                return pausedSince != null && state == RemovalQueueState.Running;
        }
    }

    // ------------------------------------------------------------------ commands (UI thread)

    public bool TryStart(RemovalBatch newBatch, out string? error)
    {
        lock (gate)
        {
            error = ValidateStartLocked(newBatch, out var snap);
            if (error != null)
            {
                Log($"TryStart refused: {error}");
                return false;
            }

            FlushDeferredAuditLocked();
            var startDetail = $"items={newBatch.Targets.Count} gen={newBatch.SnapshotGeneration} dryRun={newBatch.DryRun} fastMode={newBatch.FastMode} confirmed={newBatch.ConfirmedAtUtc:O}";
            if (!audit.TryWrite(newBatch.LocalContentId, RemovalAuditTargets.Batch, newBatch.DryRun, AuditPhase.BatchStart, "Started", startDetail))
            {
                error = $"Audit BATCH_START row could not be written ({audit.LastError ?? "unknown error"}); nothing started.";
                Log($"TryStart refused: {error}");
                return false;
            }

            ResetLocked();
            batch = newBatch;
            batchStartSnapshot = snap;
            foreach (var t in newBatch.Targets)
                items.Add(new ItemState(t));
            state = RemovalQueueState.Running;
            requiredGeneration = snap!.Generation;
            nextActionAt = clock.Monotonic;
            logoutCountAtStart = session.LogoutCount;
            if (newBatch.FastMode)
                suppressor!.SetFastBatchActive(true);
            Log($"Batch started: {startDetail}");
            MarkChanged();
            error = null;
            return true;
        }
    }

    public bool Resume(out string? error)
    {
        lock (gate)
        {
            if (state != RemovalQueueState.Halted || batch == null)
            {
                error = $"The queue is {state}, not Halted.";
                return false;
            }

            if (resumePending)
            {
                error = "Resume is already pending (waiting for a fresh friend-list snapshot).";
                return false;
            }

            if (verification is { IsInProgress: true })
            {
                error = "The fast batch's final reload / verification is still running; wait for it to finish.";
                return false;
            }

            if (verification is { BlocksResume: true } bv)
            {
                error = $"The final verification is {bv.Status}; this batch cannot be resumed. Cancel it (then Clear) and start a new batch from a fresh friend list. {bv.Detail}";
                return false;
            }

            if (resumeBlockedReason != null)
            {
                error = $"This batch cannot be resumed: {resumeBlockedReason}";
                return false;
            }

            var avail = remover.Availability;
            if (!avail.IsAvailable)
            {
                error = $"Remover unavailable: {avail.Reason}";
                return false;
            }

            if (batch.FastMode && FastModeAvailability is { IsAvailable: false } fa)
            {
                error = $"This is a fast batch and fast mode is unavailable ({fa.Reason}). Cancel it and start a standard batch.";
                return false;
            }

            resumePending = true;
            resumeRequestedAt = clock.Monotonic;
            resumeRefreshRequested = false;
            originalHaltReason ??= haltReason;
            haltReason = $"Resuming: waiting for a friend-list snapshot newer than gen {haltGeneration} (requesting one). Halted because: {originalHaltReason}";
            activity = "Resume: waiting for a fresh friend-list snapshot";
            Log("Resume requested.");
            MarkChanged();
            error = null;
            return true;
        }
    }

    public void Cancel()
    {
        lock (gate)
        {
            switch (state)
            {
                case RemovalQueueState.Running when InFlightLocked():
                    if (!cancelRequested)
                    {
                        cancelRequested = true;
                        activity = "Cancel requested: waiting for the in-flight item to settle";
                        Log("Cancel requested; waiting for the in-flight item.");
                        MarkChanged();
                    }

                    break;

                case RemovalQueueState.Running:
                case RemovalQueueState.Halted:
                    cancelRequested = true;
                    FinishCancelledLocked(state == RemovalQueueState.Halted ? $"Cancelled by the user while halted ({originalHaltReason ?? haltReason})." : "Cancelled by the user.");
                    break;
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            if (state is RemovalQueueState.Running or RemovalQueueState.Halted || verification is { IsInProgress: true })
                return;
            FlushDeferredAuditLocked();
            ResetLocked();
            MarkChanged();
        }
    }

    public virtual void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            try
            {
                FlushDeferredAuditLocked();
                if (batch != null && state is RemovalQueueState.Running or RemovalQueueState.Halted)
                {
                    if (InFlightLocked())
                    {
                        var it = items[current];
                        SetIndeterminateLocked(it, "Plugin unloaded while this item was in flight; Resume is impossible, check the friend list.");
                        WriteVerdictLocked(it, "Indeterminate", it.Detail!, out _);
                    }

                    Log($"Disposed while the batch was {state}.");
                    WriteBatchEndLocked("Disposed");
                }

                if (batch is { FastMode: true })
                {
                    suppressor?.Disarm();
                    suppressor?.SetFastBatchActive(false);
                }
            }
            catch (Exception ex)
            {
                Log($"Dispose bookkeeping threw: {ex}");
            }
        }

        reader.SnapshotCompleted -= OnSnapshot;
        if (suppressor != null)
            suppressor.ReplyReceived -= OnReply;
    }

    // ------------------------------------------------------------------ framework thread

    /// <summary>Framework thread. Advances the state machine; at most one TrySend per call.</summary>
    public void Tick()
    {
        lock (gate)
        {
            if (disposed)
                return;
            try
            {
                TickLocked();
            }
            catch (Exception ex)
            {
                OnExceptionLocked("Queue tick", ex);
            }
        }

        RaiseChanged();
    }

    private void OnSnapshot(FriendListSnapshot snap)
    {
        lock (gate)
        {
            if (disposed)
                return;
            callbackDepth++;
            try
            {
                OnSnapshotLocked(snap);
            }
            catch (Exception ex)
            {
                OnExceptionLocked("Snapshot handler", ex);
            }
            finally
            {
                callbackDepth--;
            }
        }

        RaiseChanged();
    }

    private void OnReply(ServerReply reply)
    {
        lock (gate)
        {
            if (disposed)
                return;
            callbackDepth++;
            try
            {
                OnReplyLocked(reply);
            }
            catch (Exception ex)
            {
                OnExceptionLocked("Reply handler", ex);
            }
            finally
            {
                callbackDepth--;
            }
        }

        RaiseChanged();
    }

    private void TickLocked()
    {
        var now = clock.Monotonic;
        FlushDeferredAuditLocked();
        if (batch == null)
            return;

        TickVerificationLocked(now);

        if (state is not (RemovalQueueState.Running or RemovalQueueState.Halted))
            return;

        if (CheckSessionLocked())
            return;

        if (state == RemovalQueueState.Halted)
        {
            if (resumePending)
                TickResumeLocked(now);
            return;
        }

        var s = Settings();

        // ---- in flight: only the verdict (snapshot / server reply), a timeout or the reader failing end it
        if (InFlightLocked())
        {
            var it = items[current];
            if (reader.State == FriendListState.Invalid)
            {
                SetIndeterminateLocked(it, $"Friend-list reader went Invalid while waiting for the verdict ({reader.StateReason ?? "no reason"}).");
                WriteVerdictLocked(it, "Indeterminate", it.Detail!, out var ae);
                HaltLocked($"{it.Target}: friend-list reader went Invalid while waiting for the verdict snapshot.{AuditSuffix(ae)}");
                return;
            }

            if (it.FastSend && it.SentAt is { } fastSent)
            {
                if (it.Reply == null && now - fastSent > s.FastReplyTimeout)
                {
                    SetIndeterminateLocked(it, $"No server reply within {s.FastReplyTimeout.TotalSeconds:0.#}s of the send (fast mode).");
                    WriteVerdictLocked(it, "Indeterminate", it.Detail!, out var ae);
                    HaltLocked($"{it.Target}: no server reply within {s.FastReplyTimeout.TotalSeconds:0.#}s. Resume reconciles it against a full reload; it is never re-sent.{AuditSuffix(ae)}");
                    return;
                }

                if (it.Reply is { Result: 0, Suppressed: true } && it.ReplyAt is { } ra && now - ra > s.FastReplyTimeout)
                {
                    SetIndeterminateLocked(it, $"Server replied result 0 but no local list update arrived within {s.FastReplyTimeout.TotalSeconds:0.#}s.");
                    WriteVerdictLocked(it, "Indeterminate", it.Detail!, out var ae);
                    HaltLocked($"{it.Target}: server confirmed the removal but the local list update never arrived. Resume reconciles it; it is never re-sent.{AuditSuffix(ae)}");
                    return;
                }
            }

            if (it.SentAt is { } sent && now - sent > s.ItemTimeout)
            {
                var what = it.PreSendSnapshotsSeen > 0
                    ? $"Only pre-send data ({it.PreSendSnapshotsSeen} snapshot(s) still listing the target) within {s.ItemTimeout.TotalSeconds:0}s of the send"
                    : $"No newer friend-list snapshot within {s.ItemTimeout.TotalSeconds:0}s of the send";
                SetIndeterminateLocked(it, $"{what}.");
                WriteVerdictLocked(it, "Indeterminate", it.Detail!, out var ae);
                HaltLocked($"{it.Target}: timed out waiting for the verdict ({what}). Resume reconciles it against a fresh snapshot; it is never re-sent.{AuditSuffix(ae)}");
                return;
            }

            var waitingFor = it.FastSend ? (it.Reply == null ? "server reply" : "local list update") : "refresh";
            activity = $"Awaiting {waitingFor} for {it.Target.DisplayName} ({(now - (it.SentAt ?? now)).TotalSeconds:0.0}s)";
            return;
        }

        if (cancelRequested)
        {
            FinishCancelledLocked("Cancelled by the user.");
            return;
        }

        var next = NextPendingLocked();
        if (next < 0)
        {
            CompleteLocked();
            return;
        }

        // ---- in-game Friend List window open: pause (not halt) before the next send. The in-flight item (handled
        // above) settles normally. Paused time does not count against the ready-wait or transient-refusal budgets.
        if (session.IsFriendListWindowOpen)
        {
            if (pausedSince == null)
            {
                pausedSince = now;
                Log($"Paused before {items[next].Target}: the in-game Friend List window is open. Nothing is sent until it is closed.");
                MarkChanged();
            }

            SetActivity(FriendListWindowPauseText);
            return;
        }

        if (pausedSince is { } ps)
        {
            var paused = now - ps;
            pausedSince = null;
            if (waitingSince is { } ws)
                waitingSince = ws + paused;
            if (transientSince is { } ts0)
                transientSince = ts0 + paused;
            activity = null;
            Log($"Friend List window closed after {paused.TotalSeconds:0.0}s; continuing once the friend list is usable.");
            MarkChanged();
        }

        if (now < nextActionAt)
        {
            activity = $"Cooldown ({(nextActionAt - now).TotalSeconds:0.0}s)";
            return;
        }

        // ---- preconditions for a send. Zoning: wait, and do not count it against anything.
        if (session.IsBetweenAreas || session.LocalContentId == 0)
        {
            waitingSince = null;
            SetActivity("Waiting: zoning / loading screen");
            return;
        }

        if (reader.State == FriendListState.Invalid)
        {
            HaltLocked($"Friend-list reader is Invalid ({reader.StateReason ?? "no reason"}); nothing sent for {items[next].Target}.");
            return;
        }

        if (batch.FastMode && FastModeAvailability is { IsAvailable: false } fa)
        {
            HaltLocked($"Fast mode became unavailable ({fa.Reason}); nothing sent for {items[next].Target}.");
            return;
        }

        var snap = reader.Snapshot;
        var downloadInFlight = batch.FastMode && suppressor!.IsDownloadInFlight;
        var ready = reader.State == FriendListState.Ready && !downloadInFlight && snap != null
                    && snap.LocalContentId == batch.LocalContentId && snap.Generation >= requiredGeneration;
        if (!ready)
        {
            waitingSince ??= now;
            var why = downloadInFlight
                ? "a friend-list download is in flight"
                : $"reader {reader.State}, gen {snap?.Generation ?? 0}, need ≥ {requiredGeneration}";
            if (now - waitingSince.Value > s.ItemTimeout)
            {
                HaltLocked($"Waited {s.ItemTimeout.TotalSeconds:0}s for a usable friend list before sending {items[next].Target} ({why}). Nothing sent.");
                return;
            }

            MaybeRequestRefreshAfterTransientLocked(now);
            SetActivity($"Waiting for the friend list ({why})");
            return;
        }

        waitingSince = null;

        if (transientSince is { } ts && now - ts > s.ItemTimeout)
        {
            HaltLocked($"{items[next].Target}: the remover kept refusing for {s.ItemTimeout.TotalSeconds:0}s because the list was changing ({items[next].Detail}). Nothing sent.");
            return;
        }

        var avail = remover.Availability;
        if (!avail.IsAvailable)
        {
            HaltLocked($"Remover unavailable ({avail.Reason}); nothing sent for {items[next].Target}.");
            return;
        }

        if (remover.HasUnresolvedSend)
        {
            HaltLocked($"The remover's send latch / startup block is set ({LatchReasonLocked()}); nothing sent for {items[next].Target}.");
            return;
        }

        SendLocked(next, snap!, now, s);
    }

    private void MaybeRequestRefreshAfterTransientLocked(TimeSpan now)
    {
        if (transientSince == null || transientRefreshRequested || reader.State != FriendListState.Ready || session.IsBetweenAreas)
            return;
        if (now - lastTransientAt < TransientRefreshAfter)
            return;
        transientRefreshRequested = reader.RequestRefresh();
        if (transientRefreshRequested)
            LogDetail("Transient refusal and no newer snapshot: friend-list refresh requested.");
    }

    private void SendLocked(int index, FriendListSnapshot snapAtSend, TimeSpan now, RemovalQueueSettings s)
    {
        var it = items[index];
        var dry = batch!.DryRun;
        var fast = batch.FastMode && !dry;
        current = index;
        deferredSnapshot = null;
        it.Status = RemovalItemStatus.Sending;
        it.StartedAtUtc = clock.UtcNow;
        it.StartedAt = now;
        it.Detail = null;
        activity = $"Sending {it.Target.DisplayName}";
        MarkChanged();

        RemovalSendOutcome outcome;
        SendDiagnostics d;
        try
        {
            outcome = remover.TrySend(it.Target, dry, out d);
        }
        catch (Exception ex)
        {
            deferredSnapshot = null;
            ClearTransientLocked();
            if (dry)
            {
                SetFailedLocked(it, $"TrySend (dry run) threw: {ex.Message}. Nothing sent.");
                WriteVerdictLocked(it, "Refused", it.Detail!, out var ae1);
                HaltLocked($"{it.Target}: dry-run TrySend threw ({ex.Message}).{AuditSuffix(ae1)}");
                return;
            }

            // Unknown whether the native call happened. Take the send parameters from the remover's latch if it set one.
            MarkMaybeSentLocked(it, now, snapAtSend);
            if (remover.Unresolved is { } u && u.Target.ContentId == it.Target.ContentId)
                it.SetSendParams(u.GenerationAtSend, u.CurrentRequestIdAtSend, 0);
            SetIndeterminateLocked(it, $"TrySend threw ({ex.Message}); the send may or may not have happened.");
            WriteVerdictLocked(it, "Indeterminate", it.Detail!, out var ae2);
            HaltLocked($"{it.Target}: TrySend threw ({ex.Message}). Resume reconciles it; it is never re-sent.{AuditSuffix(ae2)}");
            return;
        }

        LogDetail($"TrySend({it.Target}, dryRun={dry}) -> {outcome.Result} (mayHaveReachedServer={outcome.MayHaveReachedServer}, transient={outcome.Transient}): {outcome.Detail}");

        if (dry && outcome.Result == RemovalSendResult.DryRunResolved && !outcome.MayHaveReachedServer)
        {
            ClearTransientLocked();
            it.Status = RemovalItemStatus.DryRunOk;
            it.FinishedAtUtc = clock.UtcNow;
            it.Detail = $"Dry run OK: {outcome.Detail}";
            current = -1;
            deferredSnapshot = null;
            nextActionAt = now + s.DryRunDelay;
            MarkChanged();
            if (!WriteVerdictLocked(it, "DryRunResolved", it.Detail, out var ae))
                HaltLocked($"Audit VERDICT row for {it.Target} could not be written ({ae}).");
            return;
        }

        if (!dry && outcome.Result == RemovalSendResult.Sent)
        {
            ClearTransientLocked();
            MarkMaybeSentLocked(it, now, snapAtSend);
            it.SetSendParams(d.SnapshotGeneration, d.CurrentRequestIdBefore, d.EntryCountBefore);
            it.Status = RemovalItemStatus.AwaitingRefresh;
            if (fast)
            {
                it.FastSend = true;
                fastSends++;
                suppressor!.Arm(it.Target.ContentId);
                it.Detail = $"Sent; waiting for the server reply (fast mode). {outcome.Detail}";
                activity = $"Awaiting server reply for {it.Target.DisplayName}";
            }
            else
            {
                it.Detail = $"Sent; waiting for a snapshot newer than gen {d.SnapshotGeneration}. {outcome.Detail}";
                activity = $"Awaiting refresh for {it.Target.DisplayName}";
            }

            LogSend(index, it, d, fast);
            MarkChanged();
            if (deferredSnapshot is { } ds)
            {
                deferredSnapshot = null;
                LogDetail($"Evaluating snapshot gen {ds.Generation} that arrived while TrySend was running.");
                OnSnapshotLocked(ds);
            }

            return;
        }

        deferredSnapshot = null;

        if (outcome.MayHaveReachedServer)
        {
            // Native call happened (or may have) but not a clean Sent: e.g. returned 0, or failed mid-call.
            ClearTransientLocked();
            MarkMaybeSentLocked(it, now, snapAtSend);
            if (d.NativeCalled)
                it.SetSendParams(d.SnapshotGeneration, d.CurrentRequestIdBefore, d.EntryCountBefore);
            else if (remover.Unresolved is { } u && u.Target.ContentId == it.Target.ContentId)
                it.SetSendParams(u.GenerationAtSend, u.CurrentRequestIdAtSend, 0);
            if (d.NativeCalled)
                LogSend(index, it, d, fast);
            var notQueued = d.NativeCalled && d.NativeReturn == 0;
            var verdict = notQueued ? "NotQueued" : "Indeterminate";
            SetIndeterminateLocked(it, notQueued
                ? $"NotQueued: native RemoveFriend returned 0 (analysis: nothing sent). {outcome.Detail}"
                : $"Send may have reached the server but failed locally: {outcome.Detail}");
            WriteVerdictLocked(it, verdict, it.Detail!, out var ae);
            HaltLocked($"{it.Target}: {verdict} ({outcome.Detail}). Resume reconciles it; it is never re-sent.{AuditSuffix(ae)}");
            return;
        }

        if (outcome.Transient)
        {
            // Nothing sent and the cause is a list download / reshuffle in progress: wait for a newer snapshot (bounded).
            it.Status = RemovalItemStatus.Pending;
            it.StartedAtUtc = null;
            it.StartedAt = null;
            it.Detail = $"Waiting (nothing sent; list changing): {outcome.Result}: {outcome.Detail}";
            current = -1;
            transientSince ??= now;
            lastTransientAt = now;
            requiredGeneration = Math.Max(requiredGeneration, snapAtSend.Generation + 1);
            activity = $"Waiting for the friend list to settle before sending {it.Target.DisplayName}";
            LogDetail($"Transient refusal for {it.Target}; waiting for a snapshot newer than gen {snapAtSend.Generation}.");
            MarkChanged();
            return;
        }

        ClearTransientLocked();

        if (outcome.Result == RemovalSendResult.NotInLiveList)
        {
            // AlreadyAbsent only when OUR view agrees: a fresh Ready snapshot for this character that lacks the target.
            var snap = reader.Snapshot;
            if (reader.State == FriendListState.Ready && snap != null && snap.LocalContentId == batch.LocalContentId
                && snap.Generation >= requiredGeneration && !SendClassifier.Contains(snap, it.Target.ContentId))
            {
                if (MissingOutsideBatchLocked(snap).Count > 0)
                {
                    // Not marked AlreadyAbsent: the list it would be judged against is missing other friends too.
                    SetFailedLocked(it, $"NotInLiveList (nothing sent), but friends outside the batch are missing from gen {snap.Generation} too; not marked AlreadyAbsent.");
                    WriteVerdictLocked(it, "Refused", it.Detail!, out _);
                    current = -1;
                    HaltIfMissingOutsideBatchLocked(null, snap, $"{it.Target} reported NotInLiveList (nothing sent)");
                    return;
                }

                it.Status = RemovalItemStatus.AlreadyAbsent;
                it.FinishedAtUtc = clock.UtcNow;
                it.Detail = $"Already absent from gen {snap.Generation}; nothing sent.";
                current = -1;
                requiredGeneration = Math.Max(requiredGeneration, snap.Generation);
                nextActionAt = now + (dry ? s.DryRunDelay : fast ? FastCooldownWithJitter(s) : CooldownWithJitter(s));
                MarkChanged();
                if (!WriteVerdictLocked(it, "AlreadyAbsent", it.Detail, out var ae))
                    HaltLocked($"Audit VERDICT row for {it.Target} could not be written ({ae}).");
                return;
            }

            SetFailedLocked(it, $"NotInLiveList, but the friend-list snapshot still lists it (live list and snapshot disagree): {outcome.Detail} Nothing sent.");
            WriteVerdictLocked(it, "Refused", it.Detail!, out var ae2);
            HaltLocked($"{it.Target}: the remover did not find it in the live list but the snapshot still lists it. Nothing sent.{AuditSuffix(ae2)}");
            return;
        }

        // Every other result is a refusal (nothing sent) → halt.
        SetFailedLocked(it, $"{outcome.Result}: {outcome.Detail}");
        WriteVerdictLocked(it, "Refused", it.Detail!, out var ae3);
        HaltLocked($"{it.Target}: refused by the remover ({outcome.Result}: {outcome.Detail}). Nothing sent.{AuditSuffix(ae3)}");
    }

    private void LogSend(int index, ItemState it, SendDiagnostics d, bool fast)
    {
        LogDetail($"SEND #{index + 1}/{items.Count} {it.Target} mode={(fast ? "FAST" : "standard")} ret={d.NativeReturn?.ToString() ?? "?"} index={d.RawIndex} " +
            $"EntryCount {d.EntryCountBefore}->{d.EntryCountAfter?.ToString() ?? "?"} CurrentRequestId {d.CurrentRequestIdBefore}->{d.CurrentRequestIdAfter?.ToString() ?? "?"} " +
            $"NextRequestId {d.NextRequestIdBefore}->{d.NextRequestIdAfter?.ToString() ?? "?"} gen={d.SnapshotGeneration} proxy[{DescribeProxySafe()}]");
    }

    private void OnReplyLocked(ServerReply r)
    {
        if (batch == null)
            return;

        var inFlight = InFlightLocked() ? items[current] : null;
        LogDetail($"REPLY {r} | in flight: {(inFlight == null ? "none" : inFlight.Target.ToString())}");
        if (state != RemovalQueueState.Running || inFlight == null)
        {
            LogDetail("Reply ignored: no item in flight.");
            return;
        }

        var it = inFlight;
        if (r.ContentId != it.Target.ContentId)
        {
            LogDetail($"Reply ignored: it is for {r.ContentId:X16}, not the in-flight target {it.Target.ContentId:X16}.");
            return;
        }

        if (it.Status != RemovalItemStatus.AwaitingRefresh || it.SentAt == null)
        {
            LogDetail($"Reply ignored: the in-flight item is {it.Status}, not awaiting a result.");
            return;
        }

        if (it.Reply != null)
        {
            LogDetail("Duplicate reply for the in-flight target ignored.");
            return;
        }

        var now = clock.Monotonic;
        it.Reply = r;
        it.ReplyAt = now;

        if (!it.FastSend && r.Result == 0)
        {
            // Standard mode, success: informational only; the verdict still comes from the reloaded snapshot.
            it.Detail = $"{it.Detail} | server reply result {r.Result} after {(now - it.SentAt.Value).TotalMilliseconds:0}ms.";
            MarkChanged();
            return;
        }

        if (it.FastSend)
        {
            fastReplies++;
            if (r.Suppressed)
                fastSuppressed++;
        }

        if (r.Result != 0)
        {
            // Both modes: a definitive rejection for exactly the in-flight target. Halt now; the latch is released only
            // through the remover's narrowly-scoped TryReleaseAfterServerRejection (which writes its RELEASED row).
            if (it.FastSend)
            {
                fastRejected++;
                suppressor?.Disarm();
            }

            var text = DescribeResultSafe(r.Result);
            var released = remover.TryReleaseAfterServerRejection(it.Target, r.Result, r.ToString(), out var relDetail);
            SetFailedLocked(it,
                $"Server rejected the removal: result {r.Result} \"{text}\". Not removed; NOT re-sent. " +
                (released ? "Send latch released." : $"Send latch NOT released ({relDetail})."));
            WriteVerdictLocked(it, "ServerRejected", it.Detail!, out var ae);
            HaltLocked($"{it.Target}: the server rejected the removal (result {r.Result}: \"{text}\"). Nothing else is sent.{AuditSuffix(ae)}" +
                       (released ? string.Empty : $" The send latch is still set: {relDetail}"));
            return;
        }

        it.Detail = r.Suppressed
            ? $"Server confirmed (result 0) after {(now - it.SentAt.Value).TotalMilliseconds:0}ms; reload suppressed, waiting for the local list update."
            : $"Server confirmed (result 0); reload NOT suppressed ({r.NotSuppressedReason}), waiting for the full download.";
        MarkChanged();
    }

    private void OnSnapshotLocked(FriendListSnapshot snap)
    {
        if (batch == null)
            return;

        if (verification is { Status: FinalVerificationStatus.Requested })
            TryCompleteVerificationLocked(snap);

        if (state != RemovalQueueState.Running || !InFlightLocked())
            return;

        var it = items[current];
        if (it.Status == RemovalItemStatus.Sending)
        {
            // Raised from inside TrySend: the send parameters are not known yet. Evaluate it right after.
            deferredSnapshot = snap;
            return;
        }

        if (it.Status != RemovalItemStatus.AwaitingRefresh || !it.SendParamsKnown)
            return;

        if (it.FastSend)
        {
            OnFastSnapshotLocked(it, snap);
            return;
        }

        var ev = SendClassifier.Evaluate(snap, it.Target.ContentId, batch.LocalContentId, it.GenerationAtSend, it.RequestIdAtSend, it.EntryCountBefore);
        LogDetail($"Snapshot gen {snap.Generation} (requestId {snap.CurrentRequestId}, native {snap.NativeEntryCount}, ours={snap.WasRequestedByPlugin}, {snap.Source}) for {it.Target}: {ev.Observation} -> {ev.Verdict}");

        if (ev.Verdict == SnapshotVerdict.Ignore)
            return;

        // Every verdict path (and pre-send data): nobody outside the batch may have gone missing since batch start.
        if (HaltIfMissingOutsideBatchLocked(it, snap, $"verdict snapshot gen {snap.Generation} for {it.Target} ({ev.Verdict})"))
            return;

        switch (ev.Verdict)
        {
            case SnapshotVerdict.KeepWaiting:
                it.PreSendSnapshotsSeen++;
                it.Detail = $"Still waiting: {ev.Detail}";
                MarkChanged();
                return;

            case SnapshotVerdict.Removed:
            {
                var unexpected = UnexpectedMissingLocked(it, snap);
                if (unexpected != null)
                {
                    HaltUnexpectedLocked(it, $"Target absent and count -1 in gen {snap.Generation}, but other friends are missing too: {unexpected}");
                    return;
                }

                FinishRemovedLocked(it, snap, ev.Detail, fast: false);
                return;
            }

            case SnapshotVerdict.StillPresent:
                HaltStillPresentLocked(it, snap, ev.Detail);
                return;

            default:
                HaltUnexpectedLocked(it, WithOthersMissing(it, snap, ev.Detail));
                return;
        }
    }

    private void HaltStillPresentLocked(ItemState it, FriendListSnapshot snap, string evDetail)
    {
        var detail = WithOthersMissing(it, snap, evDetail);
        SetIndeterminateLocked(it, $"StillPresent: {detail}");
        WriteVerdictLocked(it, "StillPresent", detail, out var ae);
        HaltLocked($"{it.Target}: still present after the send ({detail}).{AuditSuffix(ae)}");
    }

    /// <summary>Appends "other friends missing since the send" (UnexpectedMissingLocked) to a halting verdict's detail.</summary>
    private static string WithOthersMissing(ItemState it, FriendListSnapshot snap, string detail) =>
        snap.LocalContentId == it.SnapshotAtSend?.LocalContentId && UnexpectedMissingLocked(it, snap) is { } others
            ? $"{detail} Other friends missing since the send: {others}"
            : detail;

    /// <summary>
    /// Friends in the batch-start snapshot that are not batch targets and are absent from <paramref name="snap"/>
    /// (same character only; another character's snapshot is its own verdict).
    /// </summary>
    private List<FriendEntry> MissingOutsideBatchLocked(FriendListSnapshot snap)
    {
        if (batch == null || batchStartSnapshot == null || snap.LocalContentId != batch.LocalContentId)
            return new List<FriendEntry>();
        var present = new HashSet<ulong>(snap.Entries.Select(e => e.ContentId));
        var targets = new HashSet<ulong>(items.Select(i => i.Target.ContentId));
        return batchStartSnapshot.Entries.Where(e => !targets.Contains(e.ContentId) && !present.Contains(e.ContentId)).ToList();
    }

    /// <summary>
    /// If anyone outside the batch went missing since batch start: settle the in-flight item (if any) as Indeterminate,
    /// name them in the item detail, halt reason, log and audit, and block Resume for this batch. Returns true when it did.
    /// </summary>
    private bool HaltIfMissingOutsideBatchLocked(ItemState? it, FriendListSnapshot snap, string context)
    {
        var missing = MissingOutsideBatchLocked(snap);
        if (missing.Count == 0)
            return false;

        var names = DescribeEntries(missing);
        var detail = $"{missing.Count} friend(s) NOT in this batch are missing from gen {snap.Generation} compared with the batch-start snapshot " +
                     $"(gen {batchStartSnapshot!.Generation}): {names}";
        BlockResumeLocked($"{detail}. Only Cancel / Clear are possible; start a new batch from a fresh friend list.",
            missing.Select(e => $"{e.DisplayName}@{e.HomeWorldName} ({e.ContentId:X16})"));
        WriteAuditLocked(RemovalAuditTargets.Batch, AuditPhase.UnexpectedMissing, "Halted", $"{detail} ({context})", out var be);

        string? ae = null;
        if (it != null && InFlightLocked() && items[current] == it)
        {
            SetIndeterminateLocked(it, $"Unexpected: {detail}");
            WriteVerdictLocked(it, "Unexpected", detail, out ae);
        }

        var reason = $"{detail} ({context}). Resume is disabled for this batch.{AuditSuffix(ae)}" +
                     (be == null ? string.Empty : $" (Audit {AuditPhase.UnexpectedMissing} row could not be written: {be}.)");
        if (state == RemovalQueueState.Running)
        {
            HaltLocked(reason);
        }
        else
        {
            haltReason = reason;
            Log($"UNEXPECTED MISSING: {reason}");
            MarkChanged();
        }

        return true;
    }

    private void BlockResumeLocked(string reason, IEnumerable<string>? missingNames = null)
    {
        resumeBlockedReason ??= reason;
        if (missingNames != null)
            missingOutsideBatch = missingOutsideBatch.Concat(missingNames).Distinct().ToList().AsReadOnly();
        MarkChanged();
    }

    private void OnFastSnapshotLocked(ItemState it, FriendListSnapshot snap)
    {
        if (snap.Generation <= it.GenerationAtSend)
            return;

        if (HaltIfMissingOutsideBatchLocked(it, snap, $"{snap.Source} snapshot gen {snap.Generation} during fast item {it.Target}"))
            return;

        if (snap.Source == SnapshotSource.LocalAfterRemoval)
        {
            var problems = new List<string>();
            if (it.Reply is not { Result: 0 })
                problems.Add("no result-0 server reply for the target preceded this local update");
            if (snap.LocalContentId != batch!.LocalContentId)
                problems.Add($"snapshot belongs to {snap.LocalContentId:X16}");
            if (SendClassifier.Contains(snap, it.Target.ContentId))
                problems.Add("the target is still listed");
            var expected = (long)it.EntryCountBefore - 1;
            if (snap.NativeEntryCount != expected)
                problems.Add($"EntryCount {it.EntryCountBefore}->{snap.NativeEntryCount} (expected {expected})");
            if (UnexpectedMissingLocked(it, snap) is { } unexpected)
                problems.Add($"other friends are missing too: {unexpected}");

            LogDetail($"LOCAL snapshot gen {snap.Generation} (requestId {snap.CurrentRequestId}, native {snap.NativeEntryCount}) for {it.Target}: " +
                (problems.Count == 0 ? "OK" : string.Join("; ", problems)));
            if (problems.Count == 0)
            {
                FinishRemovedLocked(it, snap,
                    $"Removed: server result 0, local update gen {snap.Generation} lacks it, EntryCount {it.EntryCountBefore}->{snap.NativeEntryCount}, nothing else missing (reload suppressed).",
                    fast: true);
                return;
            }

            HaltUnexpectedLocked(it, $"Local update gen {snap.Generation}: {string.Join("; ", problems)}");
            return;
        }

        // A full download completed while this fast item was in flight (reload not suppressed, or a game-initiated one).
        var ev = SendClassifier.Evaluate(snap, it.Target.ContentId, batch!.LocalContentId, it.GenerationAtSend, it.RequestIdAtSend, it.EntryCountBefore);
        LogDetail($"FULL snapshot gen {snap.Generation} (requestId {snap.CurrentRequestId}, native {snap.NativeEntryCount}) during fast item {it.Target}: {ev.Observation} -> {ev.Verdict}");
        switch (ev.Verdict)
        {
            case SnapshotVerdict.Ignore:
                return;

            case SnapshotVerdict.KeepWaiting:
                it.PreSendSnapshotsSeen++;
                MarkChanged();
                return;

            case SnapshotVerdict.Removed:
            {
                if (it.Reply is not { Result: 0 })
                {
                    HaltUnexpectedLocked(it, $"Full download gen {snap.Generation} lacks the target but no result-0 server reply was seen. {ev.Detail}");
                    return;
                }

                if (UnexpectedMissingLocked(it, snap) is { } unexpected)
                {
                    HaltUnexpectedLocked(it, $"Target absent in full download gen {snap.Generation}, but other friends are missing too: {unexpected}");
                    return;
                }

                FinishRemovedLocked(it, snap, $"Removed: server result 0, full download gen {snap.Generation} lacks it. {ev.Detail}", fast: true);
                return;
            }

            case SnapshotVerdict.StillPresent:
                HaltStillPresentLocked(it, snap, ev.Detail);
                return;

            default:
                HaltUnexpectedLocked(it, WithOthersMissing(it, snap, ev.Detail));
                return;
        }
    }

    private void FinishRemovedLocked(ItemState it, FriendListSnapshot snap, string detail, bool fast)
    {
        var now = clock.Monotonic;
        it.Status = RemovalItemStatus.Removed;
        it.FinishedAtUtc = clock.UtcNow;
        it.Detail = detail;
        if (it.SentAt is { } sent)
            durations.Add(now - sent);
        current = -1;
        requiredGeneration = Math.Max(requiredGeneration, snap.Generation);
        var s = Settings();
        nextActionAt = now + (fast ? FastCooldownWithJitter(s) : CooldownWithJitter(s));
        if (fast)
            suppressor?.Disarm();
        MarkChanged();
        if (!WriteVerdictLocked(it, "Removed", detail, out var ae))
            HaltLocked($"Audit VERDICT row for {it.Target} could not be written ({ae}).");
    }

    private void HaltUnexpectedLocked(ItemState it, string detail)
    {
        SetIndeterminateLocked(it, $"Unexpected: {detail}");
        WriteVerdictLocked(it, "Unexpected", detail, out var ae);
        HaltLocked($"{it.Target}: unexpected result ({detail}).{AuditSuffix(ae)}");
    }

    /// <summary>
    /// Null when the only ContentId that the send-time snapshot had and <paramref name="after"/> lacks is the target;
    /// otherwise a description of the other missing friends (or of why it cannot be checked).
    /// </summary>
    private static string? UnexpectedMissingLocked(ItemState it, FriendListSnapshot after)
    {
        if (it.SnapshotAtSend is not { } before)
            return "(no send-time snapshot to compare against)";
        var present = new HashSet<ulong>(after.Entries.Select(e => e.ContentId));
        var others = before.Entries.Where(e => e.ContentId != it.Target.ContentId && !present.Contains(e.ContentId)).ToList();
        return others.Count == 0 ? null : DescribeEntries(others);
    }

    private static string DescribeEntries(IEnumerable<FriendEntry> entries) =>
        string.Join(", ", entries.Select(e => $"{e.DisplayName}@{e.HomeWorldName} ({e.ContentId:X16})"));

    private void TickResumeLocked(TimeSpan now)
    {
        var s = Settings();
        var snap = reader.Snapshot;
        if (reader.State == FriendListState.Ready && snap != null && snap.LocalContentId == batch!.LocalContentId && snap.Generation > haltGeneration
            && snap.Source == SnapshotSource.FullDownload)
        {
            ReconcileLocked(snap, now, s);
            return;
        }

        if (now - resumeRequestedAt > s.ItemTimeout)
        {
            resumePending = false;
            activity = null;
            haltReason = $"Resume: no fresh friend-list snapshot (newer than gen {haltGeneration}) within {s.ItemTimeout.TotalSeconds:0}s " +
                         $"(reader {reader.State}). Still halted; press Resume again. Halted because: {originalHaltReason}";
            MarkChanged();
            return;
        }

        if (!resumeRefreshRequested && reader.State != FriendListState.Loading && !session.IsBetweenAreas)
        {
            resumeRefreshRequested = reader.RequestRefresh();
            if (resumeRefreshRequested)
                LogDetail("Resume: friend-list refresh requested.");
        }
    }

    private void ReconcileLocked(FriendListSnapshot snap, TimeSpan now, RemovalQueueSettings s)
    {
        string? auditError = null;
        var notes = new List<string>();
        foreach (var it in items)
        {
            if (!it.NeedsReconcile)
                continue;
            it.NeedsReconcile = false;
            var present = SendClassifier.Contains(snap, it.Target.ContentId);
            SendObservation obs;
            if (!present)
                obs = SendObservation.TargetAbsent;
            else if (it.SendParamsKnown)
                obs = SendClassifier.Classify(snap, it.Target.ContentId, batch!.LocalContentId, it.GenerationAtSend, it.RequestIdAtSend);
            else
                obs = SendObservation.PresentInPreSendData; // unknown request id: cannot prove the data is post-send

            string? ae;
            switch (obs)
            {
                case SendObservation.TargetAbsent:
                    it.Status = RemovalItemStatus.Removed;
                    it.Detail = $"Removed (reconciled): absent from post-halt gen {snap.Generation}. Before: {it.Detail}";
                    it.FinishedAtUtc = clock.UtcNow;
                    if (!WriteVerdictLocked(it, "Removed", $"(reconciled) absent from post-halt gen {snap.Generation}", out ae))
                        auditError = ae;
                    notes.Add($"{it.Target.DisplayName}: absent → Removed (reconciled)");
                    break;

                case SendObservation.PresentInPostSendDownload:
                    it.Status = RemovalItemStatus.Failed;
                    it.Detail = $"Still present in post-halt gen {snap.Generation} (download requested after the send); NOT re-sent. Before: {it.Detail}";
                    it.FinishedAtUtc = clock.UtcNow;
                    if (!WriteVerdictLocked(it, "StillPresent", $"(reconciled) still present in post-halt gen {snap.Generation}; not re-sent", out ae))
                        auditError = ae;
                    notes.Add($"{it.Target.DisplayName}: still present → Failed (not re-sent)");
                    break;

                default:
                    // Stays reconcilable: a later halt + Resume looks at it again with fresher data.
                    it.Status = RemovalItemStatus.Indeterminate;
                    it.NeedsReconcile = true;
                    it.Detail = $"Still listed in post-halt gen {snap.Generation}, but that data may predate the send" +
                                $"{(it.SendParamsKnown ? string.Empty : " (send parameters unknown)")}; left Indeterminate, NOT re-sent. Before: {it.Detail}";
                    notes.Add($"{it.Target.DisplayName}: still listed (possibly pre-send data) → stays Indeterminate (not re-sent)");
                    break;
            }
        }

        requiredGeneration = Math.Max(requiredGeneration, snap.Generation);
        resumePending = false;
        activity = null;

        // If we stay halted, the next Resume needs a snapshot newer than this one.
        haltGeneration = Math.Max(haltGeneration, snap.Generation);
        var summary = notes.Count == 0 ? "nothing to reconcile" : string.Join("; ", notes);
        Log($"Resume: reconciled against gen {snap.Generation}: {summary}.");
        MarkChanged();

        if (HaltIfMissingOutsideBatchLocked(null, snap, $"Resume reconciliation ({summary})"))
            return;

        if (resumeBlockedReason != null)
        {
            haltReason = $"Resume: reconciled ({summary}), but this batch cannot be resumed: {resumeBlockedReason}";
            return;
        }

        if (verification is { BlocksResume: true } bv)
        {
            haltReason = $"Resume: reconciled ({summary}), but the final verification is {bv.Status}; this batch cannot be resumed.";
            return;
        }

        if (auditError != null)
        {
            haltReason = $"Resume: reconciled ({summary}), but an audit VERDICT row could not be written ({auditError}). Still halted.";
            return;
        }

        if (remover.HasUnresolvedSend)
        {
            haltReason = $"Resume: reconciled ({summary}), but the remover's send latch / startup block is still set ({LatchReasonLocked()}). " +
                         "Still halted; open the in-game friend list (or clear the startup block) and press Resume again.";
            return;
        }

        var unreconciled = items.Where(i => i.NeedsReconcile).ToList();
        if (unreconciled.Count > 0)
        {
            haltReason = $"Resume: reconciled ({summary}), but {unreconciled.Count} item(s) may have been sent and cannot be reconciled yet " +
                         $"({string.Join(", ", unreconciled.Select(i => i.Target.DisplayName))}). Still halted; press Resume again later, or Cancel.";
            return;
        }

        var avail = remover.Availability;
        if (!avail.IsAvailable)
        {
            haltReason = $"Resume: reconciled ({summary}), but the remover is unavailable ({avail.Reason}). Still halted.";
            return;
        }

        if (batch!.FastMode && FastModeAvailability is { IsAvailable: false } fa)
        {
            haltReason = $"Resume: reconciled ({summary}), but fast mode is unavailable ({fa.Reason}). Still halted; Cancel and start a standard batch.";
            return;
        }

        // Items refused before any native call are known not-sent: they become retryable.
        var retried = 0;
        foreach (var it in items)
        {
            if (it.Status != RemovalItemStatus.Failed || it.MayHaveBeenSent)
                continue;
            it.Status = RemovalItemStatus.Pending;
            it.Detail = $"Back to Pending on Resume (nothing was sent). Before: {it.Detail}";
            it.StartedAtUtc = null;
            it.StartedAt = null;
            it.FinishedAtUtc = null;
            retried++;
        }

        state = RemovalQueueState.Running;
        haltReason = null;
        originalHaltReason = null;
        waitingSince = null;
        pausedSince = null;
        ClearTransientLocked();
        nextActionAt = now + (batch.FastMode ? FastCooldownWithJitter(s) : CooldownWithJitter(s));
        if (batch.FastMode)
            suppressor!.SetFastBatchActive(true);
        Log($"Resumed{(retried > 0 ? $" ({retried} never-sent Failed item(s) back to Pending)" : string.Empty)}.");
    }

    /// <summary>Logout / character change abort the batch. Returns true when it did.</summary>
    private bool CheckSessionLocked()
    {
        string? why = null;
        if (!session.IsLoggedIn || session.LogoutCount != logoutCountAtStart)
            why = "logged out";
        else if (session.LocalContentId != 0 && session.LocalContentId != batch!.LocalContentId)
            why = $"character changed ({batch.LocalContentId:X16} -> {session.LocalContentId:X16})";
        if (why == null)
            return false;

        if (InFlightLocked())
        {
            var it = items[current];
            SetIndeterminateLocked(it, $"Batch aborted ({why}) while this send awaited its verdict.");
            WriteVerdictLocked(it, "Indeterminate", it.Detail!, out _);
        }

        FinishCancelledLocked($"Batch aborted: {why}.", aborted: true);
        return true;
    }

    // ------------------------------------------------------------------ fast mode: end-of-batch reload + verification

    /// <summary>A fast batch stopped (completed / halted / cancelled / aborted): stop suppressing, schedule the one real reload.</summary>
    private void EndFastSegmentLocked(string why)
    {
        if (batch is not { FastMode: true })
            return;
        suppressor?.Disarm();
        suppressor?.SetFastBatchActive(false);
        ScheduleFinalReloadLocked($"Fast batch {why}");
    }

    /// <summary>Schedule the one real reload + verification (unless one is running, or a failed one must stay visible).</summary>
    private void ScheduleFinalReloadLocked(string why)
    {
        // A Mismatch / TimedOut result stays on screen (and keeps blocking Resume) until Clear.
        if (verification is { IsInProgress: true } or { BlocksResume: true })
            return;
        verification = new FinalVerification(FinalVerificationStatus.Pending, $"{why}: waiting to issue the one real friend-list reload.");
        verifyPhaseSince = clock.Monotonic;
        Log($"{why}: suppression off; final full reload + verification scheduled.");
        MarkChanged();
    }

    /// <summary>
    /// A real standard batch ended (completed / cancelled): verify against the full snapshot that settled its last item
    /// (no extra reload). Only when no usable full snapshot exists is one real reload scheduled instead.
    /// </summary>
    private void VerifyStandardBatchLocked(string why)
    {
        if (batch is not { FastMode: false, DryRun: false })
            return;
        if (verification is { IsInProgress: true } or { BlocksResume: true })
            return;
        if (!items.Any(i => i.MayHaveBeenSent || i.Status == RemovalItemStatus.AlreadyAbsent))
            return;

        var snap = reader.Snapshot;
        if (snap is { Source: SnapshotSource.FullDownload } && snap.LocalContentId == batch.LocalContentId && snap.Generation >= requiredGeneration)
        {
            Log($"Standard batch {why}: verifying against full snapshot gen {snap.Generation} (no extra reload).");
            VerifyAgainstLocked(snap, "last full snapshot");
            return;
        }

        ScheduleFinalReloadLocked($"Standard batch {why} (no usable full snapshot after the last item)");
    }

    private void TickVerificationLocked(TimeSpan now)
    {
        if (verification is not { IsInProgress: true } v)
            return;

        if (!session.IsLoggedIn || session.LogoutCount != logoutCountAtStart
            || (session.LocalContentId != 0 && session.LocalContentId != batch!.LocalContentId))
        {
            SetVerificationLocked(FinalVerificationStatus.Skipped,
                "Skipped: logged out / character changed before the final reload. The game reloads the friend list on login; check it in game.", null);
            return;
        }

        var s = Settings();
        if (v.Status == FinalVerificationStatus.Pending)
        {
            if (now - verifyPhaseSince > s.ItemTimeout + s.ItemTimeout)
            {
                SetVerificationLocked(FinalVerificationStatus.TimedOut,
                    $"TIMED OUT: could not issue the final reload within {(s.ItemTimeout + s.ItemTimeout).TotalSeconds:0}s (reader {reader.State}). Open the in-game friend list to reload it and check.", null);
                return;
            }

            if (session.IsBetweenAreas || reader.State == FriendListState.Loading)
            {
                SetActivity("Final reload: waiting for zoning / an in-progress download to finish");
                return;
            }

            if (!reader.RequestRefresh())
                return;

            verifyRequestedGeneration = reader.Generation;
            verifyPhaseSince = now;
            verification = v with { Status = FinalVerificationStatus.Requested, Detail = $"Final reload requested (current gen {verifyRequestedGeneration}); waiting for the full download." };
            Log($"FINAL RequestData requested (current gen {verifyRequestedGeneration}).");
            MarkChanged();
            return;
        }

        if (now - verifyPhaseSince > s.ItemTimeout)
        {
            SetVerificationLocked(FinalVerificationStatus.TimedOut,
                $"TIMED OUT: no full download within {s.ItemTimeout.TotalSeconds:0}s of the final reload request (reader {reader.State}). Open the in-game friend list to reload it and check.", null);
        }
    }

    private void TryCompleteVerificationLocked(FriendListSnapshot snap)
    {
        if (snap.Source != SnapshotSource.FullDownload || snap.Generation <= verifyRequestedGeneration || snap.LocalContentId != batch!.LocalContentId)
            return;

        VerifyAgainstLocked(snap, "full reload");
    }

    /// <summary>
    /// Every Removed / AlreadyAbsent item must be absent from <paramref name="snap"/>, and nothing else may be missing
    /// compared with the batch-start snapshot.
    /// </summary>
    private void VerifyAgainstLocked(FriendListSnapshot snap, string what)
    {
        var present = new HashSet<ulong>(snap.Entries.Select(e => e.ContentId));
        var stillListed = items
            .Where(i => i.Status is RemovalItemStatus.Removed or RemovalItemStatus.AlreadyAbsent && present.Contains(i.Target.ContentId))
            .ToList();
        var expectedGone = new HashSet<ulong>(items
            .Where(i => i.Status is RemovalItemStatus.Removed or RemovalItemStatus.AlreadyAbsent)
            .Select(i => i.Target.ContentId));
        var byId = items.ToDictionary(i => i.Target.ContentId);
        var unexpected = (batchStartSnapshot?.Entries ?? Array.Empty<FriendEntry>())
            .Where(e => !present.Contains(e.ContentId) && !expectedGone.Contains(e.ContentId))
            .ToList();
        var notInBatch = unexpected.Where(e => !byId.ContainsKey(e.ContentId)).ToList();
        var unconfirmedItems = unexpected.Where(e => byId.ContainsKey(e.ContentId)).ToList();
        var goneCount = expectedGone.Count;

        static string Name(FriendEntry e) => $"{e.DisplayName}@{e.HomeWorldName} ({e.ContentId:X16})";

        string detail;
        FinalVerificationStatus status;
        if (stillListed.Count == 0 && unexpected.Count == 0)
        {
            status = FinalVerificationStatus.Passed;
            detail = $"PASSED: {what} gen {snap.Generation} (EntryCount {snap.NativeEntryCount}): all {goneCount} removed / already-absent friend(s) are absent and nothing else is missing.";
        }
        else
        {
            status = FinalVerificationStatus.Mismatch;
            var parts = new List<string>();
            if (stillListed.Count > 0)
                parts.Add($"{stillListed.Count} marked Removed/AlreadyAbsent but STILL LISTED: {string.Join(", ", stillListed.Select(i => $"{i.Target} [{i.Status}]"))}");
            if (unexpected.Count > 0)
            {
                parts.Add($"{unexpected.Count} MISSING but not marked Removed/AlreadyAbsent: " +
                          string.Join(", ", unexpected.Select(e => $"{Name(e)}{(byId.TryGetValue(e.ContentId, out var bi) ? $" [batch item, {bi.Status}]" : " [NOT in the batch]")}")));
            }

            detail = $"MISMATCH: {what} gen {snap.Generation} (EntryCount {snap.NativeEntryCount}): {string.Join("; ", parts)}.";
        }

        SetVerificationLocked(status, detail, snap.Generation,
            stillListed.Select(i => $"{i.Target} [{i.Status}]").ToList(),
            notInBatch.Select(Name).ToList(),
            unconfirmedItems.Select(e => $"{Name(e)} [{byId[e.ContentId].Status}]").ToList());
    }

    private void SetVerificationLocked(
        FinalVerificationStatus status,
        string detail,
        long? generation,
        IReadOnlyList<string>? stillListed = null,
        IReadOnlyList<string>? missingNotInBatch = null,
        IReadOnlyList<string>? missingUnconfirmed = null)
    {
        verification = new FinalVerification(status, detail, generation)
        {
            StillListed = stillListed ?? Array.Empty<string>(),
            MissingNotInBatch = missingNotInBatch ?? Array.Empty<string>(),
            MissingUnconfirmedBatchItems = missingUnconfirmed ?? Array.Empty<string>(),
        };
        activity = state is RemovalQueueState.Running ? activity : null;
        Log($"FINAL VERIFICATION {status}: {detail}");
        if (!WriteAuditLocked(RemovalAuditTargets.Batch, AuditPhase.Verify, status.ToString(), detail, out var ae))
            verification = verification with { Detail = $"{detail} (Audit VERIFY row could not be written: {ae}.)" };
        if (verification.BlocksResume)
        {
            BlockResumeLocked($"the final verification is {status}: {detail}", missingNotInBatch);
            if (state == RemovalQueueState.Halted)
                haltReason = $"Final verification {status}; this batch cannot be resumed (Cancel, then Clear). Halted because: {originalHaltReason ?? haltReason}";
        }

        MarkChanged();
    }

    // ------------------------------------------------------------------ transitions

    private void HaltLocked(string reason)
    {
        if (cancelRequested)
        {
            // Cancel asked for the in-flight item to settle; it has (badly). Stop rather than halt.
            FinishCancelledLocked($"Cancelled by the user; the last item ended: {reason}");
            return;
        }

        state = RemovalQueueState.Halted;
        haltReason = reason;
        originalHaltReason = reason;
        pausedSince = null;
        haltGeneration = reader.Generation;
        resumePending = false;
        current = -1;
        deferredSnapshot = null;
        activity = null;
        waitingSince = null;
        Log($"HALTED: {reason}");
        EndFastSegmentLocked("halted");
        MarkChanged();
    }

    private void CompleteLocked()
    {
        state = RemovalQueueState.Completed;
        current = -1;
        pausedSince = null;
        activity = null;
        Log("Batch completed.");
        WriteBatchEndLocked("Completed");
        EndFastSegmentLocked("completed");
        VerifyStandardBatchLocked("completed");
        MarkChanged();
    }

    private void FinishCancelledLocked(string reason, bool aborted = false)
    {
        foreach (var it in items)
        {
            if (it.Status == RemovalItemStatus.Pending)
            {
                it.Status = RemovalItemStatus.Cancelled;
                it.FinishedAtUtc = clock.UtcNow;
                it.Detail = aborted ? "Not attempted (batch aborted)." : "Not attempted (batch cancelled).";
            }
            else if (it.Status is RemovalItemStatus.Sending or RemovalItemStatus.AwaitingRefresh)
            {
                // Defensive: callers settle the in-flight item first.
                SetIndeterminateLocked(it, $"Batch stopped while in flight: {reason}");
            }
        }

        state = RemovalQueueState.Cancelled;
        haltReason = reason;
        pausedSince = null;
        resumePending = false;
        current = -1;
        deferredSnapshot = null;
        activity = null;
        Log(reason);
        WriteBatchEndLocked(aborted ? "Aborted" : "Cancelled");
        EndFastSegmentLocked(aborted ? "aborted" : "cancelled");
        if (!aborted)
            VerifyStandardBatchLocked("cancelled");
        MarkChanged();
    }

    private void WriteBatchEndLocked(string result)
    {
        if (batch == null)
            return;
        var c = CountsLocked();
        var detail = $"removed={c.Removed} alreadyAbsent={c.AlreadyAbsent} dryRunOk={c.DryRunOk} failed={c.Failed} indeterminate={c.Indeterminate} cancelled={c.Cancelled} total={c.Total}" +
                     (batch.FastMode ? $" fast: sends={fastSends} replies={fastReplies} suppressed={fastSuppressed} rejected={fastRejected}" : string.Empty);
        if (!WriteAuditLocked(RemovalAuditTargets.Batch, AuditPhase.BatchEnd, result, detail, out var ae))
            haltReason = $"{(haltReason == null ? string.Empty : haltReason + " ")}(Audit BATCH_END row could not be written: {ae}.)";
    }

    private void OnExceptionLocked(string where, Exception ex)
    {
        Log($"{where} threw: {ex}");
        try
        {
            if (InFlightLocked())
            {
                var it = items[current];
                if (batch is { DryRun: true })
                {
                    SetFailedLocked(it, $"{where} exception (dry run, nothing sent): {ex.Message}");
                }
                else
                {
                    // TrySend may already have run: treat as a possible send so Resume reconciles it.
                    if (it.SentAtUtc == null)
                        MarkMaybeSentLocked(it, clock.Monotonic, null);
                    SetIndeterminateLocked(it, $"{where} exception while in flight: {ex.Message}");
                }
            }

            if (state == RemovalQueueState.Running)
            {
                HaltLocked($"{where} exception: {ex.Message}");
            }
            else if (state == RemovalQueueState.Halted)
            {
                resumePending = false;
                activity = null;
                haltReason = $"{where} exception: {ex.Message}. Still halted. Halted because: {originalHaltReason}";
                MarkChanged();
            }
        }
        catch (Exception ex2)
        {
            Log($"Halting after an exception failed too: {ex2}");
            state = RemovalQueueState.Halted;
            haltReason = $"{where} exceptions: {ex.Message} / {ex2.Message}";
            current = -1;
            try
            {
                suppressor?.Disarm();
            }
            catch
            {
                // Nothing more to do.
            }

            try
            {
                suppressor?.SetFastBatchActive(false);
            }
            catch
            {
                // Nothing more to do.
            }

            try
            {
                // Reloads may have been suppressed: the one real reload + verification is still owed.
                if (batch is { FastMode: true })
                    ScheduleFinalReloadLocked("Fast batch halted after an exception");
            }
            catch (Exception ex3)
            {
                Log($"Scheduling the final reload failed too: {ex3.Message}");
            }

            MarkChanged();
        }
    }

    // ------------------------------------------------------------------ validation

    private string? ValidateStartLocked(RemovalBatch? b, out FriendListSnapshot? snap)
    {
        snap = null;
        if (disposed)
            return "The queue is disposed.";
        if (state is RemovalQueueState.Running or RemovalQueueState.Halted)
            return $"A batch is already {state}.";
        if (verification is { IsInProgress: true })
            return "The previous fast batch's final reload / verification is still running.";
        if (b == null || b.Targets.Count == 0)
            return "The batch is empty.";
        if (externalBusy?.Invoke() is { } busy)
            return busy;
        if (!session.IsLoggedIn || session.LocalContentId == 0)
            return "Not logged in.";
        if (b.LocalContentId != session.LocalContentId)
            return $"The batch was confirmed for character {b.LocalContentId:X16}, but {session.LocalContentId:X16} is logged in.";

        // Every batch (standard, fast, dry run): the in-game Friend List window drives its own reloads.
        if (session.IsFriendListWindowOpen)
            return "Close the in-game Friend List window first; nothing is started while it is open.";

        if (b.FastMode)
        {
            if (b.DryRun)
                return "Fast mode is for real batches only (a dry run sends nothing to suppress).";
            if (suppressor == null)
                return "Fast mode is not wired in this build.";
            if (suppressor.Availability is { IsAvailable: false } fa)
                return $"Fast mode is unavailable: {fa.Reason}";
        }

        var avail = remover.Availability;
        if (!avail.IsAvailable)
            return $"Remover unavailable: {avail.Reason}";
        if (remover.StartupBlockReason is { } block)
            return $"Sends are blocked since startup: {block}";
        if (remover.Unresolved is { } u)
            return $"A previous send ({u.Target}) is still unresolved (waiting for a qualifying friend-list snapshot).";
        if (remover.HasUnresolvedSend)
            return "The remover's send latch is set.";

        if (reader.State != FriendListState.Ready)
            return $"Friend list is {reader.State}, not Ready.";
        snap = reader.Snapshot;
        if (snap == null)
            return "No friend-list snapshot.";
        if (snap.LocalContentId != b.LocalContentId)
            return $"The friend-list snapshot belongs to {snap.LocalContentId:X16}, not {b.LocalContentId:X16}.";
        if (b.SnapshotGeneration != snap.Generation)
            return $"The friend list changed since confirmation (confirmed gen {b.SnapshotGeneration}, now gen {snap.Generation}); review and confirm again.";

        var byId = new Dictionary<ulong, FriendEntry>(snap.Entries.Count);
        foreach (var e in snap.Entries)
            byId[e.ContentId] = e;

        var seen = new HashSet<ulong>();
        var missing = new List<string>();
        var pending = new List<string>();
        var odd = new List<string>();
        foreach (var t in b.Targets)
        {
            if (t.ContentId == 0)
                return $"Target {t.DisplayName}@{t.HomeWorldName} has ContentId 0.";
            if (!seen.Add(t.ContentId))
                return $"ContentId {t.ContentId:X16} ({t.DisplayName}) appears more than once in the batch.";
            if (!byId.TryGetValue(t.ContentId, out var e))
                missing.Add(t.ToString());
            else if (e.RequestState is 2 or 3)
                pending.Add($"{e.DisplayName}@{e.HomeWorldName} ({(e.RequestState == 2 ? "request sent" : "request received")})");
            else if (e.RequestState != 1)
                odd.Add($"{e.DisplayName}@{e.HomeWorldName} (request state {e.RequestState})");
        }

        if (pending.Count > 0)
            return $"{pending.Count} selected entr{(pending.Count == 1 ? "y is a pending friend request" : "ies are pending friend requests")} and cannot be removed this way: {string.Join(", ", pending)}.";
        if (odd.Count > 0)
            return $"{odd.Count} selected entr{(odd.Count == 1 ? "y has" : "ies have")} an unrecognised request state (only 1 = accepted friend is allowed): {string.Join(", ", odd)}.";
        if (missing.Count > 0)
            return $"{missing.Count} target(s) are not in the current snapshot: {string.Join(", ", missing)}.";
        return null;
    }

    // ------------------------------------------------------------------ helpers

    private RemovalQueueSettings Settings()
    {
        var s = settings() ?? RemovalQueueSettings.Default;
        static TimeSpan Clamp(TimeSpan v, TimeSpan lo, TimeSpan hi) => v < lo ? lo : v > hi ? hi : v;
        return new RemovalQueueSettings(
            Clamp(s.ItemTimeout, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5)),
            Clamp(s.Cooldown, TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            Clamp(s.CooldownJitterMax, TimeSpan.Zero, TimeSpan.FromSeconds(10)),
            Clamp(s.DryRunDelay, TimeSpan.Zero, TimeSpan.FromSeconds(10)))
        {
            FastReplyTimeout = Clamp(s.FastReplyTimeout, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1)),
            FastCooldown = Clamp(s.FastCooldown, TimeSpan.FromMilliseconds(200), TimeSpan.FromMinutes(1)),
            FastCooldownJitterMax = Clamp(s.FastCooldownJitterMax, TimeSpan.Zero, TimeSpan.FromSeconds(10)),
        };
    }

    private TimeSpan CooldownWithJitter(RemovalQueueSettings s) => s.Cooldown + Jitter(s.CooldownJitterMax);

    private TimeSpan FastCooldownWithJitter(RemovalQueueSettings s) => s.FastCooldown + Jitter(s.FastCooldownJitterMax);

    private TimeSpan Jitter(TimeSpan max)
    {
        var jitterMs = (int)max.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(jitterMs <= 0 ? 0 : random.Next(0, jitterMs + 1));
    }

    private TimeSpan AverageLocked()
    {
        if (durations.Count == 0)
            return batch is { FastMode: true } ? RemovalQueueSettings.FallbackFastItemDuration : RemovalQueueSettings.FallbackItemDuration;
        var sum = TimeSpan.Zero;
        foreach (var d in durations)
            sum += d;
        return sum / durations.Count;
    }

    private bool InFlightLocked() =>
        current >= 0 && current < items.Count && items[current].Status is RemovalItemStatus.Sending or RemovalItemStatus.AwaitingRefresh;

    private int NextPendingLocked()
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Status == RemovalItemStatus.Pending)
                return i;
        }

        return -1;
    }

    private string LatchReasonLocked()
    {
        if (remover.StartupBlockReason is { } b)
            return $"startup block: {b}";
        if (remover.Unresolved is { } u)
            return $"unresolved send of {u.Target} at gen {u.GenerationAtSend}, request id {u.CurrentRequestIdAtSend}";
        return "latch set";
    }

    private void MarkMaybeSentLocked(ItemState it, TimeSpan now, FriendListSnapshot? snapAtSend)
    {
        it.SentAtUtc = clock.UtcNow;
        it.SentAt = now;
        it.SnapshotAtSend ??= snapAtSend;
    }

    private void SetIndeterminateLocked(ItemState it, string detail)
    {
        it.Status = RemovalItemStatus.Indeterminate;
        it.FinishedAtUtc = clock.UtcNow;
        it.Detail = detail;
        it.NeedsReconcile = it.MayHaveBeenSent;
        if (it.FastSend)
            suppressor?.Disarm();
        MarkChanged();
    }

    private void SetFailedLocked(ItemState it, string detail)
    {
        it.Status = RemovalItemStatus.Failed;
        it.FinishedAtUtc = clock.UtcNow;
        it.Detail = detail;
        it.NeedsReconcile = false;
        MarkChanged();
    }

    private bool WriteVerdictLocked(ItemState it, string result, string detail, out string? error) =>
        WriteAuditLocked(it.Target, AuditPhase.Verdict, result, $"[queue] {detail}", out error);

    /// <summary>
    /// Inside a game callback (snapshot / reply) the row is queued and written at the start of the next tick, before
    /// anything else can be sent; returns true then. Otherwise written now.
    /// </summary>
    private bool WriteAuditLocked(RemovalTarget target, string phase, string result, string detail, out string? error)
    {
        error = null;
        var row = new PendingAuditRow(batch!.LocalContentId, target, batch.DryRun, phase, result, detail);
        if (callbackDepth > 0)
        {
            deferredAudit.Add(row);
            return true;
        }

        return TryWriteRowLocked(row, out error);
    }

    private bool TryWriteRowLocked(PendingAuditRow row, out string? error)
    {
        error = null;
        if (audit.TryWrite(row.Local, row.Target, row.DryRun, row.Phase, row.Result, row.Detail))
            return true;
        error = audit.LastError ?? "unknown error";
        Log($"Audit {row.Phase} row for {row.Target} NOT written: {error}");
        return false;
    }

    private void FlushDeferredAuditLocked()
    {
        if (deferredAudit.Count == 0)
            return;
        var rows = deferredAudit.ToArray();
        deferredAudit.Clear();
        foreach (var row in rows)
        {
            if (TryWriteRowLocked(row, out var err))
                continue;
            var what = $"Audit {row.Phase} row for {row.Target} could not be written ({err}).";
            if (state == RemovalQueueState.Running && batch != null)
                HaltLocked(what);
            else
                haltReason = $"{(haltReason == null ? string.Empty : haltReason + " ")}({what})";
            MarkChanged();
        }
    }

    private string DescribeProxySafe()
    {
        try
        {
            return suppressor?.DescribeProxyState() ?? "n/a";
        }
        catch (Exception ex)
        {
            return $"unreadable: {ex.Message}";
        }
    }

    private string DescribeResultSafe(uint result)
    {
        try
        {
            return suppressor?.DescribeResult(result) ?? $"LogMessage {result}";
        }
        catch (Exception ex)
        {
            return $"LogMessage {result} (text unavailable: {ex.Message})";
        }
    }

    private static string AuditSuffix(string? auditError) =>
        auditError == null ? string.Empty : $" (Audit VERDICT row could not be written: {auditError}.)";

    private void SetActivity(string text)
    {
        if (activity == text)
            return;
        activity = text;
        MarkChanged();
    }

    private RemovalQueueCounts CountsLocked()
    {
        int pending = 0, inFlight = 0, removed = 0, absent = 0, dryOk = 0, failed = 0, indet = 0, cancelled = 0;
        foreach (var it in items)
        {
            switch (it.Status)
            {
                case RemovalItemStatus.Pending: pending++; break;
                case RemovalItemStatus.Sending:
                case RemovalItemStatus.AwaitingRefresh: inFlight++; break;
                case RemovalItemStatus.Removed: removed++; break;
                case RemovalItemStatus.AlreadyAbsent: absent++; break;
                case RemovalItemStatus.DryRunOk: dryOk++; break;
                case RemovalItemStatus.Failed: failed++; break;
                case RemovalItemStatus.Indeterminate: indet++; break;
                case RemovalItemStatus.Cancelled: cancelled++; break;
            }
        }

        return new RemovalQueueCounts(items.Count, pending, inFlight, removed, absent, dryOk, failed, indet, cancelled);
    }

    private RemovalQueueItem[] ItemsLocked()
    {
        if (itemsCacheVersion == version)
            return itemsCache;
        var arr = new RemovalQueueItem[items.Count];
        for (var i = 0; i < items.Count; i++)
            arr[i] = items[i].ToRecord();
        itemsCache = arr;
        itemsCacheVersion = version;
        return arr;
    }

    private void ResetLocked()
    {
        items.Clear();
        durations.Clear();
        batch = null;
        batchStartSnapshot = null;
        state = RemovalQueueState.Idle;
        haltReason = null;
        originalHaltReason = null;
        activity = null;
        current = -1;
        requiredGeneration = 0;
        waitingSince = null;
        pausedSince = null;
        cancelRequested = false;
        resumePending = false;
        resumeRefreshRequested = false;
        haltGeneration = 0;
        ClearTransientLocked();
        deferredSnapshot = null;
        fastSends = fastReplies = fastSuppressed = fastRejected = 0;
        verification = null;
        verifyRequestedGeneration = 0;
        resumeBlockedReason = null;
        missingOutsideBatch = Array.Empty<string>();
    }

    /// <summary>Transient-refusal tracking ends: the "refresh requested" flag goes with it.</summary>
    private void ClearTransientLocked()
    {
        transientSince = null;
        transientRefreshRequested = false;
    }

    private void MarkChanged() => version++;

    private void RaiseChanged()
    {
        int v;
        lock (gate)
        {
            v = version;
            if (v == raisedVersion)
                return;
            raisedVersion = v;
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log($"A Changed handler threw: {ex.Message}");
        }
    }

    private void Log(string message)
    {
        try
        {
            log?.Invoke($"{clock.UtcNow:HH:mm:ss.fff}Z {message}");
        }
        catch
        {
            // Logging must never break the state machine.
        }
    }

    /// <summary>Per-send / per-reply / per-snapshot telemetry: the detail log when one was given, else the normal log.</summary>
    private void LogDetail(string message)
    {
        try
        {
            (detailLog ?? log)?.Invoke($"{clock.UtcNow:HH:mm:ss.fff}Z {message}");
        }
        catch
        {
            // Logging must never break the state machine.
        }
    }

    private sealed record PendingAuditRow(ulong Local, RemovalTarget Target, bool DryRun, string Phase, string Result, string Detail);

    private sealed class ItemState(RemovalTarget target)
    {
        public RemovalTarget Target { get; } = target;

        public RemovalItemStatus Status { get; set; } = RemovalItemStatus.Pending;

        public string? Detail { get; set; }

        // Display only (UTC).
        public DateTime? StartedAtUtc { get; set; }

        public DateTime? SentAtUtc { get; set; }

        public DateTime? FinishedAtUtc { get; set; }

        // Timers (monotonic).
        public TimeSpan? StartedAt { get; set; }

        public TimeSpan? SentAt { get; set; }

        public TimeSpan? ReplyAt { get; set; }

        /// <summary>A native call happened or may have happened for this item. Such an item is never sent again.</summary>
        public bool MayHaveBeenSent => SentAtUtc != null;

        public bool SendParamsKnown { get; private set; }

        public long GenerationAtSend { get; private set; }

        public byte RequestIdAtSend { get; private set; }

        public uint EntryCountBefore { get; private set; }

        /// <summary>The reader's snapshot the send was validated against (for "only the target went missing").</summary>
        public FriendListSnapshot? SnapshotAtSend { get; set; }

        public int PreSendSnapshotsSeen { get; set; }

        /// <summary>Indeterminate after a (possible) send: Resume must check it against a post-halt snapshot.</summary>
        public bool NeedsReconcile { get; set; }

        /// <summary>Sent in fast mode (verdict from the server reply + local update).</summary>
        public bool FastSend { get; set; }

        /// <summary>Server reply for this item's send, once seen.</summary>
        public ServerReply? Reply { get; set; }

        public void SetSendParams(long generation, byte requestId, uint entryCountBefore)
        {
            SendParamsKnown = true;
            GenerationAtSend = generation;
            RequestIdAtSend = requestId;
            EntryCountBefore = entryCountBefore;
        }

        public RemovalQueueItem ToRecord() =>
            new(Target, Status, Detail, SentAtUtc, SendParamsKnown ? GenerationAtSend : null, FinishedAtUtc) { StartedAtUtc = StartedAtUtc };
    }
}
