using System;
using System.Collections.Generic;
using System.Threading;

namespace SocialCleanup.Removal;

// Dalamud-free on purpose: linked into the test project. ReloadSuppressor.cs only reads game memory, calls the game's
// originals and forwards everything to this class; every decision is made here.

/// <summary>The two raw friend-proxy bytes fast mode relies on (patch-fragile, verified by the self-check).</summary>
/// <param name="DownloadInFlight">+0x3ADD: 1 while a friend-list download is in flight.</param>
/// <param name="ListComplete">+0xC8: 1 once the list is complete, 0 while downloading.</param>
public readonly record struct ProxyFlags(byte DownloadInFlight, byte ListComplete)
{
    /// <summary>No download in flight: 0 / 1.</summary>
    public static readonly ProxyFlags AtRest = new(0, 1);

    /// <summary>Download in flight: 1 / 0.</summary>
    public static readonly ProxyFlags Downloading = new(1, 0);

    public override string ToString() => $"+0x3ADD={DownloadInFlight} +0xC8={ListComplete}";
}

/// <summary>Everything the reply hook reads BEFORE the game's handler runs.</summary>
/// <param name="ContentId">Payload +0.</param>
/// <param name="Result">Payload +8.</param>
/// <param name="Flags">Payload +0xC (diagnostics).</param>
/// <param name="Name">Payload +0xD (diagnostics).</param>
/// <param name="OnFrameworkThread">The handler runs on the framework thread.</param>
/// <param name="Proxy">The live friend proxy (0 when not resolved / not read).</param>
/// <param name="EntryCountBefore">Proxy EntryCount.</param>
/// <param name="TargetPresentBefore">ContentId is in the live list.</param>
/// <param name="FlagsBefore">Raw bytes (1/0 when unreadable, i.e. "download in flight").</param>
/// <param name="ReaderReady">The friend-list reader is Ready.</param>
/// <param name="FastModeAvailable">Hooks + cross-checks + self-check all OK.</param>
public sealed record ReplyObservation(
    ulong ContentId,
    uint Result,
    byte Flags,
    string Name,
    bool OnFrameworkThread,
    nint Proxy,
    uint EntryCountBefore,
    bool TargetPresentBefore,
    ProxyFlags FlagsBefore,
    bool ReaderReady,
    bool FastModeAvailable);

/// <summary>What the reply hook reads AFTER the game's handler returned (null when it could not read the proxy).</summary>
public sealed record ReplyAfterObservation(uint EntryCountAfter, bool TargetPresentAfter, ProxyFlags FlagsAfter);

/// <summary>What the RequestData hook reads before deciding.</summary>
/// <param name="Self">The <c>this</c> pointer of the call.</param>
/// <param name="IsLiveFriendProxy"><paramref name="Self"/> is non-null and equals InfoProxyFriendList.Instance().</param>
/// <param name="OnFrameworkThread">The call runs on the framework thread.</param>
/// <param name="FlagsBefore">Raw bytes of <paramref name="Self"/> before the original (only meaningful for the live friend proxy).</param>
public readonly record struct RequestDataObservation(nint Self, bool IsLiveFriendProxy, bool OnFrameworkThread, ProxyFlags FlagsBefore);

/// <summary>What to do about the game's friend list when the suppressor is disposed.</summary>
public enum DisposeReloadDecision
{
    /// <summary>Nothing was suppressed since the last full download.</summary>
    NotNeeded,

    /// <summary>Issue one real RequestData now.</summary>
    Issue,

    /// <summary>A download is already in flight; its completion refreshes the list.</summary>
    CoveredByInFlightDownload,

    /// <summary>Needed but impossible right now (not on the framework thread / not logged in / no proxy).</summary>
    CannotIssue,
}

