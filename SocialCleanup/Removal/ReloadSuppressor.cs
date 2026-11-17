using System;
using System.Text;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using SocialCleanup.Friends;

namespace SocialCleanup.Removal;

/// <summary>
/// Fast mode's two hooks (game 2026.11.03; static analysis, see the report):
/// <list type="bullet">
/// <item>The RemoveFriend reply handler <c>void (uint, byte* payload)</c>: logs every reply (ContentId, result, flags,
/// name), raises <see cref="ReplyReceived"/>, and decides — once, before the game's handler runs, by
/// <see cref="SuppressionPolicy"/> — whether the re-download that handler triggers may be suppressed.</item>
/// <item>The friend proxy's RequestData (vtable slot 3): returns 0 without calling the game ONLY while that one decision
/// is pending inside that one handler call, for the friend proxy; otherwise passes through, tells the reader a download
/// started (any caller) and runs the raw-offset self-check.</item>
/// </list>
/// Fails closed: if a signature is missing / not unique, the vtable slot does not match, a hook fails, the reply handler
/// ever runs off the framework thread, or the raw-offset self-check fails, <see cref="Availability"/> is false with the
/// reason and nothing is ever suppressed (the hooks then only observe). Detours never throw into native code and, once
/// Dispose began, only call the original.
///
/// The detours contain no decision logic: they read game memory and hand everything to <see cref="ReloadSuppressionCore"/>
/// (Dalamud-free, unit-tested), which calls the originals inside its own try/finally.
/// </summary>
public sealed unsafe class ReloadSuppressor : IReloadSuppressor, IDisposable
{
    /// <summary>Reply handler 0x1419DECF0 (unique in .text).</summary>
    public const string ReplyHandlerSignature = "40 56 41 56 48 83 EC 48 48 8B 0D ?? ?? ?? ?? 4C 8B F2";

    /// <summary>InfoProxyFriendList RequestData 0x140969E90 (unique only with the full tail).</summary>
    public const string RequestDataSignature =
        "40 53 48 81 EC 80 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 70 0F 00 00 48 8B D9 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? " +
        "48 85 C0 0F 84 ?? ?? ?? ?? 44 0F B6 43 18 33 C9 38 8B E2 3A 00 00";

    /// <summary>Byte: 1 while a friend-list download is in flight (RequestData sets it, EndRequest clears it). Patch-fragile.</summary>
    public const int DownloadInFlightOffset = 0x3ADD;

    /// <summary>Byte: 0 while downloading, 1 once complete (RequestData clears it, EndRequest sets it). Patch-fragile.</summary>
    public const int ListCompleteOffset = 0xC8;

    private const int RequestDataVtableSlot = 3;
    private const int MaxNameBytes = 32;
    private static readonly TimeSpan InstallRetryInterval = TimeSpan.FromSeconds(1);

    private delegate void ReplyDelegate(uint targetId, byte* payload);

    private delegate byte RequestDataDelegate(InfoProxyFriendList* self);

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IGameInteropProvider interop;
    private readonly IDataManager data;
    private readonly FriendListReader reader;
    private readonly IPluginLog log;
    private readonly nint moduleBase;

    private readonly nint replyAddress;
    private readonly nint requestDataAddress;
    private readonly string? replyResolveFailure;
    private readonly string? requestDataResolveFailure;

    private Hook<ReplyDelegate>? replyHook;
    private Hook<RequestDataDelegate>? requestDataHook;

    // Captured before Enable and never cleared (valid after Dispose via OriginalDisposeSafe).
    private volatile ReplyDelegate? replyOriginal;
    private volatile RequestDataDelegate? requestDataOriginal;

    private volatile string? replyHookFailure;
    private volatile string? requestDataHookFailure;
    private DateTime nextInstallAttemptUtc = DateTime.MinValue;

    // Every decision (suppression, self-check, bookkeeping, dispose reload) lives in the Dalamud-free core.
    private readonly ReloadSuppressionCore core;
    private volatile bool disposing;

    public ReloadSuppressor(
        ISigScanner scanner,
        IGameInteropProvider interop,
        IFramework framework,
        IClientState clientState,
        IDataManager data,
        FriendListReader reader,
        IPluginLog log)
    {
        this.interop = interop;
        this.framework = framework;
        this.clientState = clientState;
        this.data = data;
        this.reader = reader;
        this.log = log;
        core = new ReloadSuppressionCore(m => log.Information($"[Fast] {Stamp} {m}"), m => log.Warning($"[Fast] {Stamp} {m}"));
        moduleBase = scanner.Module.BaseAddress;

        replyResolveFailure = SafeResolve(scanner, ReplyHandlerSignature, "Reply handler", out replyAddress);
        requestDataResolveFailure = SafeResolve(scanner, RequestDataSignature, "RequestData", out requestDataAddress);

        if (replyResolveFailure == null)
            InstallReplyHook();

        reader.SnapshotCompleted += OnSnapshotCompleted;
        framework.Update += OnFrameworkUpdate;
        log.Information($"[Fast] Created: {Availability.Reason}");
    }

