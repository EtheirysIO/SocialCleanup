using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using SocialCleanup.Friends;

namespace SocialCleanup.Removal;

public enum SingleTestPhase
{
    /// <summary>Nothing requested (or the last test was cleared).</summary>
    Idle,

    /// <summary>Requested from the UI; executes on the next framework tick.</summary>
    Requested,

    /// <summary>Native call returned 1; waiting for a newer snapshot that lacks the target or was requested after the send.</summary>
    AwaitingSnapshot,

    /// <summary>A verdict was reached. Further snapshots/chat in the observation window are still logged.</summary>
    Finished,
}

public enum SingleTestVerdict
{
    None,

    /// <summary>Dry run: every check passed, nothing was sent.</summary>
    DryRunResolved,

    /// <summary>The remover refused before the native call; nothing was sent.</summary>
    Refused,

    /// <summary>The native call returned 0 (analysis: nothing queued). The send latch stays until a newer snapshot.</summary>
    NotQueued,

    /// <summary>Target absent from a newer complete snapshot AND native EntryCount dropped by exactly one.</summary>
    Removed,

    /// <summary>Target still present in a newer snapshot whose download was requested AFTER the send.</summary>
    StillPresent,

    /// <summary>Absent but the count delta is wrong, character changed, or another anomaly.</summary>
    Unexpected,

    /// <summary>
    /// No qualifying snapshot within the timeout (none at all, or only pre-send data), or an exception after the send
    /// may have been attempted. Nothing further is done.
    /// </summary>
    Indeterminate,
}

/// <summary>
/// Developer harness for exactly ONE removal (or dry run) at a time. Requests come from the UI and execute on the
/// framework thread. Records telemetry to the plugin log, keeps it in memory for the UI, and writes the verdict to
/// the audit CSV. Never retries, never sends twice, never triggers a refresh on its own.
/// </summary>
public sealed unsafe class SingleRemovalTest : IDisposable
{
    /// <summary>LogMessage row the client prints on a successful removal ("&lt;name&gt; removed from friend list.").</summary>
    public const uint RemovedLogMessageId = 12;

    /// <summary>How long after the verdict we keep logging snapshots / chat for diagnosis.</summary>
    private static readonly TimeSpan ObservationTail = TimeSpan.FromSeconds(30);

    private const int MaxLines = 300;

    private readonly IFramework framework;
    private readonly IChatGui chat;
    private readonly IFriendListReader reader;
    private readonly DirectFriendRemover remover;
    private readonly RemovalAuditLog audit;
    private readonly Configuration config;
    private readonly IPluginLog log;

    private readonly object gate = new();
    private readonly List<string> lines = new();
    private string[] linesCache = Array.Empty<string>();
    private int linesVersion;
    private int linesCacheVersion = -1;

    private readonly string[] removedTextFragments;

    // Request (UI → framework thread).
    private RemovalTarget? requestedTarget;
    private bool requestedDryRun;
    private long requestedGeneration;
    private ulong requestedLocalId;

    // Test state (framework thread writes; UI reads).
    private volatile int phase = (int)SingleTestPhase.Idle;
    private volatile int verdict = (int)SingleTestVerdict.None;
    private volatile string? verdictDetail;
    private volatile RemovalTarget? target;
    private bool isDryRun;
    private DateTime startedAtUtc;
    private DateTime? sentAtUtc;
    private long stopwatchAtSend;
    private long generationAtSend;
    private ulong localIdAtSend;
    private byte currentRequestIdAtSend;
    private int preSendSnapshotsSeen;
    private uint entryCountBefore;

    // Set (framework thread) the moment a real send may have happened; any later exception is then Indeterminate.
    private bool trySendEntered;
    private bool sendAttempted;
    private volatile string? verdictAuditError;
    private long observeUntilTimestamp; // Stopwatch timestamp; 0 = not observing
    private int callbackDepth;
    private (ulong Local, RemovalTarget Target, bool DryRun, string Result, string Detail)? pendingVerdictRow;
    private bool chatSubscribed;
    private volatile bool logMessageObserved;
    private volatile bool chatTextObserved;
    private (uint Count, byte Next, byte Cur)? lastCounters;
    private bool disposed;

