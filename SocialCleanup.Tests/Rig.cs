using System;
using System.Collections.Generic;
using System.Linq;
using SocialCleanup.Friends;
using SocialCleanup.Removal;
using Xunit;

namespace SocialCleanup.Tests;

/// <summary>Queue core wired to fakes, with a friend list of <c>friends</c> accepted friends at request id 19.</summary>
public sealed class Rig : IDisposable
{
    public const ulong Me = 0x0011223344556677;
    public const ulong SomeoneElse = 0x00AABBCCDDEEFF00;

    public Rig(int friends = 6, IEnumerable<FriendEntry>? extra = null, byte requestId = 19)
    {
        RequestId = requestId;
        Session.LocalContentId = Me;
        List = Enumerable.Range(1, friends).Select(i => Entry((ulong)(0x1000 + i), $"Friend {i}", index: i - 1)).ToList();
        if (extra != null)
            List.AddRange(extra);
        Reader = new FakeReader();
        Remover = new FakeRemover(Reader, Audit, () => Session.LocalContentId);
        Reader.Publish(List, Me, RequestId);
        Queue = new RemovalQueueCore(Reader, Remover, Audit, Clock, Session, () => Settings, () => ExternalBusy, log: Log.Add, random: new Random(1),
            suppressor: Suppressor);
    }

    public FakeClock Clock { get; } = new();

    public FakeSession Session { get; } = new();

    public FakeAudit Audit { get; } = new();

    public FakeReader Reader { get; }

    public FakeRemover Remover { get; }

    public FakeSuppressor Suppressor { get; } = new();

    public RemovalQueueCore Queue { get; }

    public List<FriendEntry> List { get; }

    public List<string> Log { get; } = new();

    public byte RequestId { get; set; }

    public RemovalQueueSettings Settings { get; set; } =
        new RemovalQueueSettings(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(1000), TimeSpan.Zero, TimeSpan.FromMilliseconds(400))
        {
            FastReplyTimeout = TimeSpan.FromSeconds(10),
            FastCooldown = TimeSpan.FromMilliseconds(500),
            FastCooldownJitterMax = TimeSpan.Zero,
        };

    public string? ExternalBusy { get; set; }

    public static FriendEntry Entry(ulong cid, string name, byte requestState = 1, int index = 0) =>
        new(cid, name, 73, "Adamantoise", "Aether", 0, string.Empty, 0, false, "Offline", FriendGroup.None, false, false, 0, index, (uint)requestState << 4);

    public ulong[] Ids(int n) => List.Take(n).Select(e => e.ContentId).ToArray();

    public RemovalQueueItem Item(int i) => Queue.Items[i];

    public RemovalBatch Batch(bool dryRun, params ulong[] cids) =>
        new(cids.Select(c => RemovalTarget.From(List.First(e => e.ContentId == c))).ToList(), Me, Reader.Generation, Clock.UtcNow, dryRun);

    public void Start(bool dryRun, params ulong[] cids)
    {
        var ok = Queue.TryStart(Batch(dryRun, cids), out var err);
        Assert.True(ok, err);
        Assert.Equal(RemovalQueueState.Running, Queue.State);
    }

    public void StartFast(params ulong[] cids)
    {
        var ok = Queue.TryStart(Batch(false, cids) with { FastMode = true }, out var err);
        Assert.True(ok, err);
        Assert.Equal(RemovalQueueState.Running, Queue.State);
        Assert.True(Suppressor.FastBatchActive);
    }

    public void Tick(int times = 1)
    {
        for (var i = 0; i < times; i++)
            Queue.Tick();
    }

    public void Advance(double seconds) => Clock.Advance(TimeSpan.FromSeconds(seconds));

    /// <summary>Let the cooldown pass and tick once.</summary>
    public void AfterCooldown()
    {
        Advance(1.1);
        Tick();
    }

    /// <summary>What the game did in the field test: the list reloads without the target under a new request id (~7.4 s later).</summary>
    public FriendListSnapshot ServerRemoves(ulong cid, double afterSeconds = 7.4)
    {
        Advance(afterSeconds);
        List.RemoveAll(e => e.ContentId == cid);
        RequestId++;
        Suppressor.IsDownloadInFlight = false;
        return Reader.Publish(List, Me, RequestId);
    }

    /// <summary>Publish the current list again: <paramref name="newRequest"/> = a download requested after the send; false = pre-send data.</summary>
    public FriendListSnapshot Republish(bool newRequest)
    {
        if (newRequest)
            RequestId++;
        Suppressor.IsDownloadInFlight = false;
        return Reader.Publish(List, Me, RequestId);
    }

    /// <summary>Fast mode: the server replies ~0.2 s after the send.</summary>
    public bool FastReply(ulong cid, uint result = 0, double afterSeconds = 0.2)
    {
        Advance(afterSeconds);
        return Suppressor.Reply(this, cid, result);
    }

    /// <summary>Complete an outstanding (final / resume) reload with the current list under a new request id.</summary>
    public FriendListSnapshot CompleteReload(double afterSeconds = 7.0)
    {
        Assert.Equal(FriendListState.Loading, Reader.State);
        Advance(afterSeconds);
        return Republish(newRequest: true);
    }

    /// <summary>Invariant for every scenario: never more than one real TrySend per ContentId.</summary>
    public void AssertAtMostOneRealSendPerItem()
    {
        foreach (var (cid, n) in Remover.RealCalls)
            Assert.True(n <= 1, $"{cid:X16} got {n} real TrySend calls");
        AssertAtMostOneNativeCallPerItem();
    }

    /// <summary>The hard invariant (refused TrySend calls may repeat when transient): never more than one native call per ContentId.</summary>
    public void AssertAtMostOneNativeCallPerItem()
    {
        foreach (var (cid, n) in Remover.NativeCalls)
            Assert.True(n <= 1, $"{cid:X16} got {n} native calls");
    }

    public void Dispose() => Queue.Dispose();
}
