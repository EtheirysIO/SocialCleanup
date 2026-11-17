using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using SocialCleanup.Friends;
using SocialCleanup.Removal;

namespace SocialCleanup.Windows;

/// <summary>
/// Developer-only "Single-friend removal test" section (inside Developer tools): one dry run or one real removal of
/// exactly one selected friend, with live telemetry. The multi-select queue is not involved.
/// </summary>
public sealed partial class MainWindow
{
    private const string SingleTestPopupId = "Confirm single friend removal###SocialCleanupSingleTestConfirm";

    private SingleRemovalTest? singleTest;
    private DirectFriendRemover? directRemover;

    // Cached single selection (recomputed only when selection or snapshot changes).
    private int singleSelCacheVersion = -1;
    private FriendListSnapshot? singleSelCacheSnap;
    private FriendEntry? singleSelEntry;

    // Frozen at modal open.
    private FriendEntry? singleConfirmEntry;
    private long singleConfirmGeneration;
    private ulong singleConfirmLocalId;
    private bool singleConfirmAck;
    private bool openSingleConfirmRequested;
    private string? singleTestError;
    private string? startupBlockClearError;

    /// <summary>Wire the developer single-removal harness (optional; the section explains when it is missing).</summary>
    internal void AttachSingleTest(SingleRemovalTest? test, DirectFriendRemover? remover)
    {
        singleTest = test;
        directRemover = remover;
    }

