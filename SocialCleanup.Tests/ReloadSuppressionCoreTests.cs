using System;
using System.Collections.Generic;
using SocialCleanup.Removal;
using Xunit;

namespace SocialCleanup.Tests;

/// <summary>
/// The fast-mode decision state machine the real hooks drive (reply enter → RequestData call(s) → reply exit →
/// after-reply publish), with the game simulated: a "handler" lambda stands in for the game's reply handler.
/// </summary>
public sealed class ReloadSuppressionCoreTests
{
    private const ulong Target = 0x1001;
    private const ulong Other = 0x1002;
    private static readonly nint FriendProxy = 0x7000_0000;
    private static readonly nint OtherProxy = 0x7100_0000;

    private sealed class Game
    {
        public Game()
        {
            Core = new ReloadSuppressionCore(Log.Add, m => Log.Add("WARN " + m));
        }

        public ReloadSuppressionCore Core { get; }

        public List<string> Log { get; } = new();

        public List<string> Order { get; } = new();

        public List<ServerReply> Replies { get; } = new();

        public ProxyFlags Flags { get; set; } = ProxyFlags.AtRest;

        public bool ReaderReady { get; set; } = true;

        public bool Available { get; set; } = true;

        public bool PresentBefore { get; set; } = true;

        public bool OnFramework { get; set; } = true;

        public int OriginalRequestDataCalls { get; private set; }

        public int DownloadsNotified { get; private set; }

        public int Publishes { get; private set; }

        public bool PublishResult { get; set; } = true;

        public Action? OnRaise { get; set; }

        public Func<string, bool>? PublishOverride { get; set; }

        public bool ObserveBeforeThrows { get; set; }

        public bool ObserveAfterNull { get; set; }

        /// <summary>Arms <see cref="Target"/> inside an active fast batch.</summary>
        public Game Armed(ulong cid = Target)
        {
            Core.SetFastBatchActive(true);
            Core.Arm(cid);
            return this;
        }

        /// <summary>The proxy's RequestData through the hook (the original starts a download).</summary>
        public byte RequestData(nint? self = null, bool? onFramework = null, Func<byte>? original = null)
        {
            var s = self ?? FriendProxy;
            return Core.RunRequestData(
                () => new RequestDataObservation(s, s == FriendProxy, onFramework ?? OnFramework, Flags),
                original ?? (() =>
                {
                    OriginalRequestDataCalls++;
                    Flags = ProxyFlags.Downloading;
                    return 1;
                }),
                () => s == FriendProxy ? Flags : null,
                () => DownloadsNotified++);
        }

        /// <summary>The game's reply handler for a result-0 reply: LocalRemove, then RequestData.</summary>
        public Action StandardHandler(uint result) => () =>
        {
            if (result == 0)
                RequestData();
        };

        public void Reply(ulong cid, uint result = 0, Action? handler = null, nint? proxy = null)
        {
            Core.RunReply(
                () =>
                {
                    if (ObserveBeforeThrows)
                        throw new InvalidOperationException("payload unreadable");
                    return new ReplyObservation(cid, result, 0, "Name", OnFramework, proxy ?? FriendProxy, 6, PresentBefore, Flags, ReaderReady, Available);
                },
                handler ?? StandardHandler(result),
                _ => ObserveAfterNull ? null : new ReplyAfterObservation(5, false, Flags),
                r =>
                {
                    Order.Add($"reply {r.ContentId:X}");
                    Replies.Add(r);
                    OnRaise?.Invoke();
                },
                why =>
                {
                    Order.Add("publish");
                    Publishes++;
                    return PublishOverride?.Invoke(why) ?? PublishResult;
                });
        }

        /// <summary>The full download that follows a passed RequestData completes (EndRequest 1/0 → 0/1).</summary>
        public void EndRequest(bool full = true)
        {
            var before = Flags;
            Flags = ProxyFlags.AtRest;
            Core.OnSnapshotCompleted(full, before, Flags);
        }
    }

    // ------------------------------------------------------------------ the happy path + ordering

