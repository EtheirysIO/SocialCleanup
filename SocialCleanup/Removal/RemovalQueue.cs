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
            suppressor: suppressor)
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

    /// <summary>The in-game Friend List window is open (sampled on the framework thread).</summary>
    public bool IsFriendListWindowOpen => session.IsFriendListWindowOpen;

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
        private volatile bool friendListOpen;
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

        /// <summary>Framework thread.</summary>
        public void Refresh()
        {
            var loggedIn = clientState.IsLoggedIn;
            var id = loggedIn && playerState.IsLoaded ? playerState.ContentId : 0;
            betweenAreas = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || condition[ConditionFlag.LoggingOut];
            try
            {
                var addon = gameGui.GetAddonByName("FriendList");
                friendListOpen = !addon.IsNull && addon.IsVisible;
            }
            catch
            {
                friendListOpen = true; // unknown → treat as open (a fast batch then refuses to start)
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