    public SingleRemovalTest(
        IFramework framework,
        IChatGui chat,
        IDataManager data,
        IFriendListReader reader,
        DirectFriendRemover remover,
        RemovalAuditLog audit,
        Configuration config,
        IPluginLog log)
    {
        this.framework = framework;
        this.chat = chat;
        this.reader = reader;
        this.remover = remover;
        this.audit = audit;
        this.config = config;
        this.log = log;

        removedTextFragments = LoadRemovedTextFragments(data, log, out var macro);
        RemovedMessageTemplate = macro;

        framework.Update += OnUpdate;
        reader.SnapshotCompleted += OnSnapshot;
    }

    public SingleTestPhase Phase => (SingleTestPhase)phase;

    public SingleTestVerdict Verdict => (SingleTestVerdict)verdict;

    public string? VerdictDetail => verdictDetail;

    public RemovalTarget? Target => target;

    public bool IsDryRun => isDryRun;

    /// <summary>True while a request is queued or a real send awaits its snapshot. No new test may start.</summary>
    public bool IsInFlight => Phase is SingleTestPhase.Requested or SingleTestPhase.AwaitingSnapshot;

    /// <summary>LogMessage 12 was seen via IChatGui.LogMessage during the test (best effort).</summary>
    public bool LogMessageObserved => logMessageObserved;

    /// <summary>A chat line matching LogMessage 12's text + the target name was seen (best effort).</summary>
    public bool ChatTextObserved => chatTextObserved;

    /// <summary>LogMessage 12 as a macro string (for display), or null if it could not be loaded.</summary>
    public string? RemovedMessageTemplate { get; }

    public DateTime? SentAtUtc => sentAtUtc;

    /// <summary>Set when the VERDICT audit row could not be written (the CSV then lacks the result), else null.</summary>
    public string? VerdictAuditError => verdictAuditError;

    /// <summary>Newer snapshots seen so far that still held the target but came from a download requested before the send.</summary>
    public int PreSendSnapshotsSeen => preSendSnapshotsSeen;

    public int TimeoutSeconds => Math.Clamp(config.SingleTestTimeoutSeconds, 5, 300);

    /// <summary>Returns a reason when another removal operation (the bulk queue) is active; requests are then refused.</summary>
    public Func<string?>? ExternalBusy { get; set; }

