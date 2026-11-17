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
/// <para>
/// Layout, top to bottom: problem banners (in-game Friend List window open, startup block, hook / remover problems),
/// one status line with Refresh / Settings, filters, selection toolbar with the one primary action, developer tools
/// (only when enabled), the friend table, and the removal queue panel while a batch exists.
/// </para>
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private const string ConfirmPopupId = "Remove friends###SocialCleanupConfirm";

    /// <summary>Snapshots older than this show their age on the status line.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private static readonly Vector4 ColorWarn = new(1f, 0.6f, 0.2f, 1f);
    private static readonly Vector4 ColorError = new(1f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 ColorOk = new(0.45f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 ColorPending = new(1f, 0.85f, 0.3f, 1f);
    private static readonly Vector4 ColorMuted = new(0.6f, 0.6f, 0.6f, 1f);

    private static readonly Vector4 BannerErrorBg = new(0.45f, 0.08f, 0.08f, 0.85f);
    private static readonly Vector4 BannerWarnBg = new(0.42f, 0.27f, 0.05f, 0.85f);
    private static readonly Vector4 BannerText = new(1f, 0.95f, 0.9f, 1f);

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

    /// <summary>Set by the plugin: the in-game Friend List window is open (unknown counts as open). Nothing may act then.</summary>
    internal Func<bool>? FriendListWindowOpen { get; set; }

    /// <summary>Set by the plugin: raw detection result for the developer section.</summary>
    internal Func<string>? FriendListWindowDetail { get; set; }

    /// <summary>Set by the plugin (always a passed gate here: the main window only exists when the gate passed).</summary>
    internal VersionGate? GameVersionGate { get; set; }

    /// <summary>Set by the plugin: opens / toggles the settings window.</summary>
    internal Action? OpenSettings { get; set; }

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
        var windowOpen = FriendListWindowOpen?.Invoke() == true;

        model.SetSnapshot(snap);
        model.SelectionLocked = batchActive;

        DrawBanners(state, batchActive, windowOpen);
        DrawHeader(state, snap, batchActive, windowOpen);
        ImGui.Separator();
        DrawFilters();
        DrawSelectionToolbar(state, snap, queueState, batchActive, windowOpen);
        DrawDeveloperTools(state, snap, queueState, batchActive, windowOpen);

        var hasBatch = queue != null && (queue.HasBatch || queueState != RemovalQueueState.Idle);
        var avail = ImGui.GetContentRegionAvail();
        var queueHeight = hasBatch ? MathF.Max(180f, MathF.Min(320f, avail.Y * 0.45f)) : 0f;

        DrawTable(new Vector2(0, MathF.Max(80f, avail.Y - queueHeight)), snap, batchActive);

        if (hasBatch)
            DrawQueuePanel(queueState, windowOpen);

        // Popups must be opened/drawn at the window's top-level ID scope.
        if (openConfirmRequested)
        {
            openConfirmRequested = false;
            ImGui.OpenPopup(ConfirmPopupId);
        }

        DrawConfirmModal(windowOpen);
        DrawSingleTestConfirmModal(windowOpen);
    }

    // ------------------------------------------------------------------ banners

    /// <summary>Problems that block or limit the plugin, each with its reason and, where one exists, the action.</summary>
    private void DrawBanners(FriendListState state, bool batchActive, bool windowOpen)
    {
        if (windowOpen)
        {
            var paused = queue?.IsPausedForFriendListWindow == true;
            DrawBanner("##bannerFriendList", BannerErrorBg,
                "Close the in-game Friend List window to use SocialCleanup." +
                (paused ? "\nThe removal batch is paused and continues by itself once the window is closed." : string.Empty));
        }

        if (directRemover?.StartupBlock is { } sb)
        {
            var who = sb.Target is { } bt ? $"{bt.DisplayName} @ {bt.HomeWorldName}" : "a friend";
            var clicked = DrawBanner("##bannerStartup", BannerErrorBg,
                $"Removals are blocked: the last session may have sent a removal for {who} without seeing its result.\n" +
                "Check in the in-game friend list whether that friend is still there (then close it again), and confirm below.",
                "I have checked my friend list — allow removals###clearStartupBlock",
                buttonEnabled: !batchActive && singleTest?.IsInFlight != true,
                tooltip: sb.Reason);
            if (clicked && directRemover != null)
            {
                startupBlockClearError = directRemover.ClearStartupBlock(out var clearErr)
                    ? null
                    : $"Block cleared, but the audit row could not be written ({clearErr}); it will block again after a reload.";
            }
        }

        if (startupBlockClearError != null)
            ImGui.TextColored(ColorWarn, startupBlockClearError);

        if (!reader.IsCompletionHookActive)
            DrawBanner("##bannerHook", BannerErrorBg, "The friend list cannot be read (the completion hook is not active). Removal is disabled.");
        else if (state == FriendListState.Invalid)
            DrawBanner("##bannerInvalid", BannerWarnBg, $"The friend list could not be loaded{(reader.StateReason is { } r ? $": {r}" : ".")} Press Refresh to try again.");

        var removerAvail = queue?.Availability ?? remover?.Availability;
        if (removerAvail is { IsAvailable: false } ra)
            DrawBanner("##bannerRemover", BannerErrorBg, $"Removal is unavailable: {ra.Reason}");

        // A send awaiting its confirmation while nothing else is running (normally it settles within seconds).
        if (remover?.Unresolved is { } u && !batchActive && queue?.InFlight == null && singleTest?.IsInFlight != true)
        {
            DrawBanner("##bannerLatch", BannerWarnBg,
                $"A previous removal ({u.Target.DisplayName} @ {u.Target.HomeWorldName}) is still waiting for confirmation. New removals stay blocked " +
                "until the game reloads the friend list: open the in-game Friend List once, then close it again.");
        }

        if (configuration.FastModeDefault && queue?.FastModeAvailability is { IsAvailable: false } fa)
            DrawBanner("##bannerFast", BannerWarnBg, $"Fast mode is unavailable ({fa.Reason}). Removals run in standard mode.");
    }

    /// <summary>A full-width coloured box with wrapped text and an optional button. Returns true when the button was clicked.</summary>
    private static bool DrawBanner(string id, Vector4 background, string text, string? buttonLabel = null, bool buttonEnabled = true, string? tooltip = null)
    {
        var style = ImGui.GetStyle();
        var innerWidth = MathF.Max(60f, ImGui.GetContentRegionAvail().X - (style.WindowPadding.X * 2));
        var height = ImGui.CalcTextSize(text, false, innerWidth).Y + (style.WindowPadding.Y * 2) + 2f;
        if (buttonLabel != null)
            height += ImGui.GetFrameHeightWithSpacing();

        var clicked = false;
        using (ImRaii.PushColor(ImGuiCol.ChildBg, background))
        using (var child = ImRaii.Child(id, new Vector2(0, height), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (child.Success)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, BannerText))
                    ImGui.TextWrapped(text);
                if (tooltip != null && ImGui.IsItemHovered())
                    ImGui.SetTooltip(tooltip);
                if (buttonLabel != null)
                {
                    using (ImRaii.Disabled(!buttonEnabled))
                        clicked = ImGui.Button(buttonLabel);
                }
            }
        }

        return clicked;
    }

    // ------------------------------------------------------------------ header

    private void DrawHeader(FriendListState state, FriendListSnapshot? snap, bool batchActive, bool windowOpen)
    {
        // ---- status line: "N friends · state · updated X ago" (age only when stale); raw diagnostics in the tooltip
        ImGui.AlignTextToFramePadding();
        ImGui.BeginGroup();
        if (snap == null)
        {
            ImGui.TextColored(StateColor(state), state == FriendListState.Loading ? "Loading the friend list…" : "Friend list not loaded");
        }
        else
        {
            var age = DateTime.UtcNow - snap.CompletedAtUtc;
            ImGui.TextUnformatted($"{snap.Entries.Count} friend{Plural(snap.Entries.Count)}  ·  ");
            ImGui.SameLine(0, 0);
            ImGui.TextColored(StateColor(state), StateLabel(state));
            if (state != FriendListState.Ready || age >= StaleAfter)
            {
                ImGui.SameLine(0, 0);
                ImGui.TextUnformatted($"  ·  updated {FormatAge(age)} ago");
            }
        }

        ImGui.EndGroup();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(DiagnosticsTooltip(state, snap));

        // ---- right-aligned: Refresh, Settings
        string? refreshBlocked = null;
        if (windowOpen)
            refreshBlocked = "Close the in-game Friend List window first.";
        else if (batchActive)
            refreshBlocked = "Not available while a removal batch is running or halted (Resume gets its own fresh list).";
        else if (remover?.Unresolved != null)
            refreshBlocked = "Not available while a removal awaits its result. Open the in-game Friend List once, then close it again.";
        else if (queue?.FinalVerification is { IsInProgress: true })
            refreshBlocked = "Not available while the final check of the last batch runs.";
        else if (!reader.IsCompletionHookActive)
            refreshBlocked = "The friend list cannot be read (completion hook not active).";

        var style = ImGui.GetStyle();
        var refreshW = ImGui.CalcTextSize("Loading…").X + (style.FramePadding.X * 2);
        var settingsW = ImGui.CalcTextSize("Settings").X + (style.FramePadding.X * 2);
        ImGui.SameLine();
        var free = ImGui.GetContentRegionAvail().X - (refreshW + settingsW + style.ItemSpacing.X);
        if (free < 0)
            ImGui.NewLine();
        else
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + free);

        using (ImRaii.Disabled(state == FriendListState.Loading || refreshBlocked != null))
        {
            if (ImGui.Button(state == FriendListState.Loading ? "Loading…###refresh" : "Refresh###refresh", new Vector2(refreshW, 0)))
                refreshMessage = reader.RequestRefresh() ? null : "Refresh refused (not logged in, or the friend list cannot be read).";
        }

        if (refreshBlocked != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(refreshBlocked);

        ImGui.SameLine();
        if (ImGui.Button("Settings###openSettings", new Vector2(settingsW, 0)))
            OpenSettings?.Invoke();

        if (refreshMessage != null)
            ImGui.TextColored(ColorWarn, refreshMessage);

        if (!windowOpen)
            ImGui.TextDisabled("Keep the in-game Friend List window closed while SocialCleanup is working.");
    }

    /// <summary>Raw diagnostics behind the status line (generation, source, skipped rows, game version).</summary>
    private string DiagnosticsTooltip(FriendListState state, FriendListSnapshot? snap)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Reader: ").Append(state);
        if (reader.StateReason is { } reason)
            sb.Append(" (").Append(reason).Append(')');
        sb.Append('\n');
        if (snap != null)
        {
            sb.Append($"Snapshot generation {snap.Generation}, taken {snap.CompletedAtUtc.ToLocalTime():HH:mm:ss} ({FormatAge(DateTime.UtcNow - snap.CompletedAtUtc)} ago)\n");
            sb.Append(snap.Source == SnapshotSource.LocalAfterRemoval
                ? "Source: local update after a removal (reload suppressed)\n"
                : snap.WasRequestedByPlugin ? "Source: download requested by SocialCleanup\n" : "Source: download started by the game\n");
            sb.Append($"Native entry count {snap.NativeEntryCount}, request id {snap.CurrentRequestId}");
            if (snap.SkippedZeroIds > 0 || snap.SkippedDuplicates > 0)
                sb.Append($"; skipped {snap.SkippedZeroIds} zero-id and {snap.SkippedDuplicates} duplicate rows");
            sb.Append('\n');
        }

        if (GameVersionGate is { } g)
            sb.Append("Game version ").Append(g.DetectedVersion ?? "?").Append(": ").Append(g.Summary);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ filters

    private void DrawFilters()
    {
        ImGui.SetNextItemWidth(200f);
        if (ImGui.InputTextWithHint("##search", "Search name or world", ref searchBuffer, 64))
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
        ImGui.SetNextItemWidth(140f);
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

    private void DrawSelectionToolbar(FriendListState state, FriendListSnapshot? snap, RemovalQueueState queueState, bool batchActive, bool windowOpen)
    {
        var visible = model.View.Count;
        using (ImRaii.Disabled(batchActive || snap == null))
        {
            using (ImRaii.Disabled(visible == 0))
            {
                if (ImGui.Button("Select all filtered"))
                    model.SelectAllFiltered();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Selects every friend the current filter shows (pending friend requests are skipped).");
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
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"{model.SelectedCount} selected ({model.VisibleSelectedCount} visible)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{visible} of {snap?.Entries.Count ?? 0} friends shown by the current filter. Shift-click a checkbox to select a range.");

        var canRemove = CanStartRemoval(state, snap, queueState, windowOpen, out var disabledReason);

        // The one primary action, right-aligned on the same row (on its own line when the window is too narrow).
        var label = $"Remove {model.SelectedCount} selected…";
        var removeW = ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2);
        ImGui.SameLine();
        var free = ImGui.GetContentRegionAvail().X - removeW;
        if (free < 0)
            ImGui.NewLine();
        else
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + free);

        using (ImRaii.Disabled(!canRemove))
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.15f, 0.15f, 1f), canRemove))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.68f, 0.2f, 0.2f, 1f), canRemove))
        {
            if (ImGui.Button($"{label}###removeSelected"))
                OpenConfirmation();
        }

        if (disabledReason != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(disabledReason);

        // Why it is disabled, when that is not simply "nothing selected" or already shown as a banner.
        if (disabledReason != null && model.SelectedCount > 0 && !windowOpen)
            ImGui.TextColored(ColorMuted, disabledReason);

        if (toolbarError != null)
            ImGui.TextColored(ColorError, toolbarError);

        if (batchActive)
            ImGui.TextDisabled("The selection is locked while a removal batch is running or halted.");
    }

    private bool CanStartRemoval(FriendListState state, FriendListSnapshot? snap, RemovalQueueState queueState, bool windowOpen, out string? reason) =>
        RemovalGate.CanStartRemoval(
            queue != null,
            queueState,
            queue?.Availability ?? remover?.Availability,
            state,
            reader.IsCompletionHookActive,
            snap != null,
            model.SelectedCount,
            out reason,
            singleTestInFlight: singleTest?.IsInFlight == true,
            friendListWindowOpen: windowOpen);

    private void OpenConfirmation()
    {
        // Dry run only through the developer tools; with them hidden the dialog never dry-runs.
        var dryRun = configuration.ShowDeveloperTools && configuration.DefaultDryRun;
        var fast = configuration.FastModeDefault && queue?.FastModeAvailability.IsAvailable == true;
        confirmation = RemovalConfirmation.Create(model, dryRun, fast);
        confirmError = null;
        toolbarError = null;
        if (confirmation != null)
            openConfirmRequested = true;
    }

    // ------------------------------------------------------------------ table

    private void DrawTable(Vector2 size, FriendListSnapshot? snap, bool locked)
    {
        if (snap == null)
        {
            ImGui.TextDisabled(reader.State == FriendListState.Loading ? "Loading the friend list…" : "No friend list loaded yet. Press Refresh.");
            return;
        }

        if (snap.Entries.Count == 0)
        {
            ImGui.TextUnformatted("Your friend list is empty.");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                                      | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp
                                      | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortMulti | ImGuiTableFlags.SortTristate;
        using var table = ImRaii.Table("##friends", 7, flags, size);
        if (!table.Success)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 24f, (uint)FriendColumn.Selected);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 3f, (uint)FriendColumn.Name);
        ImGui.TableSetupColumn("Home world", ImGuiTableColumnFlags.WidthStretch, 1.5f, (uint)FriendColumn.HomeWorld);
        ImGui.TableSetupColumn("Data center", ImGuiTableColumnFlags.WidthStretch, 1.2f, (uint)FriendColumn.DataCenter);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 2f, (uint)FriendColumn.Status);
        ImGui.TableSetupColumn("Group", ImGuiTableColumnFlags.WidthStretch, 1f, (uint)FriendColumn.Group);
        ImGui.TableSetupColumn("Request", ImGuiTableColumnFlags.WidthStretch, 1.2f, (uint)FriendColumn.Pending);
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
        var selectable = FriendTableModel.IsSelectable(e);
        ImGui.TableNextRow();
        if (isSelected)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(new Vector4(0.25f, 0.45f, 0.75f, 0.35f)));
        else if (!selectable)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(new Vector4(0.55f, 0.45f, 0.1f, 0.18f)));

        // ---- checkbox (pending requests: disabled, unless one is somehow selected, so it can be unticked)
        ImGui.TableNextColumn();
        using (ImRaii.Disabled(locked || (!selectable && !isSelected)))
        {
            var check = isSelected;
            if (ImGui.Checkbox($"##sel{e.ContentId:X16}", ref check))
                model.Click(e.ContentId, shift);
        }

        if (!selectable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(e.PendingLabel is { } pl
                ? $"{pl}: pending friend requests cannot be removed here. Accept, decline or cancel it in game."
                : "Only accepted friends can be removed here.");
        }

        // ---- name
        ImGui.TableNextColumn();
        if (e.IsUnableToRetrieve)
            ImGui.TextColored(ColorMuted, e.DisplayName);
        else if (!selectable)
            ImGui.TextColored(ColorPending, e.DisplayName);
        else
            ImGui.TextUnformatted(e.DisplayName);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(RowTooltip(e));

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

    private string RowTooltip(FriendEntry e)
    {
        var sb = new System.Text.StringBuilder();
        if (e.IsUnableToRetrieve)
            sb.Append("The game could not retrieve this character's name. It can still be removed; it is identified by its character id.\n");
        if (e.PendingLabel is { } pl)
            sb.Append(pl).Append(": pending friend requests cannot be removed here.\n");
        if (e.IsOnline && e.CurrentWorldName.Length > 0)
            sb.Append("Current world: ").Append(e.CurrentWorldName).Append('\n');
        if (configuration.ShowDeveloperTools)
        {
            sb.Append($"ContentId {e.ContentId:X16}  native index {e.ListIndex}\n");
            sb.Append($"RequestState {e.RequestState}  ExtraFlags 0x{e.ExtraFlags:X8}  OnlineStatus 0x{e.OnlineStatusFlags:X}");
            if (e.IsOtherServer)
                sb.Append("  other server");
        }

        if (sb.Length == 0)
            sb.Append(e.DisplayName).Append(" @ ").Append(e.HomeWorldName);
        return sb.ToString().TrimEnd('\n');
    }

    // ------------------------------------------------------------------ confirmation modal

    private void DrawConfirmModal(bool windowOpen)
    {
        if (confirmation == null)
            return;

        ImGui.SetNextWindowSize(new Vector2(520, 460), ImGuiCond.Appearing);
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
        var devTools = configuration.ShowDeveloperTools;
        if (!devTools)
            c.DryRun = false;

        ImGui.TextUnformatted(c.DryRun
            ? $"Dry run for {n} friend{Plural(n)}? Nothing will be removed."
            : $"Remove {n} friend{Plural(n)}?");
        ImGui.PushTextWrapPos(0);
        if (!c.DryRun)
            ImGui.TextDisabled("They will be removed from your friend list. To be friends again, a new friend request would have to be sent and accepted.");
        if (c.HiddenByFilterCount > 0)
            ImGui.TextColored(ColorWarn, $"{c.HiddenByFilterCount} of them {(c.HiddenByFilterCount == 1 ? "is" : "are")} hidden by the current filter.");
        if (c.Excluded.Count > 0)
        {
            ImGui.TextColored(ColorWarn, $"{c.Excluded.Count} pending friend request{Plural(c.Excluded.Count)} {(c.Excluded.Count == 1 ? "is" : "are")} not included.");
            if (ImGui.IsItemHovered())
            {
                var sb = new System.Text.StringBuilder("Pending requests cannot be removed here:\n");
                foreach (var x in c.Excluded)
                    sb.Append(x.DisplayName).Append(" @ ").Append(x.HomeWorldName).Append(" — ").Append(x.PendingLabel ?? $"request state {x.RequestState}").Append('\n');
                ImGui.SetTooltip(sb.ToString().TrimEnd('\n'));
            }
        }

        ImGui.PopTextWrapPos();

        // ---- the names (scrollable)
        ImGui.Spacing();
        var footerLines = devTools ? 6 : 5;
        var footer = (ImGui.GetFrameHeightWithSpacing() * footerLines) + ImGui.GetStyle().ItemSpacing.Y;
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
                        if (e.IsUnableToRetrieve)
                            ImGui.TextColored(ColorMuted, $"{e.DisplayName} @ {e.HomeWorldName}");
                        else
                            ImGui.TextUnformatted($"{e.DisplayName} @ {e.HomeWorldName}");
                        if (devTools || e.IsUnableToRetrieve)
                        {
                            ImGui.SameLine();
                            ImGui.TextDisabled($"id {e.ContentId:X16}");
                        }
                    }
                }

                clipper.End();
                clipper.Destroy();
            }
        }

        // ---- fast mode: remembered; disabled (with the reason) when the hooks / self-check are not OK
        var fastAvail = queue?.FastModeAvailability ?? FastModeAvailability.Unavailable("No removal engine.");
        if (!fastAvail.IsAvailable)
            c.FastMode = false; // never start a fast batch the engine cannot run (the queue would refuse it anyway)
        using (ImRaii.Disabled(!fastAvail.IsAvailable || c.DryRun))
        {
            var fast = c.FastMode && fastAvail.IsAvailable;
            if (ImGui.Checkbox("Fast mode###confirmFast", ref fast))
            {
                c.FastMode = fast;
                configuration.FastModeDefault = fast;
                saveConfig?.Invoke();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                "Skips the ~7 s friend-list reload the game does after each removal; each result comes from the server's reply instead.\n" +
                "The friend list is reloaded and checked once at the end.\n\n" +
                (fastAvail.IsAvailable ? "Available." : $"Unavailable: {fastAvail.Reason}"));
        }

        ImGui.SameLine();
        if (c.DryRun)
            ImGui.TextDisabled("(not used for dry runs)");
        else if (!fastAvail.IsAvailable)
            ImGui.TextColored(ColorWarn, "Unavailable (hover for the reason)");
        else
            ImGui.TextDisabled(c.FastMode ? "skips the reload after each removal" : "waits for the game's reload after each removal");

        ImGui.TextDisabled($"Estimated time: {FormatDuration(EstimateBatch(n, c.FastMode && !c.DryRun, c.DryRun))} ({(c.DryRun ? "dry run" : c.FastMode ? "fast mode" : "standard mode")})");

        // ---- developer: dry run option (remembered)
        if (devTools)
        {
            var dryRun = c.DryRun;
            if (ImGui.Checkbox("Dry run (developer: check each friend, send nothing)###confirmDry", ref dryRun))
            {
                c.DryRun = dryRun;
                configuration.DefaultDryRun = dryRun;
                saveConfig?.Invoke();
            }
        }

        // ---- status line: stale list / window open / last error
        var stale = !c.IsCurrent(reader.Snapshot);
        if (stale)
        {
            ImGui.TextColored(ColorWarn, "The friend list changed since this dialog opened.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Rebuild from current selection"))
            {
                confirmation = RemovalConfirmation.Create(model, c.DryRun, c.FastMode);
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
        else if (windowOpen)
        {
            ImGui.TextColored(ColorError, "Close the in-game Friend List window first.");
        }
        else if (confirmError != null)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(ColorError, confirmError);
            ImGui.PopTextWrapPos();
        }
        else
        {
            ImGui.NewLine();
        }

        using (ImRaii.Disabled(stale || windowOpen || queue == null || n == 0))
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.55f, 0.15f, 0.15f, 1f), !c.DryRun && !stale && !windowOpen))
        {
            var label = c.DryRun ? $"Start dry run ({n})###confirmGo" : $"Remove {n} friend{Plural(n)}###confirmGo";
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
            confirmError = $"Not started: {startError ?? queue.HaltReason ?? "no reason given"}";
            return false;
        }

        confirmError = null;
        return true;
    }

    /// <summary>
    /// Rough duration of a batch of <paramref name="n"/> from the current settings: per item the measured send→verdict
    /// time (fallbacks: ~7.5 s standard, ~0.5 s fast) plus the delay and half the jitter; fast mode adds one final reload.
    /// </summary>
    private TimeSpan EstimateBatch(int n, bool fast, bool dryRun)
    {
        if (n <= 0)
            return TimeSpan.Zero;
        var c = configuration;
        if (dryRun)
            return TimeSpan.FromMilliseconds(Math.Clamp(c.RemovalDryRunDelayMs, 0, 10000)) * n;
        TimeSpan per;
        if (fast)
        {
            per = RemovalQueueSettings.FallbackFastItemDuration
                  + TimeSpan.FromMilliseconds(Math.Clamp(c.FastCooldownMs, Configuration.FastCooldownMinMs, Configuration.CooldownMaxMs))
                  + TimeSpan.FromMilliseconds(Math.Clamp(c.FastCooldownJitterMs, 0, 10000) / 2.0);
            return (per * n) + RemovalQueueSettings.FallbackItemDuration;
        }

        per = RemovalQueueSettings.FallbackItemDuration
              + TimeSpan.FromMilliseconds(Math.Clamp(c.RemovalCooldownMs, Configuration.CooldownMinMs, Configuration.CooldownMaxMs))
              + TimeSpan.FromMilliseconds(Math.Clamp(c.RemovalCooldownJitterMs, 0, 10000) / 2.0);
        return per * n;
    }

    // ------------------------------------------------------------------ helpers

    private static string Plural(int n) => n == 1 ? string.Empty : "s";

    private static string StateLabel(FriendListState state) => state switch
    {
        FriendListState.Ready => "up to date",
        FriendListState.Loading => "loading…",
        FriendListState.Invalid => "could not load",
        _ => "not loaded",
    };

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
        FriendStatusFilter.PendingApproval => "Pending requests",
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

    /// <summary>"about 45 s" / "about 3 min 20 s" / "about 1 h 5 min".</summary>
    private static string FormatDuration(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
            t = TimeSpan.Zero;
        if (t.TotalSeconds < 60)
            return $"about {Math.Max(1, (int)Math.Round(t.TotalSeconds))} s";
        if (t.TotalMinutes < 60)
            return t.Seconds == 0 ? $"about {(int)t.TotalMinutes} min" : $"about {(int)t.TotalMinutes} min {t.Seconds} s";
        return $"about {(int)t.TotalHours} h {t.Minutes} min";
    }
}
