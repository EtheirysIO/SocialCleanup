using System;
using Dalamud.Configuration;

namespace SocialCleanup;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>Current config schema. 2: dry run no longer the default (see <see cref="Migrate"/>).</summary>
    public const int CurrentVersion = 2;

    // Safe ranges (the queue clamps to the same ranges again; the settings UI clamps on edit).
    public const int ItemTimeoutMinSeconds = 10;
    public const int ItemTimeoutMaxSeconds = 300;
    public const int CooldownMinMs = 0;
    public const int FastCooldownMinMs = 200;
    public const int CooldownMaxMs = 60000;
    public const int FastReplyTimeoutMinSeconds = 2;
    public const int FastReplyTimeoutMaxSeconds = 60;

    /// <summary>New installs start at <see cref="CurrentVersion"/>; a saved file keeps its own value until migrated.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>
    /// How long a refresh we started may stay in Loading before the reader gives up and goes Invalid.
    /// No EndRequest within this window means "we do not know", never "the list is complete".
    /// </summary>
    public int RefreshTimeoutSeconds { get; set; } = 15;

    /// <summary>Emit the debug-level RequestData / EndRequest telemetry lines.</summary>
    public bool FriendListTelemetry { get; set; } = true;

    /// <summary>
    /// Developer tools only: initial state of the "Dry run" option in the confirmation dialog (remembered). Default OFF.
    /// Ignored while <see cref="ShowDeveloperTools"/> is off (the dialog then never dry-runs).
    /// </summary>
    public bool DefaultDryRun { get; set; }

    /// <summary>Show the developer section (single-friend test, dry runs, raw diagnostics, audit file). Default OFF.</summary>
    public bool ShowDeveloperTools { get; set; }

    /// <summary>Log per-send / per-reply telemetry at Information instead of Debug.</summary>
    public bool VerboseLogging { get; set; }

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

    /// <summary>
    /// One-time upgrade of a saved configuration. Version 1 → 2: a saved <c>DefaultDryRun = true</c> (the old default)
    /// becomes false once; after that the user's choice is kept. Returns true when something changed (save it).
    /// </summary>
    public bool Migrate()
    {
        if (Version >= CurrentVersion)
            return false;

        if (Version < 2)
            DefaultDryRun = false;

        Version = CurrentVersion;
        return true;
    }
}