/// <summary>
/// Fast mode's decision state machine. The real hooks call <see cref="RunReply"/> (reply handler),
/// <see cref="RunRequestData"/> (friend proxy RequestData) and <see cref="OnSnapshotCompleted"/> (after EndRequest);
/// those methods call the game's original themselves, inside try/finally, so per-reply state is always cleared even
/// when the original throws.
///
/// Rules (all fail closed):
/// <list type="bullet">
/// <item>A suppression is decided once per reply, before the game's handler runs, by <see cref="SuppressionPolicy"/>.</item>
/// <item>Only a RequestData call made INSIDE that handler call, on the framework thread, for exactly the friend proxy
/// observed at reply time can be suppressed, and only the first such call (any RequestData consumes the decision).</item>
/// <item>RequestData outside any reply handler always passes through.</item>
/// <item>A nested (re-entrant) reply cancels every pending decision; neither reply suppresses or publishes.</item>
/// <item>A suppression decided but never used (the handler did not call RequestData) is reported and disables fast mode.</item>
/// <item>The local snapshot is published only after the reply event, only when the reload was suppressed, no RequestData
/// passed through during that handler call and +0x3ADD reads 0 afterwards.</item>
/// <item>Self-check: a download start must take the bytes 0/1 → 1/0 across RequestData, and EndRequest 1/0 → 0/1;
/// anything else disables fast mode for the session.</item>
/// </list>
/// </summary>
public sealed class ReloadSuppressionCore
{
    private readonly object gate = new();
    private readonly List<ReplyFrame> frames = new();
    private readonly Action<string> info;
    private readonly Action<string> warn;

    private long armedContentId;
    private volatile bool fastBatchActive;
    private volatile bool disposing;
    private volatile bool suppressedSinceFullDownload;

    private volatile bool startCheckPassed;
    private volatile bool endCheckPassed;
    private volatile string? selfCheckFailure;
    private volatile string? anomaly;
    private volatile bool offThreadObserved;

    private int repliesSeen;
    private int suppressedCount;
    private int handlerSkippedRequestData;
    private int localPublishes;

    public ReloadSuppressionCore(Action<string>? info = null, Action<string>? warn = null)
    {
        this.info = info ?? (_ => { });
        this.warn = warn ?? (_ => { });
    }

    public bool IsDisposing => disposing;

    public bool FastBatchActive => fastBatchActive;

    public ulong ArmedContentId => (ulong)Interlocked.Read(ref armedContentId);

    /// <summary>A reload was suppressed and no full download has completed since.</summary>
    public bool SuppressedSinceFullDownload => suppressedSinceFullDownload;

    public bool StartCheckPassed => startCheckPassed;

    public bool EndCheckPassed => endCheckPassed;

    public string? SelfCheckFailure => selfCheckFailure;

    /// <summary>Something the static analysis says cannot happen did (e.g. the handler skipped RequestData). Disables fast mode.</summary>
    public string? Anomaly => anomaly;

    public bool OffThreadObserved => offThreadObserved;

    public int RepliesSeen => Volatile.Read(ref repliesSeen);

    public int SuppressedCount => Volatile.Read(ref suppressedCount);

    public int HandlerSkippedRequestDataCount => Volatile.Read(ref handlerSkippedRequestData);

    public int LocalPublishes => Volatile.Read(ref localPublishes);

    /// <summary>Number of reply-handler calls currently on the stack (0 outside any reply).</summary>
    public int ReplyDepth
    {
        get
        {
            lock (gate)
                return frames.Count;
        }
    }

    /// <summary>Null when the runtime checks this class owns allow fast mode; otherwise why not.</summary>
    public string? Blocker
    {
        get
        {
            if (offThreadObserved)
                return "The reply handler was observed off the framework thread; fast mode is disabled for this session.";
            if (selfCheckFailure is { } f)
                return $"Raw-offset self-check FAILED: {f}";
            if (anomaly is { } a)
                return $"Fast mode disabled for this session: {a}";
            if (!startCheckPassed || !endCheckPassed)
            {
                return "Self-check pending: press Refresh once (or open and close the in-game friend list); it needs one observed friend-list download start and its completion " +
                       $"to verify bytes +0x3ADD/+0xC8 (start {(startCheckPassed ? "ok" : "pending")}, end {(endCheckPassed ? "ok" : "pending")}).";
            }

            return null;
        }
    }

    /// <summary>Returns true when the flag changed.</summary>
    public bool SetFastBatchActive(bool active)
    {
        if (fastBatchActive == active)
            return false;
        fastBatchActive = active;
        if (!active)
            Disarm();
        return true;
    }

    public void Arm(ulong targetContentId) => Interlocked.Exchange(ref armedContentId, (long)targetContentId);

    public void Disarm() => Interlocked.Exchange(ref armedContentId, 0);

    /// <summary>From now on the hooks only call the originals; nothing is suppressed.</summary>
    public void BeginDispose()
    {
        disposing = true;
        fastBatchActive = false;
        Disarm();
        lock (gate)
        {
            foreach (var f in frames)
                f.SuppressPending = false;
        }
    }

    // ------------------------------------------------------------------ reply handler

