using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SocialCleanup.Friends;
using SocialCleanup.Removal;
using SocialCleanup.Safety;

namespace SocialCleanup.Windows;

/// <summary>
/// Friend list browser + bulk-selection UI. Touches only managed snapshots and the reader/queue/remover interfaces;
/// never calls native game code. Works without a removal engine (queue/remover null): the remove action is then
/// disabled with a visible reason.
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private const string ConfirmPopupId = "Confirm friend removal###SocialCleanupConfirm";

    private static readonly Vector4 ColorWarn = new(1f, 0.6f, 0.2f, 1f);
    private static readonly Vector4 ColorError = new(1f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 ColorOk = new(0.45f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 ColorPending = new(1f, 0.85f, 0.3f, 1f);
    private static readonly Vector4 ColorMuted = new(0.6f, 0.6f, 0.6f, 1f);

    private static readonly FriendStatusFilter[] StatusFilters =
        [FriendStatusFilter.All, FriendStatusFilter.Online, FriendStatusFilter.Offline, FriendStatusFilter.PendingApproval, FriendStatusFilter.UnableToRetrieve];

    private readonly IFriendListReader reader;
    private readonly Configuration configuration;
    private readonly IRemovalQueue? queue;
    private readonly IFriendRemover? remover;
    private readonly Action? saveConfig;
    private readonly FriendTableModel model = new();

    private string searchBuffer = string.Empty;
    private string? refreshMessage;
    private string? toolbarError;

    private RemovalConfirmation? confirmation;
    private bool openConfirmRequested;
    private string? confirmError;

    public MainWindow(IFriendListReader reader, Configuration configuration, IRemovalQueue? queue, IFriendRemover? remover, Action? saveConfig = null)
        : base("Social Cleanup###SocialCleanupMain")
    {
        this.reader = reader;
        this.configuration = configuration;
        this.queue = queue;
        this.remover = remover;
        this.saveConfig = saveConfig;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    /// <summary>Exposed for tests / future wiring; the window owns it.</summary>
    internal FriendTableModel Model => model;

    /// <summary>Set by the plugin: the in-game Friend List window is open (a fast batch refuses to start then).</summary>
    internal Func<bool>? FriendListWindowOpen { get; set; }

    /// <summary>Set by the plugin (always a passed gate here: the main window only exists when the gate passed).</summary>
    internal VersionGate? GameVersionGate { get; set; }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        // One read of each volatile input per frame.
        var state = reader.State;
        var snap = reader.Snapshot;
        var queueState = queue?.State ?? RemovalQueueState.Idle;
        var batchActive = queue != null && RemovalGate.IsBatchActive(queueState);

        model.SetSnapshot(snap);
        model.SelectionLocked = batchActive;

        DrawHeader(state, snap, batchActive);
        ImGui.Separator();
        DrawFilters();
        DrawSelectionToolbar(state, snap, queueState, batchActive);
        DrawSingleTestSection(state, snap, batchActive);

        var hasBatch = queue != null && (queue.HasBatch || queueState != RemovalQueueState.Idle);
        var avail = ImGui.GetContentRegionAvail();
        var queueHeight = hasBatch ? MathF.Max(160f, MathF.Min(300f, avail.Y * 0.45f)) : 0f;

        DrawTable(new Vector2(0, MathF.Max(80f, avail.Y - queueHeight)), snap, batchActive);

        if (hasBatch)
            DrawQueuePanel(queueState);

        // Popups must be opened/drawn at the window's top-level ID scope.
        if (openConfirmRequested)
        {
            openConfirmRequested = false;
            ImGui.OpenPopup(ConfirmPopupId);
        }

        DrawConfirmModal();
        DrawSingleTestConfirmModal();
    }

    // ------------------------------------------------------------------ header

    private void DrawHeader(FriendListState state, FriendListSnapshot? snap, bool batchActive)
    {
        ImGui.TextUnformatted("State:");
        ImGui.SameLine();
        ImGui.TextColored(StateColor(state), $"{state}");
        if (reader.StateReason is { } reason)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({reason})");
        }

        ImGui.SameLine();
        string? refreshBlocked = null;
        if (batchActive)
            refreshBlocked = "Disabled while a removal batch is running or halted (Resume requests its own refresh).";
        else if (remover?.Unresolved != null)
            refreshBlocked = "Disabled while the remover's send latch is set (a removal awaits its result). Open the in-game friend list to observe it.";
        else if (queue?.FinalVerification is { IsInProgress: true })
            refreshBlocked = "Disabled while the fast batch's final reload / verification runs.";
        using (ImRaii.Disabled(state == FriendListState.Loading || !reader.IsCompletionHookActive || refreshBlocked != null))
        {
            if (ImGui.Button(state == FriendListState.Loading ? "Loading…###refresh" : "Refresh###refresh"))
                refreshMessage = reader.RequestRefresh() ? null : "Refresh refused (not logged in, or the completion hook is inactive).";
        }

        if (refreshBlocked != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(refreshBlocked);

        if (refreshMessage != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorWarn, refreshMessage);
        }

        DrawGameVersionNote();

        if (!reader.IsCompletionHookActive)
            ImGui.TextColored(ColorWarn, "EndRequest completion hook is not active: the friend list cannot be loaded and removal stays disabled.");
        if (state == FriendListState.Invalid)
            ImGui.TextColored(ColorError, "Friend-list reader is Invalid. Nothing will be removed until a refresh succeeds.");

        if (snap == null)
        {
            ImGui.TextUnformatted("No snapshot loaded.");
            return;
        }

        var origin = snap.Source == SnapshotSource.LocalAfterRemoval
            ? "  (local update after a removal; reload suppressed)"
            : snap.WasRequestedByPlugin ? string.Empty : "  (game-initiated)";
        ImGui.TextUnformatted(
            $"Entries: {snap.Entries.Count}  |  generation {snap.Generation}  |  taken {FormatAge(DateTime.UtcNow - snap.CompletedAtUtc)} ago ({snap.CompletedAtUtc.ToLocalTime():HH:mm:ss}){origin}");
        if (snap.SkippedZeroIds > 0 || snap.SkippedDuplicates > 0)
            ImGui.TextDisabled($"Skipped {snap.SkippedZeroIds} zero-id and {snap.SkippedDuplicates} duplicate rows (native count {snap.NativeEntryCount}).");
        if (state != FriendListState.Ready)
            ImGui.TextDisabled("Showing the last complete snapshot; it may be stale.");
    }

    /// <summary>Unobtrusive: detected game version on the header line, gate details in the tooltip.</summary>
    private void DrawGameVersionNote()
    {
        if (GameVersionGate is not { } g)
            return;
        ImGui.SameLine();
        ImGui.TextDisabled($"| game {g.DetectedVersion ?? "?"}");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Game-version gate {g.Summary}");
    }

    // ------------------------------------------------------------------ filters

    private void DrawFilters()
    {
        ImGui.SetNextItemWidth(200f);
        if (ImGui.InputTextWithHint("##search", "Search name / world", ref searchBuffer, 64))
            model.SearchText = searchBuffer;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f);
        using (var combo = ImRaii.Combo("##world", model.HomeWorldFilter ?? "All worlds"))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable("All worlds", model.HomeWorldFilter == null))
                    model.HomeWorldFilter = null;
                foreach (var w in model.WorldOptions)
                {
                    if (ImGui.Selectable(w, w == model.HomeWorldFilter))
                        model.HomeWorldFilter = w;
                }
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        using (var combo = ImRaii.Combo("##dc", model.DataCenterFilter ?? "All DCs"))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable("All DCs", model.DataCenterFilter == null))
                    model.DataCenterFilter = null;
                foreach (var dc in model.DataCenterOptions)
                {
                    if (ImGui.Selectable(dc, dc == model.DataCenterFilter))
                        model.DataCenterFilter = dc;
                }
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f);
        using (var combo = ImRaii.Combo("##status", StatusFilterLabel(model.StatusFilter)))
        {
            if (combo.Success)
            {
                foreach (var f in StatusFilters)
                {
                    if (ImGui.Selectable(StatusFilterLabel(f), f == model.StatusFilter))
                        model.StatusFilter = f;
                }
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        using (var combo = ImRaii.Combo("##group", model.GroupFilter is { } g ? GroupLabel(g) : "All groups"))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable("All groups", model.GroupFilter == null))
                    model.GroupFilter = null;
                foreach (var grp in model.GroupOptions)
                {
                    if (ImGui.Selectable(GroupLabel(grp), grp == model.GroupFilter))
                        model.GroupFilter = grp;
                }
            }
        }

        if (model.HasActiveFilter)
        {
            ImGui.SameLine();
            if (ImGui.Button("Reset filters"))
            {
                model.ResetFilters();
                searchBuffer = string.Empty;
            }
        }
    }

    // ------------------------------------------------------------------ selection toolbar

    private void DrawSelectionToolbar(FriendListState state, FriendListSnapshot? snap, RemovalQueueState queueState, bool batchActive)
    {
        var visible = model.View.Count;
        using (ImRaii.Disabled(batchActive || snap == null))
        {
            using (ImRaii.Disabled(visible == 0))
            {
                if (ImGui.Button("Select all filtered"))
                    model.SelectAllFiltered();
                ImGui.SameLine();
                if (ImGui.Button("Deselect filtered"))
                    model.DeselectFiltered();
                ImGui.SameLine();
                if (ImGui.Button("Invert filtered"))
                    model.InvertFiltered();
            }

            ImGui.SameLine();
            using (ImRaii.Disabled(model.SelectedCount == 0))
            {
                if (ImGui.Button("Clear selection"))
                    model.ClearSelection();
            }
        }

        ImGui.SameLine();
        ImGui.TextUnformatted($"{model.SelectedCount} selected ({model.VisibleSelectedCount} visible)  |  {visible} shown");

        var canRemove = RemovalGate.CanStartRemoval(
            queue != null,
            queueState,
            queue?.Availability ?? remover?.Availability,
            state,
            reader.IsCompletionHookActive,
            snap != null,
            model.SelectedCount,
            out var disabledReason,
            singleTestInFlight: singleTest?.IsInFlight == true);

        using (ImRaii.Disabled(!canRemove))
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.15f, 0.15f, 1f), canRemove))
        {
            if (ImGui.Button($"Remove {model.SelectedCount} selected…###removeSelected"))
                OpenConfirmation();
        }

        // Dry run straight away: nothing is sent, so no confirmation dialog.
        ImGui.SameLine();
        using (ImRaii.Disabled(!canRemove))
        {
            if (ImGui.Button($"Dry run {model.SelectedCount} selected###dryRunSelected"))
                StartDryRunDirect();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Re-resolves every selected friend against the live list exactly like a real batch, but sends nothing.");

        if (disabledReason != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(model.SelectedCount == 0 ? ColorMuted : ColorWarn, disabledReason);
        }

        if (toolbarError != null)
            ImGui.TextColored(ColorError, toolbarError);

        if (batchActive)
            ImGui.TextColored(ColorWarn, "Selection is locked while a removal batch is running or halted.");
    }

    private void OpenConfirmation()
    {
        confirmation = RemovalConfirmation.Create(model, configuration.DefaultDryRun, configuration.FastModeDefault);
        confirmError = null;
        toolbarError = null;
        if (confirmation != null)
            openConfirmRequested = true;
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

    // ------------------------------------------------------------------ table

    private void DrawTable(Vector2 size, FriendListSnapshot? snap, bool locked)
    {
        if (snap == null)
            return;
        if (snap.Entries.Count == 0)
        {
            ImGui.TextUnformatted("Friend list is empty.");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                                      | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp
                                      | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortMulti | ImGuiTableFlags.SortTristate;
        using var table = ImRaii.Table("##friends", 7, flags, size);
        if (!table.Success)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 24f, (uint)FriendColumn.Selected);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 3f, (uint)FriendColumn.Name);
        ImGui.TableSetupColumn("Home world", ImGuiTableColumnFlags.WidthStretch, 1.5f, (uint)FriendColumn.HomeWorld);
        ImGui.TableSetupColumn("DC", ImGuiTableColumnFlags.WidthStretch, 1.2f, (uint)FriendColumn.DataCenter);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 2f, (uint)FriendColumn.Status);
        ImGui.TableSetupColumn("Group", ImGuiTableColumnFlags.WidthStretch, 1f, (uint)FriendColumn.Group);
        ImGui.TableSetupColumn("Pending", ImGuiTableColumnFlags.WidthStretch, 1f, (uint)FriendColumn.Pending);
        ImGui.TableHeadersRow();

        var specs = ImGui.TableGetSortSpecs();
        if (!specs.IsNull && specs.SpecsDirty)
        {
            model.BeginSortUpdate();
            for (var i = 0; i < specs.SpecsCount; i++)
            {
                var s = specs.Specs[i];
                model.AddSortKey(new FriendSortKey((FriendColumn)s.ColumnUserID, s.SortDirection != ImGuiSortDirection.Descending));
            }

            specs.SpecsDirty = false;
        }

        var rows = model.View;
        if (rows.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TextDisabled("No friends match the current filter.");
            return;
        }

        var shift = ImGui.GetIO().KeyShift;
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(rows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                DrawRow(rows[i], locked, shift);
        }

        clipper.End();
        clipper.Destroy();
    }

    private void DrawRow(FriendEntry e, bool locked, bool shift)
    {
        var isSelected = model.IsSelected(e.ContentId);
        ImGui.TableNextRow();
        if (isSelected)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(new Vector4(0.25f, 0.45f, 0.75f, 0.35f)));

        ImGui.TableNextColumn();
        using (ImRaii.Disabled(locked))
        {
            var check = isSelected;
            if (ImGui.Checkbox($"##sel{e.ContentId:X16}", ref check))
                model.Click(e.ContentId, shift);
        }

        ImGui.TableNextColumn();
        if (e.IsUnableToRetrieve)
            ImGui.TextDisabled(e.DisplayName);
        else
            ImGui.TextUnformatted(e.DisplayName);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"ContentId {e.ContentId:X16}\nCurrent world: {(e.CurrentWorldName.Length > 0 ? e.CurrentWorldName : "-")}\nNative index {e.ListIndex}" +
                $"\nRequestState {e.RequestState}  ExtraFlags 0x{e.ExtraFlags:X8}  OnlineStatus 0x{e.OnlineStatusFlags:X}" +
                $"{(e.IsOtherServer ? "\nOther server" : string.Empty)}");
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(e.HomeWorldName);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(e.DataCenterName);

        ImGui.TableNextColumn();
        if (e.IsOnline)
            ImGui.TextColored(ColorOk, e.StatusText);
        else
            ImGui.TextDisabled(e.StatusText);

        ImGui.TableNextColumn();
        if (e.Group != FriendGroup.None)
            ImGui.TextUnformatted(GroupLabel(e.Group));

        ImGui.TableNextColumn();
        if (e.PendingLabel is { } pending)
            ImGui.TextColored(ColorPending, pending);
    }

    // ------------------------------------------------------------------ confirmation modal

    private void DrawConfirmModal()
    {
        if (confirmation == null)
            return;

        ImGui.SetNextWindowSize(new Vector2(560, 480), ImGuiCond.Appearing);
        var open = true;
        using var popup = ImRaii.PopupModal(ConfirmPopupId, ref open);
        if (!open)
        {
            // Closed with the title-bar X.
            confirmation = null;
            return;
        }

        if (!popup.Success)
            return;

        var c = confirmation;
        var n = c.Targets.Count;

        ImGui.TextColored(ColorError, $"Remove {n} friend{(n == 1 ? string.Empty : "s")} from your friend list.");
        ImGui.TextWrapped(
            "Removal is PERMANENT. It is sent directly and bypasses the game's own confirmation dialog. "
            + "Removed players must be re-invited (and accept) to become friends again.");
        if (c.HiddenByFilterCount > 0)
            ImGui.TextColored(ColorWarn, $"{c.HiddenByFilterCount} of these are hidden by the current filter but ARE included.");
        if (c.Excluded.Count > 0)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ColorWarn,
                $"{c.Excluded.Count} selected entr{(c.Excluded.Count == 1 ? "y is" : "ies are")} NOT included: pending friend requests " +
                "(sent / received) use a different game packet and cannot be removed here.");
            ImGui.PopTextWrapPos();
            if (ImGui.IsItemHovered())
            {
                var sb = new System.Text.StringBuilder();
                foreach (var x in c.Excluded)
                    sb.Append(x.DisplayName).Append(" @ ").Append(x.HomeWorldName).Append(" — ").Append(x.PendingLabel ?? $"request state {x.RequestState}").Append('\n');
                ImGui.SetTooltip(sb.ToString());
            }
        }

        ImGui.Spacing();
        var footer = (ImGui.GetFrameHeightWithSpacing() * 5) + ImGui.GetStyle().ItemSpacing.Y;
        using (var child = ImRaii.Child("##confirmList", new Vector2(0, MathF.Max(80f, ImGui.GetContentRegionAvail().Y - footer)), true))
        {
            if (child.Success)
            {
                var clipper = ImGui.ImGuiListClipper();
                clipper.Begin(c.Entries.Count);
                while (clipper.Step())
                {
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        var e = c.Entries[i];
                        ImGui.TextUnformatted($"{e.DisplayName} @ {e.HomeWorldName}");
                        ImGui.SameLine();
                        ImGui.TextDisabled($"ContentId {e.ContentId:X16}");
                        if (e.PendingLabel is { } pending)
                        {
                            ImGui.SameLine();
                            ImGui.TextColored(ColorPending, $"({pending})");
                        }
                    }
                }

                clipper.End();
                clipper.Destroy();
            }
        }

        var dryRun = c.DryRun;
        if (ImGui.Checkbox("Dry run (re-resolve each target, send nothing)", ref dryRun))
            c.DryRun = dryRun;
        if (!c.DryRun)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorError, "LIVE — friends will really be removed.");
        }

        // Fast mode: default OFF, remembered, disabled (with the reason) when the hooks / self-check are not OK.
        var fastAvail = queue?.FastModeAvailability ?? FastModeAvailability.Unavailable("No removal engine.");
        var windowOpen = FriendListWindowOpen?.Invoke() == true;
        if (!fastAvail.IsAvailable)
            c.FastMode = false; // never start a fast batch the engine cannot run (the queue would refuse it anyway)
        using (ImRaii.Disabled(!fastAvail.IsAvailable || c.DryRun))
        {
            var fast = c.FastMode && fastAvail.IsAvailable;
            if (ImGui.Checkbox("Fast mode (skip the list reload between removals)", ref fast))
            {
                c.FastMode = fast;
                configuration.FastModeDefault = fast;
                saveConfig?.Invoke();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "Suppresses the ~7 s friend-list re-download the game does after each successful removal; each verdict then comes from the " +
                "server's reply plus the locally updated list. One real reload + verification runs when the batch ends.\n" + fastAvail.Reason);
        }

        if (c.DryRun)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(n/a for dry runs)");
        }
        else if (!fastAvail.IsAvailable)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorWarn, $"Unavailable: {fastAvail.Reason}");
        }
        else if (c.FastMode)
        {
            ImGui.SameLine();
            ImGui.TextColored(windowOpen ? ColorError : ColorWarn,
                windowOpen ? "Close the in-game Friend List window first." : "Keep the in-game Friend List window closed during the batch.");
        }

        var stale = !c.IsCurrent(reader.Snapshot);
        if (stale)
        {
            ImGui.TextColored(ColorWarn, "The friend list changed since this dialog opened.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Rebuild from current selection"))
            {
                var keepDry = c.DryRun;
                confirmation = RemovalConfirmation.Create(model, keepDry, c.FastMode);
                confirmError = null;
                if (confirmation == null)
                {
                    ImGui.CloseCurrentPopup();
                    return;
                }

                c = confirmation;
                n = c.Targets.Count;
                stale = !c.IsCurrent(reader.Snapshot);
            }
        }
        else if (confirmError != null)
        {
            ImGui.TextColored(ColorError, confirmError);
        }
        else
        {
            ImGui.NewLine();
        }

        using (ImRaii.Disabled(stale || queue == null || n == 0))
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f), !c.DryRun && !stale))
        {
            var label = c.DryRun ? $"Start dry run ({n})###confirmGo" : $"Permanently remove {n}{(c.FastMode ? " (fast)" : string.Empty)}###confirmGo";
            if (ImGui.Button(label))
            {
                if (TryStartBatch(c))
                {
                    confirmation = null;
                    ImGui.CloseCurrentPopup();
                    return;
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel###confirmCancel"))
        {
            confirmation = null;
            ImGui.CloseCurrentPopup();
        }
    }

    private bool TryStartBatch(RemovalConfirmation c)
    {
        if (queue == null)
        {
            confirmError = "Removal engine is not available in this build.";
            return false;
        }

        if (!c.TryBuildBatch(reader.Snapshot, DateTime.UtcNow, out var batch, out var error) || batch == null)
        {
            confirmError = error ?? "Could not build the batch.";
            return false;
        }

        if (!queue.TryStart(batch, out var startError))
        {
            confirmError = $"Queue refused to start: {startError ?? queue.HaltReason ?? "no reason given"}";
            return false;
        }

        confirmError = null;
        return true;
    }

    // ------------------------------------------------------------------ helpers

    private static Vector4 StateColor(FriendListState state) => state switch
    {
        FriendListState.Ready => ColorOk,
        FriendListState.Loading => ColorPending,
        FriendListState.Invalid => ColorError,
        _ => ColorMuted,
    };

    private static string StatusFilterLabel(FriendStatusFilter f) => f switch
    {
        FriendStatusFilter.Online => "Online",
        FriendStatusFilter.Offline => "Offline",
        FriendStatusFilter.PendingApproval => "Pending request",
        FriendStatusFilter.UnableToRetrieve => "Unable to retrieve",
        _ => "All statuses",
    };

    // Literal strings: no per-frame enum.ToString() allocations.
    private static string GroupLabel(FriendGroup g) => g switch
    {
        FriendGroup.None => "(no group)",
        FriendGroup.Star => "Star",
        FriendGroup.Circle => "Circle",
        FriendGroup.Triangle => "Triangle",
        FriendGroup.Diamond => "Diamond",
        FriendGroup.Heart => "Heart",
        FriendGroup.Spade => "Spade",
        FriendGroup.Club => "Club",
        _ => "?",
    };

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age.TotalSeconds < 60)
            return $"{(int)age.TotalSeconds}s";
        if (age.TotalMinutes < 60)
            return $"{(int)age.TotalMinutes}m {age.Seconds}s";
        return $"{(int)age.TotalHours}h {age.Minutes}m";
    }
}
