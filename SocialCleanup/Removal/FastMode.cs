using System;
using System.Collections.Generic;

namespace SocialCleanup.Removal;

// Dalamud-free on purpose: linked into the test project. The hooks live in ReloadSuppressor.cs.

/// <summary>Whether fast mode (suppressing the friend-list re-download after a removal) may be used at all.</summary>
public readonly record struct FastModeAvailability(bool IsAvailable, string Reason)
{
    public static FastModeAvailability Available(string reason = "OK") => new(true, reason);

    public static FastModeAvailability Unavailable(string reason) => new(false, reason);
}

/// <summary>
/// One server reply to a RemoveFriend request, as seen by the reply-handler hook (framework thread, after the game's
/// handler returned).
/// </summary>
/// <param name="ContentId">ContentId in the reply payload (+0).</param>
/// <param name="Result">Result code (+8). 0 = removed; anything else is a LogMessage row id shown as an error.</param>
/// <param name="Flags">Byte at +0xC (diagnostics).</param>
/// <param name="Name">NUL-terminated name at +0xD (diagnostics).</param>
/// <param name="EntryCountBefore">Friend proxy EntryCount before the game's handler ran.</param>
/// <param name="EntryCountAfter">Friend proxy EntryCount after it returned.</param>
/// <param name="TargetPresentBefore">The ContentId was in the live list before the handler ran.</param>
/// <param name="TargetPresentAfter">The ContentId was in the live list after it returned.</param>
/// <param name="Suppressed">The handler's RequestData call was suppressed (fast mode).</param>
/// <param name="NotSuppressedReason">Why suppression did not apply (null when it did).</param>
/// <param name="ReceivedAtUtc">Display timestamp.</param>
public sealed record ServerReply(
    ulong ContentId,
    uint Result,
    byte Flags,
    string Name,
    uint EntryCountBefore,
    uint EntryCountAfter,
    bool TargetPresentBefore,
    bool TargetPresentAfter,
    bool Suppressed,
    string? NotSuppressedReason,
    DateTime ReceivedAtUtc)
{
    public override string ToString() =>
        $"cid={ContentId:X16} result={Result} flags=0x{Flags:X2} name='{Name}' EntryCount {EntryCountBefore}->{EntryCountAfter} " +
        $"present {TargetPresentBefore}->{TargetPresentAfter} {(Suppressed ? "reload SUPPRESSED" : $"reload passed ({NotSuppressedReason})")}";
}

/// <summary>
/// The reply hook + RequestData hook, as the queue sees them. All members except <see cref="Availability"/>,
/// <see cref="SetFastBatchActive"/>, <see cref="Arm"/> and <see cref="Disarm"/> are framework-thread only.
/// </summary>
public interface IReloadSuppressor
{
    /// <summary>Both hooks resolved, cross-checked and installed, and the raw-offset self-check passed.</summary>
    FastModeAvailability Availability { get; }

    /// <summary>Framework thread. True while the friend proxy says a list download is in flight (or when that cannot be read).</summary>
    bool IsDownloadInFlight { get; }

    /// <summary>Framework thread. Live counters for logging (EntryCount, request ids, raw download flags).</summary>
    string DescribeProxyState();

    /// <summary>A fast batch is running: suppression may happen (only for the armed target, see <see cref="SuppressionPolicy"/>).</summary>
    void SetFastBatchActive(bool active);

    /// <summary>The single in-flight target of a fast batch (after its RemoveFriend returned Sent).</summary>
    void Arm(ulong targetContentId);

    void Disarm();

    /// <summary>Best-effort text of the LogMessage row a non-zero result shows.</summary>
    string DescribeResult(uint result);

    /// <summary>Raised on the framework thread after the game's reply handler returned (before any local snapshot is published).</summary>
    event Action<ServerReply>? ReplyReceived;
}