    /// <summary>
    /// The reply-handler detour. Calls <paramref name="original"/> exactly once (also while disposing). Exceptions from
    /// <paramref name="original"/> propagate; every other step is isolated.
    /// </summary>
    /// <param name="observeBefore">Reads the payload + proxy; null when there is no payload.</param>
    /// <param name="original">The game's handler.</param>
    /// <param name="observeAfter">Reads the proxy after the handler; null when unreadable.</param>
    /// <param name="raise">Raises <see cref="IReloadSuppressor.ReplyReceived"/>.</param>
    /// <param name="publishLocal">Publishes the reader's local snapshot; returns whether it did.</param>
    public void RunReply(
        Func<ReplyObservation?> observeBefore,
        Action original,
        Func<ReplyObservation, ReplyAfterObservation?> observeAfter,
        Action<ServerReply> raise,
        Func<string, bool> publishLocal)
    {
        if (disposing)
        {
            original();
            return;
        }

        var frame = new ReplyFrame();
        Push(frame);
        try
        {
            try
            {
                var obs = observeBefore();
                if (obs != null)
                    Decide(frame, obs);
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    frame.SuppressPending = false;
                    frame.Observation = null;
                }

                Warn($"Reply pre-processing failed; nothing will be suppressed: {ex.Message}");
            }

            original();
        }
        finally
        {
            Pop(frame);
        }

        if (disposing || frame.Observation == null)
            return;

