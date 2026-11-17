using System;
using SocialCleanup.Friends;

namespace SocialCleanup.Removal;

// Dalamud-free on purpose: linked into the test project together with RemovalQueueCore.

/// <summary>A real send whose effect has not yet been observed in a qualifying friend-list snapshot.</summary>
/// <param name="Target">What was sent.</param>
/// <param name="LocalContentId">Character that sent it.</param>
/// <param name="GenerationAtSend">Reader generation at send time; only strictly newer snapshots are considered.</param>
/// <param name="SentAtUtc">When the native call returned.</param>
/// <param name="NativeReturn">Raw native return value.</param>
/// <param name="CurrentRequestIdAtSend">
/// Proxy CurrentRequestId read just before the call. A newer snapshot whose download has this same id was requested
/// BEFORE the send and may carry pre-removal data; it can only release the latch by showing the target absent.
/// </param>
/// <param name="NextRequestIdAtSend">Proxy NextRequestId read just before the call (diagnostics).</param>
public sealed record UnresolvedSend(
    RemovalTarget Target,
    ulong LocalContentId,
    long GenerationAtSend,
    DateTime SentAtUtc,
    byte NativeReturn,
    byte CurrentRequestIdAtSend,
    byte NextRequestIdAtSend);

/// <summary>How a snapshot relates to an earlier real send.</summary>
public enum SendObservation
{
    /// <summary>Not newer than the send's generation; says nothing.</summary>
    NotNewer,

    /// <summary>Newer, but for a different character; says nothing about the send.</summary>
    OtherCharacter,

    /// <summary>Newer, same character, target ContentId absent.</summary>
    TargetAbsent,

    /// <summary>Newer, same character, target present, and the download was requested AFTER the send (request id changed).</summary>
    PresentInPostSendDownload,

    /// <summary>
    /// Newer, same character, target present, but the download carries the same request id as at send time (or its
    /// id is ambiguous): it was requested before the send and may hold pre-removal data. Keep waiting.
    /// </summary>
    PresentInPreSendData,
}

/// <summary>Everything observed during one <see cref="IFriendRemover.TrySend(RemovalTarget, bool, out SendDiagnostics)"/> call.</summary>
public sealed record SendDiagnostics(
    bool DryRun,
    DateTime StartedAtUtc,
    ulong LocalContentId,
    long SnapshotGeneration,
    uint EntryCountBefore,
    uint GetEntryCountBefore,
    byte NextRequestIdBefore,
    byte CurrentRequestIdBefore,
    int RawIndex,
    ulong ContentIdReReadAtIndex,
    uint ExtraFlagsAtIndex,
    bool NativeCalled,
    byte? NativeReturn,
    uint? EntryCountAfter,
    byte? NextRequestIdAfter,
    byte? CurrentRequestIdAfter,
    double NativeCallMs)
{
    /// <summary>ContentId read at <see cref="RawIndex"/> during the scan (the one immediately before the call is <see cref="ContentIdReReadAtIndex"/>).</summary>
    public ulong ContentIdAtScan { get; init; }

    /// <summary>Snapshot's raw index for the target (must equal <see cref="RawIndex"/>), or -1.</summary>
    public int SnapshotListIndex { get; init; } = -1;

    /// <summary>Snapshot's NativeEntryCount (must equal <see cref="EntryCountBefore"/>).</summary>
    public uint SnapshotNativeEntryCount { get; init; }

    /// <summary>Snapshot's CurrentRequestId (diagnostics).</summary>
    public byte SnapshotCurrentRequestId { get; init; }

    public static SendDiagnostics Empty(bool dryRun) =>
        new(dryRun, DateTime.UtcNow, 0, 0, 0, 0, 0, 0, -1, 0, 0, false, null, null, null, null, 0);
}

/// <summary>What a snapshot means for a send that is awaiting its verdict.</summary>
public enum SnapshotVerdict
{
    /// <summary>Not newer than the send: says nothing, not even worth counting.</summary>
    Ignore,

    /// <summary>Newer but holds pre-send data with the target present: keep waiting (timeout still applies).</summary>
    KeepWaiting,

    /// <summary>Target absent AND native EntryCount dropped by exactly one.</summary>
    Removed,

    /// <summary>Target present in a download requested after the send.</summary>
    StillPresent,

    /// <summary>Different character, or target absent with the wrong count delta.</summary>
    Unexpected,
}

