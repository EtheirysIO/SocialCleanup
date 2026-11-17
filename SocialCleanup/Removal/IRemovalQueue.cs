using System;
using System.Collections.Generic;

namespace SocialCleanup.Removal;

public enum RemovalItemStatus
{
    /// <summary>Not started.</summary>
    Pending,

    /// <summary>Re-resolving / issuing the native call right now (the single in-flight item).</summary>
    Sending,

    /// <summary>Sent; waiting for a friend-list snapshot newer than the send to confirm absence.</summary>
    AwaitingRefresh,

    /// <summary>
    /// A post-send snapshot no longer contains the ContentId, its count dropped by exactly one and nothing else went
    /// missing (fast mode additionally: the server replied result 0); or, when reconciled on Resume, a post-halt
    /// snapshot lacked it.
    /// </summary>
    Removed,

    /// <summary>Re-resolution before send found the friend already gone; nothing was sent.</summary>
    AlreadyAbsent,

    /// <summary>Dry-run batch: every check passed for this item; nothing was sent.</summary>
    DryRunOk,

    /// <summary>
    /// Not removed. Either nothing was sent (refused before the native call: mismatch, unavailable, audit failure;
    /// Resume puts these back to Pending), or it was sent and definitely did not take effect (server replied with a
    /// non-zero result in fast mode, or still present in a post-send download when reconciled on Resume; never re-sent).
    /// </summary>
    Failed,

    /// <summary>
    /// A send may have reached the server but the outcome could not be confirmed (timeout, no reply, still present after
    /// the send, unexpected count / other friends missing). Halts the queue; Resume reconciles it and never re-sends it.
    /// </summary>
    Indeterminate,

    /// <summary>Never attempted because the batch was cancelled / aborted.</summary>
    Cancelled,
}

public enum RemovalQueueState
{
    /// <summary>No batch, or the last batch was cleared.</summary>
    Idle,

    /// <summary>Processing items one at a time.</summary>
    Running,

    /// <summary>Stopped on a timeout / unclear result / failure. Only an explicit <see cref="IRemovalQueue.Resume"/> continues; there is no automatic retry.</summary>
    Halted,

    /// <summary>Every item reached a terminal status.</summary>
    Completed,

    /// <summary>Cancelled by the user, or aborted by logout / character change; remaining Pending items became Cancelled.</summary>
    Cancelled,
}

/// <summary>
/// A batch frozen at confirmation. <paramref name="LocalContentId"/> and <paramref name="SnapshotGeneration"/> record the
/// snapshot the user confirmed against; the queue must refuse to run for a different character.
/// </summary>
public sealed record RemovalBatch(
    IReadOnlyList<RemovalTarget> Targets,
    ulong LocalContentId,
    long SnapshotGeneration,
    DateTime ConfirmedAtUtc,
    bool DryRun)
{
    /// <summary>
    /// Fast mode: the friend-list re-download the game triggers after each successful removal reply is suppressed and
    /// the verdict comes from the server reply + the locally updated list. One real reload + verification at the end.
    /// Real batches only (refused together with <see cref="DryRun"/>).
    /// </summary>
    public bool FastMode { get; init; }
}

/// <param name="Target">The frozen target.</param>
/// <param name="Status">Current status.</param>
/// <param name="Detail">Latest human-readable detail (reason for Failed/Indeterminate, etc.).</param>
/// <param name="SentAtUtc">When the native call was issued, if it was.</param>
/// <param name="SentAfterGeneration">Reader generation at send time; confirmation needs a strictly newer snapshot.</param>
/// <param name="FinishedAtUtc">When a terminal status was reached.</param>
public sealed record RemovalQueueItem(
    RemovalTarget Target,
    RemovalItemStatus Status,
    string? Detail,
    DateTime? SentAtUtc,
    long? SentAfterGeneration,
    DateTime? FinishedAtUtc)
{
    /// <summary>When the queue started working on this item (TrySend entered), real or dry run.</summary>
    public DateTime? StartedAtUtc { get; init; }

    /// <summary>Time spent on this item so far (from send / start until the verdict, or until <paramref name="nowUtc"/>), or null if never started.</summary>
    public TimeSpan? ElapsedAt(DateTime nowUtc) =>
        (SentAtUtc ?? StartedAtUtc) is { } s ? (FinishedAtUtc ?? nowUtc) - s : null;
}

/// <summary>Item counts by status (a <see cref="RemovalItemStatus.Sending"/>/<see cref="RemovalItemStatus.AwaitingRefresh"/> item counts as <see cref="InFlight"/>).</summary>
public readonly record struct RemovalQueueCounts(
    int Total,
    int Pending,
    int InFlight,
    int Removed,
    int AlreadyAbsent,
    int DryRunOk,
    int Failed,
    int Indeterminate,
    int Cancelled)
{
    /// <summary>Items in a terminal status.</summary>
    public int Done => Total - Pending - InFlight;
}