    public event Action<ServerReply>? ReplyReceived;

    public int RepliesSeen => core.RepliesSeen;

    public int SuppressedCount => core.SuppressedCount;

    public FastModeAvailability Availability
    {
        get
        {
            if (disposing)
                return FastModeAvailability.Unavailable("Disposed.");
            if (replyResolveFailure != null)
                return FastModeAvailability.Unavailable(replyResolveFailure);
            if (replyHookFailure != null)
                return FastModeAvailability.Unavailable(replyHookFailure);
            if (replyHook is not { IsEnabled: true, IsDisposed: false })
                return FastModeAvailability.Unavailable("Reply-handler hook is not installed.");
            if (requestDataResolveFailure != null)
                return FastModeAvailability.Unavailable(requestDataResolveFailure);
            if (requestDataHookFailure != null)
                return FastModeAvailability.Unavailable(requestDataHookFailure);
            if (requestDataHook is not { IsEnabled: true, IsDisposed: false })
                return FastModeAvailability.Unavailable("RequestData hook not installed yet (waiting for the friend proxy).");
            if (core.Blocker is { } blocker)
                return FastModeAvailability.Unavailable(blocker);

            return FastModeAvailability.Available(
                $"Reply handler +0x{(long)(replyAddress - moduleBase):X}, RequestData +0x{(long)(requestDataAddress - moduleBase):X} (= vtable slot {RequestDataVtableSlot}), " +
                $"offsets +0x{DownloadInFlightOffset:X}/+0x{ListCompleteOffset:X} verified.");
        }
    }

    /// <summary>Framework thread. Unknown (off-thread, no proxy) counts as "in flight".</summary>
    public bool IsDownloadInFlight
    {
        get
        {
            if (!framework.IsInFrameworkUpdateThread)
                return true;
            var proxy = InfoProxyFriendList.Instance();
            return proxy == null || ReadByte(proxy, DownloadInFlightOffset) != 0;
        }
    }

    public string DescribeProxyState()
    {
        if (!framework.IsInFrameworkUpdateThread)
            return "not on the framework thread";
        var proxy = InfoProxyFriendList.Instance();
        return proxy == null ? "proxy=null" : Describe(proxy);
    }

    public void SetFastBatchActive(bool active)
    {
        if (core.SetFastBatchActive(active))
            log.Information($"[Fast] {Stamp} Fast batch {(active ? "ACTIVE: suppression possible for the armed target only" : "inactive: nothing is suppressed")}.");
    }

    public void Arm(ulong targetContentId)
    {
        core.Arm(targetContentId);
        log.Information($"[Fast] {Stamp} Armed {targetContentId:X16}.");
    }

    public void Disarm() => core.Disarm();

    public string DescribeResult(uint result)
    {
        if (result is < 1 or > 0x6FFFFFFF)
            return $"(no LogMessage row for result {result})";
        try
        {
            var sheet = data.GetExcelSheet<LogMessage>();
            if (sheet != null && sheet.TryGetRow(result, out var row))
            {
                var text = row.Text.ExtractText();
                return string.IsNullOrWhiteSpace(text) ? $"(LogMessage {result} is empty)" : text;
            }

            return $"(LogMessage {result} not found)";
        }
        catch (Exception ex)
        {
            return $"(LogMessage {result} unreadable: {ex.Message})";
        }
    }

    public void Dispose()
    {
        if (disposing)
            return;
        disposing = true;
        core.BeginDispose();

        framework.Update -= OnFrameworkUpdate;
        reader.SnapshotCompleted -= OnSnapshotCompleted;

        replyHook?.Dispose();
        replyHook = null;
        requestDataHook?.Dispose();
        requestDataHook = null;
        ReplyReceived = null;

        // Reloads suppressed since the last full download: leave the game with a fresh list (decided by the core).
        try
        {
            var proxy = framework.IsInFrameworkUpdateThread && clientState.IsLoggedIn ? InfoProxyFriendList.Instance() : null;
            var decision = core.DecideDisposeReload(proxy != null, proxy != null ? ReadFlags(proxy) : null);
            switch (decision)
            {
                case DisposeReloadDecision.Issue:
                    var ok = proxy->RequestData();
                    log.Information($"[Fast] Dispose: reloads had been suppressed; issued one real RequestData -> {ok}.");
                    break;
                case DisposeReloadDecision.CoveredByInFlightDownload:
                    log.Information("[Fast] Dispose: reloads had been suppressed; a friend-list download is already in flight and its completion refreshes the list (no extra RequestData).");
                    break;
                case DisposeReloadDecision.CannotIssue:
                    log.Warning("[Fast] Dispose: reloads had been suppressed but a reload could not be issued now; open the in-game friend list to refresh it.");
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Fast] Dispose: final RequestData failed; open the in-game friend list to refresh it.");
        }
    }

