using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SocialCleanup.Windows;

/// <summary>
/// Small settings window (Settings button in the main window, or the plugin installer's settings cog). Every value is
/// clamped to the same safe range the removal queue enforces again; no setting skips or relaxes a safety check.
/// Changes apply to the next removal (the queue reads them live).
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly Configuration configuration;
    private readonly Action save;

    public SettingsWindow(Configuration configuration, Action save)
        : base("Social Cleanup settings###SocialCleanupSettings", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.configuration = configuration;
        this.save = save;
    }

    public override void Draw()
    {
        var c = configuration;
        var changed = false;

        ImGui.TextDisabled("Removal");
        var fast = c.FastModeDefault;
        if (ImGui.Checkbox("Use fast mode by default", ref fast))
        {
            c.FastModeDefault = fast;
            changed = true;
        }

        Help("Pre-selects Fast mode in the remove dialog. Fast mode skips the ~7 s friend-list reload after each removal and reloads + checks once at the end.");

        changed |= IntSetting("Delay between removals (standard)", "ms", c.RemovalCooldownMs, Configuration.CooldownMinMs, Configuration.CooldownMaxMs, 100, v => c.RemovalCooldownMs = v,
            "Pause after one removal is confirmed before the next is sent (standard mode). A small random amount is added.");
        changed |= IntSetting("Delay between removals (fast)", "ms", c.FastCooldownMs, Configuration.FastCooldownMinMs, Configuration.CooldownMaxMs, 100, v => c.FastCooldownMs = v,
            "Pause after one removal is confirmed before the next is sent (fast mode). At least 200 ms.");
        changed |= IntSetting("Per-friend timeout", "s", c.RemovalItemTimeoutSeconds, Configuration.ItemTimeoutMinSeconds, Configuration.ItemTimeoutMaxSeconds, 5, v => c.RemovalItemTimeoutSeconds = v,
            "How long to wait for a removal to be confirmed before the batch halts (the friend is then checked on Resume, never sent twice). " +
            "Also bounds waiting for a usable friend list.");
        changed |= IntSetting("Server reply timeout (fast)", "s", c.FastReplyTimeoutSeconds, Configuration.FastReplyTimeoutMinSeconds, Configuration.FastReplyTimeoutMaxSeconds, 1, v => c.FastReplyTimeoutSeconds = v,
            "Fast mode: how long to wait for the server's reply to a removal before the batch halts.");

        ImGui.Spacing();
        ImGui.TextDisabled("Logging");
        var verbose = c.VerboseLogging;
        if (ImGui.Checkbox("Verbose logging", ref verbose))
        {
            c.VerboseLogging = verbose;
            TelemetryLog.Verbose = verbose;
            changed = true;
        }

        Help("Writes the per-removal details (each send, server reply and friend-list update) to /xllog at Information level instead of Debug.");

        ImGui.Spacing();
        ImGui.TextDisabled("Advanced");
        var dev = c.ShowDeveloperTools;
        if (ImGui.Checkbox("Show developer tools", ref dev))
        {
            c.ShowDeveloperTools = dev;
            changed = true;
        }

        Help("Adds a Developer tools section to the main window: dry runs, the single-friend test, raw diagnostics, the in-game Friend List detection state and the audit file.");

        if (changed)
            save();
    }

    /// <summary>One clamped integer input. Returns true when the stored value changed.</summary>
    private static bool IntSetting(string label, string unit, int current, int min, int max, int step, Action<int> set, string help)
    {
        var v = Math.Clamp(current, min, max);
        ImGui.SetNextItemWidth(120f);
        ImGui.InputInt($"{unit}###{label}", ref v, step, step * 10);
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        Help($"{help}\nAllowed: {min}–{max} {unit}.");

        // A stored value outside the safe range is normalised (and saved) the first time the window is drawn.
        v = Math.Clamp(v, min, max);
        if (v == current)
            return false;
        set(v);
        return true;
    }

    private static void Help(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