    [Fact]
    public void ArmedResult0Reply_SuppressesTheHandlersRequestData_ThenPublishesAfterTheReply()
    {
        var g = new Game().Armed();
        g.Reply(Target);

        Assert.Equal(0, g.OriginalRequestDataCalls);
        Assert.Equal(0, g.DownloadsNotified);
        var r = Assert.Single(g.Replies);
        Assert.True(r.Suppressed);
        Assert.Null(r.NotSuppressedReason);
        Assert.Equal(new[] { $"reply {Target:X}", "publish" }, g.Order); // reply received, THEN the local snapshot
        Assert.Equal(1, g.Core.SuppressedCount);
        Assert.Equal(1, g.Core.LocalPublishes);
        Assert.True(g.Core.SuppressedSinceFullDownload);
        Assert.Equal(0ul, g.Core.ArmedContentId); // one reply per armed send
        Assert.Equal(0, g.Core.ReplyDepth);
    }

    [Fact]
    public void AtMostOneSuppressionPerReply_ASecondRequestDataPasses_AndBlocksThePublish()
    {
        var g = new Game().Armed();
        g.Reply(Target, handler: () =>
        {
            Assert.Equal(0, g.RequestData());
            Assert.Equal(1, g.RequestData());
        });

        Assert.Equal(1, g.OriginalRequestDataCalls);
        Assert.Equal(1, g.Core.SuppressedCount);
        Assert.True(Assert.Single(g.Replies).Suppressed);
        Assert.Equal(0, g.Publishes); // a RequestData passed through during that handler call
        Assert.Contains(g.Log, l => l.Contains("NOT published") && l.Contains("passed through"));
    }

    [Fact]
    public void SecondReplyForTheSameTarget_IsNotSuppressed_BecauseTheArmIsConsumed()
    {
        var g = new Game().Armed();
        g.Reply(Target);
        g.Flags = ProxyFlags.AtRest;
        g.Reply(Target);

        Assert.Equal(1, g.OriginalRequestDataCalls);
        Assert.False(g.Replies[1].Suppressed);
        Assert.Contains("no armed target", g.Replies[1].NotSuppressedReason);
        Assert.Equal(1, g.Publishes);
    }

    // ------------------------------------------------------------------ what never suppresses

    [Fact]
    public void ReplyForANonArmedContentId_NeverSuppresses_AndLeavesTheArmInPlace()
    {
        var g = new Game().Armed();
        g.Reply(Other);

        Assert.Equal(1, g.OriginalRequestDataCalls);
        Assert.Equal(1, g.DownloadsNotified);
        var r = Assert.Single(g.Replies);
        Assert.False(r.Suppressed);
        Assert.Contains("armed target is", r.NotSuppressedReason);
        Assert.Equal(0, g.Publishes);
        Assert.Equal(Target, g.Core.ArmedContentId);
    }

    [Fact]
    public void NonZeroResult_NeverSuppresses_EvenIfTheHandlerCallsRequestData()
    {
        var g = new Game().Armed();
        g.Reply(Target, result: 340, handler: () => g.RequestData());

        Assert.Equal(1, g.OriginalRequestDataCalls);
        var r = Assert.Single(g.Replies);
        Assert.False(r.Suppressed);
        Assert.Contains("result 340", r.NotSuppressedReason);
        Assert.Equal(0, g.Publishes);
        Assert.Equal(0ul, g.Core.ArmedContentId);
        Assert.Null(g.Core.Anomaly);
    }

    [Theory]
    [InlineData("otherProxy")]
    [InlineData("nullProxy")]
    [InlineData("offThread")]
    public void RequestDataWithTheWrongIdentity_PassesThrough(string kind)
    {
        var g = new Game().Armed();
        g.Reply(Target, handler: () =>
        {
            var ret = kind switch
            {
                "otherProxy" => g.RequestData(self: OtherProxy, original: () => 1),
                "nullProxy" => g.RequestData(self: 0, original: () => 1),
                _ => g.RequestData(onFramework: false),
            };
            Assert.Equal(1, ret);
        });

        var r = Assert.Single(g.Replies);
        Assert.False(r.Suppressed);
        Assert.Contains("did not qualify", r.NotSuppressedReason);
        Assert.Equal(0, g.Publishes);
        Assert.Equal(0, g.Core.SuppressedCount);
        Assert.False(g.Core.SuppressedSinceFullDownload);
    }

    [Fact]
    public void ReplyObservedForADifferentProxyThanTheHandlersCall_PassesThrough()
    {
        var g = new Game().Armed();
        g.Reply(Target, proxy: OtherProxy); // proxy at reply time != the one RequestData is called on
        Assert.Equal(1, g.OriginalRequestDataCalls);
        Assert.False(Assert.Single(g.Replies).Suppressed);
    }

