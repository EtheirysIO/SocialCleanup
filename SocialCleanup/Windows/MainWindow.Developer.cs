using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using SocialCleanup.Friends;
using SocialCleanup.Removal;

namespace SocialCleanup.Windows;

/// <summary>
/// Developer tools (only drawn when <see cref="Configuration.ShowDeveloperTools"/> is on; collapsed by default):
/// in-game Friend List detection state, dry runs, raw diagnostics, the audit file, and the single-friend test.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Set by the plugin so the developer section can show where the audit file is.</summary>
    internal string AuditPathHint { get; set; } = "(unknown)";

    private string? auditFolderError;

    private void DrawDeveloperTools(FriendListState state, FriendListSnapshot? snap, RemovalQueueState queueState, bool batchActive, bool windowOpen)
    {
        if (!configuration.ShowDeveloperTools)
            return;

        bool expanded;
        using (ImRaii.PushColor(ImGuiCol.Header, new Vector4(0.45f, 0.12f, 0.12f, 0.8f)))
            expanded = ImGui.CollapsingHeader("Developer tools###devTools");
        if (!expanded)
            return;

        using var indent = ImRaii.PushIndent();

        // ---- in-game Friend List detection (unverified in game: confirm it flips when the window is opened)
        ImGui.TextUnformatted("In-game Friend List:");
        ImGui.SameLine();
        ImGui.TextColored(windowOpen ? ColorError : ColorOk, windowOpen ? "open" : "closed");
        ImGui.SameLine();
        ImGui.TextDisabled(FriendListWindowDetail?.Invoke() ?? "(detection not wired)");

        // ---- dry run
        var canDryRun = CanStartRemoval(state, snap, queueState, windowOpen, out var dryBlocked);
        using (ImRaii.Disabled(!canDryRun))
        {
            if (ImGui.Button($"Dry run {model.SelectedCount} selected###dryRunSelected"))
                StartDryRunDirect();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Checks every selected friend against the live list exactly like a real batch, but sends nothing." +
                             (dryBlocked != null ? $"\n\nDisabled: {dryBlocked}" : string.Empty));

        ImGui.SameLine();
        var dryDefault = configuration.DefaultDryRun;
        if (ImGui.Checkbox("Dry run pre-selected in the remove dialog###devDryDefault", ref dryDefault))
        {
            configuration.DefaultDryRun = dryDefault;
            saveConfig?.Invoke();
        }

        // ---- raw diagnostics
        if (ImGui.TreeNode("Diagnostics###devDiagnostics"))
        {
            DrawDiagnostics(state, snap, queueState);
            ImGui.TreePop();
        }

        // ---- audit file
        ImGui.TextUnformatted("Audit file:");
        ImGui.SameLine();
        ImGui.TextDisabled(AuditPathHint);
        ImGui.SameLine();
        if (ImGui.SmallButton("Open folder###devAuditFolder"))
            auditFolderError = OpenAuditFolder(AuditPathHint);
        if (auditFolderError != null)
            ImGui.TextColored(ColorWarn, auditFolderError);

        DrawSingleTestSection(state, snap, batchActive, windowOpen);
        ImGui.Spacing();
    }

    private void DrawDiagnostics(FriendListState state, FriendListSnapshot? snap, RemovalQueueState queueState)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled($"Reader: {state}{(reader.StateReason is { } r ? $" ({r})" : string.Empty)}; completion hook {(reader.IsCompletionHookActive ? "active" : "INACTIVE")}");
        if (snap != null)
        {
            ImGui.TextDisabled($"Snapshot gen {snap.Generation} ({snap.Source}, {(snap.WasRequestedByPlugin ? "requested by plugin" : "game-initiated")}), " +
                               $"taken {snap.CompletedAtUtc.ToLocalTime():HH:mm:ss}, {snap.Entries.Count} entries, native count {snap.NativeEntryCount}, " +
                               $"request id {snap.CurrentRequestId}, skipped {snap.SkippedZeroIds} zero-id / {snap.SkippedDuplicates} duplicate rows");
        }

        if (GameVersionGate is { } g)
            ImGui.TextDisabled($"Game version {g.DetectedVersion ?? "?"}: {g.Summary}");

        var avail = queue?.Availability ?? remover?.Availability;
        ImGui.TextDisabled($"Remover: {(avail is { } a ? (a.IsAvailable ? "available" : "UNAVAILABLE") + $" — {a.Reason}" : "not wired")}");
        if (directRemover is { } dr && dr.FunctionAddress != 0)
            ImGui.TextDisabled($"RemoveFriend at +0x{dr.FunctionOffset:X}");

        if (remover?.Unresolved is { } u)
        {
            ImGui.TextColored(ColorWarn,
                $"Send latch SET: {u.Target} sent {u.SentAtUtc.ToLocalTime():HH:mm:ss} at gen {u.GenerationAtSend}, request id {u.CurrentRequestIdAtSend} (native return {u.NativeReturn}).");
        }
        else
        {
            ImGui.TextDisabled(remover?.StartupBlockReason is { } sbr ? $"Send latch: startup block ({sbr})" : "Send latch: clear.");
        }

        if (queue != null)
        {
            var fa = queue.FastModeAvailability;
            ImGui.TextDisabled($"Fast mode: {(fa.IsAvailable ? "available" : "UNAVAILABLE")} — {fa.Reason}");
            ImGui.TextDisabled($"Queue: {queueState}{(queue.IsPausedForFriendListWindow ? " (paused: Friend List window)" : string.Empty)}; activity: {queue.Activity ?? "-"}");
            if (queue.Batch is { } b)
            {
                var fs = queue.FastStats;
                ImGui.TextDisabled($"Batch confirmed {b.ConfirmedAtUtc.ToLocalTime():HH:mm:ss} at gen {b.SnapshotGeneration}, dryRun={b.DryRun}, fast={b.FastMode}; " +
                                   $"fast stats: {fs.Sends} sent, {fs.Replies} replies, {fs.Suppressed} reloads suppressed, {fs.RejectedReplies} rejected");
            }

            if (queue.FinalVerification is { } fv)
                ImGui.TextDisabled($"Final verification: {fv.Status} — {fv.Detail}");
        }

        ImGui.PopTextWrapPos();
    }

    private void StartDryRunDirect()
    {
        var c = RemovalConfirmation.Create(model, dryRunDefault: true);
        if (c == null)
        {
            toolbarError = "Nothing to dry-run (no snapshot or nothing selected).";
            return;
        }

        if (c.Targets.Count == 0)
        {
            toolbarError = "Nothing to dry-run: every selected entry is a pending friend request.";
            return;
        }

        toolbarError = TryStartBatch(c)
            ? (c.Excluded.Count > 0 ? $"Dry run started; {c.Excluded.Count} pending friend request(s) left out." : null)
            : $"Dry run not started: {confirmError}";
        confirmError = null;
    }

    /// <summary>Opens Explorer on the audit file (or its folder). Returns an error text, or null.</summary>
    private static string? OpenAuditFolder(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return null;
            }

            var dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                return null;
            }

            return "The audit folder does not exist yet.";
        }
        catch (Exception ex)
        {
            return $"Could not open the folder: {ex.Message}";
        }
    }
}