    /// <summary>Copy of the telemetry lines (cached; cheap to call per frame).</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (gate)
            {
                if (linesCacheVersion != linesVersion)
                {
                    linesCache = lines.ToArray();
                    linesCacheVersion = linesVersion;
                }

                return linesCache;
            }
        }
    }

    /// <summary>
    /// UI thread. Queue one test for the next framework tick. A real removal is only executed if the reader's snapshot
    /// is still <paramref name="expectedGeneration"/> for <paramref name="expectedLocalContentId"/> at execution time.
    /// </summary>
    public bool Request(RemovalTarget t, bool dryRun, long expectedGeneration, ulong expectedLocalContentId, out string? error)
    {
        lock (gate)
        {
            if (disposed)
            {
                error = "Harness is disposed.";
                return false;
            }

            if (IsInFlight)
            {
                error = "A test is already in flight.";
                return false;
            }

            if (ExternalBusy?.Invoke() is { } busy)
            {
                error = busy;
                return false;
            }

            if (!dryRun && remover.StartupBlock is { } block)
            {
                error = $"Real sends are blocked since startup: {block.Reason}";
                return false;
            }

            if (!dryRun && remover.HasUnresolvedSend)
            {
                error = "A previous real send is still unresolved (waiting for a qualifying friend-list snapshot).";
                return false;
            }

            ResetStateLocked();
            requestedTarget = t;
            requestedDryRun = dryRun;
            requestedGeneration = expectedGeneration;
            requestedLocalId = expectedLocalContentId;
            target = t;
            isDryRun = dryRun;
            startedAtUtc = DateTime.UtcNow;
            phase = (int)SingleTestPhase.Requested;
        }

        Line($"{(dryRun ? "DRY RUN" : "REAL REMOVAL")} requested for {t} (confirmed against gen {expectedGeneration}, local {expectedLocalContentId:X16}).");
        error = null;
        return true;
    }

    /// <summary>UI thread. Forget a finished test. Refused while in flight.</summary>
    public bool Clear()
    {
        lock (gate)
        {
            if (IsInFlight)
                return false;
            ResetStateLocked();
        }

        // Unsubscribe happens on the framework thread on the next tick (observeUntilUtc cleared).
        return true;
    }

    public void Dispose()
    {
        lock (gate)
            disposed = true;
        framework.Update -= OnUpdate;
        try
        {
            FlushPendingVerdict();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[SingleTest] Writing the pending VERDICT row on dispose failed.");
        }

        reader.SnapshotCompleted -= OnSnapshot;
        UnsubscribeChat();
    }

    // ------------------------------------------------------------------ framework thread

    private void OnUpdate(IFramework fw)
    {
        if (disposed)
            return;
        try
        {
            FlushPendingVerdict();
            switch (Phase)
            {
                case SingleTestPhase.Requested:
                    Execute();
                    break;

                case SingleTestPhase.AwaitingSnapshot:
                    PollCounters();
                    if (stopwatchAtSend != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(stopwatchAtSend) > TimeSpan.FromSeconds(TimeoutSeconds))
                    {
                        var what = preSendSnapshotsSeen > 0
                            ? $"Only pre-send data ({preSendSnapshotsSeen} snapshot(s) with request id {currentRequestIdAtSend}, still listing the target) within {TimeoutSeconds}s of the send"
                            : $"No newer friend-list snapshot within {TimeoutSeconds}s of the send";
                        Finish(SingleTestVerdict.Indeterminate,
                            $"{what}. Nothing further will be done; open the in-game friend list to observe the result (Refresh is " +
                            "disabled while the send latch is set; the latch clears on a snapshot that lacks the target or was requested after the send).");
                    }

                    break;

                case SingleTestPhase.Finished:
                case SingleTestPhase.Idle:
                    if (chatSubscribed)
                    {
                        if (observeUntilTimestamp != 0 && System.Diagnostics.Stopwatch.GetTimestamp() < observeUntilTimestamp)
                            PollCounters();
                        else
                            UnsubscribeChat();
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[SingleTest] Update failed.");
            try
            {
                if (Phase is SingleTestPhase.Requested)
                {
                    if (sendAttempted || (trySendEntered && !isDryRun))
                        Finish(SingleTestVerdict.Indeterminate, $"Harness exception after the send may have been attempted: {ex.Message}");
                    else
                        Finish(SingleTestVerdict.Refused, $"Harness exception before send: {ex.Message}");
                }
                else if (Phase is SingleTestPhase.AwaitingSnapshot)
                {
                    Finish(SingleTestVerdict.Indeterminate, $"Harness exception after send: {ex.Message}");
                }
            }
            catch (Exception ex2)
            {
                // Finish itself failed; force a terminal, non-"Refused" state so the UI cannot claim nothing was sent.
                log.Error(ex2, "[SingleTest] Finish after exception failed.");
                if (Phase is SingleTestPhase.Requested or SingleTestPhase.AwaitingSnapshot)
                {
                    verdictDetail = $"Harness exceptions: {ex.Message} / {ex2.Message}";
                    verdict = (int)(sendAttempted || (trySendEntered && !isDryRun) ? SingleTestVerdict.Indeterminate : SingleTestVerdict.Refused);
                    phase = (int)SingleTestPhase.Finished;
                }
            }
        }
    }

    private void Execute()
    {
        RemovalTarget t;
        bool dry;
        long expectedGen;
        ulong expectedLocal;
        lock (gate)
        {
            if (requestedTarget == null)
            {
                phase = (int)SingleTestPhase.Idle;
                return;
            }

            t = requestedTarget;
            dry = requestedDryRun;
            expectedGen = requestedGeneration;
            expectedLocal = requestedLocalId;
            requestedTarget = null;
        }

        if (!dry)
        {
            var snap = reader.Snapshot;
            if (snap == null || snap.Generation != expectedGen || snap.LocalContentId != expectedLocal)
            {
                Finish(SingleTestVerdict.Refused,
                    $"Friend list changed since confirmation (now gen {snap?.Generation ?? 0}, owner {snap?.LocalContentId ?? 0:X16}). Nothing sent; re-confirm.");
                return;
            }

            // Subscribe before the call so a fast LogMessage cannot be missed.
            SubscribeChat();
        }

        trySendEntered = true;
        var outcome = remover.TrySend(t, dry, out var d);
        if (outcome.Result == RemovalSendResult.Sent || outcome.MayHaveReachedServer)
            sendAttempted = true;

        Line($"TrySend -> {outcome.Result} (mayHaveReachedServer={outcome.MayHaveReachedServer}): {outcome.Detail}");
        Line($"  ContentId={t.ContentId:X16} name='{t.DisplayName}' homeWorld={t.HomeWorldName}({t.HomeWorldId}) local={d.LocalContentId:X16} gen={d.SnapshotGeneration}");
        // The re-read only happens right before a real native call.
        var reRead = d.DryRun ? "n/a (dry run)" : d.ContentIdReReadAtIndex.ToString("X16", CultureInfo.InvariantCulture);
        Line($"  rawIndex={d.RawIndex} (snapshot {d.SnapshotListIndex}) contentIdAtScan={d.ContentIdAtScan:X16} contentIdReRead={reRead} " +
             $"extraFlags=0x{d.ExtraFlagsAtIndex:X8} (requestState {(d.ExtraFlagsAtIndex >> 4) & 0xF})");
        Line($"  before: EntryCount={d.EntryCountBefore} (snapshot {d.SnapshotNativeEntryCount}) GetEntryCount()={d.GetEntryCountBefore} " +
             $"NextRequestId={d.NextRequestIdBefore} CurrentRequestId={d.CurrentRequestIdBefore} (snapshot {d.SnapshotCurrentRequestId})");
        if (d.NativeCalled)
        {
            Line($"  native: return={d.NativeReturn} call={d.NativeCallMs.ToString("0.000", CultureInfo.InvariantCulture)}ms " +
                 $"after: EntryCount={d.EntryCountAfter} NextRequestId={d.NextRequestIdAfter} CurrentRequestId={d.CurrentRequestIdAfter}");
        }

        if (outcome.Result == RemovalSendResult.DryRunResolved)
        {
            Finish(SingleTestVerdict.DryRunResolved, $"Dry run OK — would call RemoveFriend(index {d.RawIndex}). Nothing sent.");
            return;
        }

        if (!outcome.MayHaveReachedServer)
        {
            Finish(SingleTestVerdict.Refused, $"{outcome.Result}: {outcome.Detail}");
            return;
        }

        // A native call happened (or may have).
        sentAtUtc = DateTime.UtcNow;
        stopwatchAtSend = System.Diagnostics.Stopwatch.GetTimestamp();
        generationAtSend = d.SnapshotGeneration;
        localIdAtSend = d.LocalContentId;
        currentRequestIdAtSend = d.CurrentRequestIdBefore;
        preSendSnapshotsSeen = 0;
        entryCountBefore = d.EntryCountBefore;
        lastCounters = d.EntryCountAfter is { } c ? (c, d.NextRequestIdAfter ?? 0, d.CurrentRequestIdAfter ?? 0) : null;

        if (outcome.Result == RemovalSendResult.Sent)
        {
            phase = (int)SingleTestPhase.AwaitingSnapshot;
            Line($"Waiting up to {TimeoutSeconds}s for a snapshot newer than gen {generationAtSend} that lacks the target or whose " +
                 $"download was requested after the send (request id != {currentRequestIdAtSend}).");
            return;
        }

        if (d.NativeCalled && d.NativeReturn == 0)
        {
            Finish(SingleTestVerdict.NotQueued,
                "Native RemoveFriend returned 0 (analysis: nothing sent). The send latch stays set until a newer snapshot; open the in-game friend list to clear it.");
            return;
        }

        Finish(SingleTestVerdict.Indeterminate, $"Send may have reached the server but failed locally: {outcome.Detail}");
    }

    private void OnSnapshot(FriendListSnapshot snap)
    {
        callbackDepth++;
        try
        {
            var p = Phase;
            var observing = p == SingleTestPhase.AwaitingSnapshot
                            || (p == SingleTestPhase.Finished && sentAtUtc != null && observeUntilTimestamp != 0
                                && System.Diagnostics.Stopwatch.GetTimestamp() < observeUntilTimestamp);
            if (!observing || target is not { } t)
                return;

            // Same rule as the removal queue (SendClassifier is the single source of truth).
            var ev = SendClassifier.Evaluate(snap, t.ContentId, localIdAtSend, generationAtSend, currentRequestIdAtSend, entryCountBefore);

            var proxy = InfoProxyFriendList.Instance();
            var counters = proxy == null ? "proxy=null" : $"EntryCount={proxy->EntryCount} NextRequestId={proxy->NextRequestId} CurrentRequestId={proxy->CurrentRequestId}";
            Line($"Snapshot gen={snap.Generation} owner={snap.LocalContentId:X16} native={snap.NativeEntryCount} entries={snap.Entries.Count} " +
                 $"ours={snap.WasRequestedByPlugin} requestId={snap.CurrentRequestId}{(snap.HasStableRequestId ? string.Empty : $"->{snap.CurrentRequestIdAfterEnd}")} " +
                 $"(at send {currentRequestIdAtSend}) -> {ev.Observation} | {counters} | +{ElapsedMs()}ms");

            if (p != SingleTestPhase.AwaitingSnapshot || ev.Verdict == SnapshotVerdict.Ignore)
                return;

            var chatNote = $" (chat: LogMessage12={logMessageObserved}, textMatch={chatTextObserved}; best effort)";
            switch (ev.Verdict)
            {
                case SnapshotVerdict.KeepWaiting:
                    preSendSnapshotsSeen++;
                    Line($"  {ev.Detail} Still waiting; send latch kept.");
                    break;
                case SnapshotVerdict.StillPresent:
                    Finish(SingleTestVerdict.StillPresent, ev.Detail + chatNote);
                    break;
                case SnapshotVerdict.Removed:
                    Finish(SingleTestVerdict.Removed, ev.Detail + chatNote);
                    break;
                default:
                    Finish(SingleTestVerdict.Unexpected, ev.Detail + chatNote);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[SingleTest] Snapshot handler failed.");
            if (Phase == SingleTestPhase.AwaitingSnapshot)
                Finish(SingleTestVerdict.Indeterminate, $"Harness exception while evaluating snapshot: {ex.Message}");
        }
        finally
        {
            callbackDepth--;
        }
    }

    private void PollCounters()
    {
        var proxy = InfoProxyFriendList.Instance();
        if (proxy == null)
            return;
        (uint Count, byte Next, byte Cur) now = (proxy->EntryCount, proxy->NextRequestId, proxy->CurrentRequestId);
        if (lastCounters is { } prev && prev == now)
            return;
        lastCounters = now;
        Line($"Counters changed: EntryCount={now.Count} NextRequestId={now.Next} CurrentRequestId={now.Cur} | +{ElapsedMs()}ms");
    }

    private void Finish(SingleTestVerdict v, string detail)
    {
        verdictDetail = detail;
        verdict = (int)v;
        phase = (int)SingleTestPhase.Finished;
        if (sentAtUtc != null)
            observeUntilTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(ObservationTail.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        else
            UnsubscribeChat();

        Line($"VERDICT {v}: {detail}");
        log.Information($"[SingleTest] VERDICT {v} for {target}: {detail}");

        if (target is { } t)
        {
            pendingVerdictRow = (localIdAtSend != 0 ? localIdAtSend : requestedLocalId, t, isDryRun, v.ToString(), detail);

            // Inside the EndRequest detour (snapshot callback) the row is written on the next framework tick instead.
            if (callbackDepth == 0)
                FlushPendingVerdict();
        }
    }

    private void FlushPendingVerdict()
    {
        if (pendingVerdictRow is not { } row)
            return;
        pendingVerdictRow = null;
        if (audit.TryWrite(row.Local, row.Target, row.DryRun, AuditPhase.Verdict, row.Result, row.Detail))
        {
            verdictAuditError = null;
        }
        else
        {
            verdictAuditError = $"VERDICT row NOT written to the audit CSV: {audit.LastError ?? "unknown error"}";
            Line(verdictAuditError);
        }
    }

    // ------------------------------------------------------------------ chat (best effort)

    private void SubscribeChat()
    {
        if (chatSubscribed)
            return;
        chat.LogMessage += OnLogMessage;
        chat.ChatMessage += OnChatMessage;
        chatSubscribed = true;
    }

    private void UnsubscribeChat()
    {
        if (!chatSubscribed)
            return;
        chat.LogMessage -= OnLogMessage;
        chat.ChatMessage -= OnChatMessage;
        chatSubscribed = false;
    }

    private void OnLogMessage(ILogMessage message)
    {
        try
        {
            if (message.LogMessageId != RemovedLogMessageId)
                return;
            logMessageObserved = true;
            string text;
            try
            {
                text = message.FormatLogMessageForDebugging().ExtractText();
            }
            catch
            {
                text = "(could not format)";
            }

            Line($"IChatGui.LogMessage id={RemovedLogMessageId} observed: \"{text}\" | +{ElapsedMs()}ms");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[SingleTest] LogMessage handler failed.");
        }
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            if (target is not { } t || removedTextFragments.Length == 0)
                return;
            var text = message.Message.TextValue;
            if (string.IsNullOrEmpty(text) || !text.Contains(t.Name, StringComparison.OrdinalIgnoreCase))
                return;
            foreach (var f in removedTextFragments)
            {
                if (!text.Contains(f, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            chatTextObserved = true;
            Line($"Chat [{message.LogKind}] matched removal text: \"{text}\" | +{ElapsedMs()}ms");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[SingleTest] ChatMessage handler failed.");
        }
    }

    private static string[] LoadRemovedTextFragments(IDataManager data, IPluginLog log, out string? macro)
    {
        macro = null;
        try
        {
            var sheet = data.GetExcelSheet<LogMessage>();
            if (sheet == null || !sheet.TryGetRow(RemovedLogMessageId, out var row))
                return Array.Empty<string>();
            macro = row.Text.ToMacroString();

            // Only TOP-LEVEL text payloads are literal text; macros (with arbitrarily nested expressions) are skipped
            // whole, e.g. "<if(...,<if(gnum7,<ennoun(...)>,gstr2)>)> removed from friend list." -> ["removed from friend list."].
            var parts = new List<string>();
            foreach (var payload in row.Text)
            {
                if (payload.Type != ReadOnlySePayloadType.Text)
                    continue;
                var p = Encoding.UTF8.GetString(payload.Body.Span).Trim();
                if (p.Length >= 4)
                    parts.Add(p);
            }

            log.Telemetry($"[SingleTest] LogMessage {RemovedLogMessageId} template: \"{macro}\" -> match fragments [{string.Join(" | ", parts)}] (best effort).");
            return parts.ToArray();
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[SingleTest] Could not load LogMessage {RemovedLogMessageId}; chat text matching disabled.");
            return Array.Empty<string>();
        }
    }

    // ------------------------------------------------------------------ helpers

    private void ResetStateLocked()
    {
        requestedTarget = null;
        target = null;
        verdict = (int)SingleTestVerdict.None;
        verdictDetail = null;
        sentAtUtc = null;
        stopwatchAtSend = 0;
        generationAtSend = 0;
        localIdAtSend = 0;
        currentRequestIdAtSend = 0;
        preSendSnapshotsSeen = 0;
        trySendEntered = false;
        sendAttempted = false;
        verdictAuditError = null;
        entryCountBefore = 0;
        observeUntilTimestamp = 0;
        logMessageObserved = false;
        chatTextObserved = false;
        lastCounters = null;
        lines.Clear();
        linesVersion++;
        phase = (int)SingleTestPhase.Idle;
    }

    private string ElapsedMs() =>
        stopwatchAtSend == 0
            ? "-"
            : System.Diagnostics.Stopwatch.GetElapsedTime(stopwatchAtSend).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);

    private void Line(string message)
    {
        var stamped = $"{DateTime.UtcNow:HH:mm:ss.fff}Z {message}";
        log.Telemetry($"[SingleTest] {message}");
        lock (gate)
        {
            lines.Add(stamped);
            if (lines.Count > MaxLines)
                lines.RemoveAt(0);
            linesVersion++;
        }
    }
}
