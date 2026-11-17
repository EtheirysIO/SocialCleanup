using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using SocialCleanup.Safety;

namespace SocialCleanup.Windows;

/// <summary>
/// The ONLY window when the game-version gate blocked: static text, no table, no buttons, no settings, no references to
/// any reader / remover / queue (none exist in that state).
/// </summary>
public sealed class VersionBlockedWindow : Window
{
    private static readonly Vector4 ColorError = new(1f, 0.35f, 0.35f, 1f);

    private readonly VersionGate gate;

    public VersionBlockedWindow(VersionGate gate)
        : base("Social Cleanup (disabled)###SocialCleanupBlocked")
    {
        this.gate = gate;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        ImGui.TextColored(ColorError, "Social Cleanup is DISABLED for this game version.");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "This plugin calls and hooks reverse-engineered game functions and reads raw memory offsets that were verified "
            + "against exactly one game version. Nothing has been scanned, hooked, read or sent.");
        ImGui.Spacing();
        ImGui.TextUnformatted($"Supported game version: {gate.SupportedVersion}");
        ImGui.TextWrapped($"Detected game version:  {gate.DetectedDescription}");
        ImGui.TextWrapped($"Reason: {gate.Reason}");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Every signature and offset must be re-verified against the new game executable before the supported version "
            + "(SupportedGameVersion.Value) is changed and the plugin rebuilt. There is no override.");
    }
}
