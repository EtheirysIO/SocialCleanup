using System;
using System.Collections.Generic;

namespace SocialCleanup.Friends;

public enum FriendListState
{
    /// <summary>No snapshot: not logged in, just logged out / switched character, or never refreshed.</summary>
    Unloaded,

    /// <summary>
    /// A download is in flight: one we requested, or a game-initiated one (seen through the RequestData hook when it is
    /// installed). <see cref="IFriendListReader.Snapshot"/> may still hold the previous complete one.
    /// </summary>
    Loading,

    /// <summary>The last EndRequest produced a complete, validated snapshot (possibly with 0 entries).</summary>
    Ready,

    /// <summary>
    /// Something is wrong (bad pointer/count, request rejected, timeout, hook missing). Consumers that act on
    /// the list must fail closed. See <see cref="IFriendListReader.StateReason"/>.
    /// </summary>
    Invalid,
}

/// <summary>One complete, immutable friend-list snapshot. Generation and owner are captured atomically with the rows.</summary>
/// <param name="Entries">Deduped rows (by ContentId), zero ContentIds skipped, in native list order.</param>
/// <param name="Generation">Monotonically increasing completion counter (1 for the first snapshot of the session).</param>
/// <param name="LocalContentId">Content id of the logged-in character when the snapshot was taken.</param>
/// <param name="CompletedAtUtc">When EndRequest was observed.</param>
/// <param name="NativeEntryCount">Raw EntryCount at EndRequest time.</param>
/// <param name="SkippedZeroIds">Rows skipped because ContentId was 0.</param>
/// <param name="SkippedDuplicates">Rows skipped because their ContentId was already seen.</param>
/// <param name="WasRequestedByPlugin">True if a RequestData call from this plugin was outstanding; false for game-initiated refreshes.</param>
/// <param name="CurrentRequestId">
/// Proxy <c>CurrentRequestId</c> read in the EndRequest detour BEFORE the original ran: the id of the download this
/// snapshot completes (RequestData sets <c>CurrentRequestId = NextRequestId++</c>).
/// </param>
/// <param name="NextRequestId">Proxy <c>NextRequestId</c> read at the same moment (diagnostics only).</param>
/// <param name="CurrentRequestIdAfterEnd">
/// <c>CurrentRequestId</c> re-read after the original EndRequest returned. When it differs from
/// <see cref="CurrentRequestId"/> the snapshot's request identity is ambiguous and consumers must not treat it as
/// proof of anything (see <see cref="HasStableRequestId"/>).
/// </param>
public sealed record FriendListSnapshot(
    IReadOnlyList<FriendEntry> Entries,
    long Generation,
    ulong LocalContentId,
    DateTime CompletedAtUtc,
    uint NativeEntryCount,
    int SkippedZeroIds,
    int SkippedDuplicates,
    bool WasRequestedByPlugin,
    byte CurrentRequestId,
    byte NextRequestId,
    byte CurrentRequestIdAfterEnd)
{
    /// <summary>CurrentRequestId was the same before and after the original EndRequest.</summary>
    public bool HasStableRequestId => CurrentRequestId == CurrentRequestIdAfterEnd;

    /// <summary>
    /// Where the rows came from. <see cref="SnapshotSource.FullDownload"/>: a completed server download (EndRequest).
    /// <see cref="SnapshotSource.LocalAfterRemoval"/>: read from live memory right after the game removed a row locally
    /// on a successful removal reply whose re-download was suppressed (fast mode); request ids are unchanged.
    /// </summary>
    public SnapshotSource Source { get; init; } = SnapshotSource.FullDownload;
}

/// <summary>How long the reader waits for the EndRequest of a download before going Invalid.</summary>
public static class FriendListTimeouts
{
    /// <summary>Floor for game-initiated downloads while a removal send is in flight or a batch is active.</summary>
    public static readonly TimeSpan MinimumWhileRemoving = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for a game-initiated download (e.g. the re-download the game starts after a removal reply).
    /// <paramref name="removalItemTimeout"/> is the active removal's item timeout, or null when no send is in flight and
    /// no batch is active; then the configured refresh timeout applies. Otherwise never less than
    /// max(<paramref name="removalItemTimeout"/>, 30 s), so the reader never declares the verdict download dead before the
    /// removal flow would.
    /// </summary>
    public static TimeSpan GameDownloadTimeout(int configuredSeconds, TimeSpan? removalItemTimeout)
    {
        var configured = TimeSpan.FromSeconds(Math.Max(1, configuredSeconds));
        if (removalItemTimeout is not { } item)
            return configured;
        var floor = item > MinimumWhileRemoving ? item : MinimumWhileRemoving;
        return configured > floor ? configured : floor;
    }
}

public enum SnapshotSource
{
    FullDownload,
    LocalAfterRemoval,
}

/// <summary>
/// Read-only view of the game's friend list. All native access happens on the framework thread;
/// everything exposed here is managed and immutable, so it can be read from the UI.
/// </summary>
public interface IFriendListReader : IDisposable
{
    FriendListState State { get; }

    /// <summary>Why the reader is Invalid / Unloaded (human readable), or null.</summary>
    string? StateReason { get; }

    /// <summary>Last COMPLETE snapshot, or null. Dropped on logout / character change.</summary>
    FriendListSnapshot? Snapshot { get; }

    /// <summary>Convenience: <c>Snapshot?.Entries</c> or an empty list.</summary>
    IReadOnlyList<FriendEntry> Entries { get; }

    /// <summary>Generation of <see cref="Snapshot"/>, or 0 when there is none. Never decreases during a plugin session.</summary>
    long Generation { get; }

    /// <summary>Owner of <see cref="Snapshot"/>, or 0 when there is none.</summary>
    ulong SnapshotLocalContentId { get; }

    /// <summary>True once the EndRequest hook is installed and enabled. Without it the reader never becomes Ready.</summary>
    bool IsCompletionHookActive { get; }

    /// <summary>
    /// Ask for a fresh list. Thread-safe; the native RequestData call is made on the next framework tick.
    /// Returns false (and does nothing) when not logged in or the completion hook is not active.
    /// </summary>
    bool RequestRefresh();

    /// <summary>
    /// Raised on the framework thread right after a new complete snapshot is published
    /// (including game-initiated refreshes, e.g. opening the in-game friend list). Handlers must not throw.
    /// </summary>
    event Action<FriendListSnapshot>? SnapshotCompleted;

    /// <summary>Raised on the framework thread whenever <see cref="State"/> changes.</summary>
    event Action<FriendListState>? StateChanged;
}
