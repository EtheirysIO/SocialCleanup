namespace SocialCleanup.Friends;

/// <summary>
/// Friend-list group marker. Numeric values mirror
/// FFXIVClientStructs <c>InfoProxyCommonList.DisplayGroup</c> (All = -1 is a filter value, never stored on an entry).
/// </summary>
public enum FriendGroup : sbyte
{
    None = 0,
    Star = 1,
    Circle = 2,
    Triangle = 3,
    Diamond = 4,
    Heart = 5,
    Spade = 6,
    Club = 7,
}

/// <summary>
/// Immutable managed copy of one friend-list row, taken on the framework thread inside a completed refresh.
/// Holds no pointers; safe to keep and to read from any thread.
/// </summary>
/// <param name="ContentId">Character content id. Never 0 (zero rows are skipped by the reader).</param>
/// <param name="Name">Character name as the game stores it.</param>
/// <param name="HomeWorldId">World sheet row id of the home world.</param>
/// <param name="HomeWorldName">World name, "—" when the id is 0, or "#id" if the row could not be resolved.</param>
/// <param name="DataCenterName">Data center of the home world, "—" when the world id is 0, or "" if unknown.</param>
/// <param name="CurrentWorldId">World the friend is currently on (0 or stale when offline).</param>
/// <param name="CurrentWorldName">Current world name, or "" when the id is 0 / unresolved.</param>
/// <param name="OnlineStatusFlags">Raw <c>CharacterData.State</c> (OnlineStatus flags as ulong).</param>
/// <param name="IsOnline">True when the Online flag is set.</param>
/// <param name="StatusText">Short human-readable status for display.</param>
/// <param name="Group">Friend group marker.</param>
/// <param name="OnlineStatusPendingBit">
/// Raw ClientStructs <c>WaitingForFriendListApproval</c> (OnlineStatus bit 0x40), copied as-is for diagnostics.
/// NOT used for display, filtering or gating; <see cref="RequestState"/> is authoritative.
/// </param>
/// <param name="IsOtherServer">ClientStructs <c>IsOtherServer</c> flag, copied as-is.</param>
/// <param name="SortKey">Raw <c>CharacterData.Sort</c> byte.</param>
/// <param name="ListIndex">
/// Raw index into the native <c>CharData</c> array at snapshot time (the reader's loop index, which still advances
/// over skipped zero-id / duplicate rows). Not an identity; the remover only uses it to check the live list has not shifted.
/// </param>
/// <param name="ExtraFlags">Raw <c>CharacterData.ExtraFlags</c> at snapshot time.</param>
public sealed record FriendEntry(
    ulong ContentId,
    string Name,
    ushort HomeWorldId,
    string HomeWorldName,
    string DataCenterName,
    ushort CurrentWorldId,
    string CurrentWorldName,
    ulong OnlineStatusFlags,
    bool IsOnline,
    string StatusText,
    FriendGroup Group,
    bool OnlineStatusPendingBit,
    bool IsOtherServer,
    byte SortKey,
    int ListIndex,
    uint ExtraFlags)
{
    /// <summary>Shown instead of an empty name (the game shows these rows as "unable to retrieve").</summary>
    public const string UnableToRetrieveName = "(Unable to retrieve)";

    /// <summary>True when the game sent no name for this row.</summary>
    public bool IsUnableToRetrieve => Name.Length == 0;

    /// <summary><see cref="Name"/>, or <see cref="UnableToRetrieveName"/> when it is empty. Use for every display/log.</summary>
    public string DisplayName => Name.Length == 0 ? UnableToRetrieveName : Name;

    /// <summary>Friend request not yet accepted, from <see cref="RequestState"/> (2 = sent, 3 = received).</summary>
    public bool IsPendingApproval => RequestState is 2 or 3;

    /// <summary>"Request sent" / "Request received" for pending rows, otherwise null.</summary>
    public string? PendingLabel => RequestState switch
    {
        2 => "Request sent",
        3 => "Request received",
        _ => null,
    };

    /// <summary>
    /// Request-state nibble <c>(ExtraFlags &gt;&gt; 4) &amp; 0xF</c>. Static analysis: 2 = outgoing friend request,
    /// 3 = incoming friend request (the game never offers Remove for those). ClientStructs documents 0x10 (= 1) as
    /// "set when accepted". Never run in game; see <see cref="Removal.DirectFriendRemover"/>.
    /// </summary>
    public byte RequestState => (byte)((ExtraFlags >> 4) & 0xF);
}