/// <summary>
/// Serial removal queue: at most one item in flight; each send is confirmed by a fresh friend-list snapshot (fast mode:
/// by the server reply + the locally updated list) before the next one starts. Any unclear outcome halts the queue.
/// All members are safe to call from the UI; work happens on the framework thread.
/// </summary>
public interface IRemovalQueue : IDisposable
{
    RemovalQueueState State { get; }

    /// <summary>
    /// Why the queue is Halted (or why it was cancelled/aborted, or why the last resume did not continue), else null.
    /// </summary>
    string? HaltReason { get; }

    /// <summary>Dry-run flag of the current batch (false when Idle).</summary>
    bool IsDryRun { get; }

    /// <summary>The current batch, or null when Idle.</summary>
    RemovalBatch? Batch { get; }

    /// <summary>True while a batch (running, halted or finished but not cleared) is held.</summary>
    bool HasBatch { get; }

    /// <summary>The remover's availability (signature resolution); the queue cannot start without it.</summary>
    RemoverAvailability Availability { get; }

    /// <summary>Immutable copy of all items in batch order.</summary>
    IReadOnlyList<RemovalQueueItem> Items { get; }

    /// <summary>The one item in Sending / AwaitingRefresh, or null.</summary>
    RemovalQueueItem? InFlight { get; }

    /// <summary>Counts by status.</summary>
    RemovalQueueCounts Counts { get; }

    /// <summary>Measured average send→verdict time of this batch's confirmed removals (7.5 s until one is measured).</summary>
    TimeSpan AverageItemDuration { get; }

    /// <summary>Estimated time until the batch finishes, while Running; null otherwise.</summary>
    TimeSpan? EstimatedRemaining { get; }

    /// <summary>What the queue is doing right now ("cooldown", "waiting for zoning to finish", ...), or null.</summary>
    string? Activity { get; }

    /// <summary>Cancel was requested; the in-flight item is settling.</summary>
    bool IsCancelRequested { get; }

    /// <summary>Resume was requested; waiting for a fresh post-halt snapshot to reconcile against.</summary>
    bool IsResumePending { get; }

    /// <summary>The current batch runs in fast mode.</summary>
    bool IsFastMode { get; }

    /// <summary>Whether fast mode can be used right now (hooks + self-check), with the reason.</summary>
    FastModeAvailability FastModeAvailability { get; }

    /// <summary>Fast-mode counters for the current batch.</summary>
    FastModeStats FastStats { get; }

    /// <summary>
    /// Result of the end-of-batch verification (fast: after the one real reload; standard: against the full snapshot
    /// that settled the last item), or null before the end / for dry runs.
    /// </summary>
    FinalVerification? FinalVerification { get; }

    /// <summary>
    /// Why this batch may never be resumed (friends outside the batch went missing, or the final verification is
    /// Mismatch / TimedOut), or null. Only Cancel / Clear (then a new batch from a fresh snapshot) remain.
    /// </summary>
    string? ResumeBlockedReason { get; }

    /// <summary>Friends NOT in the batch that went missing since batch start (as observed by any verdict snapshot).</summary>
    IReadOnlyList<string> MissingOutsideBatch { get; }

    /// <summary>
    /// Start a confirmed batch. Refused (false + <paramref name="error"/>) unless the queue is not Running/Halted, the
    /// remover is available with no latch or startup block, the reader is Ready on exactly the batch's generation, the
    /// batch's LocalContentId matches the logged-in character, ContentIds are non-zero and unique, every target is
    /// in the snapshot, and none is a pending friend request (RequestState 2/3).
    /// </summary>
    bool TryStart(RemovalBatch batch, out string? error);

    /// <summary>
    /// Manual resume from Halted. Refused while a final verification runs, when it ended Mismatch / TimedOut, or when
    /// <see cref="ResumeBlockedReason"/> is set. Waits for a fresh snapshot obtained after the halt (requesting one), reconciles the
    /// halted item against it (absent → Removed (reconciled); present → Failed / stays Indeterminate — NEVER re-sent),
    /// requires the remover's latch to be clear and every possibly-sent item to be reconciled, puts Failed items that
    /// were never sent back to Pending, then continues with the next Pending item. Returns false only when the resume
    /// request itself is refused; the eventual result shows in <see cref="State"/> / <see cref="HaltReason"/>.
    /// </summary>
    bool Resume(out string? error);

    /// <summary>Stop after the in-flight item settles; remaining Pending items become Cancelled.</summary>
    void Cancel();

    /// <summary>Forget a finished/cancelled batch and return to Idle. No-op while Running or Halted (Cancel first) or while a fast batch's final verification runs.</summary>
    void Clear();

    /// <summary>Raised (framework thread) whenever State or any item changes.</summary>
    event Action? Changed;
}