/// <param name="Observation">Raw classification.</param>
/// <param name="Verdict">What it means for the send.</param>
/// <param name="Detail">Human-readable explanation (no trailing context).</param>
public readonly record struct SendEvaluation(SendObservation Observation, SnapshotVerdict Verdict, string Detail);

/// <summary>
/// The single source of truth for "what does this snapshot say about that send". Used by the remover's in-flight
/// latch, the single-removal test and the removal queue.
/// </summary>
public static class SendClassifier
{
    /// <summary>
    /// Classifies <paramref name="snap"/> against a send. Request-id equality/inequality of NextRequestId vs
    /// CurrentRequestId is deliberately NOT used (at rest Next == Current + 1). Only the snapshot's own download id vs
    /// the id recorded at send time matters. A <see cref="SnapshotSource.LocalAfterRemoval"/> snapshot classifies only as
    /// <see cref="SendObservation.TargetAbsent"/> or <see cref="SendObservation.PresentInPreSendData"/> (never as a
    /// post-send download), besides NotNewer / OtherCharacter.
    /// </summary>
    public static SendObservation Classify(FriendListSnapshot snap, ulong targetContentId, ulong localContentId, long generationAtSend, byte currentRequestIdAtSend)
    {
        if (snap.Generation <= generationAtSend)
            return SendObservation.NotNewer;
        if (snap.LocalContentId != localContentId)
            return SendObservation.OtherCharacter;

        foreach (var e in snap.Entries)
        {
            if (e.ContentId == targetContentId)
            {
                // A local snapshot (fast mode, read from memory after a suppressed reload) is never a download: it can
                // only show the target absent, or count as pre-send data.
                if (snap.Source == SnapshotSource.LocalAfterRemoval)
                    return SendObservation.PresentInPreSendData;
                return snap.HasStableRequestId && snap.CurrentRequestId != currentRequestIdAtSend
                    ? SendObservation.PresentInPostSendDownload
                    : SendObservation.PresentInPreSendData;
            }
        }

        return SendObservation.TargetAbsent;
    }

    /// <summary>
    /// Classifies and turns the observation into a verdict, exactly as the proven single-removal flow does:
    /// absent + EntryCount before−1 → Removed; present in a post-send download → StillPresent; pre-send data → keep
    /// waiting; other character / wrong count delta → Unexpected.
    /// </summary>
    public static SendEvaluation Evaluate(
        FriendListSnapshot snap,
        ulong targetContentId,
        ulong localContentId,
        long generationAtSend,
        byte currentRequestIdAtSend,
        uint entryCountBefore)
    {
        var obs = Classify(snap, targetContentId, localContentId, generationAtSend, currentRequestIdAtSend);
        var expected = (long)entryCountBefore - 1;
        return obs switch
        {
            SendObservation.NotNewer => new SendEvaluation(obs, SnapshotVerdict.Ignore,
                $"gen {snap.Generation} is not newer than the send's gen {generationAtSend}."),
            SendObservation.OtherCharacter => new SendEvaluation(obs, SnapshotVerdict.Unexpected,
                $"Newer snapshot belongs to a different character ({snap.LocalContentId:X16})."),
            SendObservation.PresentInPreSendData => new SendEvaluation(obs, SnapshotVerdict.KeepWaiting,
                $"gen {snap.Generation} still lists the target but its download (request id {snap.CurrentRequestId}) was requested before the send: pre-send data, ignored."),
            SendObservation.PresentInPostSendDownload => new SendEvaluation(obs, SnapshotVerdict.StillPresent,
                $"Target still present in gen {snap.Generation}, a download requested after the send (request id {currentRequestIdAtSend}->{snap.CurrentRequestId}; " +
                $"native {entryCountBefore}->{snap.NativeEntryCount})."),
            _ when snap.NativeEntryCount == expected => new SendEvaluation(obs, SnapshotVerdict.Removed,
                $"Target absent from gen {snap.Generation}; native EntryCount {entryCountBefore}->{snap.NativeEntryCount}."),
            _ => new SendEvaluation(obs, SnapshotVerdict.Unexpected,
                $"Target absent from gen {snap.Generation} but native EntryCount went {entryCountBefore}->{snap.NativeEntryCount} (expected {expected})."),
        };
    }

    /// <summary>True when <paramref name="snap"/> lists <paramref name="contentId"/>.</summary>
    public static bool Contains(FriendListSnapshot snap, ulong contentId)
    {
        foreach (var e in snap.Entries)
        {
            if (e.ContentId == contentId)
                return true;
        }

        return false;
    }
}
