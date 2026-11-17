using System;
using System.Collections.Generic;
using System.Globalization;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using SocialCleanup.Friends;
using SocialCleanup.Safety;

namespace SocialCleanup.Removal;

// UnresolvedSend, SendObservation, SendDiagnostics and the classification rule live in SendClassifier.cs (Dalamud-free).

/// <summary>
/// Removes ONE friend by calling the game's own <c>RemoveFriend(InfoProxyFriendList* this, uint index)</c>
/// (resolved by signature + an independent call-site cross-check). Fails closed everywhere: any doubt is a refusal.
/// Never writes game memory. No retries. After a real send every further send is refused until the reader publishes a
/// newer snapshot for the same character that either lacks the target or comes from a download requested after the
/// send (the in-flight latch). The only other release is <see cref="TryReleaseAfterServerRejection"/>: the server's
/// reply to exactly the latched target carried a non-zero result. A latch left behind by a reload/crash is restored
/// from the audit CSV at startup. Refusals caused by a list download in progress are marked transient (still refused).
/// </summary>
public sealed unsafe class DirectFriendRemover : IFriendRemover, IDisposable
{
    /// <summary>Function start (unique in .text for game 2026.11.03.0000.0000).</summary>
    public const string FunctionSignature =
        "48 89 5C 24 18 48 89 74 24 20 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B 01 48 8B D9 8B FA FF 50 20";

    /// <summary>A call site of the function: <c>E8 rel32</c> at match+6.</summary>
    public const string CallSiteSignature =
        "48 8B 4B 28 8B D0 E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 8B 73 20";

    private const uint MaxEntries = FriendListReader.MaxPlausibleEntries;

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IFriendListReader reader;
    private readonly RemovalAuditLog audit;
    private readonly IPluginLog log;

    private readonly delegate* unmanaged<InfoProxyFriendList*, uint, byte> removeFriend;
    private readonly string? resolveFailure;

    private volatile UnresolvedSend? unresolved;
    private volatile StartupLatchCheck? startupBlock;
    private bool disposed;

    // RELEASED rows produced inside game callbacks (EndRequest / reply detours) are written on the next framework tick
    // (or at the start of the next TrySend, before its INTENT), never from inside the detour.
    private readonly object deferredGate = new();
    private readonly List<(ulong Local, RemovalTarget Target, string Result, string Detail)> deferredReleased = new();

    public DirectFriendRemover(
        VersionGate gate,
        ISigScanner scanner,
        IFramework framework,
        IClientState clientState,
        IPlayerState playerState,
        ICondition condition,
        IFriendListReader reader,
        RemovalAuditLog audit,
        IPluginLog log,
        StartupLatchCheck? startupLatch)
    {
        // Defence in depth: before any signature scan, subscription or native read.
        VersionGate.EnsurePassed(gate, nameof(DirectFriendRemover));

        this.framework = framework;
        this.clientState = clientState;
        this.playerState = playerState;
        this.condition = condition;
        this.reader = reader;
        this.audit = audit;
        this.log = log;

        if (startupLatch is { Blocked: true })
        {
            startupBlock = startupLatch;
            log.Warning($"[Remover] Sends BLOCKED at startup: {startupLatch.Reason}");
        }
        else if (startupLatch != null)
        {
            log.Information($"[Remover] Startup latch check: {startupLatch.Reason}");
        }

        try
        {
            resolveFailure = Resolve(scanner, out var fn);
            if (resolveFailure == null)
                removeFriend = (delegate* unmanaged<InfoProxyFriendList*, uint, byte>)fn;
        }
        catch (Exception ex)
        {
            resolveFailure = $"Signature resolution threw: {ex.Message}";
            log.Error(ex, "[Remover] Signature resolution threw.");
        }

        if (resolveFailure != null)
            log.Warning($"[Remover] UNAVAILABLE: {resolveFailure}");

        reader.SnapshotCompleted += OnSnapshotCompleted;
        framework.Update += OnFrameworkUpdate;
    }

    /// <summary>Resolved function address (0 when unresolved).</summary>
    public nint FunctionAddress { get; private set; }

    /// <summary>Resolved address relative to the game module base (0 when unresolved).</summary>
    public long FunctionOffset { get; private set; }

