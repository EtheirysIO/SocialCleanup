using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using SocialCleanup.Removal;

namespace SocialCleanup.Windows;

/// <summary>Removal queue panel (shown only while the queue holds a batch).</summary>
public sealed partial class MainWindow
{
    private string? queueActionError;

    private void DrawQueuePanel(RemovalQueueState queueState)
    {
        if (queue == null)
            return;

        using var child = ImRaii.Child("##queuePanel", Vector2.Zero, true);
        if (!child.Success)
            return;

        var items = queue.Items;
        var batch = queue.Batch;
        var counts = queue.Counts;
        var now = DateTime.UtcNow;

        ImGui.TextUnformatted("Removal queue:");
        ImGui.SameLine();
        ImGui.TextColored(QueueStateColor(queueState), $"{queueState}");
        if (queue.IsDryRun || batch?.DryRun == true)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorPending, "[DRY RUN]");
        }
        else if (batch?.FastMode == true)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorWarn, "[FAST MODE]");
        }
        else if (batch != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("[standard]");
        }

        if (batch != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"(confirmed {batch.ConfirmedAtUtc.ToLocalTime():HH:mm:ss} against generation {batch.SnapshotGeneration})");
        }

        if (batch?.FastMode == true)
        {
            var fs = queue.FastStats;
            ImGui.TextDisabled($"Fast mode: {fs.Sends} sent, {fs.Replies} server replies, {fs.Suppressed} reloads suppressed, {fs.RejectedReplies} rejected. " +
                               "Keep the in-game Friend List window closed.");
        }

        var resumeBlocked = queue.ResumeBlockedReason;
        if (queue.FinalVerification is { } fv)
        {
            ImGui.PushTextWrapPos(0);
            if (fv.Status == FinalVerificationStatus.Mismatch)
            {
                DrawMismatchBanner(fv);
            }
            else
            {
                var color = fv.Status switch
                {
                    FinalVerificationStatus.Passed => ColorOk,
                    FinalVerificationStatus.TimedOut => ColorError,
                    FinalVerificationStatus.Skipped => ColorWarn,
                    _ => ColorPending,
                };
                ImGui.TextColored(color, fv.Status == FinalVerificationStatus.TimedOut
                    ? $"FINAL VERIFICATION TIMED OUT — this batch cannot be resumed. {fv.Detail}"
                    : $"Final verification: {fv.Detail}");
            }

            ImGui.PopTextWrapPos();
        }

        var missingOutside = queue.MissingOutsideBatch;
        if (missingOutside.Count > 0 && queue.FinalVerification is not { Status: FinalVerificationStatus.Mismatch })
        {
            ImGui.PushTextWrapPos(0);
            ImGui.Separator();
            ImGui.TextColored(ColorError, $"FRIENDS NOT IN THIS BATCH WENT MISSING ({missingOutside.Count}) — Resume is disabled; Cancel / Clear only:");
            foreach (var name in missingOutside)
                ImGui.BulletText(name);
            ImGui.Separator();
            ImGui.PopTextWrapPos();
        }

        var eta = queue.EstimatedRemaining;
        var overlay = eta is { } e ? $"{counts.Done} / {counts.Total}  —  ETA {FormatAge(e)}" : $"{counts.Done} / {counts.Total}";
        ImGui.ProgressBar(counts.Total == 0 ? 0f : (float)counts.Done / counts.Total, new Vector2(-1, 0), overlay);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Average send→verdict per item this batch: {queue.AverageItemDuration.TotalSeconds:0.0}s " +
                             $"(until one is measured: {(batch?.FastMode == true ? "0.5" : "7.5")}s), plus the cooldown.");

        if (queue.Activity is { } act && queueState is RemovalQueueState.Running or RemovalQueueState.Halted)
            ImGui.TextDisabled(act);

        if (queue.HaltReason is { } halt)
        {
            ImGui.PushTextWrapPos(0);
            if (queueState == RemovalQueueState.Halted)
            {
                ImGui.TextColored(ColorError, "HALTED:");
                ImGui.SameLine();
                ImGui.TextColored(ColorError, halt);
            }
            else
            {
                ImGui.TextColored(ColorWarn, halt);
            }

            ImGui.PopTextWrapPos();
        }

        if (queue.InFlight is { } inFlight)
        {
            var el = inFlight.ElapsedAt(now);
            ImGui.TextUnformatted($"In flight: {inFlight.Target.DisplayName} @ {inFlight.Target.HomeWorldName} ({inFlight.Target.ContentId:X16}) — " +
                                  $"{ItemStatusLabel(inFlight.Status)}{(el is { } t ? $" {t.TotalSeconds:0.0}s" : string.Empty)}");
        }

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

                ImGui.SameLine();
                ImGui.TextDisabled("Stops after the in-flight item settles.");
                break;

            case RemovalQueueState.Halted:
                using (ImRaii.Disabled(queue.IsResumePending || resumeBlocked != null))
                using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.45f, 0.2f, 1f)))
                {
                    if (ImGui.Button(queue.IsResumePending ? "Resuming…###queueResume" : "Resume###queueResume"))
                        queueActionError = queue.Resume(out var err) ? null : $"Resume refused: {err ?? "no reason given"}";
                }

                if (resumeBlocked != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip($"Resume is disabled for this batch: {resumeBlocked}");
                }
                else if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip("Gets a fresh friend list and reconciles every item that may have been sent against it " +
                                     "(absent → Removed; still listed → Failed; such items are NEVER re-sent).\n" +
                                     "Continues only if the send latch is clear and every possibly-sent item could be reconciled; " +
                                     "otherwise it stays halted.\nItems refused before anything was sent go back to Pending and are retried.\n" +
                                     "A fast batch waits for its final reload / verification first.");
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
                ImGui.TextColored(queueState == RemovalQueueState.Completed ? ColorOk : ColorWarn, SummaryLine(counts, batch?.DryRun == true));
                using (ImRaii.Disabled(queue.FinalVerification is { IsInProgress: true }))
                {
                    if (ImGui.Button("Clear###queueClear"))
                    {
                        queue.Clear();
                        queueActionError = null;
                    }
                }

                break;
        }

        if (queueActionError != null)
            ImGui.TextColored(ColorError, queueActionError);

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                                      | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        using var table = ImRaii.Table("##queueItems", 4, flags, ImGui.GetContentRegionAvail());
        if (!table.Success)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthStretch, 2f);
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
                    ImGui.TextUnformatted(d);
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
        ImGui.Separator();
        ImGui.TextColored(ColorError, "FINAL VERIFICATION MISMATCH — the friend list disagrees with this batch. Resume is disabled; Cancel / Clear only.");
        if (fv.MissingNotInBatch.Count > 0)
        {
            ImGui.TextColored(ColorError, $"Missing but NOT in this batch ({fv.MissingNotInBatch.Count}):");
            foreach (var name in fv.MissingNotInBatch)
                ImGui.BulletText(name);
        }

        if (fv.StillListed.Count > 0)
        {
            ImGui.TextColored(ColorError, $"Marked Removed / Already absent but STILL LISTED ({fv.StillListed.Count}):");
            foreach (var name in fv.StillListed)
                ImGui.BulletText(name);
        }

        if (fv.MissingUnconfirmedBatchItems.Count > 0)
        {
            ImGui.TextColored(ColorWarn, $"Batch items missing without a confirmed removal ({fv.MissingUnconfirmedBatchItems.Count}):");
            foreach (var name in fv.MissingUnconfirmedBatchItems)
                ImGui.BulletText(name);
        }

        ImGui.TextDisabled(fv.Detail);
        ImGui.Separator();
    }

    private static string SummaryLine(RemovalQueueCounts c, bool dryRun) =>
        dryRun
            ? $"Dry run: {c.DryRunOk} OK, {c.AlreadyAbsent} already absent, {c.Failed} failed, {c.Indeterminate} indeterminate, {c.Cancelled} cancelled (nothing was sent)."
            : $"Removed {c.Removed}, already absent {c.AlreadyAbsent}, failed {c.Failed}, indeterminate {c.Indeterminate}, cancelled {c.Cancelled}.";

    private static Vector4 QueueStateColor(RemovalQueueState s) => s switch
    {
        RemovalQueueState.Running => ColorPending,
        RemovalQueueState.Halted => ColorError,
        RemovalQueueState.Completed => ColorOk,
        _ => ColorMuted,
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
        RemovalItemStatus.Pending => "Pending",
        RemovalItemStatus.Sending => "Sending",
        RemovalItemStatus.AwaitingRefresh => "Awaiting refresh",
        RemovalItemStatus.Removed => "Removed",
        RemovalItemStatus.AlreadyAbsent => "Already absent",
        RemovalItemStatus.DryRunOk => "Dry run OK",
        RemovalItemStatus.Failed => "Failed",
        RemovalItemStatus.Indeterminate => "INDETERMINATE",
        RemovalItemStatus.Cancelled => "Cancelled",
        _ => "?",
    };
}