    [Fact]
    public void RequestDataOutsideAnyReplyHandler_IsNeverSuppressed()
    {
        var g = new Game().Armed();
        Assert.Equal(1, g.RequestData());
        Assert.Equal(1, g.OriginalRequestDataCalls);
        Assert.Equal(1, g.DownloadsNotified);
        Assert.Equal(0, g.Core.SuppressedCount);
        Assert.Equal(Target, g.Core.ArmedContentId);
    }

    [Theory]
    [InlineData("notAvailable")]
    [InlineData("noBatch")]
    [InlineData("downloadInFlight")]
    [InlineData("listNotComplete")]
    [InlineData("readerNotReady")]
    [InlineData("targetNotListed")]
    [InlineData("offThread")]
    public void PolicyInputsAtReplyTime_EachPreventsSuppression(string kind)
    {
        var g = new Game();
        if (kind != "noBatch")
            g.Armed();
        else
            g.Core.Arm(Target);
        switch (kind)
        {
            case "notAvailable": g.Available = false; break;
            case "downloadInFlight": g.Flags = new ProxyFlags(1, 1); break;
            case "listNotComplete": g.Flags = new ProxyFlags(0, 0); break;
            case "readerNotReady": g.ReaderReady = false; break;
            case "targetNotListed": g.PresentBefore = false; break;
            case "offThread": g.OnFramework = false; break;
        }

        g.Reply(Target, handler: () => g.RequestData(original: () => 1));
        Assert.False(Assert.Single(g.Replies).Suppressed);
        Assert.Equal(0, g.Publishes);
        Assert.Equal(0, g.Core.SuppressedCount);
        if (kind == "offThread")
        {
            Assert.True(g.Core.OffThreadObserved);
            Assert.NotNull(g.Core.Blocker);
        }
    }

    // ------------------------------------------------------------------ "handler never called RequestData"

    [Fact]
    public void HandlerNeverCalledRequestData_IsReported_AndDisablesFastMode()
    {
        var g = new Game().Armed();
        g.Reply(Target, handler: () => { });

        var r = Assert.Single(g.Replies);
        Assert.False(r.Suppressed);
        Assert.Contains("never called RequestData", r.NotSuppressedReason);
        Assert.Equal(1, g.Core.HandlerSkippedRequestDataCount);
        Assert.Contains("never called RequestData", g.Core.Anomaly);
        Assert.Contains("never called RequestData", g.Core.Blocker);
        Assert.Contains(g.Log, l => l.StartsWith("WARN ANOMALY"));
        Assert.Equal(0, g.Publishes);
    }

    // ------------------------------------------------------------------ exceptions at each step

    [Fact]
    public void OriginalHandlerThrowsBeforeRequestData_StateClearedInFinally_NextRequestDataPasses()
    {
        var g = new Game().Armed();
        Assert.Throws<InvalidOperationException>(() => g.Reply(Target, handler: () => throw new InvalidOperationException("game")));

        Assert.Equal(0, g.Core.ReplyDepth);
        Assert.Empty(g.Replies); // the original failed: no post-processing
        Assert.Equal(1, g.RequestData()); // outside any reply now: never suppressed
        Assert.Equal(1, g.OriginalRequestDataCalls);
        Assert.Equal(0, g.Core.SuppressedCount);
    }

    [Fact]
    public void OriginalHandlerThrowsAfterTheSuppressedRequestData_StateCleared_NoPublish()
    {
        var g = new Game().Armed();
        Assert.Throws<InvalidOperationException>(() => g.Reply(Target, handler: () =>
        {
            g.RequestData();
            throw new InvalidOperationException("game");
        }));

        Assert.Equal(0, g.Core.ReplyDepth);
        Assert.Equal(0, g.Publishes);
        Assert.Equal(1, g.RequestData());
        Assert.Equal(1, g.OriginalRequestDataCalls);
    }

    [Fact]
    public void OriginalRequestDataThrowsInsideTheHandler_PropagatesAndClearsTheFrame()
    {
        var g = new Game().Armed(Other); // the reply below is not armed: its RequestData passes to the original
        Assert.Throws<InvalidOperationException>(() =>
            g.Reply(Target, handler: () => g.RequestData(original: () => throw new InvalidOperationException("game RequestData"))));
        Assert.Equal(0, g.Core.ReplyDepth);
        Assert.Equal(1, g.RequestData());
    }