        try
        {
            AfterReply(frame, observeAfter, raise, publishLocal);
        }
        catch (Exception ex)
        {
            Warn($"Reply post-processing failed: {ex.Message}");
        }
    }

    private void Push(ReplyFrame frame)
    {
        lock (gate)
        {
            if (frames.Count > 0)
            {
                // Re-entrant reply: RequestData calls can no longer be attributed. Nobody suppresses or publishes.
                foreach (var outer in frames)
                {
                    outer.SuppressPending = false;
                    outer.Nested = true;
                }

                frame.Nested = true;
            }

            frames.Add(frame);
        }
    }

    private void Pop(ReplyFrame frame)
    {
        lock (gate)
        {
            frame.SuppressPending = false;
            var i = frames.LastIndexOf(frame);
            if (i >= 0)
                frames.RemoveAt(i);
        }
    }

    private void Decide(ReplyFrame frame, ReplyObservation obs)
    {
        if (!obs.OnFrameworkThread && !offThreadObserved)
        {
            offThreadObserved = true;
            Warn("Reply handler observed OFF the framework thread; fast mode permanently disabled for this session.");
        }

        var armed = ArmedContentId;
        var active = fastBatchActive;
        var why = SuppressionPolicy.WhyNotSuppress(disposing, obs.FastModeAvailable && !offThreadObserved, active, armed, obs.ContentId, obs.Result,
            obs.OnFrameworkThread, obs.Proxy != 0, obs.FlagsBefore.DownloadInFlight, obs.FlagsBefore.ListComplete, obs.ReaderReady,
            obs.TargetPresentBefore);

        // One reply per armed send: whatever happens next, this target is no longer armed.
        if (armed != 0 && obs.ContentId == armed)
            Interlocked.CompareExchange(ref armedContentId, 0, (long)armed);

        lock (gate)
        {
            if (why == null && frame.Nested)
                why = "nested reply-handler call";
            frame.Observation = obs;
            frame.Why = why;
            frame.SuppressPending = why == null;
        }

        Info($"REPLY cid={obs.ContentId:X16} result={obs.Result} flags=0x{obs.Flags:X2} name='{obs.Name}' EntryCount={obs.EntryCountBefore} {obs.FlagsBefore} " +
             $"targetPresent={obs.TargetPresentBefore} readerReady={obs.ReaderReady} armed={armed:X16} fastBatch={active} frameworkThread={obs.OnFrameworkThread} -> " +
             (why == null ? "will SUPPRESS the reload" : $"reload passes ({why})"));
    }

    private void AfterReply(ReplyFrame frame, Func<ReplyObservation, ReplyAfterObservation?> observeAfter, Action<ServerReply> raise, Func<string, bool> publishLocal)
    {
        var obs = frame.Observation!;
        ReplyAfterObservation? after;
        try
        {
            after = observeAfter(obs);
        }
        catch (Exception ex)
        {
            after = null;
            Warn($"Reading the proxy after the reply handler failed: {ex.Message}");
        }

        bool suppressed, passed, nested;
        int calls;
        string? why;
        lock (gate)
        {
            suppressed = frame.Suppressed;
            passed = frame.RequestDataPassed;
            nested = frame.Nested;
            calls = frame.RequestDataCalls;
            why = frame.Why;
        }

        Interlocked.Increment(ref repliesSeen);
        if (suppressed)
        {
            Interlocked.Increment(ref suppressedCount);
            suppressedSinceFullDownload = true;
        }

        string? notSuppressed = null;
        if (!suppressed)
        {
            if (why != null)
            {
                notSuppressed = why;
            }
            else if (nested)
            {
                notSuppressed = "nested reply-handler call";
            }
            else if (calls == 0)
            {
                notSuppressed = "suppression was decided but the handler never called RequestData";
                Interlocked.Increment(ref handlerSkippedRequestData);
                anomaly ??= $"the reply handler for {obs.ContentId:X16} (result {obs.Result}) never called RequestData although a suppression was decided";
                Warn($"ANOMALY: {notSuppressed} (reply {obs.ContentId:X16}); fast mode disabled for this session.");
            }
            else
            {
                notSuppressed = "the handler's RequestData did not qualify (proxy identity / thread)";
            }
        }

        Info($"REPLY handled cid={obs.ContentId:X16} result={obs.Result} EntryCount {obs.EntryCountBefore}->{after?.EntryCountAfter.ToString() ?? "?"} " +
             $"targetStillPresent={after?.TargetPresentAfter.ToString() ?? "?"} RequestData calls={calls} " +
             (suppressed ? "SUPPRESSED" : $"not suppressed ({notSuppressed})") + (passed ? " (a RequestData passed through)" : string.Empty));

        var reply = new ServerReply(obs.ContentId, obs.Result, obs.Flags, obs.Name, obs.EntryCountBefore, after?.EntryCountAfter ?? 0,
            obs.TargetPresentBefore, after?.TargetPresentAfter ?? true, suppressed, notSuppressed, DateTime.UtcNow);

        // Order: the reply first (the queue records it), then the local snapshot.
        try
        {
            raise(reply);
        }
        catch (Exception ex)
        {
            Warn($"A ReplyReceived handler threw: {ex.Message}");
        }

        string? noPublish = null;
        if (!suppressed)
            noPublish = "reload not suppressed";
        else if (disposing)
            noPublish = "disposing";
        else if (passed)
            noPublish = "a RequestData passed through during this handler call";
        else if (nested)
            noPublish = "nested reply-handler call";
        else if (after == null)
            noPublish = "the proxy could not be read after the handler";
        else if (after.FlagsAfter.DownloadInFlight != 0)
            noPublish = $"a download is in flight after the handler ({after.FlagsAfter})";

        if (noPublish != null)
        {
            if (suppressed)
                Warn($"Local snapshot NOT published after the suppressed reload for {obs.ContentId:X16}: {noPublish}.");
            return;
        }

        var ok = publishLocal($"after the suppressed reload for {obs.ContentId:X16}");
        if (ok)
            Interlocked.Increment(ref localPublishes);
        Info($"Local snapshot after suppressed reload: {(ok ? "published" : "NOT published (reader refused)")}.");
    }

    // ------------------------------------------------------------------ RequestData

    /// <summary>
    /// The RequestData detour. Returns 0 without calling <paramref name="original"/> only for the one qualifying call;
    /// otherwise calls it exactly once (exceptions propagate) and returns its result.
    /// </summary>
    /// <param name="observe">Reads identity, thread and the bytes before the call.</param>
    /// <param name="original">The game's RequestData.</param>
    /// <param name="readFlagsAfter">Reads the bytes after the call (null when unreadable).</param>
    /// <param name="notifyDownloadStarted">Tells the reader a download started (friend proxy, non-zero return).</param>
    public byte RunRequestData(Func<RequestDataObservation> observe, Func<byte> original, Func<ProxyFlags?> readFlagsAfter, Action notifyDownloadStarted)
    {
        if (disposing)
            return original();

        ReplyFrame? frame;
        lock (gate)
            frame = frames.Count > 0 ? frames[^1] : null;

        var observed = false;
        var obs = default(RequestDataObservation);
        var suppress = false;
        try
        {
            obs = observe();
            observed = true;
        }
        catch (Exception ex)
        {
            Warn($"RequestData pre-processing failed; passing through: {ex.Message}");
        }

        if (frame != null)
        {
            lock (gate)
            {
                suppress = observed && frame.SuppressPending && frame.Observation is { } ro && obs.IsLiveFriendProxy && obs.OnFrameworkThread
                           && obs.Self != 0 && obs.Self == ro.Proxy && !disposing;
                frame.SuppressPending = false; // at most one suppression per reply; any RequestData consumes the decision
                frame.RequestDataCalls++;
                if (suppress)
                    frame.Suppressed = true;
                else
                    frame.RequestDataPassed = true;
            }
        }

        if (suppress)
        {
            Info($"RequestData SUPPRESSED (returned 0) for the reply to {frame!.Observation!.ContentId:X16} | {obs.FlagsBefore}");
            return 0;
        }

        var ret = original();
        try
        {
            AfterRequestData(observed, obs, frame, ret, readFlagsAfter, notifyDownloadStarted);
        }
        catch (Exception ex)
        {
            Warn($"RequestData post-processing failed: {ex.Message}");
        }

        return ret;
    }

    private void AfterRequestData(bool observed, RequestDataObservation obs, ReplyFrame? frame, byte ret, Func<ProxyFlags?> readFlagsAfter, Action notifyDownloadStarted)
    {
        if (disposing || !observed || !obs.IsLiveFriendProxy)
            return;

        var after = readFlagsAfter();
        Info($"RequestData passed -> {ret} (caller: {(frame != null ? "inside a reply handler" : "plugin/game")}) | before {obs.FlagsBefore} after {after?.ToString() ?? "unreadable"}");
        if (ret == 0)
            return;

        if (obs.FlagsBefore == ProxyFlags.AtRest && after == ProxyFlags.Downloading)
        {
            if (!startCheckPassed)
            {
                startCheckPassed = true;
                Info("Self-check: download start took +0x3ADD/+0xC8 from 0/1 to 1/0 as expected.");
            }
        }
        else
        {
            FailSelfCheck($"RequestData returned {ret} but the bytes went {obs.FlagsBefore} -> {after?.ToString() ?? "unreadable"} (expected 0/1 -> 1/0)");
        }

        notifyDownloadStarted();
    }

    // ------------------------------------------------------------------ EndRequest

    /// <summary>
    /// Reader snapshot event. For a full download (raised inside the EndRequest detour, right after the game's
    /// EndRequest) this clears the "suppressed since the last full download" mark and runs the end self-check.
    /// </summary>
    /// <param name="isFullDownload">The snapshot's source is a full download.</param>
    /// <param name="before">Bytes read before the original EndRequest (null when unknown).</param>
    /// <param name="after">Bytes read now (null when unreadable).</param>
    public void OnSnapshotCompleted(bool isFullDownload, ProxyFlags? before, ProxyFlags? after)
    {
        if (disposing || !isFullDownload)
            return;

        suppressedSinceFullDownload = false;
        if (before == ProxyFlags.Downloading && after == ProxyFlags.AtRest)
        {
            if (!endCheckPassed)
            {
                endCheckPassed = true;
                Info("Self-check: EndRequest took +0x3ADD/+0xC8 from 1/0 to 0/1 as expected.");
            }
        }
        else
        {
            FailSelfCheck($"EndRequest took the bytes {before?.ToString() ?? "unknown"} -> {after?.ToString() ?? "unreadable"} (expected 1/0 -> 0/1)");
        }
    }

    // ------------------------------------------------------------------ dispose

    /// <param name="canIssue">On the framework thread, logged in, proxy resolved.</param>
    /// <param name="flags">Current bytes (null when unreadable).</param>
    public DisposeReloadDecision DecideDisposeReload(bool canIssue, ProxyFlags? flags)
    {
        if (!suppressedSinceFullDownload)
            return DisposeReloadDecision.NotNeeded;
        if (!canIssue || flags is not { } f)
            return DisposeReloadDecision.CannotIssue;
        return f.DownloadInFlight != 0 ? DisposeReloadDecision.CoveredByInFlightDownload : DisposeReloadDecision.Issue;
    }

    // ------------------------------------------------------------------ helpers

    private void FailSelfCheck(string what)
    {
        if (selfCheckFailure != null)
            return;
        selfCheckFailure = what;
        Warn($"Self-check FAILED ({what}); fast mode disabled for this session (offsets probably moved with a patch).");
    }

    private void Info(string message)
    {
        try
        {
            info(message);
        }
        catch
        {
            // Logging must never throw into the game.
        }
    }

    private void Warn(string message)
    {
        try
        {
            warn(message);
        }
        catch
        {
            // Logging must never throw into the game.
        }
    }

    private sealed class ReplyFrame
    {
        public ReplyObservation? Observation;
        public string? Why;
        public bool SuppressPending;
        public bool Suppressed;
        public bool RequestDataPassed;
        public int RequestDataCalls;
        public bool Nested;
    }
}
