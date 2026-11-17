using System;
using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using SocialCleanup.Friends;

namespace SocialCleanup.Removal;

/// <summary>
/// The removal queue as used by the plugin: <see cref="RemovalQueueCore"/> driven from <see cref="IFramework.Update"/>,
/// with login / character / zoning / friend-window state sampled on the framework thread and settings read live from the config.
/// </summary>
public sealed class RemovalQueue : RemovalQueueCore
{
    private readonly IFramework framework;
    private readonly FrameworkGameSession session;
    private bool disposed;

    public RemovalQueue(
        IFramework framework,
        IClientState clientState,
        IPlayerState playerState,
        ICondition condition,
        IGameGui gameGui,
        IFriendListReader reader,
        IFriendRemover remover,
        IRemovalAudit audit,
        Configuration config,
        IPluginLog log,
        Func<string?>? externalBusy,
        IReloadSuppressor? suppressor)
        : this(new FrameworkGameSession(clientState, playerState, condition, gameGui), framework, reader, remover, audit, config, log, externalBusy, suppressor)
    {
    }

    private RemovalQueue(
        FrameworkGameSession session,
        IFramework framework,
        IFriendListReader reader,
        IFriendRemover remover,
        IRemovalAudit audit,
        Configuration config,
        IPluginLog log,
        Func<string?>? externalBusy,
        IReloadSuppressor? suppressor)
        : base(reader, remover, audit, SystemQueueClock.Instance, session, () => SettingsFrom(config), externalBusy, m => log.Information($"[Queue] {m}"),
            suppressor: suppressor, detailLog: m => log.Telemetry($"[Queue] {m}"))
    {
        this.framework = framework;
        this.session = session;
        framework.Update += OnUpdate;
    }

    public static RemovalQueueSettings SettingsFrom(Configuration c) => new(
        TimeSpan.FromSeconds(c.RemovalItemTimeoutSeconds),
        TimeSpan.FromMilliseconds(c.RemovalCooldownMs),
        TimeSpan.FromMilliseconds(c.RemovalCooldownJitterMs),
        TimeSpan.FromMilliseconds(c.RemovalDryRunDelayMs))
    {
        FastReplyTimeout = TimeSpan.FromSeconds(c.FastReplyTimeoutSeconds),
        FastCooldown = TimeSpan.FromMilliseconds(c.FastCooldownMs),
        FastCooldownJitterMax = TimeSpan.FromMilliseconds(c.FastCooldownJitterMs),
    };

    /// <summary>
    /// The in-game Friend List window is open (sampled on the framework thread; unknown counts as open). Detection:
    /// <c>IGameGui.GetAddonByName("FriendList")</c> (FFXIVClientStructs <c>AddonFriendList</c> is
    /// <c>[Addon("FriendList")]</c>) exists and <c>IsVisible</c>.
    /// </summary>
    public bool IsFriendListWindowOpen => session.IsFriendListWindowOpen;

    /// <summary>Raw detection result for the developer section ("open (visible)", "closed (not loaded)", ...).</summary>
    public string FriendListWindowDetail => session.FriendListWindowDetail;

    public override void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        framework.Update -= OnUpdate;
        session.Dispose();
        base.Dispose();
    }

    private void OnUpdate(IFramework fw)
    {
        if (disposed)
            return;
        session.Refresh();
        Tick();
    }

    /// <summary>Session state sampled once per framework tick (so UI-thread reads never touch game state).</summary>
    private sealed class FrameworkGameSession : IGameSession, IDisposable
    {
        private readonly IClientState clientState;
        private readonly IPlayerState playerState;
        private readonly ICondition condition;
        private readonly IGameGui gameGui;
        private volatile bool isLoggedIn;
        private volatile bool betweenAreas;
        private volatile bool friendListOpen = true; // unknown until the first framework tick → treated as open
        private volatile string friendListDetail = "unknown (not sampled yet) → treated as open";
        private long contentId;
        private int logoutCount;

        public FrameworkGameSession(IClientState clientState, IPlayerState playerState, ICondition condition, IGameGui gameGui)
        {
            this.clientState = clientState;
            this.playerState = playerState;
            this.condition = condition;
            this.gameGui = gameGui;
            clientState.Logout += OnLogout;
        }

        public bool IsLoggedIn => isLoggedIn;

        public ulong LocalContentId => (ulong)Interlocked.Read(ref contentId);

        public bool IsBetweenAreas => betweenAreas;

        public int LogoutCount => Volatile.Read(ref logoutCount);

        public bool IsFriendListWindowOpen => friendListOpen;

        public string FriendListWindowDetail => friendListDetail;

        /// <summary>Framework thread.</summary>
        public void Refresh()
        {
            var loggedIn = clientState.IsLoggedIn;
            var id = loggedIn && playerState.IsLoaded ? playerState.ContentId : 0;
            betweenAreas = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || condition[ConditionFlag.LoggingOut];
            try
            {
                // "FriendList" = FFXIVClientStructs AddonFriendList ([Addon("FriendList")]). Open = exists AND visible.
                var addon = gameGui.GetAddonByName("FriendList");
                if (addon.IsNull)
                {
                    friendListOpen = false;
                    friendListDetail = "closed (addon \"FriendList\" not loaded)";
                }
                else if (addon.IsVisible)
                {
                    friendListOpen = true;
                    friendListDetail = "OPEN (addon \"FriendList\" loaded and visible)";
                }
                else
                {
                    friendListOpen = false;
                    friendListDetail = "closed (addon \"FriendList\" loaded but hidden)";
                }
            }
            catch (Exception ex)
            {
                friendListOpen = true; // unknown → treat as open (nothing starts, a running batch pauses)
                friendListDetail = $"unknown ({ex.GetType().Name}: {ex.Message}) → treated as open";
            }

            Interlocked.Exchange(ref contentId, (long)id);
            isLoggedIn = loggedIn;
        }

        public void Dispose() => clientState.Logout -= OnLogout;

        private void OnLogout(int type, int code)
        {
            Interlocked.Increment(ref logoutCount);
            isLoggedIn = false;
        }
    }
}