    /// <summary>The latched unresolved send, or null.</summary>
    public UnresolvedSend? Unresolved => unresolved;

    /// <summary>Set when the audit CSV showed a real send with no conclusive result at startup; cleared only by the user.</summary>
    public StartupLatchCheck? StartupBlock => startupBlock;

    /// <summary>True while real sends must be refused (in-session latch or startup block).</summary>
    public bool HasUnresolvedSend => unresolved != null || startupBlock != null;

    /// <summary>Availability of the native function only (resolution). Runtime conditions are checked in <see cref="TrySend(RemovalTarget, bool)"/>.</summary>
    public RemoverAvailability Availability =>
        resolveFailure != null
            ? RemoverAvailability.Unavailable(resolveFailure)
            : disposed
                ? RemoverAvailability.Unavailable("Disposed.")
                : RemoverAvailability.Available($"RemoveFriend resolved at ffxiv_dx11.exe+0x{FunctionOffset:X}");

    public bool IsAvailable => Availability.IsAvailable;

    /// <summary>Why real sends are blocked since startup, or null.</summary>
    public string? StartupBlockReason => startupBlock?.Reason;

    /// <summary>See <see cref="SendClassifier.Classify"/> (kept here for existing callers).</summary>
    public static SendObservation Classify(FriendListSnapshot snap, ulong targetContentId, ulong localContentId, long generationAtSend, byte currentRequestIdAtSend) =>
        SendClassifier.Classify(snap, targetContentId, localContentId, generationAtSend, currentRequestIdAtSend);