/// <summary>
/// The complete, closed rule for suppressing the friend-list re-download the reply handler triggers. Shared by the real
/// hook and the tests.
/// </summary>
public static class SuppressionPolicy
{
    /// <summary>
    /// Returns null when the reload may be suppressed, otherwise the (first) reason it may not. Everything is read at
    /// reply time, before the game's handler runs: the friend list must be at rest (+0x3ADD == 0 AND +0xC8 == 1) and
    /// the reader Ready.
    /// </summary>
    public static string? WhyNotSuppress(
        bool disposing,
        bool fastModeAvailable,
        bool fastBatchActive,
        ulong armedContentId,
        ulong replyContentId,
        uint result,
        bool onFrameworkThread,
        bool proxyResolved,
        byte downloadInFlightByte,
        byte listCompleteByte,
        bool readerReady,
        bool targetInLiveList)
    {
        if (disposing)
            return "disposing";
        if (!fastModeAvailable)
            return "fast mode unavailable";
        if (!fastBatchActive)
            return "no fast batch active";
        if (armedContentId == 0)
            return "no armed target";
        if (replyContentId != armedContentId)
            return $"reply is for {replyContentId:X16}, armed target is {armedContentId:X16}";
        if (result != 0)
            return $"result {result} (server rejected; the game does not reload)";
        if (!onFrameworkThread)
            return "not on the framework thread";
        if (!proxyResolved)
            return "friend proxy not resolved";
        if (downloadInFlightByte != 0)
            return "a friend-list download is in flight";
        if (listCompleteByte != 1)
            return $"the friend list is not marked complete (+0xC8={listCompleteByte}, expected 1)";
        if (!readerReady)
            return "the friend-list reader is not Ready";
        if (!targetInLiveList)
            return "target not in the live list (local removal would not happen)";
        return null;
    }
}

public enum FinalVerificationStatus
{
    /// <summary>The batch ended; waiting to issue the one real RequestData.</summary>
    Pending,

    /// <summary>The real RequestData was issued; waiting for its full snapshot.</summary>
    Requested,

    /// <summary>Every Removed / AlreadyAbsent item is absent and nothing else went missing.</summary>
    Passed,

    /// <summary>The full snapshot disagrees with the batch's verdicts. Reported prominently and audited; Resume is refused.</summary>
    Mismatch,

    /// <summary>No full snapshot in time. Unknown; Resume is refused.</summary>
    TimedOut,

    /// <summary>Could not run (logged out / character changed). The game reloads the list on login.</summary>
    Skipped,
}

/// <summary>
/// Result of the end-of-batch check: fast batches after their one real reload; standard batches against the full
/// snapshot that settled their last item.
/// </summary>
public sealed record FinalVerification(FinalVerificationStatus Status, string Detail, long? Generation = null)
{
    public bool IsInProgress => Status is FinalVerificationStatus.Pending or FinalVerificationStatus.Requested;

    /// <summary>Mismatch / TimedOut: the batch may not be resumed (only Cancel / Clear, then a new batch from a fresh snapshot).</summary>
    public bool BlocksResume => Status is FinalVerificationStatus.Mismatch or FinalVerificationStatus.TimedOut;

    /// <summary>Mismatch: Removed / AlreadyAbsent items the full snapshot still lists.</summary>
    public IReadOnlyList<string> StillListed { get; init; } = Array.Empty<string>();

    /// <summary>Mismatch: friends missing since batch start that were NOT in the batch.</summary>
    public IReadOnlyList<string> MissingNotInBatch { get; init; } = Array.Empty<string>();

    /// <summary>Mismatch: batch items missing although they are not marked Removed / AlreadyAbsent.</summary>
    public IReadOnlyList<string> MissingUnconfirmedBatchItems { get; init; } = Array.Empty<string>();
}

/// <summary>Counters for the fast-mode part of a batch.</summary>
public readonly record struct FastModeStats(int Sends, int Replies, int Suppressed, int RejectedReplies);