    // ------------------------------------------------------------------ install

    private string? SafeResolve(ISigScanner scanner, string signature, string what, out nint address)
    {
        address = 0;
        try
        {
            var matches = scanner.ScanAllText(signature);
            if (matches.Length == 0)
                return $"{what} signature not found.";
            if (matches.Length > 1)
                return $"{what} signature is not unique ({matches.Length} matches).";
            var off = (long)(matches[0] - moduleBase);
            var textStart = scanner.TextSectionOffset;
            if (off < textStart || off >= textStart + scanner.TextSectionSize)
                return $"{what} match +0x{off:X} is outside .text.";
            address = matches[0];
            log.Information($"[Fast] {what} resolved at ffxiv_dx11.exe+0x{off:X}.");
            return null;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[Fast] {what} signature resolution threw.");
            return $"{what} signature resolution threw: {ex.Message}";
        }
    }

    private void InstallReplyHook()
    {
        Hook<ReplyDelegate>? hook = null;
        try
        {
            hook = interop.HookFromAddress<ReplyDelegate>(replyAddress, ReplyDetour);
            replyOriginal = hook.OriginalDisposeSafe;
            replyHook = hook;
            hook.Enable();
            log.Information($"[Fast] Reply-handler hook installed at +0x{(long)(replyAddress - moduleBase):X}.");
        }
        catch (Exception ex)
        {
            // replyOriginal is deliberately kept: if Enable() partially succeeded the detour still needs it.
            hook?.Dispose();
            replyHook = null;
            replyHookFailure = $"Reply-handler hook failed: {ex.Message}";
            log.Error(ex, "[Fast] Could not hook the reply handler; fast mode unavailable.");
        }
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        if (disposing || requestDataHook != null || requestDataResolveFailure != null || requestDataHookFailure != null)
            return;
        var now = DateTime.UtcNow;
        if (now < nextInstallAttemptUtc)
            return;
        nextInstallAttemptUtc = now + InstallRetryInterval;

        Hook<RequestDataDelegate>? hook = null;
        try
        {
            var proxy = InfoProxyFriendList.Instance();
            if (proxy == null || *(nint*)proxy == 0)
                return; // UI module not up yet.

            var slot = ((nint*)*(nint*)proxy)[RequestDataVtableSlot];
            if (slot != requestDataAddress)
            {
                requestDataHookFailure = $"RequestData signature +0x{(long)(requestDataAddress - moduleBase):X} != friend proxy vtable slot {RequestDataVtableSlot} " +
                                         $"(+0x{(long)(slot - moduleBase):X}); refusing to hook.";
                log.Warning($"[Fast] {requestDataHookFailure}");
                return;
            }

            hook = interop.HookFromAddress<RequestDataDelegate>(requestDataAddress, RequestDataDetour);
            requestDataOriginal = hook.OriginalDisposeSafe;
            requestDataHook = hook;
            hook.Enable();
            log.Information($"[Fast] RequestData hook installed at +0x{(long)(requestDataAddress - moduleBase):X} (= vtable slot {RequestDataVtableSlot} of proxy 0x{(nint)proxy:X}).");
        }
        catch (Exception ex)
        {
            hook?.Dispose();
            requestDataHook = null;
            requestDataHookFailure = $"RequestData hook failed: {ex.Message}";
            log.Error(ex, "[Fast] Could not hook RequestData; fast mode unavailable.");
        }
    }

    // ------------------------------------------------------------------ detours (game thread)
    // These only read game memory and call the originals; ReloadSuppressionCore makes every decision and runs the
    // originals inside its own try/finally.

    private void ReplyDetour(uint targetId, byte* payload)
    {
        var original = replyOriginal;
        if (original == null)
            return; // Cannot happen (captured before Enable).
        var p = (nint)payload;
        core.RunReply(
            () => ObserveReplyBefore(p),
            () => original(targetId, (byte*)p),
            ObserveReplyAfter,
            RaiseReply,
            reader.PublishLocalSnapshot);
    }