    /// <summary>
    /// UI thread. The user confirms they checked the in-game friend list; lifts the startup block. Writes a USERCLEARED
    /// audit row so the next startup does not block again; returns false (but still clears) if that row could not be written.
    /// </summary>
    public bool ClearStartupBlock(out string? auditError)
    {
        auditError = null;
        var b = startupBlock;
        if (b == null)
            return true;
        startupBlock = null;
        log.Warning($"[Remover] Startup send-block cleared by the user ({b.Reason}).");
        if (b.Target is { } t)
        {
            if (!audit.TryWrite(b.LocalContentId, t, false, AuditPhase.UserCleared, "UserCleared",
                    $"User confirmed they checked the friend list; INTENT at {b.IntentUtc:O}"))
            {
                auditError = audit.LastError ?? "unknown error";
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        reader.SnapshotCompleted -= OnSnapshotCompleted;
        framework.Update -= OnFrameworkUpdate;
        FlushDeferredAudit();
    }

    /// <summary>See <see cref="IFriendRemover.TryReleaseAfterServerRejection"/>.</summary>
    public bool TryReleaseAfterServerRejection(RemovalTarget target, uint result, string replyDetail, out string detail)
    {
        try
        {
            if (disposed)
            {
                detail = "remover disposed";
                return false;
            }

            if (!framework.IsInFrameworkUpdateThread)
            {
                detail = "not on the framework thread";
                return false;
            }

            if (result == 0)
            {
                detail = "result 0 is not a rejection";
                return false;
            }

            if (unresolved is not { } u)
            {
                detail = "no in-session latch is set";
                return false;
            }

            if (u.Target.ContentId != target.ContentId)
            {
                detail = $"the latch is for {u.Target}, not {target}";
                return false;
            }

            if (!clientState.IsLoggedIn || !playerState.IsLoaded || playerState.ContentId != u.LocalContentId)
            {
                detail = "the logged-in character is not the sender of the latched send";
                return false;
            }

            unresolved = null;
            detail = "released";
            log.Information($"[Remover] Latch released (ServerRejected, result {result}) for {u.Target}: {replyDetail}");
            Defer(u.LocalContentId, u.Target, "ServerRejected", $"result={result}; reply {replyDetail}; sent at gen {u.GenerationAtSend} requestId {u.CurrentRequestIdAtSend}");
            return true;
        }
        catch (Exception ex)
        {
            detail = $"release check threw: {ex.Message}";
            log.Error(ex, "[Remover] TryReleaseAfterServerRejection failed; latch unchanged.");
            return false;
        }
    }

    private void Defer(ulong local, RemovalTarget target, string result, string detail)
    {
        lock (deferredGate)
            deferredReleased.Add((local, target, result, detail));
    }

    private void OnFrameworkUpdate(IFramework fw) => FlushDeferredAudit();

    private void FlushDeferredAudit()
    {
        (ulong Local, RemovalTarget Target, string Result, string Detail)[] rows;
        lock (deferredGate)
        {
            if (deferredReleased.Count == 0)
                return;
            rows = deferredReleased.ToArray();
            deferredReleased.Clear();
        }

        foreach (var r in rows)
        {
            if (!audit.TryWrite(r.Local, r.Target, false, AuditPhase.Released, r.Result, r.Detail))
                log.Warning($"[Remover] RELEASED audit row for {r.Target} could not be written ({audit.LastError}); the next startup may block until checked.");
        }
    }

    public RemovalSendOutcome TrySend(RemovalTarget target, bool dryRun) => TrySend(target, dryRun, out _);

    /// <summary>Framework thread only. See <see cref="IFriendRemover.TrySend"/>.</summary>
    public RemovalSendOutcome TrySend(RemovalTarget target, bool dryRun, out SendDiagnostics diag)
    {
        diag = SendDiagnostics.Empty(dryRun);
        var ctx = new SendContext();
        try
        {
            return TrySendCore(target, dryRun, ref diag, ctx);
        }
        catch (Exception ex)
        {
            var localId = ctx.LocalId;
            var nativeStarted = ctx.NativeStarted;
            if (nativeStarted && unresolved == null)
                unresolved = new UnresolvedSend(target, localId, ctx.Generation, DateTime.UtcNow, 0xFF, ctx.CurrentRequestId, ctx.NextRequestId);
            log.Error(ex, $"[Remover] TrySend({target}, dryRun={dryRun}) threw (nativeStarted={nativeStarted}).");
            var outcome = new RemovalSendOutcome(
                RemovalSendResult.Failed,
                $"Exception{(nativeStarted ? " during/after the native call" : " before the native call")}: {ex.Message}",
                MayHaveReachedServer: nativeStarted);
            SafeAudit(localId, target, dryRun, nativeStarted ? AuditPhase.Outcome : AuditPhase.Refused, outcome);
            return outcome;
        }
    }

    private sealed class SendContext
    {
        public ulong LocalId;
        public bool NativeStarted;
        public long Generation;
        public byte CurrentRequestId;
        public byte NextRequestId;
    }

    private RemovalSendOutcome TrySendCore(RemovalTarget target, bool dryRun, ref SendDiagnostics diag, SendContext ctx)
    {
        FlushDeferredAudit();

        // ---- environment
        if (disposed)
            return Refuse(RemovalSendResult.Unavailable, "Remover is disposed.");
        if (resolveFailure != null || removeFriend == null)
            return Refuse(RemovalSendResult.Unavailable, resolveFailure ?? "RemoveFriend not resolved.");
        if (!framework.IsInFrameworkUpdateThread)
            return Refuse(RemovalSendResult.Unavailable, "Not on the framework thread.");
        if (!clientState.IsLoggedIn || !playerState.IsLoaded)
            return Refuse(RemovalSendResult.Unavailable, "Not logged in.");
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || condition[ConditionFlag.LoggingOut])
            return Refuse(RemovalSendResult.Unavailable, "Zoning or logging out.");

        var localId = ctx.LocalId = playerState.ContentId;
        if (localId == 0)
            return Refuse(RemovalSendResult.Unavailable, "Local ContentId is 0.");

        if (unresolved is { } pending)
            return Refuse(RemovalSendResult.Unavailable,
                $"A previous send ({pending.Target}) at generation {pending.GenerationAtSend} (request id {pending.CurrentRequestIdAtSend}) is unresolved; " +
                "waiting for a newer friend-list snapshot that lacks it or was requested after the send.");

        if (startupBlock is { } block)
            return Refuse(RemovalSendResult.Unavailable, $"Sends are blocked since startup: {block.Reason}");

        // ---- reader
        if (!reader.IsCompletionHookActive)
            return Refuse(RemovalSendResult.Unavailable, "Friend-list completion hook is not active.");
        if (reader.State != FriendListState.Ready)
            return Refuse(RemovalSendResult.Unavailable, $"Friend-list reader is {reader.State}, not Ready.", transient: reader.State == FriendListState.Loading);
        var snap = reader.Snapshot;
        if (snap == null)
            return Refuse(RemovalSendResult.Unavailable, "No friend-list snapshot.");
        if (snap.LocalContentId != localId)
            return Refuse(RemovalSendResult.Unavailable,
                $"Snapshot owner {snap.LocalContentId:X16} is not the logged-in character {localId:X16}.");

        // ---- target
        if (target.ContentId == 0)
            return Refuse(RemovalSendResult.Failed, "Target ContentId is 0.");
        FriendEntry? snapEntry = null;
        foreach (var e in snap.Entries)
        {
            if (e.ContentId == target.ContentId)
            {
                snapEntry = e;
                break;
            }
        }

        if (snapEntry == null)
            return Refuse(RemovalSendResult.NotInLiveList, "Target is not in the current friend-list snapshot.");

        diag = diag with
        {
            SnapshotListIndex = snapEntry.ListIndex,
            SnapshotNativeEntryCount = snap.NativeEntryCount,
            SnapshotCurrentRequestId = snap.CurrentRequestId,
        };

        // ---- live native list
        var proxy = InfoProxyFriendList.Instance();
        if (proxy == null)
            return Refuse(RemovalSendResult.Unavailable, "InfoProxyFriendList.Instance() is null.");
        var charData = proxy->CharData;
        if (charData == null)
            return Refuse(RemovalSendResult.Unavailable, "CharData is null.");

        var count = proxy->EntryCount;
        if (count == 0 || count > MaxEntries)
        {
            // RequestData zeroes EntryCount: 0 while the snapshot has rows means a download is in progress.
            return Refuse(RemovalSendResult.Unavailable, $"EntryCount {count} is outside 1..{MaxEntries}.",
                transient: count == 0 && snap.NativeEntryCount > 0);
        }
        var vCount = proxy->GetEntryCount();
        if (vCount != count)
            return Refuse(RemovalSendResult.Unavailable, $"GetEntryCount() {vCount} != EntryCount {count}.");

        // RequestData zeroes EntryCount, so a game-initiated download in progress shows up as a count mismatch here.
        if (count != snap.NativeEntryCount)
            return Refuse(RemovalSendResult.Unavailable,
                $"Live EntryCount {count} != snapshot NativeEntryCount {snap.NativeEntryCount} (list download in progress or list changed); refresh and re-confirm.",
                transient: true);

        var nextReq = proxy->NextRequestId;
        var curReq = proxy->CurrentRequestId;

        var idx = -1;
        var matches = 0;
        for (var i = 0u; i < count; i++)
        {
            if (charData[i].ContentId == target.ContentId)
            {
                matches++;
                if (idx < 0)
                    idx = (int)i;
            }
        }

        diag = diag with
        {
            LocalContentId = localId,
            SnapshotGeneration = snap.Generation,
            EntryCountBefore = count,
            GetEntryCountBefore = vCount,
            NextRequestIdBefore = nextReq,
            CurrentRequestIdBefore = curReq,
            RawIndex = idx,
        };

        if (matches == 0)
            return Refuse(RemovalSendResult.NotInLiveList, $"No live row has ContentId {target.ContentId:X16} (EntryCount {count}).");
        if (matches > 1)
            return Refuse(RemovalSendResult.TargetMismatch, $"{matches} live rows share ContentId {target.ContentId:X16}; refusing.");
        if (idx != snapEntry.ListIndex)
            return Refuse(RemovalSendResult.TargetMismatch,
                $"Live raw index {idx} != snapshot raw index {snapEntry.ListIndex} for {target.ContentId:X16} (list shifted); refresh and re-confirm.",
                transient: true);

        var cd = charData + idx;
        var liveName = cd->NameString ?? string.Empty;
        var liveWorld = cd->HomeWorld;
        var flags = cd->ExtraFlags;
        var requestState = (flags >> 4) & 0xF;
        diag = diag with { ContentIdAtScan = cd->ContentId, ExtraFlagsAtIndex = flags };

        if (!string.Equals(liveName, target.Name, StringComparison.Ordinal) || liveWorld != target.HomeWorldId)
            return Refuse(RemovalSendResult.TargetMismatch,
                $"Live row {idx} is '{DisplayName(liveName)}' world {liveWorld}; frozen target is '{target.DisplayName}' world {target.HomeWorldId}.");

        if (requestState is 2 or 3)
            return Refuse(RemovalSendResult.Failed,
                $"Row {idx} is a friend {(requestState == 2 ? "request you sent (outgoing)" : "request you received (incoming)")} " +
                $"(request state {requestState}, ExtraFlags 0x{flags:X8}); the game uses a different packet for those.");
        if (requestState != 1)
            return Refuse(RemovalSendResult.Failed,
                $"Row {idx} has unrecognised request state {requestState} (ExtraFlags 0x{flags:X8}); only state 1 (accepted friend) is allowed.");

        // ---- dry run stops here
        if (dryRun)
        {
            var dry = new RemovalSendOutcome(
                RemovalSendResult.DryRunResolved,
                $"index={idx} EntryCount={count} ExtraFlags=0x{flags:X8} gen={snap.Generation} CurrentRequestId={curReq} (nothing sent)",
                MayHaveReachedServer: false);
            SafeAudit(localId, target, true, AuditPhase.DryRun, dry);
            log.Information($"[Remover] DRY RUN {target}: {dry.Detail}");
            return dry;
        }

        // ---- intent row must be on disk before the call
        var intentDetail = $"index={idx} EntryCount={count} NextRequestId={nextReq} CurrentRequestId={curReq} gen={snap.Generation} snapRequestId={snap.CurrentRequestId} fn=+0x{FunctionOffset:X}";
        if (!audit.TryWrite(localId, target, false, AuditPhase.Intent, "PENDING", intentDetail))
            return Refuse(RemovalSendResult.Failed, $"Audit INTENT row could not be written ({audit.LastError}); nothing sent.", auditRow: false);

        // ---- final re-read, then the call (same frame, no allocation in between that could re-enter the game)
        if (InfoProxyFriendList.Instance() != proxy || proxy->CharData != charData || proxy->EntryCount != count
            || proxy->CurrentRequestId != curReq || proxy->NextRequestId != nextReq)
            return Refuse(RemovalSendResult.Failed, "Friend proxy / CharData / EntryCount / request ids changed between scan and call.", transient: true);
        var reRead = charData[idx].ContentId;
        diag = diag with { ContentIdReReadAtIndex = reRead };
        if (reRead != target.ContentId)
            return Refuse(RemovalSendResult.Failed, $"ContentId re-read at index {idx} is {reRead:X16}, expected {target.ContentId:X16}.");

        // Belt and braces (lists only edited locally, e.g. after suppressed reloads): the game's own accessors must agree
        // with the raw CharData[idx] the native call will read. Any disagreement is a hard (non-transient) refusal.
        var vCountFinal = proxy->GetEntryCount();
        if (vCountFinal != count)
            return Refuse(RemovalSendResult.Failed, $"GetEntryCount() {vCountFinal} != EntryCount {count} immediately before the call.");
        if (CheckGetEntry(proxy, charData, idx, target.ContentId) is { } getEntryProblem)
            return Refuse(RemovalSendResult.Failed, getEntryProblem);

        ctx.Generation = snap.Generation;
        ctx.CurrentRequestId = curReq;
        ctx.NextRequestId = nextReq;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        ctx.NativeStarted = true;
        var ret = removeFriend(proxy, (uint)idx);
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        // Latch first, before anything else can fail.
        unresolved = new UnresolvedSend(target, localId, snap.Generation, DateTime.UtcNow, ret, curReq, nextReq);

        diag = diag with
        {
            NativeCalled = true,
            NativeReturn = ret,
            EntryCountAfter = proxy->EntryCount,
            NextRequestIdAfter = proxy->NextRequestId,
            CurrentRequestIdAfter = proxy->CurrentRequestId,
            NativeCallMs = ms,
        };

        var after = $"ret={ret} index={idx} EntryCount {count}->{diag.EntryCountAfter} NextRequestId {nextReq}->{diag.NextRequestIdAfter} CurrentRequestId {curReq}->{diag.CurrentRequestIdAfter} call={ms.ToString("0.000", CultureInfo.InvariantCulture)}ms";
        var outcome = ret != 0
            ? new RemovalSendOutcome(RemovalSendResult.Sent, $"Queued (NOT confirmed): {after}", MayHaveReachedServer: true)
            : new RemovalSendOutcome(RemovalSendResult.Failed, $"Native RemoveFriend returned 0 (expected: nothing queued): {after}", MayHaveReachedServer: true);

        SafeAudit(localId, target, false, AuditPhase.Outcome, outcome);
        log.Information($"[Remover] SENT {target}: {outcome.Result} {outcome.Detail}");
        return outcome;

        // transient: nothing was sent and the cause is a list download / reshuffle in progress (the queue waits and
        // re-checks everything from scratch later). It never relaxes the refusal itself.
        RemovalSendOutcome Refuse(RemovalSendResult result, string detail, bool auditRow = true, bool transient = false)
        {
            var o = new RemovalSendOutcome(result, detail, MayHaveReachedServer: false) { Transient = transient };
            log.Information($"[Remover] REFUSED{(dryRun ? " (dry run)" : string.Empty)}{(transient ? " (transient)" : string.Empty)} {target}: {result} — {detail}");
            if (auditRow)
                SafeAudit(ctx.LocalId, target, dryRun, AuditPhase.Refused, o);
            return o;
        }
    }

    private static string DisplayName(string name) => name.Length == 0 ? FriendEntry.UnableToRetrieveName : name;

    /// <summary>Prologue of InfoProxyCommonList.GetEntry as FFXIVClientStructs signatures it (mov eax,[rcx+10h]; cmp rdx,rax; jae +0Ch).</summary>
    private static readonly byte[] GetEntryPrologue = { 0x8B, 0x41, 0x10, 0x48, 0x3B, 0xD0, 0x73, 0x0C };

    /// <summary>
    /// FFXIVClientStructs <c>InfoProxyCommonList.GetEntry(uint idx)</c> (member function, resolved by ClientStructs'
    /// signature) must return exactly <c>&amp;CharData[idx]</c> holding <paramref name="contentId"/>. Skipped (null) when
    /// ClientStructs did not resolve it; refused when its live prologue is not the signatured one (patched / hooked).
    /// <c>GetEntryByContentId(contentId, nameCrc32, a4)</c> is deliberately NOT used: its extra parameters' semantics are
    /// unknown. Returns null when OK / skipped, else the refusal detail.
    /// </summary>
    private string? CheckGetEntry(InfoProxyFriendList* proxy, InfoProxyCommonList.CharacterData* charData, int idx, ulong contentId)
    {
        var fn = InfoProxyCommonList.Addresses.GetEntry.Value;
        if (fn == 0)
        {
            log.Information("[Remover] GetEntry cross-check skipped: FFXIVClientStructs did not resolve InfoProxyCommonList.GetEntry.");
            return null;
        }

        var code = (byte*)fn;
        for (var i = 0; i < GetEntryPrologue.Length; i++)
        {
            if (code[i] != GetEntryPrologue[i])
                return $"InfoProxyCommonList.GetEntry at 0x{fn:X} does not start with its signature bytes (patched or hooked); refusing.";
        }

        var expected = charData + idx;
        var got = proxy->GetEntry((uint)idx);
        if (got != expected)
            return $"GetEntry({idx}) returned 0x{(nint)got:X}, expected &CharData[{idx}] = 0x{(nint)expected:X}; refusing.";
        if (got->ContentId != contentId)
            return $"GetEntry({idx})->ContentId is {got->ContentId:X16}, expected {contentId:X16}; refusing.";
        return null;
    }

    private void SafeAudit(ulong localId, RemovalTarget target, bool dryRun, string phase, RemovalSendOutcome o)
    {
        try
        {
            audit.TryWrite(localId, target, dryRun, phase, o.Result.ToString(), o.Detail);
        }
        catch
        {
            // TryWrite never throws; belt and braces.
        }
    }

    private void OnSnapshotCompleted(FriendListSnapshot snap)
    {
        try
        {
            var u = unresolved;
            if (u == null)
                return;

            var obs = Classify(snap, u.Target.ContentId, u.LocalContentId, u.GenerationAtSend, u.CurrentRequestIdAtSend);
            var ids = $"snapshot gen {snap.Generation} requestId {snap.CurrentRequestId}{(snap.HasStableRequestId ? string.Empty : $"->{snap.CurrentRequestIdAfterEnd}")}; " +
                      $"send at gen {u.GenerationAtSend} requestId {u.CurrentRequestIdAtSend}";
            switch (obs)
            {
                case SendObservation.TargetAbsent:
                case SendObservation.PresentInPostSendDownload:
                    unresolved = null;
                    log.Information($"[Remover] Latch released ({obs}, {snap.Source}) for {u.Target}: {ids}.");
                    Defer(u.LocalContentId, u.Target, obs.ToString(), $"{ids}; source {snap.Source}");
                    break;

                case SendObservation.PresentInPreSendData:
                    log.Information($"[Remover] Latch KEPT for {u.Target}: target present in pre-send data ({ids}).");
                    break;

                case SendObservation.OtherCharacter:
                    log.Information($"[Remover] Latch KEPT for {u.Target}: snapshot belongs to {snap.LocalContentId:X16} ({ids}).");
                    break;
            }
        }
        catch (Exception ex)
        {
            // Latch stays set.
            log.Error(ex, "[Remover] Snapshot handler failed; latch unchanged.");
        }
    }

    // ------------------------------------------------------------------ resolution

    private string? Resolve(ISigScanner scanner, out nint function)
    {
        function = 0;
        var moduleBase = scanner.Module.BaseAddress;

        var fnMatches = scanner.ScanAllText(FunctionSignature);
        if (fnMatches.Length == 0)
            return "RemoveFriend function signature not found.";
        if (fnMatches.Length > 1)
            return $"RemoveFriend function signature is not unique ({fnMatches.Length} matches).";

        var csMatches = scanner.ScanAllText(CallSiteSignature);
        if (csMatches.Length == 0)
            return "RemoveFriend call-site signature not found.";
        if (csMatches.Length > 1)
            return $"RemoveFriend call-site signature is not unique ({csMatches.Length} matches).";

        var fn = fnMatches[0];
        var site = csMatches[0];

        // Matches are live addresses; the pattern was matched against the scanner's clean copy of the module when
        // IsCopy (SearchBase = copy base), so read E8/rel32 from the same clean copy, then require live bytes to agree.
        var siteOffset = (long)(site - moduleBase);
        var textStart = scanner.TextSectionOffset;
        var textEnd = textStart + scanner.TextSectionSize;
        if (siteOffset < textStart || siteOffset + 11 > textEnd)
            return $"Call-site match +0x{siteOffset:X} is outside .text (+0x{textStart:X}..+0x{textEnd:X}); refusing.";
        var fnOffset = (long)(fn - moduleBase);
        if (fnOffset < textStart || fnOffset >= textEnd)
            return $"Function match +0x{fnOffset:X} is outside .text; refusing.";

        var cleanSite = scanner.SearchBase + (nint)siteOffset;
        var cleanOp = *(byte*)(cleanSite + 6);
        var cleanRel = *(int*)(cleanSite + 7);
        var liveOp = *(byte*)(site + 6);
        var liveRel = *(int*)(site + 7);

        if (cleanOp != 0xE8)
            return $"Call-site match has 0x{cleanOp:X2}, not E8, at +6 in the clean copy.";
        if (liveOp != cleanOp || liveRel != cleanRel)
            return $"Call-site bytes differ between the clean copy (E8 {cleanRel:X8}) and live memory ({liveOp:X2} {liveRel:X8}); " +
                   "something has patched the call site. Refusing.";

        var target = site + 11 + cleanRel;

        log.Information(
            $"[Remover] RemoveFriend sig at +0x{fnOffset:X}; call site at +0x{siteOffset:X} -> +0x{(long)(target - moduleBase):X} " +
            $"(module base 0x{moduleBase:X}, scanner copy={scanner.IsCopy}).");

        if (target != fn)
            return $"Call-site target +0x{(long)(target - moduleBase):X} != function sig +0x{fnOffset:X}.";

        FunctionAddress = fn;
        FunctionOffset = fnOffset;
        function = fn;
        return null;
    }
}