    private void DrawSingleTestSection(FriendListState state, FriendListSnapshot? snap, bool batchActive, bool windowOpen)
    {
        // Collapsed by default (no DefaultOpen flag).
        using var node = ImRaii.TreeNode("Single-friend removal test###singleTestHeader");
        if (!node.Success)
            return;

        if (singleTest == null || directRemover == null)
        {
            ImGui.TextColored(ColorWarn, "Single-removal harness is not wired in this build.");
            return;
        }

        var test = singleTest;
        var rem = directRemover;

        // ---- remover status
        var avail = rem.Availability;
        ImGui.TextUnformatted("Remover:");
        ImGui.SameLine();
        ImGui.TextColored(avail.IsAvailable ? ColorOk : ColorError, avail.IsAvailable ? "available" : "UNAVAILABLE");
        ImGui.SameLine();
        ImGui.TextDisabled(avail.Reason);
        if (rem.StartupBlock is { } sb)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ColorError, $"Sends BLOCKED since startup (clear it with the banner at the top): {sb.Reason}" +
                                          (sb.Target is { } bt ? $" Target ContentId {bt.ContentId:X16}." : string.Empty));
            ImGui.PopTextWrapPos();
        }

        if (rem.Unresolved is { } u)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ColorWarn,
                $"Send latch SET: {u.Target} sent {u.SentAtUtc.ToLocalTime():HH:mm:ss} at gen {u.GenerationAtSend}, request id {u.CurrentRequestIdAtSend} " +
                $"(native return {u.NativeReturn}). Further sends are refused until a newer snapshot for this character lacks the target " +
                "or comes from a download requested after the send.");
            ImGui.PopTextWrapPos();
        }
        else if (rem.StartupBlock == null)
        {
            ImGui.TextDisabled("Send latch: clear.");
        }

        ImGui.TextDisabled($"Timeout {test.TimeoutSeconds}s  |  removal message template (best effort): {test.RemovedMessageTemplate ?? "(not loaded)"}");

        // ---- selection
        var entry = GetSingleSelection(snap);
        var inFlight = test.IsInFlight;
        string? blocked = null;
        if (windowOpen)
            blocked = "Close the in-game Friend List window first.";
        else if (batchActive)
            blocked = "Disabled while a removal batch is running or halted.";
        else if (model.SelectedCount != 1)
            blocked = $"Select exactly one friend ({model.SelectedCount} selected).";
        else if (entry == null)
            blocked = "Selected friend is not in the current snapshot.";
        else if (!avail.IsAvailable)
            blocked = $"Remover unavailable: {avail.Reason}";
        else if (inFlight)
            blocked = $"A test is in flight ({test.Phase}).";
        else if (snap == null || state != FriendListState.Ready || !reader.IsCompletionHookActive)
            blocked = $"Friend list must be Ready (is {state}).";

        if (entry != null)
        {
            ImGui.TextUnformatted($"Target: {entry.DisplayName} @ {entry.HomeWorldName}");
            ImGui.SameLine();
            ImGui.TextDisabled($"ContentId {entry.ContentId:X16}  index(snapshot) {entry.ListIndex}  ExtraFlags 0x{entry.ExtraFlags:X8}  " +
                               $"requestState {entry.RequestState}{(entry.PendingLabel is { } pl ? $" ({pl})" : string.Empty)}");
        }

        var ineligible = entry == null ? null : IneligibleReason(entry);
        if (ineligible != null)
            ImGui.TextColored(ColorWarn, $"Not removable: {ineligible}");

        if (blocked != null)
            ImGui.TextColored(model.SelectedCount == 0 ? ColorMuted : ColorWarn, blocked);

        using (ImRaii.Disabled(blocked != null))
        {
            var dryLabel = model.SelectedCount > 1
                ? $"Dry run (needs exactly 1 selected friend; {model.SelectedCount} selected)###singleDry"
                : "Dry run (send nothing)###singleDry";
            if (ImGui.Button(dryLabel) && entry != null && snap != null)
            {
                singleTestError = test.Request(RemovalTarget.From(entry), true, snap.Generation, snap.LocalContentId, out var err)
                    ? null
                    : err;
            }

            ImGui.SameLine();
            using (ImRaii.Disabled(ineligible != null || rem.HasUnresolvedSend))
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.6f, 0.1f, 0.1f, 1f)))
            {
                if (ImGui.Button("REMOVE THIS FRIEND FOR REAL…###singleReal") && entry != null && snap != null)
                {
                    singleConfirmEntry = entry;
                    singleConfirmGeneration = snap.Generation;
                    singleConfirmLocalId = snap.LocalContentId;
                    singleConfirmAck = false;
                    singleTestError = null;
                    openSingleConfirmRequested = true;
                }
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(inFlight || test.Phase == SingleTestPhase.Idle))
        {
            if (ImGui.Button("Clear result###singleClear"))
                test.Clear();
        }

        if (singleTestError != null)
            ImGui.TextColored(ColorError, singleTestError);

        // ---- live state
        if (test.Phase != SingleTestPhase.Idle)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"Test: {test.Target}{(test.IsDryRun ? "  [DRY RUN]" : "  [REAL]")}  phase {test.Phase}");
            if (test.SentAtUtc is { } sent)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"sent {(DateTime.UtcNow - sent).TotalSeconds:0.0}s ago");
            }

            ImGui.TextUnformatted("Verdict:");
            ImGui.SameLine();
            ImGui.TextColored(VerdictColor(test.Verdict), test.Verdict.ToString());
            if (test.VerdictDetail is { } vd)
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextUnformatted(vd);
                ImGui.PopTextWrapPos();
            }

            if (test.VerdictAuditError is { } vae)
                ImGui.TextColored(ColorError, vae);

            if (!test.IsDryRun)
            {
                ImGui.TextDisabled($"Chat (best effort): LogMessage 12 seen={test.LogMessageObserved}  text match={test.ChatTextObserved}");
                if (test.PreSendSnapshotsSeen > 0)
                    ImGui.TextColored(ColorWarn, $"{test.PreSendSnapshotsSeen} newer snapshot(s) ignored as pre-send data (download requested before the send).");
            }

            var lines = test.Lines;
            using var child = ImRaii.Child("##singleTestLines", new Vector2(0, 140f), true, ImGuiWindowFlags.HorizontalScrollbar);
            if (child.Success)
            {
                foreach (var l in lines)
                    ImGui.TextUnformatted(l);
            }
        }
    }

    private void DrawSingleTestConfirmModal(bool windowOpen)
    {
        if (openSingleConfirmRequested)
        {
            openSingleConfirmRequested = false;
            ImGui.OpenPopup(SingleTestPopupId);
        }

        if (singleConfirmEntry is not { } e || singleTest == null)
            return;

        ImGui.SetNextWindowSize(new Vector2(520, 0), ImGuiCond.Appearing);
        var open = true;
        using var popup = ImRaii.PopupModal(SingleTestPopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize);
        if (!open)
        {
            singleConfirmEntry = null;
            return;
        }

        if (!popup.Success)
            return;

        ImGui.TextColored(ColorError, $"Remove {e.DisplayName} @ {e.HomeWorldName} from your friend list?");
        ImGui.TextUnformatted($"ContentId {e.ContentId:X16}");
        if (e.IsUnableToRetrieve)
            ImGui.TextColored(ColorWarn, "The game could not retrieve this character's name; identify it by the ContentId above.");
        ImGui.Spacing();
        ImGui.PushTextWrapPos(500);
        ImGui.TextUnformatted(
            "This is PERMANENT. The removal is sent directly via the game's internal function and BYPASSES the game's own " +
            "confirmation dialog. To be friends again you would have to send a new request and they would have to accept it.");
        ImGui.TextUnformatted("This is an untested native call. Only one removal will be sent; nothing is retried.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        var snap = reader.Snapshot;
        var stale = snap == null || snap.Generation != singleConfirmGeneration || snap.LocalContentId != singleConfirmLocalId;
        if (stale)
            ImGui.TextColored(ColorWarn, "The friend list changed since this dialog opened. Cancel and try again.");

        ImGui.Checkbox($"I understand {e.DisplayName} @ {e.HomeWorldName} ({e.ContentId:X16}) will be removed permanently.", ref singleConfirmAck);

        var batchActive = queue != null && RemovalGate.IsBatchActive(queue.State);
        if (batchActive)
            ImGui.TextColored(ColorWarn, "A removal batch is active; cancel it first.");
        if (windowOpen)
            ImGui.TextColored(ColorError, "Close the in-game Friend List window first.");
        var canGo = singleConfirmAck && !stale && !batchActive && !windowOpen && !singleTest.IsInFlight && IneligibleReason(e) == null
                    && directRemover is { HasUnresolvedSend: false };
        using (ImRaii.Disabled(!canGo))
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.6f, 0.1f, 0.1f, 1f)))
        {
            if (ImGui.Button($"Permanently remove {e.DisplayName} ({e.ContentId:X16})###singleConfirmGo"))
            {
                singleTestError = singleTest.Request(RemovalTarget.From(e), false, singleConfirmGeneration, singleConfirmLocalId, out var err)
                    ? null
                    : err;
                singleConfirmEntry = null;
                ImGui.CloseCurrentPopup();
                return;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel###singleConfirmCancel"))
        {
            singleConfirmEntry = null;
            ImGui.CloseCurrentPopup();
        }
    }

    private FriendEntry? GetSingleSelection(FriendListSnapshot? snap)
    {
        if (singleSelCacheVersion == model.SelectionVersion && ReferenceEquals(singleSelCacheSnap, snap))
            return singleSelEntry;

        singleSelCacheVersion = model.SelectionVersion;
        singleSelCacheSnap = snap;
        singleSelEntry = null;
        if (snap != null && model.SelectedCount == 1)
        {
            List<FriendEntry> sel = model.GetSelectedEntries();
            if (sel.Count == 1)
                singleSelEntry = sel[0];
        }

        return singleSelEntry;
    }

    /// <summary>Mirrors the remover's request-state refusal so the UI can explain it up front.</summary>
    private static string? IneligibleReason(FriendEntry e) => e.RequestState switch
    {
        1 => null,
        2 => $"this is a friend request you SENT (request state 2, ExtraFlags 0x{e.ExtraFlags:X8}); the game cancels those with a different packet.",
        3 => $"this is a friend request you RECEIVED (request state 3, ExtraFlags 0x{e.ExtraFlags:X8}); the game declines those with a different packet.",
        _ => $"unrecognised request state {e.RequestState} (ExtraFlags 0x{e.ExtraFlags:X8}); only state 1 (accepted friend) is allowed.",
    };

    private static Vector4 VerdictColor(SingleTestVerdict v) => v switch
    {
        SingleTestVerdict.Removed or SingleTestVerdict.DryRunResolved => ColorOk,
        SingleTestVerdict.None => ColorMuted,
        SingleTestVerdict.Refused => ColorWarn,
        _ => ColorError,
    };
}
