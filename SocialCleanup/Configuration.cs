using System;
using Dalamud.Configuration;

namespace SocialCleanup;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// How long a refresh we started may stay in Loading before the reader gives up and goes Invalid.
    /// No EndRequest within this window means "we do not know", never "the list is complete".
    /// </summary>
    public int RefreshTimeoutSeconds { get; set; } = 15;

    /// <summary>Emit the debug-level RequestData / EndRequest telemetry lines.</summary>
    public bool FriendListTelemetry { get; set; } = true;

    /// <summary>Initial state of the "Dry run" checkbox in the removal confirmation dialog. Keep ON unless you mean it.</summary>
    public bool DefaultDryRun { get; set; } = true;

    /// <summary>
    /// Single-friend removal test: how long to wait after a real send for a newer friend-list snapshot before the
    /// verdict becomes Indeterminate. Nothing is retried or re-sent on timeout.
    /// </summary>
    public int SingleTestTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Removal queue: max time from a real send to its verdict snapshot before the item becomes Indeterminate and the
    /// queue halts (clamped 10..300 s). A removal measured ~7.5 s in game. Nothing is retried on timeout.
    /// </summary>
    public int RemovalItemTimeoutSeconds { get; set; } = 30;

    /// <summary>Removal queue: pause between one item's verdict and the next send (clamped 0..60000 ms).</summary>
    public int RemovalCooldownMs { get; set; } = 1000;

    /// <summary>Removal queue: random 0..this many ms added to every cooldown (clamped 0..10000).</summary>
    public int RemovalCooldownJitterMs { get; set; } = 250;

    /// <summary>Removal queue, dry-run batches only: pause between items so progress is watchable.</summary>
    public int RemovalDryRunDelayMs { get; set; } = 400;

    /// <summary>Last state of the "Fast mode" checkbox in the confirmation dialog (default OFF).</summary>
    public bool FastModeDefault { get; set; }

    /// <summary>Fast mode: max wait for the server reply after a send (and for the local update after it), clamped 2..60 s.</summary>
    public int FastReplyTimeoutSeconds { get; set; } = 10;

    /// <summary>Fast mode: pause between one item's verdict and the next send (clamped 200..60000 ms).</summary>
    public int FastCooldownMs { get; set; } = 500;

    /// <summary>Fast mode: random 0..this many ms added to every fast cooldown (clamped 0..10000).</summary>
    public int FastCooldownJitterMs { get; set; } = 250;
}
