using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using SocialCleanup.Removal;

namespace SocialCleanup.Windows;

/// <summary>
/// Removal queue panel (shown only while the queue holds a batch): status line, progress bar, current friend, the
/// actions the queue allows in its state, the end-of-batch summary + final check, and a collapsible per-item list.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The per-item list starts collapsed for batches larger than this.</summary>
    private const int ItemListOpenUpTo = 10;

    private string? queueActionError;

    // Auto-expand the per-item list once per halt / mismatch (the user may collapse it again).
    private string? itemListAutoExpandedFor;

    private void DrawQueuePanel(RemovalQueueState queueState, bool windowOpen)
    {
        if (queue == null)
            return;

        using var child = ImRaii.Child("##queuePanel", Vector2.Zero, true);
        if (!child.Success)
            return;

        var items = queue.Items;
        var batch = queue.Batch;
        var counts = queue.Counts;
        var fv = queue.FinalVerification;
        var dryRun = queue.IsDryRun || batch?.DryRun == true;
        var paused = queue.IsPausedForFriendListWindow;
        var now = DateTime.UtcNow;

        // ---- status line
        var mode = dryRun ? "dry run" : batch?.FastMode == true ? "fast mode" : "standard mode";
        var (title, color) = queueState switch
        {
            RemovalQueueState.Running when paused => (RemovalQueueCore.FriendListWindowPauseText, ColorWarn),
            RemovalQueueState.Running when queue.IsCancelRequested => ("Cancelling after the current friend…", ColorWarn),
            RemovalQueueState.Running => (dryRun ? "Dry run in progress" : "Removing friends", ColorPending),
            RemovalQueueState.Halted when queue.IsResumePending => ("Resuming: getting a fresh friend list…", ColorPending),
            RemovalQueueState.Halted => ("Halted", ColorError),
            RemovalQueueState.Completed => ("Finished", ColorOk),
            RemovalQueueState.Cancelled => ("Stopped", ColorWarn),
            _ => ("Idle", ColorMuted),
        };
        ImGui.TextColored(color, title);
        ImGui.SameLine();
        ImGui.TextDisabled($"({mode})");
        if (batch != null && ImGui.IsItemHovered())
            ImGui.SetTooltip($"Confirmed {batch.ConfirmedAtUtc.ToLocalTime():HH:mm:ss} against friend-list generation {batch.SnapshotGeneration}.");

        // ---- progress
        var removed = counts.Removed + counts.AlreadyAbsent;
        var progressText = dryRun ? $"{counts.DryRunOk} of {counts.Total} checked" : $"{removed} of {counts.Total} removed";
        if (queueState == RemovalQueueState.Running && !paused && queue.EstimatedRemaining is { } eta)
            progressText += $"  ·  about {FormatAge(eta)} left";
        ImGui.ProgressBar(counts.Total == 0 ? 0f : (float)counts.Done / counts.Total, new Vector2(-1, 0), progressText);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"Average time per friend this batch: {queue.AverageItemDuration.TotalSeconds:0.0}s " +
                             $"(until one is measured: {(batch?.FastMode == true ? "0.5" : "7.5")}s), plus the delay between removals.");
        }

        // ---- current friend
        if (queue.InFlight is { } inFlight)
        {
            var el = inFlight.ElapsedAt(now);
            ImGui.TextUnformatted($"Current: {inFlight.Target.DisplayName} @ {inFlight.Target.HomeWorldName}");
            ImGui.SameLine();
            ImGui.TextDisabled($"{CurrentStepLabel(inFlight.Status, batch?.FastMode == true)}{(el is { } t ? $" {t.TotalSeconds:0.0}s" : string.Empty)}");
        }
        else if (queueState == RemovalQueueState.Running && !paused && queue.Activity is { } act)
        {
            ImGui.TextDisabled(act);
        }

        // ---- problems that block Resume for this batch
        if (fv is { Status: FinalVerificationStatus.Mismatch })
        {
            DrawMismatchBanner(fv);
        }
        else if (queue.MissingOutsideBatch is { Count: > 0 } missingOutside)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ColorError, $"{missingOutside.Count} friend{Plural(missingOutside.Count)} not in this batch went missing. This batch cannot be resumed; cancel or clear it.");
            foreach (var name in missingOutside)
                ImGui.BulletText(name);
            ImGui.PopTextWrapPos();
        }

        // ---- per state: reason + actions
        switch (queueState)
        {
            case RemovalQueueState.Running:
                using (ImRaii.Disabled(queue.IsCancelRequested))
                {
                    if (ImGui.Button(queue.IsCancelRequested ? "Cancelling…###queueCancel" : "Cancel###queueCancel"))
                    {
                        queue.Cancel();
                        queueActionError = null;
                    }
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Stops after the current friend's removal settles. Friends not yet started are left alone.");
                break;

            case RemovalQueueState.Halted:
                if (queue.HaltReason is { } halt)
                {
                    ImGui.PushTextWrapPos(0);
                    ImGui.TextColored(queue.IsResumePending ? ColorPending : ColorError, halt);
                    ImGui.PopTextWrapPos();
                }

                var resumeBlocked = queue.ResumeBlockedReason;
                using (ImRaii.Disabled(queue.IsResumePending || resumeBlocked != null || windowOpen))
                using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.45f, 0.2f, 1f)))
                {
                    if (ImGui.Button(queue.IsResumePending ? "Resuming…###queueResume" : "Resume###queueResume"))
                        queueActionError = queue.Resume(out var err) ? null : $"Resume refused: {err ?? "no reason given"}";
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(
                        resumeBlocked != null ? $"This batch cannot be resumed: {resumeBlocked}"
                        : windowOpen ? "Close the in-game Friend List window first."
                        : "Gets a fresh friend list and checks every friend that may already have been sent against it " +
                          "(gone → Removed; still listed → Failed). Those are never sent again.\n" +
                          "Continues only if everything could be checked; otherwise it stays halted.\n" +
                          "Friends refused before anything was sent are tried again.");
                }

                ImGui.SameLine();
                if (ImGui.Button("Cancel batch###queueCancel"))
                {
                    queue.Cancel();
                    queueActionError = null;
                }

                break;

            case RemovalQueueState.Completed:
            case RemovalQueueState.Cancelled:
            case RemovalQueueState.Idle:
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(queueState == RemovalQueueState.Completed ? ColorOk : ColorWarn, SummaryLine(counts, dryRun));
                if (queueState == RemovalQueueState.Cancelled && queue.HaltReason is { } why)
                    ImGui.TextDisabled(why);
                ImGui.PopTextWrapPos();
                DrawFinalCheckLine(fv, dryRun);
                using (ImRaii.Disabled(fv is { IsInProgress: true }))
                {
                    if (ImGui.Button("Clear###queueClear"))
                    {
                        queue.Clear();
                        queueActionError = null;
                        itemListAutoExpandedFor = null;
                    }
                }

                if (fv is { IsInProgress: true } && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Available once the final check has finished.");
                break;
        }

        // A halted batch may also carry a final-check result (fast mode reloads once when it halts).
        if (queueState == RemovalQueueState.Halted && fv is { Status: not FinalVerificationStatus.Mismatch })
            DrawFinalCheckLine(fv, dryRun);

        if (queueActionError != null)
            ImGui.TextColored(ColorError, queueActionError);

        DrawQueueItems(items, queueState, batch, fv, now);
    }

    /// <summary>One line for the end-of-batch check (Mismatch has its own banner).</summary>
    private static void DrawFinalCheckLine(FinalVerification? fv, bool dryRun)
    {
        if (dryRun || fv == null || fv.Status == FinalVerificationStatus.Mismatch)
            return;

        var (text, color) = fv.Status switch
        {
            FinalVerificationStatus.Passed => ("Final check passed: the reloaded friend list matches.", ColorOk),
            FinalVerificationStatus.Pending or FinalVerificationStatus.Requested => ("Final check: reloading the friend list…", ColorPending),
            FinalVerificationStatus.TimedOut => ("Final check timed out: the friend list did not reload in time. Open the in-game Friend List to check it; this batch cannot be resumed.", ColorError),
            FinalVerificationStatus.Skipped => ("Final check skipped (logged out or character changed). The game reloads the friend list on login; check it there.", ColorWarn),
            _ => (fv.Detail, ColorMuted),
        };
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(fv.Detail);
    }

    private void DrawQueueItems(System.Collections.Generic.IReadOnlyList<RemovalQueueItem> items, RemovalQueueState queueState, RemovalBatch? batch, FinalVerification? fv, DateTime now)
    {
        if (items.Count == 0)
            return;

        // Per batch: default open for small batches; force open once on a halt or a final-check mismatch.
        var batchKey = batch?.ConfirmedAtUtc.Ticks ?? 0;
        var expandKey = queueState == RemovalQueueState.Halted ? $"{batchKey}:halt:{queue?.HaltReason}"
            : fv is { Status: FinalVerificationStatus.Mismatch } ? $"{batchKey}:mismatch"
            : null;
        if (expandKey != null && expandKey != itemListAutoExpandedFor)
        {
            itemListAutoExpandedFor = expandKey;
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        }
        else
        {
            ImGui.SetNextItemOpen(items.Count <= ItemListOpenUpTo, ImGuiCond.Once);
        }

        if (!ImGui.CollapsingHeader($"Friends in this batch ({items.Count})###queueItems{batchKey}"))
            return;

        const ImGuiTableFlags flags = ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                                      | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        var height = MathF.Max(ImGui.GetContentRegionAvail().Y, ImGui.GetTextLineHeightWithSpacing() * 6);
        using var table = ImRaii.Table("##queueItems", 4, flags, new Vector2(0, height));
        if (!table.Success)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Friend", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableHeadersRow();

        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(items.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var it = items[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{it.Target.DisplayName} @ {it.Target.HomeWorldName}");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"ContentId {it.Target.ContentId:X16}");
                ImGui.TableNextColumn();
                ImGui.TextColored(ItemStatusColor(it.Status), ItemStatusLabel(it.Status));
                ImGui.TableNextColumn();
                if (it.ElapsedAt(now) is { } elapsed)
                    ImGui.TextDisabled($"{elapsed.TotalSeconds:0.0}s");
                ImGui.TableNextColumn();
                if (it.Detail is { } d)
                {
                    ImGui.TextDisabled(d);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(d);
                }
            }
        }

        clipper.End();
        clipper.Destroy();
    }

    /// <summary>Final verification Mismatch: prominent, naming every friend involved.</summary>
    private static void DrawMismatchBanner(FinalVerification fv)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(ColorError, "Final check failed: the reloaded friend list does not match this batch. This batch cannot be resumed; cancel or clear it.");
        if (fv.MissingNotInBatch.Count > 0)
        {
            ImGui.TextColored(ColorError, $"Missing, but not part of this batch ({fv.MissingNotInBatch.Count}):");
            foreach (var name in fv.MissingNotInBatch)
                ImGui.BulletText(name);
        }

        if (fv.StillListed.Count > 0)
        {
            ImGui.TextColored(ColorError, $"Marked removed, but still listed ({fv.StillListed.Count}):");
            foreach (var name in fv.StillListed)
                ImGui.BulletText(name);
        }

        if (fv.MissingUnconfirmedBatchItems.Count > 0)
        {
            ImGui.TextColored(ColorWarn, $"In this batch and missing, without a confirmed removal ({fv.MissingUnconfirmedBatchItems.Count}):");
            foreach (var name in fv.MissingUnconfirmedBatchItems)
                ImGui.BulletText(name);
        }

        ImGui.TextDisabled(fv.Detail);
        ImGui.PopTextWrapPos();
    }

    private static string SummaryLine(RemovalQueueCounts c, bool dryRun)
    {
        if (dryRun)
            return $"Dry run: {c.DryRunOk} OK, {c.AlreadyAbsent} already gone, {c.Failed} failed, {c.Indeterminate} unclear, {c.Cancelled} not started. Nothing was sent.";

        var s = $"Removed {c.Removed} of {c.Total}";
        if (c.AlreadyAbsent > 0)
            s += $", {c.AlreadyAbsent} already gone";
        if (c.Failed > 0)
            s += $", {c.Failed} failed";
        if (c.Indeterminate > 0)
            s += $", {c.Indeterminate} unclear";
        if (c.Cancelled > 0)
            s += $", {c.Cancelled} not started";
        return s + ".";
    }

    private static string CurrentStepLabel(RemovalItemStatus s, bool fast) => s switch
    {
        RemovalItemStatus.Sending => "sending",
        RemovalItemStatus.AwaitingRefresh => fast ? "waiting for the server" : "waiting for the friend list to reload",
        _ => ItemStatusLabel(s),
    };

    private static Vector4 ItemStatusColor(RemovalItemStatus s) => s switch
    {
        RemovalItemStatus.Removed or RemovalItemStatus.AlreadyAbsent or RemovalItemStatus.DryRunOk => ColorOk,
        RemovalItemStatus.Sending or RemovalItemStatus.AwaitingRefresh => ColorPending,
        RemovalItemStatus.Failed => ColorError,
        RemovalItemStatus.Indeterminate => ColorWarn,
        _ => ColorMuted,
    };

    private static string ItemStatusLabel(RemovalItemStatus s) => s switch
    {
        RemovalItemStatus.Pending => "Waiting",
        RemovalItemStatus.Sending => "Sending",
        RemovalItemStatus.AwaitingRefresh => "Confirming",
        RemovalItemStatus.Removed => "Removed",
        RemovalItemStatus.AlreadyAbsent => "Already gone",
        RemovalItemStatus.DryRunOk => "Dry run OK",
        RemovalItemStatus.Failed => "Failed",
        RemovalItemStatus.Indeterminate => "Unclear",
        RemovalItemStatus.Cancelled => "Not started",
        _ => "?",
    };
}
