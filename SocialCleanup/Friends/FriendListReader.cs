using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using SocialCleanup.Removal;
using OnlineStatus = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyCommonList.CharacterData.OnlineStatus;

namespace SocialCleanup.Friends;

/// <summary>
/// Read-only friend-list reader.
///
/// Completion detection: the friend proxy's virtual EndRequest (vtable slot 10) is hooked. The game calls it
/// once all pages of a friend-list response have been added. The detour calls the original first, checks that
/// <c>this</c> is the live <see cref="InfoProxyFriendList"/> instance (the same function body may be shared by
/// other InfoProxy classes), then copies the native rows into a managed snapshot. Completion is never inferred
/// from EntryCount, from counts being stable, or from request-id bytes.
///
/// This class never writes to game memory and never calls anything except RequestData (a read request).
///
/// Fast mode (ReloadSuppressor): <see cref="PublishLocalSnapshot"/> publishes a snapshot read from live memory right
/// after a suppressed post-removal reload (<see cref="SnapshotSource.LocalAfterRemoval"/>), and
/// <see cref="NotifyDownloadStarted"/> puts the reader in Loading for game-initiated downloads seen by the RequestData hook.
/// </summary>
public sealed unsafe class FriendListReader : IFriendListReader
{
    /// <summary>The in-game friend list is capped at 200; anything above is treated as a garbage read.</summary>
    public const uint MaxPlausibleEntries = 200;

    private static readonly TimeSpan HookRetryInterval = TimeSpan.FromSeconds(1);

    private delegate void EndRequestDelegate(InfoProxyFriendList* self);

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly ExcelSheet<World>? worldSheet;
    private readonly Dictionary<ushort, (string World, string DataCenter)> worldCache = new();

    private static readonly long ClockOrigin = Stopwatch.GetTimestamp();

    private Hook<EndRequestDelegate>? endRequestHook;

    // Captured once, before the hook is enabled, and NEVER cleared: the detour calls the original through this so it
    // can still do so after Dispose nulled/disposed endRequestHook (OriginalDisposeSafe is valid after Dispose).
    private volatile EndRequestDelegate? endRequestOriginal;
    private string? hookFailure;
    private TimeSpan nextHookAttempt = TimeSpan.MinValue;

    // Written on the framework thread only; read from anywhere.
    private volatile FriendListSnapshot? snapshot;
    private volatile string? stateReason = "Not loaded yet.";
    private volatile int state = (int)FriendListState.Unloaded;
    private long lastGeneration;

    // Request bookkeeping (framework thread only, except the flag). Timers are monotonic.
    private int refreshRequested;
    private TimeSpan? pendingRequestSince;
    private TimeSpan? gameDownloadSince;
    private bool issuingOwnRequest;

    // Volatile: read by the detour, which may run concurrently with Dispose.
    private volatile bool disposed;

    public FriendListReader(
        IFramework framework,
        IClientState clientState,
        IPlayerState playerState,
        IGameInteropProvider interop,
        IDataManager data,
        IPluginLog log,
        Configuration config)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.playerState = playerState;
        this.interop = interop;
        this.log = log;
        this.config = config;

        try
        {
            worldSheet = data.GetExcelSheet<World>();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[FriendList] Could not load the World sheet; world names will show as ids.");
        }