    private ReplyObservation? ObserveReplyBefore(nint payloadAddress)
    {
        var payload = (byte*)payloadAddress;
        if (payload == null)
            return null;
        var onFw = framework.IsInFrameworkUpdateThread;
        var contentId = *(ulong*)payload;
        var proxy = onFw ? InfoProxyFriendList.Instance() : null;
        return new ReplyObservation(
            ContentId: contentId,
            Result: *(uint*)(payload + 8),
            Flags: payload[0xC],
            Name: ReadName(payload + 0xD),
            OnFrameworkThread: onFw,
            Proxy: (nint)proxy,
            EntryCountBefore: proxy != null ? proxy->EntryCount : 0,
            TargetPresentBefore: proxy != null && InLiveList(proxy, contentId),
            FlagsBefore: proxy != null ? ReadFlags(proxy) : ProxyFlags.Downloading,
            ReaderReady: reader.State == FriendListState.Ready,
            FastModeAvailable: Availability.IsAvailable);
    }

    private ReplyAfterObservation? ObserveReplyAfter(ReplyObservation before)
    {
        if (!framework.IsInFrameworkUpdateThread)
            return null;
        var proxy = InfoProxyFriendList.Instance();
        if (proxy == null)
            return null;
        return new ReplyAfterObservation(proxy->EntryCount, InLiveList(proxy, before.ContentId), ReadFlags(proxy));
    }

    private void RaiseReply(ServerReply reply) => ReplyReceived?.Invoke(reply);

    private byte RequestDataDetour(InfoProxyFriendList* self)
    {
        var original = requestDataOriginal;
        if (original == null)
            return 0; // Cannot happen (captured before Enable).
        var s = (nint)self;
        return core.RunRequestData(
            () => ObserveRequestData(s),
            () => original((InfoProxyFriendList*)s),
            () => ReadLiveFlags(s),
            reader.NotifyDownloadStarted);
    }

    private RequestDataObservation ObserveRequestData(nint self)
    {
        var proxy = (InfoProxyFriendList*)self;
        var live = proxy != null && proxy == InfoProxyFriendList.Instance();
        return new RequestDataObservation(self, live, framework.IsInFrameworkUpdateThread, live ? ReadFlags(proxy) : default);
    }

    private static ProxyFlags? ReadLiveFlags(nint self)
    {
        var proxy = (InfoProxyFriendList*)self;
        return proxy != null && proxy == InfoProxyFriendList.Instance() ? ReadFlags(proxy) : null;
    }

    /// <summary>Reader event; for a full download it runs inside the EndRequest detour, right after the game's EndRequest.</summary>
    private void OnSnapshotCompleted(FriendListSnapshot snap)
    {
        try
        {
            if (disposing)
                return;
            var proxy = framework.IsInFrameworkUpdateThread ? InfoProxyFriendList.Instance() : null;
            ProxyFlags? after = proxy != null ? ReadFlags(proxy) : null;
            var before = reader.LastEndRequestFlagsBefore;
            log.Information($"[Fast] {Stamp} Snapshot gen {snap.Generation} ({snap.Source}) EntryCount={snap.NativeEntryCount} entries={snap.Entries.Count} " +
                            $"requestId={snap.CurrentRequestId} ours={snap.WasRequestedByPlugin} EndRequest bytes before {before?.ToString() ?? "n/a"} now {after?.ToString() ?? "unreadable"}");
            core.OnSnapshotCompleted(snap.Source == SnapshotSource.FullDownload, before, after);
        }
        catch (Exception ex)
        {
            SafeLogError(ex, "[Fast] EndRequest self-check failed.");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string Stamp => DateTime.UtcNow.ToString("HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static byte ReadByte(InfoProxyFriendList* proxy, int offset) => *((byte*)proxy + offset);

    /// <summary>The two raw bytes the self-check verifies (+0x3ADD, +0xC8).</summary>
    internal static ProxyFlags ReadFlags(InfoProxyFriendList* proxy) =>
        new(ReadByte(proxy, DownloadInFlightOffset), ReadByte(proxy, ListCompleteOffset));

    private static string Describe(InfoProxyFriendList* p) =>
        $"EntryCount={p->EntryCount} CurrentRequestId={p->CurrentRequestId} NextRequestId={p->NextRequestId} " +
        $"+0x{DownloadInFlightOffset:X}={ReadByte(p, DownloadInFlightOffset)} +0x{ListCompleteOffset:X}={ReadByte(p, ListCompleteOffset)}";

    private static bool InLiveList(InfoProxyFriendList* proxy, ulong contentId)
    {
        var count = proxy->EntryCount;
        var cd = proxy->CharData;
        if (cd == null || count > FriendListReader.MaxPlausibleEntries)
            return false;
        for (var i = 0u; i < count; i++)
        {
            if (cd[i].ContentId == contentId)
                return true;
        }

        return false;
    }

    private static string ReadName(byte* p)
    {
        var len = 0;
        while (len < MaxNameBytes && p[len] != 0)
            len++;
        return Encoding.UTF8.GetString(p, len);
    }

    private void SafeLogError(Exception ex, string message)
    {
        try
        {
            log.Error(ex, message);
        }
        catch
        {
            // Never throw into the game.
        }
    }
}