    [Fact]
    public void ObservingThePayloadThrows_NothingSuppressed_OriginalStillRunsOnce()
    {
        var g = new Game().Armed();
        g.ObserveBeforeThrows = true;
        var handlerCalls = 0;
        g.Reply(Target, handler: () =>
        {
            handlerCalls++;
            g.RequestData();
        });

        Assert.Equal(1, handlerCalls);
        Assert.Equal(1, g.OriginalRequestDataCalls);
        Assert.Empty(g.Replies);
        Assert.Equal(0, g.Core.ReplyDepth);
        Assert.Contains(g.Log, l => l.Contains("pre-processing failed"));
    }

    [Fact]
    public void ReplyEventHandlerThrows_StillPublishes_AndPublishThrowingIsContained()
    {
        var g = new Game().Armed();
        g.OnRaise = () => throw new InvalidOperationException("queue");
        g.Reply(Target);
        Assert.Equal(1, g.Publishes);

        var h = new Game().Armed();
        h.PublishOverride = _ => throw new InvalidOperationException("reader");
        h.Reply(Target); // must not throw into the game
        Assert.Equal(0, h.Core.ReplyDepth);
        Assert.Contains(h.Log, l => l.Contains("post-processing failed"));
    }

    // ------------------------------------------------------------------ nested / re-entrant replies

    [Fact]
    public void NestedReply_CancelsEveryPendingSuppression_AndNobodyPublishes()
    {
        var g = new Game().Armed();
        g.Reply(Target, handler: () =>
        {
            g.Reply(Other, handler: () => { });
            Assert.Equal(1, g.RequestData()); // the outer handler's RequestData now passes
        });

        Assert.Equal(1, g.OriginalRequestDataCalls);
        Assert.Equal(2, g.Replies.Count);
        Assert.All(g.Replies, r => Assert.False(r.Suppressed));
        Assert.Equal(0, g.Publishes);
        Assert.Equal(0, g.Core.ReplyDepth);
        Assert.Null(g.Core.Anomaly); // not mistaken for "never called RequestData"
    }

    [Fact]
    public void NestedReplyForTheArmedTarget_IsNotSuppressedEither()
    {
        var g = new Game().Armed();
        g.Reply(Other, handler: () => g.Reply(Target));
        Assert.Equal(1, g.OriginalRequestDataCalls);
        Assert.All(g.Replies, r => Assert.False(r.Suppressed));
        Assert.Equal(0, g.Publishes);
    }

    // ------------------------------------------------------------------ after-reply publish conditions

    [Fact]
    public void NoLocalPublish_WhenADownloadIsInFlightAfterTheHandler()
    {
        var g = new Game().Armed();
        g.Reply(Target, handler: () =>
        {
            g.RequestData();
            g.Flags = new ProxyFlags(1, 0); // something started a download (bytes say so) without a hooked call
        });
        Assert.True(Assert.Single(g.Replies).Suppressed);
        Assert.Equal(0, g.Publishes);
    }

    [Fact]
    public void NoLocalPublish_WhenTheProxyIsUnreadableAfterTheHandler()
    {
        var g = new Game().Armed();
        g.ObserveAfterNull = true;
        g.Reply(Target);
        Assert.True(Assert.Single(g.Replies).Suppressed);
        Assert.Equal(0, g.Publishes);
    }

    // ------------------------------------------------------------------ bookkeeping + dispose

    [Fact]
    public void SuppressedSinceFullDownload_AndTheDisposeReloadDecision()
    {
        var g = new Game().Armed();
        Assert.Equal(DisposeReloadDecision.NotNeeded, g.Core.DecideDisposeReload(true, ProxyFlags.AtRest));

        g.Reply(Target);
        Assert.True(g.Core.SuppressedSinceFullDownload);
        Assert.Equal(DisposeReloadDecision.Issue, g.Core.DecideDisposeReload(true, ProxyFlags.AtRest));
        Assert.Equal(DisposeReloadDecision.CoveredByInFlightDownload, g.Core.DecideDisposeReload(true, ProxyFlags.Downloading));
        Assert.Equal(DisposeReloadDecision.CannotIssue, g.Core.DecideDisposeReload(false, null));
        Assert.Equal(DisposeReloadDecision.CannotIssue, g.Core.DecideDisposeReload(true, null));

        g.Core.OnSnapshotCompleted(false, null, ProxyFlags.AtRest); // a local snapshot is not a full download
        Assert.True(g.Core.SuppressedSinceFullDownload);

        g.RequestData();
        g.EndRequest();
        Assert.False(g.Core.SuppressedSinceFullDownload);
        Assert.Equal(DisposeReloadDecision.NotNeeded, g.Core.DecideDisposeReload(true, ProxyFlags.AtRest));
    }