        framework.Update += OnFrameworkUpdate;
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;
    }

    public FriendListState State => (FriendListState)state;

    public string? StateReason => stateReason;

    public FriendListSnapshot? Snapshot => snapshot;

    public IReadOnlyList<FriendEntry> Entries => snapshot?.Entries ?? Array.Empty<FriendEntry>();

    public long Generation => snapshot?.Generation ?? 0;

    public ulong SnapshotLocalContentId => snapshot?.LocalContentId ?? 0;

    public bool IsCompletionHookActive => endRequestHook is { IsEnabled: true, IsDisposed: false };

    public event Action<FriendListSnapshot>? SnapshotCompleted;

    public event Action<FriendListState>? StateChanged;

    public bool RequestRefresh()
    {
        // One request at a time: while Loading, the in-flight request's EndRequest (or the timeout) settles it.
        if (disposed || !IsCompletionHookActive || !clientState.IsLoggedIn || State == FriendListState.Loading)
            return false;

        Interlocked.Exchange(ref refreshRequested, 1);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        framework.Update -= OnFrameworkUpdate;
        clientState.Login -= OnLogin;
        clientState.Logout -= OnLogout;

        endRequestHook?.Dispose();
        endRequestHook = null;

        SnapshotCompleted = null;
        StateChanged = null;
        snapshot = null;
    }

    // ---------------------------------------------------------------- framework thread

    private void OnFrameworkUpdate(IFramework fw)
    {
        if (disposed)
            return;

        try
        {
            EnsureHook();
            CheckOwner();
            CheckTimeout();

            if (Interlocked.Exchange(ref refreshRequested, 0) == 1)
                IssueRequestData();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[FriendList] Framework update failed.");
            SetState(FriendListState.Invalid, $"Internal error: {ex.Message}");
        }
    }

    private void OnLogin() => Drop("Logged in; list not loaded yet.");

    private void OnLogout(int type, int code) => Drop("Logged out.");

    private void EnsureHook()
    {
        if (endRequestHook != null || hookFailure != null)
            return;

        var now = Now;
        if (now < nextHookAttempt)
            return;
        nextHookAttempt = now + HookRetryInterval;

        var proxy = InfoProxyFriendList.Instance();
        if (proxy == null || proxy->VirtualTable == null)
            return; // UI module not up yet; try again shortly.

        var address = (nint)proxy->VirtualTable->EndRequest;
        if (address == 0)
            return;

        Hook<EndRequestDelegate>? hook = null;
        try
        {
            hook = interop.HookFromAddress<EndRequestDelegate>(address, EndRequestDetour);
            endRequestOriginal = hook.OriginalDisposeSafe;
            endRequestHook = hook;
            hook.Enable();
            log.Information($"[FriendList] EndRequest hook installed at 0x{address:X} (proxy 0x{(nint)proxy:X}).");
        }
        catch (Exception ex)
        {
            // endRequestOriginal is deliberately kept: if Enable() partially succeeded the detour still needs it.
            hook?.Dispose();
            endRequestHook = null;
            hookFailure = $"EndRequest hook failed: {ex.Message}";
            log.Error(ex, "[FriendList] Could not hook InfoProxyFriendList.EndRequest; the reader will stay Invalid.");
            SetState(FriendListState.Invalid, hookFailure);
        }
    }

    /// <summary>Drop the snapshot when nobody is logged in or the logged-in character no longer owns it.</summary>
    private void CheckOwner()
    {
        var current = CurrentContentId();
        if (current == 0)
        {
            if (State != FriendListState.Unloaded || snapshot != null)
                Drop("Not logged in.");
            return;
        }

        var snap = snapshot;
        if (snap != null && snap.LocalContentId != current)
            Drop("Character changed.");
    }

    private void CheckTimeout()
    {
        if (State != FriendListState.Loading || (pendingRequestSince ?? gameDownloadSince) is not { } since)
            return;

        var ours = pendingRequestSince != null;
        TimeSpan? removalTimeout = null;
        if (!ours)
        {
            try
            {
                removalTimeout = RemovalItemTimeoutWhileActive?.Invoke();
            }
            catch (Exception ex)
            {
                // Unknown whether a removal is active: assume it is (the longer wait never sends anything).
                log.Error(ex, "[FriendList] Removal-activity query failed; using the removal floor.");
                removalTimeout = FriendListTimeouts.MinimumWhileRemoving;
            }
        }

        var timeout = ours
            ? TimeSpan.FromSeconds(Math.Max(1, config.RefreshTimeoutSeconds))
            : FriendListTimeouts.GameDownloadTimeout(config.RefreshTimeoutSeconds, removalTimeout);
        if (Now - since < timeout)
            return;

        pendingRequestSince = null;
        gameDownloadSince = null;
        Telemetry($"{(ours ? "RequestData" : "Game-initiated download")} timed out after {timeout.TotalSeconds:0}s with no EndRequest");
        SetState(FriendListState.Invalid, $"No EndRequest within {timeout.TotalSeconds:0}s of {(ours ? "RequestData" : "a game-initiated download")}.");
    }

    private void IssueRequestData()
    {
        if (!IsCompletionHookActive)
        {
            SetState(FriendListState.Invalid, hookFailure ?? "EndRequest hook is not installed.");
            return;
        }

        if (CurrentContentId() == 0 || State == FriendListState.Loading)
            return;

        var proxy = InfoProxyFriendList.Instance();
        if (proxy == null)
        {
            SetState(FriendListState.Invalid, "InfoProxyFriendList.Instance() is null.");
            return;
        }

        var before = ReadCounters(proxy);
        bool ok;
        issuingOwnRequest = true;
        try
        {
            ok = proxy->RequestData();
        }
        finally
        {
            issuingOwnRequest = false;
        }

        var after = ReadCounters(proxy);

        Telemetry($"RequestData() -> {ok} | before {before} | after {after} | gen={lastGeneration}");

        if (ok)
        {
            pendingRequestSince = Now;
            gameDownloadSince = null;
            SetState(FriendListState.Loading, null);
        }
        else
        {
            // Possibly another request already in flight; if its EndRequest arrives we still go Ready.
            pendingRequestSince = null;
            SetState(FriendListState.Invalid, "RequestData returned false (game refused the request).");
        }
    }

    // ---------------------------------------------------------------- hook (game thread)

    private void EndRequestDetour(InfoProxyFriendList* self)
    {
        // Never dereference endRequestHook here: Dispose may have nulled/disposed it while a call is in flight.
        var original = endRequestOriginal;
        if (original == null)
        {
            // Cannot happen (captured before Enable). Without a trampoline the original cannot be called; touch nothing.
            return;
        }

        // Identity check + raw counters before calling the original, so we only touch the friend proxy's fields.
        // After Dispose began nothing but the original call happens.
        var observe = !disposed;
        var isFriendProxy = false;
        uint countBefore = 0;
        byte nextBefore = 0, curBefore = 0;
        ProxyFlags? flagsBefore = null;
        if (observe)
        {
            try
            {
                isFriendProxy = self != null && self == InfoProxyFriendList.Instance();
                if (isFriendProxy)
                {
                    countBefore = self->EntryCount;
                    nextBefore = self->NextRequestId;
                    curBefore = self->CurrentRequestId;
                    flagsBefore = ReloadSuppressor.ReadFlags(self); // fast-mode self-check: bytes BEFORE EndRequest
                }
            }
            catch
            {
                isFriendProxy = false;
            }
        }

        original(self);

        if (!observe || !isFriendProxy || disposed)
            return;

        try
        {
            LastEndRequestFlagsBefore = flagsBefore;
            OnFriendListEndRequest(self, countBefore, nextBefore, curBefore);
        }
        catch (Exception ex)
        {
            if (disposed)
                return;
            log.Error(ex, "[FriendList] Snapshot after EndRequest failed.");
            SetState(FriendListState.Invalid, $"Snapshot failed: {ex.Message}");
        }
        finally
        {
            LastEndRequestFlagsBefore = null;
        }
    }

    /// <summary>
    /// Framework thread; only set while a full-download snapshot is being published from the EndRequest detour: the raw
    /// +0x3ADD / +0xC8 bytes read BEFORE the game's EndRequest ran (fast-mode self-check). Null otherwise.
    /// </summary>
    internal ProxyFlags? LastEndRequestFlagsBefore { get; private set; }

    /// <summary>
    /// Set by the plugin: while a removal send is in flight or a batch is active, the removal item timeout to respect
    /// (null otherwise). A game-initiated download is then not timed out sooner than max(that, 30 s).
    /// </summary>
    public Func<TimeSpan?>? RemovalItemTimeoutWhileActive { get; set; }

    private void OnFriendListEndRequest(InfoProxyFriendList* self, uint countBefore, byte nextBefore, byte curBefore)
    {
        var countersBefore = $"EntryCount={countBefore} NextRequestId={nextBefore} CurrentRequestId={curBefore}";
        var curAfter = self->CurrentRequestId;
        var wasOurs = pendingRequestSince != null;
        pendingRequestSince = null;
        gameDownloadSince = null;

        Telemetry($"EndRequest observed | before {countersBefore} | after {ReadCounters(self)} | ours={wasOurs} | prevGen={lastGeneration}"
                  + (framework.IsInFrameworkUpdateThread ? string.Empty : " | NOT on framework thread"));

        if (CurrentContentId() == 0)
        {
            Telemetry("EndRequest ignored: not logged in");
            return;
        }

        BuildAndPublish(self, SnapshotSource.FullDownload, wasOurs, curBefore, nextBefore, curAfter);
    }

    /// <summary>
    /// Framework thread only; called by the reload suppressor right after the game's removal-reply handler removed the
    /// row locally and its re-download was suppressed. Publishes a snapshot read from live memory with the same
    /// validation as the EndRequest path, a new Generation, <see cref="SnapshotSource.LocalAfterRemoval"/> and the
    /// unchanged request ids. Refuses (and goes Invalid, so consumers fail closed) unless the reader is Ready for the
    /// logged-in character. Returns true when a snapshot was published.
    /// </summary>
    public bool PublishLocalSnapshot(string why)
    {
        if (disposed)
            return false;
        try
        {
            if (!framework.IsInFrameworkUpdateThread)
            {
                SetState(FriendListState.Invalid, $"Local snapshot ({why}) requested off the framework thread.");
                return false;
            }

            if (State != FriendListState.Ready || snapshot is not { } prev || prev.LocalContentId != CurrentContentId() || CurrentContentId() == 0)
            {
                SetState(FriendListState.Invalid, $"Local snapshot ({why}) refused: reader is {State}, snapshot owner {snapshot?.LocalContentId ?? 0:X16}.");
                return false;
            }

            var proxy = InfoProxyFriendList.Instance();
            if (proxy == null)
            {
                SetState(FriendListState.Invalid, $"Local snapshot ({why}): InfoProxyFriendList.Instance() is null.");
                return false;
            }

            var cur = proxy->CurrentRequestId;
            Telemetry($"Local snapshot ({why}) | {ReadCounters(proxy)} | prevGen={lastGeneration}");
            return BuildAndPublish(proxy, SnapshotSource.LocalAfterRemoval, false, cur, proxy->NextRequestId, cur);
        }
        catch (Exception ex)
        {
            if (disposed)
                return false;
            log.Error(ex, $"[FriendList] Local snapshot ({why}) failed.");
            SetState(FriendListState.Invalid, $"Local snapshot failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Framework thread; called by the RequestData hook after a friend-list RequestData from ANY caller returned
    /// non-zero (not suppressed). A game-initiated download puts the reader in Loading until its EndRequest.
    /// </summary>
    internal void NotifyDownloadStarted()
    {
        if (disposed || issuingOwnRequest)
            return;
        if (CurrentContentId() == 0)
            return;
        gameDownloadSince = Now;
        Telemetry($"Game-initiated friend-list download started | gen={lastGeneration}");
        SetState(FriendListState.Loading, "Game-initiated download in progress.");
    }

    private bool BuildAndPublish(InfoProxyFriendList* self, SnapshotSource source, bool wasOurs, byte curBefore, byte nextBefore, byte curAfter)
    {
        var localId = CurrentContentId();
        var count = self->EntryCount;
        if (count > MaxPlausibleEntries)
        {
            SetState(FriendListState.Invalid, $"EntryCount {count} exceeds {MaxPlausibleEntries}; refusing to read.");
            return false;
        }

        var charData = self->CharData;
        if (count > 0 && charData == null)
        {
            SetState(FriendListState.Invalid, $"EntryCount is {count} but CharData is null.");
            return false;
        }

        var entries = new List<FriendEntry>((int)count);
        var seen = new HashSet<ulong>();
        int zero = 0, dupes = 0;

        for (var i = 0u; i < count; i++)
        {
            var cd = charData + i;
            var contentId = cd->ContentId;
            if (contentId == 0)
            {
                zero++;
                continue;
            }

            if (!seen.Add(contentId))
            {
                dupes++;
                continue;
            }

            entries.Add(ToEntry(cd, (int)i));
        }

        var gen = ++lastGeneration;
        var snap = new FriendListSnapshot(
            entries.AsReadOnly(), gen, localId, DateTime.UtcNow, count, zero, dupes, wasOurs,
            CurrentRequestId: curBefore, NextRequestId: nextBefore, CurrentRequestIdAfterEnd: curAfter)
        {
            Source = source,
        };

        if (disposed)
            return false;
        snapshot = snap;
        SetState(FriendListState.Ready, null);
        Telemetry($"Snapshot gen={gen} ({source}) published: {entries.Count} entries (native {count}, zeroIds {zero}, dupes {dupes}) owner={localId:X} " +
                  $"requestId={curBefore}{(curBefore == curAfter ? string.Empty : $"->{curAfter} (UNSTABLE)")} next={nextBefore}");

        try
        {
            SnapshotCompleted?.Invoke(snap);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[FriendList] A SnapshotCompleted handler threw.");
        }

        return true;
    }

    // ---------------------------------------------------------------- helpers

    private FriendEntry ToEntry(InfoProxyCommonList.CharacterData* cd, int index)
    {
        var flags = cd->State;
        var pendingBit = cd->WaitingForFriendListApproval; // raw, diagnostics only
        var extraFlags = cd->ExtraFlags;
        var requestState = (extraFlags >> 4) & 0xF; // same derivation as FriendEntry.RequestState / the remover
        var online = flags.HasFlag(OnlineStatus.Online);
        var (homeName, dcName) = ResolveWorld(cd->HomeWorld);
        var currentName = cd->CurrentWorld == 0 ? string.Empty : ResolveWorld(cd->CurrentWorld).World;

        return new FriendEntry(
            ContentId: cd->ContentId,
            Name: cd->NameString ?? string.Empty,
            HomeWorldId: cd->HomeWorld,
            HomeWorldName: homeName,
            DataCenterName: dcName,
            CurrentWorldId: cd->CurrentWorld,
            CurrentWorldName: currentName,
            OnlineStatusFlags: (ulong)flags,
            IsOnline: online,
            StatusText: DescribeStatus(flags, requestState, online),
            Group: (FriendGroup)(sbyte)cd->Group,
            OnlineStatusPendingBit: pendingBit,
            IsOtherServer: cd->IsOtherServer,
            SortKey: cd->Sort,
            ListIndex: index,
            ExtraFlags: extraFlags);
    }

    private static string DescribeStatus(OnlineStatus flags, uint requestState, bool online)
    {
        if (requestState == 2)
            return "Request sent";
        if (requestState == 3)
            return "Request received";
        if (!online)
            return "Offline";
        if (flags.HasFlag(OnlineStatus.AwayFromKeyboard))
            return "Online (AFK)";
        if (flags.HasFlag(OnlineStatus.Busy))
            return "Online (Busy)";
        if (flags.HasFlag(OnlineStatus.InDuty))
            return "Online (In duty)";
        return "Online";
    }

    /// <summary>Shown for world / data center when the world id is 0 (World row 0 is "Dev"; never show that).</summary>
    public const string NoWorldText = "—";

    private (string World, string DataCenter) ResolveWorld(ushort id)
    {
        if (id == 0)
            return (NoWorldText, NoWorldText);
        if (worldCache.TryGetValue(id, out var cached))
            return cached;

        var result = ($"#{id}", string.Empty);
        if (worldSheet != null && worldSheet.TryGetRow(id, out var row))
        {
            var name = row.Name.ExtractText();
            var dc = row.DataCenter.ValueNullable?.Name.ExtractText() ?? string.Empty;
            result = (string.IsNullOrEmpty(name) ? $"#{id}" : name, dc);
        }

        worldCache[id] = result;
        return result;
    }

    private ulong CurrentContentId()
    {
        if (!clientState.IsLoggedIn || !playerState.IsLoaded)
            return 0;
        return playerState.ContentId;
    }

    private void Drop(string reason)
    {
        pendingRequestSince = null;
        gameDownloadSince = null;
        Interlocked.Exchange(ref refreshRequested, 0);
        if (snapshot != null)
            Telemetry($"Snapshot gen={snapshot.Generation} dropped: {reason}");
        snapshot = null;
        if (hookFailure != null)
            SetState(FriendListState.Invalid, hookFailure);
        else
            SetState(FriendListState.Unloaded, reason);
    }

    private void SetState(FriendListState next, string? reason)
    {
        var prev = (FriendListState)Interlocked.Exchange(ref state, (int)next);
        stateReason = reason;
        if (prev == next)
            return;

        log.Debug($"[FriendList] State {prev} -> {next}{(reason is null ? string.Empty : $" ({reason})")}");
        try
        {
            StateChanged?.Invoke(next);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[FriendList] A StateChanged handler threw.");
        }
    }

    private static TimeSpan Now => Stopwatch.GetElapsedTime(ClockOrigin);

    private static string ReadCounters(InfoProxyFriendList* proxy) =>
        $"EntryCount={proxy->EntryCount} NextRequestId={proxy->NextRequestId} CurrentRequestId={proxy->CurrentRequestId}";

    private void Telemetry(string message)
    {
        if (config.FriendListTelemetry)
            log.Debug($"[FriendList] {DateTime.UtcNow:O} {message}");
    }
}
