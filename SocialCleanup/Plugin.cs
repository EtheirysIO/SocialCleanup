using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SocialCleanup.Friends;
using SocialCleanup.Removal;
using SocialCleanup.Safety;
using SocialCleanup.Windows;

namespace SocialCleanup;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/socialcleanup";

    /// <summary>A real INTENT younger than this with no conclusive result blocks sends at startup.</summary>
    private static readonly TimeSpan StartupLatchMaxAge = TimeSpan.FromMinutes(10);

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("SocialCleanup");

    // Nullable so a partially failed constructor can dispose exactly what was built.
    private MainWindow? mainWindow;
    private VersionBlockedWindow? blockedWindow;
    private DirectFriendRemover? remover;
    private ReloadSuppressor? suppressor;
    private SingleRemovalTest? singleTest;
    private RemovalQueue? queue;
    private FriendListReader? friendList;
    private bool commandAdded;
    private bool uiHooked;
    private bool disposed;

    public Plugin()
    {
        // FIRST, before anything else is created: the hard game-version gate (never throws; any doubt blocks).
        Gate = GameVersionProbe.Check();

        try
        {
            if (!Gate.Passed)
            {
                // Blocked: ONLY a static message window and the command that opens it. No configuration load, no reader /
                // remover / suppressor / queue / single test, no signature scan, hook, native read, framework or chat
                // subscription and no audit log. There is no override.
                Log.Warning($"[VersionGate] {Gate.Summary} Plugin DISABLED: nothing is scanned, hooked, read or sent. " +
                            $"Every signature/offset must be re-verified against the new game executable before {nameof(SupportedGameVersion)}.{nameof(SupportedGameVersion.Value)} is changed.");
                blockedWindow = new VersionBlockedWindow(Gate);
                windowSystem.AddWindow(blockedWindow);
                RegisterCommandAndUi();
                return;
            }

            Log.Information($"[VersionGate] {Gate.Summary}");

            var config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            Configuration = config;

            friendList = new FriendListReader(Gate, Framework, ClientState, PlayerState, GameInterop, DataManager, Log, config);
            FriendList = friendList;

            var auditLog = new RemovalAuditLog(PluginInterface.GetPluginConfigDirectory(), Log);
            AuditLog = auditLog;
            var startupLatch = auditLog.CheckStartupLatch(StartupLatchMaxAge, DateTime.UtcNow);
            remover = new DirectFriendRemover(Gate, SigScanner, Framework, ClientState, PlayerState, Condition, friendList, auditLog, Log, startupLatch);
            singleTest = new SingleRemovalTest(Framework, ChatGui, DataManager, friendList, remover, auditLog, config, Log);

            // Fast mode hooks (reply handler + RequestData). Fail closed: when anything does not resolve / check out it
            // reports itself unavailable and only observes; the standard queue does not depend on it.
            try
            {
                suppressor = new ReloadSuppressor(Gate, SigScanner, GameInterop, Framework, ClientState, DataManager, friendList, Log);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Plugin] Fast-mode hooks could not be created; fast mode unavailable.");
                suppressor = null;
            }

            // Bulk queue. The single test and the queue exclude each other (both also go through the remover's latch).
            var test = singleTest;
            queue = new RemovalQueue(Framework, ClientState, PlayerState, Condition, GameGui, friendList, remover, auditLog, config, Log,
                externalBusy: () => test.IsInFlight ? "A single-friend removal test is in flight." : null,
                suppressor: suppressor);
            var q = queue;
            singleTest.ExternalBusy = () =>
                RemovalGate.IsBatchActive(q.State) ? $"A removal batch is {q.State}."
                : q.FinalVerification is { IsInProgress: true } ? "A removal batch's final reload / verification is in progress."
                : null;

            // Game-initiated downloads (e.g. the re-download after a removal reply) must not be timed out by the reader
            // sooner than max(item timeout, 30 s) while a send is in flight or a batch is active.
            friendList.RemovalItemTimeoutWhileActive = () =>
            {
                TimeSpan? t = null;
                if (RemovalGate.IsBatchActive(q.State) || q.InFlight != null)
                    t = q.EffectiveItemTimeout;
                if (test.IsInFlight)
                {
                    var single = TimeSpan.FromSeconds(test.TimeoutSeconds);
                    t = t is { } bt && bt > single ? bt : single;
                }

                return t;
            };

            mainWindow = new MainWindow(friendList, config, queue: queue, remover: remover, saveConfig: SaveConfiguration);
            mainWindow.AttachSingleTest(singleTest, remover);
            mainWindow.FriendListWindowOpen = () => q.IsFriendListWindowOpen;
            mainWindow.AuditPathHint = auditLog.FilePath;
            mainWindow.GameVersionGate = Gate;
            windowSystem.AddWindow(mainWindow);

            RegisterCommandAndUi();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Plugin] Construction failed; disposing what was built.");
            DisposeCore();
            throw;
        }
    }

    /// <summary>The game-version gate decided at construction. When not passed, only the blocked window exists.</summary>
    internal VersionGate Gate { get; }

    /// <summary>Null when the version gate blocked (the configuration is then not even loaded).</summary>
    public Configuration? Configuration { get; }

    /// <summary>Null when the version gate blocked.</summary>
    public IFriendListReader? FriendList { get; }

    /// <summary>Null when the version gate blocked.</summary>
    internal RemovalAuditLog? AuditLog { get; }

    public void Dispose() => DisposeCore();

    public void SaveConfiguration()
    {
        if (Configuration != null)
            PluginInterface.SavePluginConfig(Configuration);
    }

    public void ToggleMainUi()
    {
        if (mainWindow != null)
            mainWindow.Toggle();
        else
            blockedWindow?.Toggle();
    }

    private void OnCommand(string command, string args) => ToggleMainUi();

    private void RegisterCommandAndUi()
    {
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Social Cleanup window.",
        });
        commandAdded = true;

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        uiHooked = true;
    }

    /// <summary>Null-tolerant teardown in reverse construction order; each step isolated so one failure doesn't skip the rest.</summary>
    private void DisposeCore()
    {
        if (disposed)
            return;
        disposed = true;

        if (uiHooked)
        {
            Try(() => PluginInterface.UiBuilder.Draw -= windowSystem.Draw);
            Try(() => PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi);
        }

        if (commandAdded)
            Try(() => CommandManager.RemoveHandler(CommandName));

        Try(windowSystem.RemoveAllWindows);
        if (mainWindow != null)
            Try(mainWindow.Dispose);

        // Queue, harness and remover subscribe to reader/framework/chat events; drop them before the reader goes.
        // Queue first: it drives the remover.
        if (queue != null)
            Try(queue.Dispose);
        if (singleTest != null)
            Try(singleTest.Dispose);
        if (remover != null)
            Try(remover.Dispose);

        // After the queue (which stops suppression) and before the reader (it publishes local snapshots through it).
        // Issues one real reload if reloads were suppressed since the last full download.
        if (suppressor != null)
            Try(suppressor.Dispose);

        // Unsubscribes framework/login events and disposes the EndRequest hook.
        if (friendList != null)
            Try(friendList.Dispose);
    }

    private static void Try(Action a)
    {
        try
        {
            a();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Plugin] Dispose step failed.");
        }
    }
}