    [Fact]
    public void AfterBeginDispose_OnlyTheOriginalsRun()
    {
        var g = new Game().Armed();
        g.Core.BeginDispose();
        g.Reply(Target);
        Assert.Equal(1, g.OriginalRequestDataCalls);
        Assert.Empty(g.Replies);
        Assert.Equal(0, g.Publishes);
        Assert.Equal(0, g.DownloadsNotified);
        Assert.False(g.Core.FastBatchActive);
    }

    // ------------------------------------------------------------------ self-check

    [Fact]
    public void SelfCheck_PassesOnlyOnTheExactTransitions()
    {
        var g = new Game();
        Assert.Contains("pending", g.Core.Blocker);
        g.RequestData();                 // 0/1 -> 1/0
        Assert.True(g.Core.StartCheckPassed);
        Assert.False(g.Core.EndCheckPassed);
        g.EndRequest();                  // 1/0 -> 0/1
        Assert.True(g.Core.EndCheckPassed);
        Assert.Null(g.Core.Blocker);
        Assert.Equal(1, g.DownloadsNotified);
    }

    [Theory]
    [InlineData(0, 1, 1, 1)] // +0xC8 not cleared
    [InlineData(0, 1, 0, 1)] // nothing changed
    [InlineData(1, 0, 1, 0)] // already downloading before the call
    [InlineData(0, 0, 1, 0)] // +0xC8 was not 1 before
    public void SelfCheck_DownloadStart_AnyOtherTransitionFails(byte dlBefore, byte c8Before, byte dlAfter, byte c8After)
    {
        var g = new Game { Flags = new ProxyFlags(dlBefore, c8Before) };
        g.RequestData(original: () =>
        {
            g.Flags = new ProxyFlags(dlAfter, c8After);
            return 1;
        });
        Assert.NotNull(g.Core.SelfCheckFailure);
        Assert.Contains("FAILED", g.Core.Blocker);

        // Sticky: a later correct transition does not clear it.
        g.Flags = ProxyFlags.AtRest;
        g.RequestData();
        g.EndRequest();
        Assert.Contains("FAILED", g.Core.Blocker);
    }

    [Theory]
    [InlineData(1, 0, 1, 0)]
    [InlineData(0, 1, 0, 1)]
    [InlineData(1, 0, 0, 0)]
    [InlineData(1, 1, 0, 1)]
    public void SelfCheck_EndRequest_AnyOtherTransitionFails(byte dlBefore, byte c8Before, byte dlAfter, byte c8After)
    {
        var g = new Game();
        g.Core.OnSnapshotCompleted(true, new ProxyFlags(dlBefore, c8Before), new ProxyFlags(dlAfter, c8After));
        Assert.NotNull(g.Core.SelfCheckFailure);
        Assert.False(g.Core.EndCheckPassed);
    }

    [Fact]
    public void SelfCheck_UnknownBytesFail()
    {
        var g = new Game();
        g.Core.OnSnapshotCompleted(true, null, ProxyFlags.AtRest);
        Assert.NotNull(g.Core.SelfCheckFailure);

        var h = new Game();
        h.Core.RunRequestData(() => new RequestDataObservation(FriendProxy, true, true, ProxyFlags.AtRest), () => 1, () => null, () => { });
        Assert.NotNull(h.Core.SelfCheckFailure);
    }

    [Fact]
    public void SelfCheck_ARefusedRequestData_ChecksNothing_AndNotifiesNothing()
    {
        var g = new Game();
        g.RequestData(original: () => 0);
        Assert.Null(g.Core.SelfCheckFailure);
        Assert.False(g.Core.StartCheckPassed);
        Assert.Equal(0, g.DownloadsNotified);
    }
}
