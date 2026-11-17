using SocialCleanup.Friends;

namespace SocialCleanup.Removal;

/// <summary>
/// A removal target frozen at confirmation time. Identity is ContentId; Name and HomeWorldId are used to
/// re-verify the live row right before sending (a mismatch must fail, not "fix itself").
/// </summary>
public sealed record RemovalTarget(ulong ContentId, string Name, ushort HomeWorldId, string HomeWorldName)
{
    public static RemovalTarget From(FriendEntry entry) =>
        new(entry.ContentId, entry.Name, entry.HomeWorldId, entry.HomeWorldName);

    /// <summary><see cref="Name"/>, or "(Unable to retrieve)" when the game sent no name.</summary>
    public string DisplayName => Name.Length == 0 ? FriendEntry.UnableToRetrieveName : Name;

    public override string ToString() => $"{DisplayName}@{HomeWorldName} ({ContentId:X16})";
}

/// <summary>Whether the remover can be used at all. When false the queue must not start or resume.</summary>
public readonly record struct RemoverAvailability(bool IsAvailable, string Reason)
{
    public static RemoverAvailability Available(string reason = "OK") => new(true, reason);

    public static RemoverAvailability Unavailable(string reason) => new(false, reason);
}

public enum RemovalSendResult
{
    /// <summary>The native removal was issued. Success is NOT implied; confirm via a later friend-list refresh.</summary>
    Sent,

    /// <summary>Dry run: target re-resolved fine, nothing was sent.</summary>
    DryRunResolved,

    /// <summary>Re-resolution found no live row with this ContentId. Nothing was sent.</summary>
    NotInLiveList,

    /// <summary>A live row with this ContentId exists but name/world differ from the frozen target. Nothing was sent.</summary>
    TargetMismatch,

    /// <summary>The remover is unavailable (see <see cref="IFriendRemover.Availability"/>). Nothing was sent.</summary>
    Unavailable,

    /// <summary>Something failed before or during sending; whether anything reached the server may be unknown.</summary>
    Failed,
}

/// <param name="Result">What happened.</param>
/// <param name="Detail">Human-readable explanation for logs/UI.</param>
/// <param name="MayHaveReachedServer">True if a native send was attempted (Sent, or Failed mid-send) — the queue must then confirm or halt, never retry blindly.</param>
public sealed record RemovalSendOutcome(RemovalSendResult Result, string Detail, bool MayHaveReachedServer)
{
    /// <summary>
    /// Refused with nothing sent, for a reason expected to clear by itself (a friend-list download is in progress:
    /// reader Loading, live EntryCount / raw index differs from the snapshot, counters moved between scan and call).
    /// Never set when <see cref="MayHaveReachedServer"/> is true. The queue keeps the item Pending and waits (bounded).
    /// </summary>
    public bool Transient { get; init; }
}

/// <summary>
/// Issues one native friend removal. Implementations must fail closed: anything unexpected is
/// <see cref="RemovalSendResult.Unavailable"/> or <see cref="RemovalSendResult.Failed"/>, never a guess.
/// </summary>
public interface IFriendRemover
{
    /// <summary>Current availability (signature/function resolved, logged in, game state acceptable, ...).</summary>
    RemoverAvailability Availability { get; }

    bool IsAvailable => Availability.IsAvailable;

    string AvailabilityReason => Availability.Reason;

    /// <summary>
    /// Framework thread only. Re-resolves <paramref name="target"/> against the LIVE native friend list
    /// (ContentId, then name + home world) immediately before sending, then issues exactly one removal.
    /// Does not wait for the server; confirmation is the queue's job (via a refreshed snapshot).
    /// With <paramref name="dryRun"/> it performs the re-resolution only and never sends.
    /// </summary>
    RemovalSendOutcome TrySend(RemovalTarget target, bool dryRun);

    /// <summary>As <see cref="TrySend(RemovalTarget, bool)"/>, also returning what was observed (generation, request ids, counts).</summary>
    RemovalSendOutcome TrySend(RemovalTarget target, bool dryRun, out SendDiagnostics diag);

    /// <summary>True while every send must be refused: in-session in-flight latch, or the startup block from the audit CSV.</summary>
    bool HasUnresolvedSend { get; }

    /// <summary>The in-session in-flight latch, or null.</summary>
    UnresolvedSend? Unresolved { get; }

    /// <summary>Why sends are blocked since startup (unresolved INTENT in the audit CSV), or null.</summary>
    string? StartupBlockReason { get; }

    /// <summary>
    /// Framework thread. The ONLY latch release that is not a qualifying snapshot: the server's reply to the latched
    /// send carried a non-zero result (definitive rejection; the game neither removes the row nor reloads the list).
    /// Releases only when the in-session latch is set for exactly <paramref name="target"/>'s ContentId and
    /// <paramref name="result"/> is non-zero; never touches the startup block. Writes a RELEASED audit row.
    /// </summary>
    bool TryReleaseAfterServerRejection(RemovalTarget target, uint result, string replyDetail, out string detail);
}
