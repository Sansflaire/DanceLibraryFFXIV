/// <summary>
/// Primary ImGui window for the Dance Library plugin.
///
/// Responsibilities:
///   - Displays all emote mod entries in four tabs: "Dance", "Emote", "NSFW", "Other".
///   - All mods start in "Other"; the user assigns them to categories via a dropdown.
///   - Category assignments are persisted in <see cref="Configuration.ModCategories"/>.
///   - Supports user-created collapsible groups within each tab (see <see cref="ModGroup"/>).
///   - Starred mods sort to the top of their section (group or ungrouped) via the ★ button.
///   - Drag-and-drop reordering: "≡" handle drags mods; ArrowButton drags groups.
///   - Move Mode: clicking a mod's row re-categorizes it to a chosen tab instead of performing.
///   - "Perform" button (or clicking mod name): activates the mod via Penumbra temp
///     settings and executes the emote command in-game.
///   - "Settings" button: opens <see cref="ModSettingsWindow"/> for option editing.
///   - "Reset" button: removes temporary Penumbra settings for the mod.
///   - "Refresh" button: re-scans all Penumbra mods for emote overrides.
///   - Runs the scan on a background thread to avoid game frame stutter.
///
/// Threading notes:
///   - The mod scan runs on a background Task thread.
///   - UI lists are updated under a lock to avoid race conditions.
///   - UngroupedOrder is updated on the draw thread (not background) to stay thread-safe.
///   - The emote command execution is scheduled on the game thread via
///     <c>Plugin.Framework.RunOnTick</c>.
///   - All ImGui calls happen on the game thread (called from Plugin.OnDraw).
///
/// Dependencies:
///   - <see cref="PenumbraIpc"/> for mod activation / deactivation.
///   - <see cref="ModScanner"/> for discovering emote mods.
///   - <see cref="ChatSender"/> for executing emote commands.
///   - <see cref="ModSettingsWindow"/> for the settings popup.
///   - <see cref="Plugin.Framework"/> for game-thread scheduling.
/// </summary>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace DanceLibraryFFXIV.Windows;

/// <summary>
/// The main Dance Library window with four tabs and per-tab group organization.
/// </summary>
public sealed class MainWindow : IDisposable
{
    // ── Dependencies ─────────────────────────────────────────────────────────────

    /// <summary>Plugin configuration (window visibility, categories, groups, favorites).</summary>
    private readonly Configuration _config;

    /// <summary>Scans Penumbra mods for emote overrides.</summary>
    private readonly ModScanner _scanner;

    /// <summary>Penumbra IPC bridge for mod activation/deactivation.</summary>
    private readonly PenumbraIpc _penumbra;

    /// <summary>Executes in-game slash commands (must be called on game thread).</summary>
    private readonly ChatSender _chatSender;

    /// <summary>Settings popup window for editing mod option groups.</summary>
    private readonly ModSettingsWindow _settingsWindow;

    /// <summary>Dalamud framework for scheduling actions on the game thread.</summary>
    private readonly IFramework _framework;

    /// <summary>Plugin logger for performance and error reporting.</summary>
    private readonly IPluginLog _log;

    // ── Window State ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the main window is currently visible. Synced from/to <see cref="_config"/>.
    /// When toggled, the configuration is saved immediately.
    /// </summary>
    public bool IsVisible
    {
        get => _config.IsMainWindowVisible;
        set
        {
            _config.IsMainWindowVisible = value;
            _config.Save();
        }
    }

    // ── Scan State ───────────────────────────────────────────────────────────────

    /// <summary>Enum tracking the current scan lifecycle stage.</summary>
    private enum ScanState { NotScanned, Scanning, Done, Error }

    /// <summary>Current state of the mod scan (updated from background thread under <see cref="_lock"/>).</summary>
    private ScanState _scanState = ScanState.NotScanned;

    // ── Backup Popup State ────────────────────────────────────────────────────────

    /// <summary>
    /// Set to true for one frame after the "?" button is clicked to trigger
    /// <c>ImGui.OpenPopup</c> for the How To modal on the next draw.
    /// Reset to false immediately after the popup is opened.
    /// </summary>
    private bool _howToPopupPending;

    /// <summary>
    /// Set to true for one frame after <see cref="DoBackup"/> runs to trigger
    /// <c>ImGui.OpenPopup</c> for the backup result modal on the next draw.
    /// Reset to false immediately after the popup is opened.
    /// </summary>
    private bool _backupPopupPending;

    /// <summary>
    /// Message shown inside the backup result modal.
    /// Set by <see cref="DoBackup"/> to either a success path string or an error description.
    /// </summary>
    private string _backupResultMessage = string.Empty;

    /// <summary>Error message to show if the scan failed.</summary>
    private string _scanError = string.Empty;

    /// <summary>Background Task running the current scan (null when idle).</summary>
    private Task? _scanTask;

    /// <summary>
    /// Lock object protecting <see cref="_allEntries"/> from concurrent reads (Draw thread)
    /// and writes (background scan thread).
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// All emote mod entries found by the last scan. Every entry starts uncategorized
    /// (shown in "Other"). The user assigns categories via the dropdown in each row,
    /// which writes to <see cref="Configuration.ModCategories"/> and persists across sessions.
    /// Updated by the background scan thread (under <see cref="_lock"/>).
    /// </summary>
    private List<EmoteModEntry> _allEntries = new();

    // ── Category Constants ───────────────────────────────────────────────────────

    /// <summary>
    /// The three permanent built-in tab names (not including "Other").
    /// "Other" is always appended last by <see cref="RebuildCategories"/> and cannot be deleted.
    /// These names cannot be renamed or removed by the user.
    /// </summary>
    private static readonly string[] BuiltInCategories = { "Dance", "Emote", "NSFW" };

    /// <summary>
    /// Effective tab list: built-ins + user custom categories + "Other" (always last).
    /// Rebuilt by <see cref="RebuildCategories"/> when custom tabs are added/deleted/renamed.
    /// Custom tabs are persisted in <see cref="Configuration.CustomCategories"/>.
    /// "Other" is the default for any mod not explicitly assigned to another category.
    /// </summary>
    private string[] _categories = { "Dance", "Emote", "NSFW", "Other" };

    // ── Active Mod Tracking ──────────────────────────────────────────────────────

    /// <summary>
    /// Set of mod directory names that currently have temporary Penumbra settings applied.
    /// A mod is added when Perform is clicked; all are cleared when Reset or Perform is clicked.
    /// Perform always resets every active mod first to avoid cross-mod animation/audio conflicts.
    /// </summary>
    private readonly HashSet<string> _activeMods = new();

    // ── Move Mode State ──────────────────────────────────────────────────────────

    /// <summary>
    /// When true, clicking a mod's row or Perform button re-categorizes the mod to
    /// <see cref="_moveModeTargetIdx"/> instead of performing the emote.
    /// Star, Settings, and Reset buttons are not affected by Move Mode.
    /// </summary>
    private bool _moveModeEnabled = false; // dormant — button removed; logic kept for future use

    /// <summary>
    /// Index into <see cref="_categories"/> for the target tab in Move Mode.
    /// Default 0 = "Dance".
    /// </summary>
    private int _moveModeTargetIdx;

    // ── Drag-and-Drop State ──────────────────────────────────────────────────────

    /// <summary>
    /// Mod directory name currently being dragged (DL_MOD payload).
    /// Null when no drag is in progress.
    /// </summary>
    private string? _draggedModDir;

    /// <summary>The category tab the dragged mod came from.</summary>
    private string? _draggedFromCategory;

    /// <summary>The group name the dragged mod came from, or null if ungrouped.</summary>
    private string? _draggedFromGroupName;

    /// <summary>
    /// Payload data for drag-and-drop operations. Must be non-empty — passing
    /// ReadOnlySpan&lt;byte&gt;.Empty causes AcceptDragDropPayload to return null Data.
    /// Used for both DL_MOD (mod reorder) and DL_GROUP (group reorder) payloads.
    /// </summary>
    private static readonly byte[] DragPayloadData = { 1 };

    /// <summary>
    /// Index of the group currently being reordered via drag (DL_GROUP payload).
    /// -1 when no group drag is in progress.
    /// </summary>
    private int _draggedGroupReorderIndex = -1;

    /// <summary>Category tab containing the group being reordered.</summary>
    private string? _draggedGroupReorderCategory;

    // ── Multi-Select State ─────────────────────────────────────────────────────

    /// <summary>Mod directories currently selected (highlighted blue in the list).</summary>
    private readonly HashSet<string> _selectedMods = new();

    /// <summary>
    /// Anchor mod directory for Shift+click range selection.
    /// Set on every Ctrl+click or plain click. Null after tab switch or ClearSelection().
    /// </summary>
    private string? _selectionAnchorDir;

    /// <summary>
    /// Per-category ordered list of mod directories in draw order (groups first, then ungrouped).
    /// Rebuilt by RebuildTabDrawOrder(), called from RebuildRenderRows().
    /// Used by Shift+click range selection.
    /// </summary>
    private readonly Dictionary<string, List<string>> _cachedTabDrawOrder = new();

    /// <summary>
    /// The category tab active on the last draw frame.
    /// When it changes, the selection is cleared automatically.
    /// </summary>
    private string? _activeTabCategory;

    // ── Inline Rename State ──────────────────────────────────────────────────────

    /// <summary>Category tab containing the group currently being renamed. Null when not renaming.</summary>
    private string? _renamingCategory;

    /// <summary>Index of the group currently being renamed. -1 when not renaming.</summary>
    private int _renamingGroupIndex = -1;

    /// <summary>Buffer for the InputText widget during inline group rename.</summary>
    private string _renameBuffer = string.Empty;

    /// <summary>
    /// True for one frame when the group rename InputText should receive keyboard focus.
    /// Set when rename is activated (✎ button or right-click → Rename).
    /// Consumed inside the isRenaming block before the InputText is rendered.
    /// </summary>
    private bool _renameGroupNeedsFocus;

    /// <summary>
    /// True once the rename InputText has been active (focused) at least once in the current
    /// rename session. Guards the "lost focus → confirm" condition so it cannot fire on the
    /// very first frame before keyboard focus has transferred to the widget.
    /// Reset to false when a new rename session begins.
    /// </summary>
    private bool _renameGroupWasActive;

    // ── Tab Management State ──────────────────────────────────────────────────────

    /// <summary>
    /// InputText buffer for the "Add Tab" inline input inside a tab context menu.
    /// Shared across all tabs since only one context menu can be open at a time.
    /// </summary>
    private string _newTabBuffer = string.Empty;

    /// <summary>
    /// True while the "Add Tab" inline InputText is showing inside an open context menu.
    /// Set to false when the context menu closes or the user confirms/cancels.
    /// </summary>
    private bool _addingTabInMenu;

    /// <summary>
    /// The category name of the tab whose context menu currently shows the inline Add Tab input.
    /// Null when no add-tab input is active. Used to reset <see cref="_addingTabInMenu"/> only
    /// when the correct popup closes — not when any other tab's popup check returns false.
    /// </summary>
    private string? _addingTabForCat;

    /// <summary>
    /// Set to true for one frame when the inline Add Tab InputText should receive keyboard focus.
    /// Consumed by <see cref="DrawTabContextMenu"/> on the first frame the InputText is visible.
    /// </summary>
    private bool _addTabNeedsKeyboardFocus;

    /// <summary>
    /// Name of the custom tab currently being renamed via inline InputText in its
    /// right-click context menu. Null when no tab rename is in progress.
    /// </summary>
    private string? _renamingTabName;

    /// <summary>InputText buffer for the inline tab rename within the context menu.</summary>
    private string _renameTabBuffer = string.Empty;

    // ── Star Filter State ─────────────────────────────────────────────────────────

    /// <summary>
    /// Minimum star rating required to show a mod in the current tab view.
    /// 0 = show all (no filter); 1–5 = hide mods with fewer stars than this threshold.
    /// Applied at draw time in <see cref="DrawUngroupedSection"/> and <see cref="DrawGroupItems"/>.
    /// Changing this does not require a cache rebuild — it takes effect on the next drawn frame.
    /// </summary>
    private int _starFilter;

    /// <summary>
    /// Labels for the star filter combo box. Index corresponds directly to <see cref="_starFilter"/> value.
    /// "All" = 0 stars (no filter); others are "N★+" thresholds; "5★ only" = exactly 5 stars.
    /// Declared as a static field to avoid allocating a new array every draw frame.
    /// </summary>
    private static readonly string[] StarFilterLabels = { "All", "1★+", "2★+", "3★+", "4★+", "5★ only" };

    // ── Multi-Emote Expand State ──────────────────────────────────────────────────

    /// <summary>
    /// Set of mod directory names that are currently expanded in the multi-emote parent row view.
    /// Only relevant for mods whose scan produced more than one emote entry (i.e., the mod
    /// overrides multiple distinct emote animations). When a directory is in this set, child rows
    /// appear below the parent showing each emote individually with its own Perform button.
    /// Expand state is not persisted — everything collapses on plugin reload.
    /// </summary>
    private readonly HashSet<string> _expandedMods = new();

    // ── Render Row Cache ──────────────────────────────────────────────────────────

    /// <summary>
    /// Discriminates how a single visual row in the mod list is rendered.
    /// Single: one mod → one emote — all action buttons present (standard row).
    /// MultiParent: one mod → many emotes — shows expand toggle instead of emote name;
    ///   Perform sends the first emote as a quick action.
    /// MultiChild: one emote of an expanded multi-emote mod — indented, Perform only.
    /// </summary>
    private enum RowKind { Single, MultiParent, MultiChild }

    /// <summary>
    /// One element of the flat render list consumed by <see cref="DrawUngroupedSection"/>.
    /// Carries the entry to render, how to render it, and (for parents) the emote count.
    /// </summary>
    private readonly struct RenderRow
    {
        /// <summary>The mod entry backing this visual row.</summary>
        public readonly EmoteModEntry Entry;

        /// <summary>Controls which buttons and columns are shown for this row.</summary>
        public readonly RowKind Kind;

        /// <summary>
        /// Total number of emotes in this mod's group.
        /// Only meaningful for <see cref="RowKind.MultiParent"/> rows;
        /// displayed in the expand toggle label ("▶ N" / "▼ N").
        /// </summary>
        public readonly int EmoteCount;

        /// <summary>Creates a render row.</summary>
        public RenderRow(EmoteModEntry entry, RowKind kind, int emoteCount = 0)
        { Entry = entry; Kind = kind; EmoteCount = emoteCount; }
    }

    /// <summary>
    /// Per-category flat list of visual rows for the ungrouped section.
    /// Accounts for multi-emote mod grouping and the current <see cref="_expandedMods"/> state:
    /// collapsed multi-emote mods contribute one parent row; expanded ones contribute parent + N children.
    /// Rebuilt by <see cref="RebuildRenderRows"/> at the end of every <see cref="RebuildTabCache"/>
    /// call and also whenever the expand state changes (user clicks a toggle button).
    /// Consumed by <see cref="DrawUngroupedSection"/> for virtual-scroll rendering.
    /// </summary>
    private readonly Dictionary<string, List<RenderRow>> _cachedRenderRows = new();

    // ── Tab Content Cache ─────────────────────────────────────────────────────────

    /// <summary>
    /// Dirty flag: set to true whenever a mutation (category change, group add/delete,
    /// favorite toggle, mod move) occurs. Cleared by <see cref="RebuildTabCache"/>.
    /// Also triggers a rebuild when the scan reference changes (new scan completed).
    /// </summary>
    private bool _tabCacheDirty = true;

    /// <summary>
    /// Reference to the <see cref="_allEntries"/> list used to build the current cache.
    /// Compared by reference in <see cref="DrawTabBar"/> to detect scan completion
    /// (background thread replaces _allEntries with a new list object).
    /// </summary>
    private List<EmoteModEntry>? _cachedEntriesRef;

    /// <summary>
    /// Per-category partitioned entry lists. Key = category name.
    /// Rebuilt once per mutation / scan completion by <see cref="RebuildTabCache"/>.
    /// Read every frame by <see cref="DrawTabBar"/> for tab item counts and content.
    /// </summary>
    private readonly Dictionary<string, List<EmoteModEntry>> _cachedTabEntries = new();

    /// <summary>
    /// Per-category ordered ungrouped entry lists (favorites first, then UngroupedOrder).
    /// Rebuilt once per mutation / scan completion by <see cref="RebuildTabCache"/>.
    /// Consumed every frame by <see cref="DrawUngroupedSection"/> for virtual-scroll rendering.
    /// </summary>
    private readonly Dictionary<string, List<EmoteModEntry>> _cachedUngroupedOrdered = new();

    // ── Row Layout Cache ──────────────────────────────────────────────────────────

    /// <summary>
    /// Whether row button pixel widths have been cached via <see cref="EnsureRowWidthsCached"/>.
    /// Set on the first draw frame; never reset (ImGui style doesn't change at runtime).
    /// </summary>
    private bool _rowWidthsCached;

    /// <summary>Cached pixel width of the "Reset" SmallButton.</summary>
    private float _cachedResetW;

    /// <summary>Cached pixel width of the "Settings" SmallButton.</summary>
    private float _cachedSettingsW;

    /// <summary>Cached pixel width of the "Perform" SmallButton.</summary>
    private float _cachedPerformW;

    /// <summary>Cached pixel width of the category dropdown SmallButton (widest category name).</summary>
    private float _cachedCatBtnW;

    /// <summary>
    /// Cached pixel width of the "Pnb" SmallButton that opens Penumbra to this mod's page.
    /// </summary>
    private float _cachedPnbW;

    // ── ImGui Colors ─────────────────────────────────────────────────────────────

    /// <summary>Green color for active mod rows and status indicators.</summary>
    private static readonly Vector4 ColorActive = new(0.4f, 1f, 0.5f, 1f);

    /// <summary>Yellow/warning color for status messages and filled star icons.</summary>
    private static readonly Vector4 ColorWarning = new(1f, 0.85f, 0.3f, 1f);

    /// <summary>Red/error color for failure messages.</summary>
    private static readonly Vector4 ColorError = new(1f, 0.4f, 0.3f, 1f);

    /// <summary>Muted grey used for secondary text (emote names, empty star icons, notes).</summary>
    private static readonly Vector4 ColorMuted = new(0.7f, 0.7f, 0.7f, 1f);

    /// <summary>Light purple for the window title.</summary>
    private static readonly Vector4 ColorTitle = new(0.9f, 0.8f, 1f, 1f);

    /// <summary>Green tint used for Move Mode button background when active.</summary>
    private static readonly Vector4 ColorMoveModeOn = new(0.2f, 0.7f, 0.3f, 1f);

    // ── Constructor ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the main window with all required dependencies.
    /// Does NOT trigger a scan on construction — the scan starts when the window
    /// is first opened (toggled via the /dl command or OpenMainUi).
    /// </summary>
    /// <param name="config">Plugin configuration for window visibility persistence.</param>
    /// <param name="scanner">Mod scanner used to find emote overrides in Penumbra.</param>
    /// <param name="penumbra">Penumbra IPC bridge for mod activation/settings.</param>
    /// <param name="chatSender">Chat command executor for performing emotes.</param>
    /// <param name="settingsWindow">Settings popup window reference.</param>
    /// <param name="framework">Dalamud framework for game-thread scheduling.</param>
    /// <param name="log">Plugin logger.</param>
    public MainWindow(
        Configuration  config,
        ModScanner     scanner,
        PenumbraIpc    penumbra,
        ChatSender     chatSender,
        ModSettingsWindow settingsWindow,
        IFramework     framework,
        IPluginLog     log)
    {
        _config         = config;
        _scanner        = scanner;
        _penumbra       = penumbra;
        _chatSender     = chatSender;
        _settingsWindow = settingsWindow;
        _framework      = framework;
        _log            = log;

        // Incorporate any user-created custom tabs stored in config.
        RebuildCategories();
    }

    // ── Public API ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the window and triggers the first scan if one hasn't run yet.
    /// Use this when you want to guarantee the window becomes visible (e.g., from OpenMainUi).
    /// </summary>
    public void Open()
    {
        IsVisible = true;
        // Auto-scan on first open so the lists are populated immediately.
        if (_scanState == ScanState.NotScanned)
            StartScan();
    }

    /// <summary>
    /// Toggles the window's visibility. Opening triggers <see cref="Open"/> which
    /// also starts the first scan if one hasn't run yet.
    /// Closing leaves any in-progress scan running so results are ready on reopen.
    /// </summary>
    public void Toggle()
    {
        if (IsVisible)
            IsVisible = false;
        else
            Open();
    }

    // ── Scan Logic ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the background mod scan. Silently ignores concurrent scan requests.
    /// The scan runs on a Task thread to avoid blocking the game frame.
    /// Results are committed under <see cref="_lock"/> when complete.
    /// </summary>
    private void StartScan()
    {
        // Prevent concurrent scans.
        if (_scanState == ScanState.Scanning) return;

        _scanState = ScanState.Scanning;
        _scanError = string.Empty;

        // Recheck Penumbra availability before scanning.
        // This handles the case where Penumbra was loaded after plugin startup.
        if (!_penumbra.IsAvailable)
            _penumbra.CheckAvailability();

        // Launch the scan on a background thread.
        // The lambda captures _scanner, _lock, _allEntries, _scanState, _scanError, _log.
        _scanTask = Task.Run(() =>
        {
            try
            {
                _log.Debug("[DanceLibrary] Background scan started");

                // ScanMods() makes 1-2 Penumbra IPC calls per mod.
                // Safe to call from a background thread.
                var results = _scanner.ScanMods();

                // Final dedup: remove any (ModDirectory, EmoteDisplayName) duplicates
                // that slipped through scanner-level dedup (e.g. Penumbra reporting
                // aliased commands that produce the same display name). This is the
                // absolute last line of defence — _allEntries must never contain two
                // rows with the same mod+emote-name pair.
                var dedupSeen = new System.Collections.Generic.HashSet<string>(
                    System.StringComparer.OrdinalIgnoreCase);
                var beforeDedup = results.Count;
                results.RemoveAll(e => !dedupSeen.Add(e.ModDirectory + "\0" + e.EmoteDisplayName));
                if (results.Count != beforeDedup)
                    _log.Warning($"[DanceLibrary] Final dedup removed {beforeDedup - results.Count} duplicate scan entries");

                // Store all entries as a flat list.
                // Categories are NOT derived from scan data — the user assigns them
                // manually via the dropdown, and they persist in Configuration.ModCategories.
                lock (_lock)
                {
                    _allEntries = results;
                    _scanState  = ScanState.Done;
                }

                _log.Info($"[DanceLibrary] Scan complete: {results.Count} emote mod entries");
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _scanError = ex.Message;
                    _scanState = ScanState.Error;
                }
                _log.Error(ex, "[DanceLibrary] Background scan failed");
            }
        });
    }

    // ── ImGui Draw ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the main window and the settings popup. Called every frame from Plugin.OnDraw.
    /// Only renders when <see cref="IsVisible"/> is true.
    /// </summary>
    public void Draw()
    {
        // Always draw the settings window (it guards its own visibility internally).
        _settingsWindow.Draw();

        // Guard: only draw the main window when visible.
        if (!IsVisible) return;

        // --- Set window dimensions: 650×540 initial size ---
        ImGui.SetNextWindowSize(new Vector2(650, 540), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(450, 300), new Vector2(1400, 1200));

        // --- Begin main window. "###DLMain" is the stable ImGui ID. ---
        var visible = IsVisible;
        if (!ImGui.Begin("Dance Library###DLMain", ref visible))
        {
            ImGui.End();
            if (!visible) IsVisible = false;
            return;
        }

        try
        {
            DrawWindowContents();
        }
        finally
        {
            // Always call End() to match Begin(), even if an exception occurs during draw.
            ImGui.End();
        }

        // Sync close state (X button).
        if (!visible) IsVisible = false;
    }

    /// <summary>
    /// Draws all content inside the main window: header, status bar, tab bar, and entry lists.
    /// Called only within a Begin/End block.
    /// </summary>
    private void DrawWindowContents()
    {
        // --- Header row: plugin title + Move Mode + Backup + Refresh button ---
        DrawHeader();

        // --- How To and Backup modals (must be drawn every frame inside Begin/End) ---
        DrawHowToPopup();
        DrawBackupPopup();

        // --- Status row: scan state, Penumbra availability ---
        DrawStatusBar();

        ImGui.Separator();
        ImGui.Spacing();

        // --- Guard: Penumbra not available ---
        if (!_penumbra.IsAvailable)
        {
            ImGui.TextColored(ColorError, "Penumbra is not available.");
            ImGui.TextWrapped("Make sure Penumbra is installed and loaded, then click Refresh.");
            return;
        }

        // --- Guard: scan in progress ---
        if (_scanState == ScanState.Scanning)
        {
            ImGui.TextColored(ColorWarning, "Scanning mods...");
            return;
        }

        // --- Guard: scan error ---
        if (_scanState == ScanState.Error)
        {
            ImGui.TextColored(ColorError, "Scan failed:");
            ImGui.TextWrapped(_scanError);
            ImGui.Spacing();
            if (ImGui.Button("Retry")) StartScan();
            return;
        }

        // --- Guard: not yet scanned ---
        if (_scanState == ScanState.NotScanned)
        {
            ImGui.TextDisabled("Press Refresh to scan your Penumbra mods.");
            return;
        }

        // --- Four-tab bar ---
        DrawTabBar();
    }

    /// <summary>
    /// Renders the header row: title on the left, Move Mode controls and Refresh on the right.
    /// When Move Mode is active, a category combo box appears to the left of the button.
    /// </summary>
    private void DrawHeader()
    {
        // --- Plugin title ---
        ImGui.TextColored(ColorTitle, "Dance Library");

        // --- Right-side controls (right-aligned) ---
        // Build right section width estimate: ? + Reset All + Backup + Refresh
        var spacing    = ImGui.GetStyle().ItemSpacing.X;
        var padding    = ImGui.GetStyle().WindowPadding.X;
        var fpX        = ImGui.GetStyle().FramePadding.X;
        var refreshLbl = _scanState == ScanState.Scanning ? "Scanning..." : "Refresh";
        var refreshW   = ImGui.CalcTextSize(refreshLbl).X + fpX * 2;
        var resetAllW  = ImGui.CalcTextSize("Reset All").X + fpX * 2;
        var backupW    = ImGui.CalcTextSize("Backup").X + fpX * 2;
        var howToW     = ImGui.CalcTextSize("?").X + fpX * 2;
        var totalRightW = howToW + spacing + resetAllW + spacing + backupW + spacing + refreshW;

        ImGui.SameLine(ImGui.GetWindowWidth() - totalRightW - padding - 4f);

        // --- How To button ---
        // Opens a scrollable reference popup explaining every UI feature.
        if (ImGui.SmallButton("?"))
            _howToPopupPending = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How to use Dance Library");

        ImGui.SameLine();

        // --- Reset All button ---
        // Disabled until a scan has completed (nothing to reset before that).
        var canReset = _scanState == ScanState.Done;
        if (!canReset) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Reset All"))
            ResetAllMods();
        if (!canReset) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Remove temporary Penumbra settings from all known mods");

        ImGui.SameLine();

        // --- Backup button ---
        // Copies the plugin config JSON to the user's Downloads folder with a timestamp.
        if (ImGui.SmallButton("Backup"))
            DoBackup();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Save a copy of your library layout to Downloads");

        ImGui.SameLine();

        // --- Refresh button ---
        // SmallButton matches the height of the other header buttons.
        if (_scanState == ScanState.Scanning) ImGui.BeginDisabled();
        if (ImGui.SmallButton(refreshLbl))
        {
            // Re-check Penumbra availability in case it was loaded after plugin init.
            _penumbra.CheckAvailability();
            StartScan();
        }
        if (_scanState == ScanState.Scanning) ImGui.EndDisabled();
    }

    /// <summary>
    /// Renders the "How To Use Dance Library" reference popup.
    /// Must be called every frame inside the main window's Begin/End block.
    /// Opens when <see cref="_howToPopupPending"/> is set by the "?" button.
    ///
    /// Uses a fixed-size window with an inner scrollable child region so the
    /// content can exceed the popup height without resizing the window.
    /// </summary>
    private void DrawHowToPopup()
    {
        // Trigger: open the popup the frame after the ? button sets the flag.
        if (_howToPopupPending)
        {
            ImGui.OpenPopup("How To Use Dance Library##dlhowto");
            _howToPopupPending = false;
        }

        // Fixed size: tall enough to read comfortably, narrow enough to stay on screen.
        ImGui.SetNextWindowSize(new Vector2(520, 580), ImGuiCond.Always);

        // Center on screen.
        var displaySize = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(displaySize * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));

        if (!ImGui.BeginPopupModal("How To Use Dance Library##dlhowto", ImGuiWindowFlags.NoResize))
            return;

        // --- Scrollable content region (leaves room for the Close button below) ---
        ImGui.BeginChild("##howto_scroll", new Vector2(0, -35f), false, ImGuiWindowFlags.None);

        // ── Performing Emotes ─────────────────────────────────────────────────────
        ImGui.TextColored(ColorTitle, "Performing Emotes");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("Click a mod name or the Perform button to temporarily enable the mod via Penumbra and execute the emote in-game.");
        ImGui.Spacing();
        ImGui.TextWrapped("Reset  — removes the temporary Penumbra setting for that mod (returns it to its normal state).");
        ImGui.TextWrapped("Reset All  — removes temporary settings from every mod at once.");
        ImGui.Spacing();

        // ── Row Buttons ───────────────────────────────────────────────────────────
        ImGui.TextColored(ColorTitle, "Row Buttons");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("★ / ☆  — Favorite toggle. Favorited mods sort to the top of their section.");
        ImGui.TextWrapped("[Category]  — Click to reassign the mod to a different tab.");
        ImGui.TextWrapped("Perform  — Enable mod + execute emote.");
        ImGui.TextWrapped("Settings  — Open the option editor for mods with configurable Penumbra options (sound, outfit variants, etc.).");
        ImGui.TextWrapped("Pnb  — Jump directly to this mod in the Penumbra mod browser.");
        ImGui.TextWrapped("Reset  — Remove this mod's temporary Penumbra setting.");
        ImGui.Spacing();

        // ── Star Ratings & Filter ─────────────────────────────────────────────────
        ImGui.TextColored(ColorTitle, "Star Ratings & Filter");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("Open Settings for any mod to assign it a 1–5 star personal quality rating.");
        ImGui.TextWrapped("Use the star filter dropdown (All / 1★+ / 2★+ … / 5★ only) at the top of each tab to show only mods at or above your chosen rating.");
        ImGui.Spacing();

        // ── Right-Click Context Menu ──────────────────────────────────────────────
        ImGui.TextColored(ColorTitle, "Right-Click Menu (on any mod row)");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("Open in Penumbra  — Open this mod's page in Penumbra (single-mod only).");
        ImGui.TextWrapped("Move to category  — Reassign to a different tab.");
        ImGui.TextWrapped("Move to group  — Move into an existing group in this tab.");
        ImGui.TextWrapped("Remove from group  — Return mod to ungrouped.");
        ImGui.TextWrapped("Block mod  — Hide the mod from all plugin operations. Find it again in the Unblock tab; right-click there to restore it.");
        ImGui.Spacing();
        ImGui.TextWrapped("Ctrl+click or Shift+click to select multiple mods, then right-click for bulk Move / Block operations.");
        ImGui.Spacing();

        // ── Organizing with Groups ────────────────────────────────────────────────
        ImGui.TextColored(ColorTitle, "Organizing with Groups");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("Click '+ New Group' at the bottom of any tab to create a collapsible named group.");
        ImGui.TextWrapped("≡  — Drag handle on each row. Drag a mod onto another mod or group header to reorder or move it.");
        ImGui.TextWrapped("▲/▼  — Arrow button on group headers. Drag to reorder groups.");
        ImGui.TextWrapped("✎  — Rename a group inline.");
        ImGui.TextWrapped("X  — Delete a group (its mods return to ungrouped).");
        ImGui.TextWrapped("Right-click a group header for Rename / Delete options.");
        ImGui.Spacing();

        // ── Tabs ──────────────────────────────────────────────────────────────────
        ImGui.TextColored(ColorTitle, "Tabs");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("Dance, Emote, NSFW, and Other are built-in tabs that cannot be renamed or deleted.");
        ImGui.TextWrapped("Right-click any tab to create a custom tab with any name. Right-click a custom tab to rename or delete it.");
        ImGui.TextWrapped("Deleting a custom tab moves all its mods to Other automatically.");
        ImGui.TextWrapped("Unblock tab  — Lists mods you have blocked. Right-click any entry to unblock it.");
        ImGui.Spacing();

        // ── Header Buttons ────────────────────────────────────────────────────────
        ImGui.TextColored(ColorTitle, "Header Buttons");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("Backup  — Copies your library layout (categories, groups, favorites, ratings) to a timestamped JSON file in your Downloads folder. Does not include Penumbra settings.");
        ImGui.TextWrapped("Refresh  — Re-scans all Penumbra mods for emote overrides. Run this after installing or removing mods.");

        ImGui.EndChild();

        // --- Close button pinned to the bottom ---
        ImGui.Spacing();
        var closeW = 120f;
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - closeW) * 0.5f);
        if (ImGui.Button("Close", new Vector2(closeW, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    /// <summary>
    /// Copies the plugin config JSON to the user's Downloads folder with a timestamp suffix.
    /// Sets <see cref="_backupPopupPending"/> so the result modal opens on the next frame.
    ///
    /// Source: %APPDATA%\XIVLauncher\pluginConfigs\DanceLibraryFFXIV.json
    /// Destination: %USERPROFILE%\Downloads\DanceLibraryFFXIV_yyyy-MM-dd_HH-mm-ss.json
    /// </summary>
    private void DoBackup()
    {
        try
        {
            // Resolve source — the Dalamud-managed config file for this plugin.
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var src     = Path.Combine(appData, "XIVLauncher", "pluginConfigs", "DanceLibraryFFXIV.json");

            // Resolve destination — timestamped file in the user's Downloads folder.
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var fileName  = $"DanceLibraryFFXIV_{timestamp}.json";
            var dst       = Path.Combine(downloads, fileName);

            File.Copy(src, dst, overwrite: false);

            _backupResultMessage = $"Backup saved!\n\nDownloads\\{fileName}";
            _log.Info($"[DanceLibrary] Backup saved to {dst}");
        }
        catch (Exception ex)
        {
            _backupResultMessage = $"Backup failed:\n{ex.Message}";
            _log.Error(ex, "[DanceLibrary] Backup failed");
        }

        // Signal the popup to open on the next draw frame.
        _backupPopupPending = true;
    }

    /// <summary>
    /// Renders the backup result modal popup.
    /// Must be called every frame inside the main window's Begin/End block.
    /// Opens automatically when <see cref="_backupPopupPending"/> is set by <see cref="DoBackup"/>.
    ///
    /// The modal is AlwaysAutoResize so it fits its message without manual sizing.
    /// Centered on screen via SetNextWindowPos with a 0.5/0.5 pivot.
    /// </summary>
    private void DrawBackupPopup()
    {
        // Trigger: open the popup the frame after DoBackup() sets the flag.
        if (_backupPopupPending)
        {
            ImGui.OpenPopup("Backup Result##dlbk");
            _backupPopupPending = false;
        }

        // Center the modal on the display.
        var displaySize = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(displaySize * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));

        // --- Modal contents ---
        if (ImGui.BeginPopupModal("Backup Result##dlbk", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(_backupResultMessage);
            ImGui.Spacing();

            // OK button: dismiss the modal.
            if (ImGui.Button("OK", new Vector2(120, 0)))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }
    }

    /// <summary>
    /// Renders the scan status bar showing total mod count and per-category breakdown.
    /// </summary>
    private void DrawStatusBar()
    {
        List<EmoteModEntry> allEntries;
        lock (_lock)
        {
            // Snapshot under lock; render outside lock to minimize hold time.
            allEntries = _allEntries;
        }

        switch (_scanState)
        {
            case ScanState.Done:
                if (allEntries.Count == 0)
                {
                    ImGui.TextColored(ColorMuted, "No emote mods found. Install Penumbra dance mods to see them here.");
                }
                else
                {
                    // Count how many mods are in each user-assigned category.
                    var catCounts = new Dictionary<string, int>();
                    foreach (var cat in _categories) catCounts[cat] = 0;
                    foreach (var e in allEntries)
                    {
                        var cat = GetModCategory(e);
                        if (catCounts.ContainsKey(cat)) catCounts[cat]++;
                        else catCounts["Other"]++;
                    }

                    // Build a compact summary showing only non-zero categories.
                    var parts = _categories
                        .Where(c => catCounts[c] > 0)
                        .Select(c => $"{catCounts[c]} {c}");
                    ImGui.TextColored(ColorMuted,
                        $"Found {allEntries.Count} emote mod entries ({string.Join(", ", parts)})");

                }
                break;

            case ScanState.Scanning:
                ImGui.TextColored(ColorWarning, "Scanning...");
                break;

            case ScanState.Error:
                ImGui.TextColored(ColorError, $"Scan error: {_scanError}");
                break;

            case ScanState.NotScanned:
            default:
                ImGui.TextColored(ColorMuted, "Not yet scanned.");
                break;
        }
    }

    /// <summary>
    /// Renders the four-tab bar: Dance | Emote | NSFW | Other.
    /// Each tab shows only the mods the user has assigned to that category.
    /// <summary>
    /// Renders the "Unblock" tab content: a simplified read-only list of blocked mods.
    /// This tab is permanent and always appears last in the tab bar.
    /// The only interaction is right-click → "Unblock", which moves the mod to "Other".
    /// No action buttons (Perform, Settings, Pnb, Reset) are shown — the plugin never
    /// interacts with blocked mods in any way.
    /// </summary>
    private void DrawUnblockTab()
    {
        var blocked = _cachedTabEntries.TryGetValue("Unblock", out var bl) ? bl : new List<EmoteModEntry>();

        // Use ### so the stable ID is "tab_Unblock" regardless of the count in the label.
        var tabOpened = ImGui.BeginTabItem($"Unblock ({blocked.Count})###tab_Unblock");
        if (!tabOpened) return;

        // Clear multi-selection when switching to this tab.
        if (_activeTabCategory != "Unblock")
        {
            ClearSelection();
            _activeTabCategory = "Unblock";
        }

        ImGui.Spacing();
        if (blocked.Count == 0)
        {
            ImGui.TextColored(ColorMuted, "No blocked mods.");
        }
        else
        {
            // Informational header — makes it clear why this tab exists.
            ImGui.TextDisabled("Blocked mods are hidden from all plugin operations.");
            ImGui.TextDisabled("Right-click a mod to unblock it. It will return to its previous category (or Other).");
            ImGui.Separator();
            ImGui.Spacing();

            foreach (var entry in blocked)
                DrawBlockedModRow(entry);
        }

        ImGui.EndTabItem();
    }

    /// <summary>
    /// Renders a single read-only row for a blocked mod in the "Unblock" tab.
    /// Uses a <see cref="ImGui.Selectable"/> so right-click context items work.
    /// No action buttons are rendered — blocked mods are inert to the plugin.
    /// </summary>
    /// <param name="entry">The blocked mod entry to render.</param>
    private void DrawBlockedModRow(EmoteModEntry entry)
    {
        ImGui.PushID($"blocked_{entry.ModDirectory}");

        // Selectable (muted colour) — no click action, but right-click context menu works.
        ImGui.PushStyleColor(ImGuiCol.Text, ColorMuted);
        ImGui.Selectable($"  {entry.ModDisplayName}##blockedrow", false);
        ImGui.PopStyleColor();

        // Right-click context menu: only "Unblock" is available.
        if (ImGui.BeginPopupContextItem("##blockedrowctx"))
        {
            if (ImGui.MenuItem("Unblock"))
                UnblockMod(entry.ModDirectory);
            ImGui.EndPopup();
        }

        ImGui.PopID();
    }

    /// All newly scanned mods appear in "Other" until the user moves them.
    /// Categorization is persistent — read from <see cref="Configuration.ModCategories"/>.
    /// </summary>
    private void DrawTabBar()
    {
        List<EmoteModEntry> allEntries;
        lock (_lock)
        {
            // Snapshot list under lock; cache work and rendering happen outside lock.
            allEntries = _allEntries;
        }

        // Rebuild cache only when dirty (a mutation occurred since last rebuild) or when
        // a new scan replaced the allEntries reference. Both are O(1) to detect.
        if (_tabCacheDirty || !ReferenceEquals(allEntries, _cachedEntriesRef))
            RebuildTabCache(allEntries);

        // --- Tab bar: "##DLTabs" is the stable ImGui ID ---
        if (!ImGui.BeginTabBar("##DLTabs")) return;

        // Snapshot _categories before the loop — AddCustomTab / DeleteCustomTab replace
        // the field with a new array, so using a local reference keeps iteration stable.
        var snapshot = _categories;
        foreach (var cat in snapshot)
        {
            // Use cached partition — no per-frame allocation or iteration.
            var list     = _cachedTabEntries.TryGetValue(cat, out var l) ? l : new List<EmoteModEntry>();
            var isCustom = _config.CustomCategories.Contains(cat);

            // Use ### so ImGui uses only the stable "tab_{cat}" as the ID,
            // ignoring the changing count in the label. This prevents tab jumping.
            var tabOpened = ImGui.BeginTabItem($"{cat} ({list.Count})###tab_{cat}");

            // --- Right-click context menu on this tab ---
            // Returns true if the tab was just deleted (we must skip EndTabItem).
            if (DrawTabContextMenu(cat, isCustom, ref tabOpened)) continue;

            if (tabOpened)
            {
                DrawEntryList(list, cat);
                ImGui.EndTabItem();
            }
        }

        // --- "Unblock" tab: permanent, always last, cannot be renamed or deleted ---
        // Shows blocked mods with a right-click "Unblock" option only.
        // Not part of _categories so it is invisible to Move Mode and category dropdowns.
        DrawUnblockTab();

        ImGui.EndTabBar();
    }

    /// <summary>
    /// Renders a right-click context menu for a tab bar item.
    ///
    /// Built-in tabs: only "Add Tab..." is available.
    /// Custom tabs: "Rename" (with inline InputText when active), "Delete", and "Add Tab...".
    ///
    /// The "Add Tab..." item opens a nested popup inside the context menu for entering
    /// the new name — keeping both the context menu and the name input in a single popup stack.
    /// </summary>
    /// <param name="cat">The category name for this tab.</param>
    /// <param name="isCustom">Whether this is a user-created tab (built-ins are not editable).</param>
    /// <param name="tabOpened">
    /// Whether BeginTabItem returned true for this tab. Set to false if the tab is deleted.
    /// </param>
    /// <returns>True if the tab was deleted (caller must skip EndTabItem and continue the loop).</returns>
    private bool DrawTabContextMenu(string cat, bool isCustom, ref bool tabOpened)
    {
        // BeginPopupContextItem opens a popup when the last ImGui item (the tab) is right-clicked.
        // Only reset inline-add state when the popup for THIS specific tab closes — not when other
        // tabs' popup checks return false (which happens every frame for every non-active tab).
        if (!ImGui.BeginPopupContextItem($"##tabctx_{cat}"))
        {
            if (_addingTabForCat == cat)
            {
                _addingTabInMenu = false;
                _addingTabForCat = null;
            }
            return false;
        }

        if (isCustom)
        {
            if (_renamingTabName == cat)
            {
                // --- Inline InputText rename (replaces menu items while rename is active) ---
                ImGui.Text("Rename tab:");
                ImGui.SetNextItemWidth(160f);
                var done = ImGui.InputText("##tabren", ref _renameTabBuffer, 64,
                                           ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.SameLine();
                if (ImGui.SmallButton("OK") || done)
                {
                    var newName = _renameTabBuffer.Trim();
                    if (!string.IsNullOrEmpty(newName) &&
                        !Array.Exists(_categories, c => c.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                        RenameCustomTab(cat, newName);
                    _renamingTabName = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel"))
                {
                    _renamingTabName = null;
                    ImGui.CloseCurrentPopup();
                }
            }
            else
            {
                // --- Normal custom tab menu items ---
                if (ImGui.MenuItem("Rename"))
                {
                    // Don't close the popup — switch to rename InputText mode on next render.
                    _renamingTabName = cat;
                    _renameTabBuffer = cat;
                }

                if (ImGui.MenuItem($"Delete \"{cat}\""))
                {
                    DeleteCustomTab(cat);
                    // End the tab item before closing the popup (required by ImGui).
                    if (tabOpened) { ImGui.EndTabItem(); tabOpened = false; }
                    ImGui.EndPopup();
                    return true; // tell caller: this tab is gone, skip to next
                }

                ImGui.Separator();
            }
        }

        // --- "Reset Category" — remove temp Penumbra settings for all mods in this tab ---
        if (ImGui.MenuItem("Reset Category"))
            ResetCategory(cat);

        ImGui.Separator();

        // --- "Add Tab" — inline input inside the context menu ---
        // Uses DontClosePopups so clicking the item keeps the context menu open, then shows
        // an InputText in-place. (A nested popup approach doesn't work because MenuItem closes
        // the parent context menu on the same frame, dismissing any child popup before it renders.)
        if (!_addingTabInMenu)
        {
            // Show "Add Tab..." as a selectable that does NOT auto-close the popup on click.
            if (ImGui.Selectable("Add Tab...", false, ImGuiSelectableFlags.DontClosePopups))
            {
                _addingTabInMenu          = true;
                _addingTabForCat          = cat;
                _addTabNeedsKeyboardFocus = true;
                _newTabBuffer             = string.Empty;
            }
        }
        else
        {
            // --- Inline InputText: user is typing the new tab name ---
            ImGui.Text("New tab name:");
            ImGui.SetNextItemWidth(160f);
            // Auto-focus the input field on the first frame it appears.
            if (_addTabNeedsKeyboardFocus)
            {
                ImGui.SetKeyboardFocusHere();
                _addTabNeedsKeyboardFocus = false;
            }
            var submitted = ImGui.InputText("##newtabname", ref _newTabBuffer, 64,
                                            ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if (ImGui.SmallButton("Add") || submitted)
            {
                var name = _newTabBuffer.Trim();
                if (!string.IsNullOrEmpty(name) &&
                    !Array.Exists(_categories, c => c.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    AddCustomTab(name);
                _addingTabInMenu = false;
                _addingTabForCat = null;
                _newTabBuffer    = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel"))
            {
                _addingTabInMenu = false;
                _addingTabForCat = null;
                _newTabBuffer    = string.Empty;
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.EndPopup();
        return false;
    }

    /// <summary>
    /// Renders all entries for a category tab.
    ///
    /// Outside the scrollable child (fixed / non-scrolling):
    ///   - "+ New Group" button + star filter combo.
    ///   - Column header row ("Mod Name" / "Emote" / "Category") — stays visible while scrolling.
    ///
    /// Inside the scrollable child (always has a vertical scrollbar):
    ///   - Ungrouped mods (favorites at top, alphabetical within groups, star-filtered).
    ///   - User-created collapsible groups (each with their own star-filtered rows).
    /// </summary>
    /// <param name="entries">All mod entries in this category tab.</param>
    /// <param name="category">The category name (tab name), e.g. "Dance".</param>
    private void DrawEntryList(List<EmoteModEntry> entries, string category)
    {
        // Clear multi-selection when the user switches to a different tab.
        // Selections are category-specific; keeping them across tabs would be confusing
        // and could cause bulk operations to act on mods that are no longer visible.
        if (_activeTabCategory != category)
        {
            ClearSelection();
            _activeTabCategory = category;
        }

        // UpdateUngroupedOrderForCategory is called in RebuildTabCache, not here.
        // This avoids running O(n) LINQ + HashSet construction every draw frame.

        // --- Empty state ---
        if (entries.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(ColorMuted, "No mods in this category yet.");
            ImGui.TextDisabled("Use the category dropdown (or right-click) to assign mods here.");
            return;
        }

        // Ensure row widths are cached so the column header row can use the same positions.
        // Called here (outside BeginChild) so it runs before both headers and rows.
        EnsureRowWidthsCached();

        // Build a HashSet of present mod directories once per DrawEntryList call (not per group).
        // Passed down to DrawGroup so it can count group members in O(1) per entry instead
        // of the previous O(n*m) pattern (group.ModDirectories.Count(d => tabEntries.Any(...))).
        var presentDirs = new HashSet<string>(entries.Select(e => e.ModDirectory));

        // --- Controls row (outside the scrollable area — stays fixed while list scrolls) ---

        // "+ New Group" button.
        if (ImGui.SmallButton($"+ New Group##ng_{category}"))
            AddGroup(category);

        // Star filter combo: inline to the right of the button.
        // Changing the filter takes effect immediately on the next draw (no cache rebuild needed).
        ImGui.SameLine();
        ImGui.TextDisabled("  ★ Filter:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(72f);
        var prevFilter = _starFilter;
        if (ImGui.Combo("##starfilter", ref _starFilter, StarFilterLabels, StarFilterLabels.Length))
            if (prevFilter != _starFilter)
                _log.Debug($"[DanceLibrary] Star filter changed: {StarFilterLabels[_starFilter]}");

        ImGui.Spacing();

        // --- Column header row (outside child — stays fixed while list scrolls) ---
        // DrawColumnHeaders subtracts ScrollbarSize from rightEdge to compensate for the
        // AlwaysVerticalScrollbar reservation in the child window below.
        DrawColumnHeaders();

        // --- Scrollable child window for the mod list ---
        // AlwaysVerticalScrollbar keeps the scrollbar permanently visible so the child's
        // content width is a fixed (parentWidth − ScrollbarSize), matching DrawColumnHeaders.
        var scrollSize = new Vector2(0, ImGui.GetContentRegionAvail().Y);
        ImGui.BeginChild($"##DLScroll_{category}", scrollSize, false,
                         ImGuiWindowFlags.AlwaysVerticalScrollbar);

        // --- Ungrouped section (starred first, virtual-scrolled, star-filtered) ---
        DrawUngroupedSection(entries, category);

        // --- User-created groups ---
        var groups = GetCategoryGroups(category);
        for (var gi = 0; gi < groups.Count; gi++)
            DrawGroup(entries, category, gi, groups[gi], presentDirs);

        ImGui.EndChild();
    }

    /// <summary>
    /// Renders a thin column header row that labels the fixed right-aligned columns.
    /// Uses the same absolute X positions as <see cref="DrawEntryRow"/> so "Mod Name",
    /// "Emote", and "Category" labels align directly above their respective columns.
    ///
    /// Called OUTSIDE the scrollable <see cref="ImGui.BeginChild"/> so the header stays
    /// fixed while the list scrolls. Because the child uses
    /// <see cref="ImGuiWindowFlags.AlwaysVerticalScrollbar"/> its content width is always
    /// <c>parentWidth − ScrollbarSize</c>; we subtract the same amount from <c>rightEdge</c>
    /// here so column positions match exactly.
    /// </summary>
    private void DrawColumnHeaders()
    {
        var spc       = ImGui.GetStyle().ItemSpacing.X;
        // Subtract the scrollbar reservation so header column positions match the rows
        // inside the child window (which permanently reserves scrollbar space).
        var rightEdge = ImGui.GetContentRegionMax().X - ImGui.GetStyle().ScrollbarSize;
        const float EmoteColW = 130f;

        // Mirror the column layout from DrawEntryRow (right-to-left).
        var xReset    = rightEdge - _cachedResetW;
        var xPnb      = xReset    - spc - _cachedPnbW;
        var xSettings = xPnb      - spc - _cachedSettingsW;
        var xPerform  = xSettings - spc - _cachedPerformW;
        var xCat      = xPerform  - spc - _cachedCatBtnW;
        var xEmote    = xCat      - spc - EmoteColW;

        // TextColored renders muted header labels at the exact same offsets as the data rows.
        // The "###" trick does NOT work with TextColored, so the visible text is what ImGui shows.
        ImGui.TextColored(ColorMuted, "Mod Name");
        ImGui.SameLine(xEmote);
        ImGui.TextColored(ColorMuted, "Emote");
        ImGui.SameLine(xCat);
        ImGui.TextColored(ColorMuted, "Category");

        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>
    /// Renders ungrouped mods (those not assigned to any group in this category tab).
    /// Starred mods appear first, then regular mods, each respecting the stored
    /// <see cref="Configuration.UngroupedOrder"/> for user-specified ordering.
    /// </summary>
    /// <param name="tabEntries">All entries in the current category tab.</param>
    /// <param name="category">The current category name.</param>
    private void DrawUngroupedSection(List<EmoteModEntry> tabEntries, string category)
    {
        // Use the pre-built flat render list from the cache — no LINQ or grouping each frame.
        // Built once by RebuildTabCache/RebuildRenderRows and reused until the next mutation,
        // scan, or expand-state change.
        if (!_cachedRenderRows.TryGetValue(category, out var allRows) || allRows.Count == 0)
            return;

        // Apply the star filter if active. The filtered list is computed here (draw time)
        // so changes to star ratings in ModSettingsWindow take effect immediately.
        // All rows for a multi-emote mod (parent + children) share the same ModDirectory
        // and therefore the same star rating, so filtering is always consistent.
        var rows = _starFilter > 0
            ? allRows.Where(r => GetStarRating(r.Entry.ModDirectory) >= _starFilter).ToList()
            : allRows;

        if (rows.Count == 0)
        {
            ImGui.TextColored(ColorMuted, $"No mods with {_starFilter}+ stars in this category.");
            ImGui.Spacing();
            return;
        }

        // --- Render all rows unconditionally (no virtual scrolling) ---
        //
        // Virtual scrolling (replacing off-screen rows with Dummy spacers) was removed because
        // it caused two fatal drag-drop problems:
        //   1. Off-screen rows had no widgets, so their drop targets didn't exist in ImGui's
        //      hit-test tree — you couldn't drop a mod onto an item that wasn't rendered.
        //   2. Switching from virtual-scroll mode to full-render mode on the first frame of a
        //      drag (when _draggedModDir transitions null → non-null) changed the total content
        //      height. ImGui recalculated and clamped scrollY, causing the "list jumps to top"
        //      effect the user saw.
        //
        // ImGui culls draw calls for widgets that are outside the clipping rectangle, so
        // submitting all rows to the CPU layout pass is the only overhead. For typical mod
        // counts (hundreds to low thousands), this is imperceptible.
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Kind == RowKind.MultiChild)
                DrawChildRow(row.Entry, category);
            else
                DrawEntryRow(row.Entry, i, category, groupName: null,
                             isParent:   row.Kind == RowKind.MultiParent,
                             emoteCount: row.EmoteCount);
        }

        ImGui.Spacing();
    }

    /// <summary>
    /// Renders a single collapsible group header and (if expanded) its mod entries.
    /// Group header layout: [▼/►] [Name (N)] [✎] [X]
    ///
    /// Drag-and-drop:
    ///   - ArrowButton: DL_GROUP source + DL_GROUP target (group reordering).
    ///   - Selectable (group name): DL_MOD target (drops a mod into this group at the end).
    ///
    /// Inline rename: when the ✎ button is clicked, the name Selectable is replaced
    /// by an InputText widget until Enter is pressed or focus is lost.
    /// </summary>
    /// <param name="tabEntries">All entries in the current category tab.</param>
    /// <param name="category">The current category name.</param>
    /// <param name="gi">Group index within the category's group list.</param>
    /// <param name="group">The <see cref="ModGroup"/> data to render.</param>
    private void DrawGroup(List<EmoteModEntry> tabEntries, string category, int gi, ModGroup group, HashSet<string> presentDirs)
    {
        ImGui.Spacing();
        ImGui.PushID($"grp_{category}_{gi}");

        // ── Arrow button: collapse toggle + DL_GROUP drag source + DL_GROUP drop target ──

        // Clicking the arrow toggles collapse.
        var arrowDir = group.IsCollapsed ? ImGuiDir.Right : ImGuiDir.Down;
        if (ImGui.ArrowButton($"##arrow", arrowDir))
        {
            group.IsCollapsed = !group.IsCollapsed;
            _config.Save();
        }

        // --- DL_GROUP drag source on the ArrowButton ---
        // Dragging a group's ArrowButton lets the user reorder groups within the tab.
        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoHoldToOpenOthers))
        {
            _draggedGroupReorderIndex    = gi;
            _draggedGroupReorderCategory = category;
            ImGui.SetDragDropPayload("DL_GROUP", DragPayloadData);
            ImGui.Text($"Group: {group.Name}");
            ImGui.EndDragDropSource();
        }

        // --- DL_GROUP drop target on the ArrowButton ---
        // Dropping another group here reorders the two groups.
        // AcceptDragDropPayload returns non-null on EVERY hover frame (not just on release),
        // so we gate the actual reorder behind IsMouseReleased to prevent firing every frame.
        // IMPORTANT: p.IsNull must be checked before accessing any other property.
        // When a DL_MOD drag hovers over this target, AcceptDragDropPayload("DL_GROUP") returns
        // a null-wrapped ImGuiPayloadPtr (IsNull == true). Accessing p.Data on it throws
        // NullReferenceException, which aborts the render loop and causes rows below to disappear.
        if (ImGui.BeginDragDropTarget())
        {
            unsafe
            {
                var p = ImGui.AcceptDragDropPayload("DL_GROUP");
                if (!p.IsNull
                    && _draggedGroupReorderCategory == category
                    && _draggedGroupReorderIndex >= 0
                    && _draggedGroupReorderIndex != gi
                    && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    var groups = GetCategoryGroups(category);
                    var moved  = groups[_draggedGroupReorderIndex];
                    groups.RemoveAt(_draggedGroupReorderIndex);
                    groups.Insert(gi, moved);
                    _config.Save();
                    _draggedGroupReorderIndex = -1;
                }
            }
            ImGui.EndDragDropTarget();
        }

        ImGui.SameLine();

        // ── Group name: Selectable or InputText (rename mode) ──

        var spacing   = ImGui.GetStyle().ItemSpacing.X;
        // Reserve space for [✎] and [X] buttons on the right.
        var btnW      = ImGui.CalcTextSize("✎").X + ImGui.GetStyle().FramePadding.X * 2;
        var labelW    = ImGui.GetContentRegionAvail().X - btnW * 2 - spacing * 2;
        labelW        = Math.Max(60f, labelW);

        var isRenaming = _renamingGroupIndex == gi && _renamingCategory == category;

        if (isRenaming)
        {
            // --- Inline InputText for rename ---
            ImGui.SetNextItemWidth(labelW);
            // Auto-focus the InputText on the first frame it appears.
            if (_renameGroupNeedsFocus)
            {
                ImGui.SetKeyboardFocusHere();
                _renameGroupNeedsFocus = false;
            }
            if (ImGui.InputText("##rename", ref _renameBuffer, 128))
            {
                // Updated live as user types.
            }
            // Track whether focus has ever landed on this InputText.
            // We must not confirm the rename on the first frame (before focus transfers).
            if (ImGui.IsItemActive())
                _renameGroupWasActive = true;

            // Confirm rename when the user presses Enter (IsItemDeactivatedAfterEdit)
            // or clicks away (IsItemActive just became false) — but only after the widget
            // has been active at least once to avoid confirming on the very first frame.
            if (ImGui.IsItemDeactivatedAfterEdit() ||
                (!ImGui.IsItemActive() && _renameGroupWasActive && _renamingGroupIndex == gi))
            {
                group.Name = string.IsNullOrWhiteSpace(_renameBuffer) ? "New Group" : _renameBuffer.Trim();
                _config.Save();
                _renamingGroupIndex   = -1;
                _renamingCategory     = null;
                _renameGroupWasActive = false;
            }
        }
        else
        {
            // --- Selectable as group name label + DL_MOD drop target ---
            // Clicking the selectable also toggles collapse.
            // Use the pre-built presentDirs HashSet for O(1) lookups.
            // The old pattern (tabEntries.Any(e => ...)) was O(n) per group entry = O(n*m) total.
            var groupCount = group.ModDirectories.Count(d => presentDirs.Contains(d));
            var labelText  = $"{group.Name} ({groupCount})###grplbl_{category}_{gi}";

            if (ImGui.Selectable(labelText, false, ImGuiSelectableFlags.None, new Vector2(labelW, 0)))
            {
                group.IsCollapsed = !group.IsCollapsed;
                _config.Save();
            }

            // --- Right-click context menu on group name ---
            // "Rename" starts inline rename (InputText replaces Selectable next frame).
            // "Delete" removes the group and returns its mods to ungrouped.
            if (ImGui.BeginPopupContextItem($"##grpctx_{category}_{gi}"))
            {
                if (ImGui.MenuItem("Rename"))
                {
                    _renamingGroupIndex    = gi;
                    _renamingCategory      = category;
                    _renameBuffer          = group.Name;
                    _renameGroupNeedsFocus = true;
                    _renameGroupWasActive  = false;
                }
                if (ImGui.MenuItem("Delete"))
                {
                    DeleteGroup(category, gi);
                    ImGui.EndPopup();
                    ImGui.PopID();
                    return;
                }
                ImGui.EndPopup();
            }

            // --- DL_MOD drop target on group name Selectable ---
            // Dropping a mod here moves it into this group (appended at end).
            // AcceptDragDropPayload returns non-null on EVERY hover frame, not just on drop.
            // Gate the move behind IsMouseReleased so it only fires once (on release).
            // IMPORTANT: p.IsNull must be checked before accessing any other property.
            // When a DL_GROUP drag hovers over this target, AcceptDragDropPayload("DL_MOD")
            // returns a null-wrapped ImGuiPayloadPtr (IsNull == true). Accessing p.Data
            // throws NullReferenceException, aborting the render loop → rows below disappear.
            if (ImGui.BeginDragDropTarget())
            {
                unsafe
                {
                    var p = ImGui.AcceptDragDropPayload("DL_MOD");
                    if (!p.IsNull
                        && _draggedModDir != null
                        && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                    {
                        MoveToGroup(_draggedFromCategory!, _draggedFromGroupName,
                                    category, group.Name);
                    }
                }
                ImGui.EndDragDropTarget();
            }
        }

        // ── Right-side buttons: rename and delete ──

        ImGui.SameLine();
        if (ImGui.SmallButton($"✎##ren_{gi}"))
        {
            // Begin inline rename; keyboard focus will be applied next frame when InputText appears.
            _renamingGroupIndex    = gi;
            _renamingCategory      = category;
            _renameBuffer          = group.Name;
            _renameGroupNeedsFocus = true;
            _renameGroupWasActive  = false;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"X##del_{gi}"))
        {
            // Delete group and move its mods back to ungrouped.
            DeleteGroup(category, gi);
            ImGui.PopID();
            return;  // group deleted — skip rendering items
        }

        // ── Group items (only when expanded) ──

        if (!group.IsCollapsed)
            DrawGroupItems(tabEntries, category, gi, group);

        ImGui.PopID();
    }

    /// <summary>
    /// Renders the mod entries inside an expanded group.
    /// Items are sorted with favorites first, then in the order stored in
    /// <see cref="ModGroup.ModDirectories"/>. Items indented 16px relative to the group header.
    /// </summary>
    /// <param name="tabEntries">All entries in the current category tab.</param>
    /// <param name="category">The current category name.</param>
    /// <param name="gi">Group index (used for unique widget IDs).</param>
    /// <param name="group">The group whose entries are being rendered.</param>
    private void DrawGroupItems(List<EmoteModEntry> tabEntries, string category, int gi, ModGroup group)
    {
        // Use ToLookup — one directory can have multiple emote entries (one per emote override).
        var byDir = tabEntries.ToLookup(e => e.ModDirectory);

        // Build display list: only mods that exist in current scan.
        // Sort: favorites first, then alphabetically within each section.
        // Distinct() is a defensive guard against duplicate directories in the config list
        // (both within this group and from cross-group duplicates not yet cleaned by
        // RebuildTabCache). Without it a duplicated directory produces duplicate rows.
        var ordered = group.ModDirectories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => byDir.Contains(d))
            .OrderByDescending(d => _config.FavoriteMods.Contains(d))
            .ThenBy(d => byDir[d].First().ModDisplayName)
            .SelectMany(d => byDir[d])
            .ToList();

        // Apply the star filter — same as DrawUngroupedSection.
        if (_starFilter > 0)
            ordered = ordered.Where(e => GetStarRating(e.ModDirectory) >= _starFilter).ToList();

        if (ordered.Count == 0)
        {
            ImGui.Indent(16f);
            ImGui.TextColored(ColorMuted, "(empty — drag mods here)");
            ImGui.Unindent(16f);
            return;
        }

        // Indent group contents visually, then render one parent row per mod.
        // Multi-emote mods show a parent row (with expand toggle) and — when expanded —
        // child rows for each individual emote below the parent.
        ImGui.Indent(16f);
        var rowIdx = 0; // mod-level index within this group (used for drag-drop insertion)
        var i = 0;
        while (i < ordered.Count)
        {
            // Find all consecutive entries that share the same ModDirectory.
            var dir = ordered[i].ModDirectory;
            var j   = i + 1;
            while (j < ordered.Count && ordered[j].ModDirectory == dir) j++;
            var count = j - i;

            if (count == 1)
            {
                // Single-emote mod — standard full row.
                DrawEntryRow(ordered[i], rowIdx, category, group.Name);
            }
            else
            {
                // Multi-emote mod — parent row, then child rows if expanded.
                DrawEntryRow(ordered[i], rowIdx, category, group.Name,
                             isParent: true, emoteCount: count);
                if (_expandedMods.Contains(dir))
                    for (var k = i; k < j; k++)
                        DrawChildRow(ordered[k], category);
            }

            rowIdx++;
            i = j;
        }
        ImGui.Unindent(16f);
    }

    /// <summary>
    /// Renders a single mod entry row with: drag handle, star, mod name (Selectable),
    /// emote name or expand toggle, category dropdown, and action buttons.
    ///
    /// Clicking the mod name or Perform button either performs the emote (normal mode)
    /// or re-categorizes the mod (Move Mode). The drag handle initiates a DL_MOD drag.
    ///
    /// When <paramref name="isParent"/> is true the row represents a mod that overrides multiple
    /// emotes. The emote column shows an expand/collapse toggle ("▶ N" / "▼ N") instead of an
    /// emote name. Clicking Perform on a parent row performs the first emote as a quick action.
    /// Child rows are rendered separately by <see cref="DrawChildRow"/>.
    /// </summary>
    /// <param name="entry">The mod entry data for this row (first entry for parent rows).</param>
    /// <param name="rowIndex">Row index used for drop-target positioning in drag-and-drop.</param>
    /// <param name="category">Current category tab (for unique IDs and drag context).</param>
    /// <param name="groupName">Group name if this entry is inside a group, null if ungrouped.</param>
    /// <param name="isParent">
    /// True when this mod overrides multiple emotes and this is the collapsed/expanded parent row.
    /// Changes the emote column to an expand toggle button.
    /// </param>
    /// <param name="emoteCount">
    /// Total emote override count for this mod. Only used when <paramref name="isParent"/> is true;
    /// shown in the expand toggle label (e.g., "▶ 4").
    /// </param>
    private void DrawEntryRow(EmoteModEntry entry, int rowIndex, string category, string? groupName,
                              bool isParent = false, int emoteCount = 0)
    {
        // Push a unique scope ID for all widgets in this row.
        // Using the full mod directory path ensures no collisions across categories and groups.
        ImGui.PushID($"{category}_{groupName ?? "ung"}_{entry.ModDirectory}");

        var isActive   = _activeMods.Contains(entry.ModDirectory);
        var isFav      = _config.FavoriteMods.Contains(entry.ModDirectory);
        var isSelected = _selectedMods.Contains(entry.ModDirectory);

        // ── ≡ Drag handle — DL_MOD drag source ──────────────────────────────────
        // SmallButton is used (not Text or Dummy) because non-interactive widgets
        // cannot be drag sources. The button captures mouse input needed for BeginDragDropSource.
        ImGui.SmallButton("≡");

        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoHoldToOpenOthers))
        {
            _draggedModDir        = entry.ModDirectory;
            _draggedFromCategory  = category;
            _draggedFromGroupName = groupName;
            ImGui.SetDragDropPayload("DL_MOD", DragPayloadData);
            ImGui.Text(entry.ModDisplayName);
            ImGui.EndDragDropSource();
        }

        ImGui.SameLine();

        // ── ★/☆ Favorite toggle button ────────────────────────────────────────────
        ImGui.PushStyleColor(ImGuiCol.Text, isFav ? ColorWarning : ColorMuted);
        if (ImGui.SmallButton(isFav ? "★" : "☆"))
            ToggleFavorite(entry.ModDirectory);
        ImGui.PopStyleColor();

        ImGui.SameLine();

        // ── Compute right-aligned column positions ────────────────────────────────
        // All X values are in ImGui cursor coordinates (relative to the content region's
        // left edge, i.e. after WindowPadding). SameLine(absoluteX) positions each column
        // at an exact fixed offset from the right edge so they never shift with name length.
        // Note: ImGui.TextDisabled ignores SetNextItemWidth, so we MUST use absolute positions
        // for the emote column — otherwise variable-length names push all buttons right of it.
        //
        // Widget widths are cached after the first draw (stable for the session).
        // rightEdge is re-read each row because it changes when the window is resized,
        // but the arithmetic is trivially fast (no CalcTextSize calls per row).
        EnsureRowWidthsCached();

        var spc       = ImGui.GetStyle().ItemSpacing.X;
        var rightEdge = ImGui.GetContentRegionMax().X;
        const float EmoteColW = 130f; // wide enough for "Little Ladies' Dance" and similar

        // Column start positions, computed right-to-left from cached widths.
        var xReset    = rightEdge - _cachedResetW;
        var xPnb      = xReset    - spc - _cachedPnbW;
        var xSettings = xPnb      - spc - _cachedSettingsW;
        var xPerform  = xSettings - spc - _cachedPerformW;
        var xCat      = xPerform  - spc - _cachedCatBtnW;
        var xEmote    = xCat      - spc - EmoteColW;

        // Name Selectable stretches from the current cursor (after ★) to the emote column.
        var nameWidth = Math.Max(40f, xEmote - spc - ImGui.GetCursorPosX());

        // ── Mod name Selectable — click action + DL_MOD drop target ───────────────
        // Active mods: green tint on background + text.
        // Selected mods: blue tint on background (unless also active, in which case green wins).
        if (isActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Text,          ColorActive);
            ImGui.PushStyleColor(ImGuiCol.Header,         new Vector4(0.2f, 0.5f, 0.2f, 0.4f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered,  new Vector4(0.2f, 0.5f, 0.2f, 0.5f));
        }
        else if (isSelected)
        {
            // Blue selection highlight for selected-but-not-active rows.
            ImGui.PushStyleColor(ImGuiCol.Header,         new Vector4(0.26f, 0.59f, 0.98f, 0.35f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered,  new Vector4(0.26f, 0.59f, 0.98f, 0.45f));
        }

        // Prepend "● " for active mods for a quick visual indicator.
        var nameLabel = isActive ? $"● {entry.ModDisplayName}" : $"  {entry.ModDisplayName}";
        var clicked   = ImGui.Selectable(nameLabel, isActive || isSelected,
                            ImGuiSelectableFlags.None, new Vector2(nameWidth, 0));

        if (isActive)        ImGui.PopStyleColor(3);
        else if (isSelected) ImGui.PopStyleColor(2);

        // Left-click: Ctrl selects, Shift range-selects, plain click performs (existing behaviour).
        if (clicked)
        {
            var io = ImGui.GetIO();
            if (io.KeyCtrl)
            {
                // Ctrl+click: toggle this mod in/out of the selection.
                if (!_selectedMods.Remove(entry.ModDirectory))
                    _selectedMods.Add(entry.ModDirectory);
                _selectionAnchorDir = entry.ModDirectory;
            }
            else if (io.KeyShift
                     && _selectionAnchorDir != null
                     && _cachedTabDrawOrder.TryGetValue(category, out var drawOrder))
            {
                // Shift+click: select all rows between anchor and this row, inclusive.
                var anchorIdx = drawOrder.IndexOf(_selectionAnchorDir);
                var currIdx   = drawOrder.IndexOf(entry.ModDirectory);
                if (anchorIdx >= 0 && currIdx >= 0)
                {
                    var lo = Math.Min(anchorIdx, currIdx);
                    var hi = Math.Max(anchorIdx, currIdx);
                    for (var i = lo; i <= hi; i++)
                        _selectedMods.Add(drawOrder[i]);
                }
            }
            else
            {
                // Plain click: clear any selection and perform the emote as before.
                ClearSelection();
                HandleRowClick(entry);
            }
        }

        // --- Right-click context menu on the mod name Selectable ---
        // If the right-clicked mod is not in the current selection, clear selection and select
        // just this one (standard file-manager behaviour).
        // The PushID scope (set at the top of DrawEntryRow) keeps this popup's ID unique.
        if (ImGui.BeginPopupContextItem("##rowctx"))
        {
            if (!_selectedMods.Contains(entry.ModDirectory))
            {
                ClearSelection();
                _selectedMods.Add(entry.ModDirectory);
                _selectionAnchorDir = entry.ModDirectory;
            }

            var selCount = _selectedMods.Count;

            // Header: show count when multiple mods are selected.
            if (selCount > 1)
                ImGui.TextDisabled($"{selCount} mods selected");

            // ── Open in Penumbra (single-mod only) ──────────────────────────────
            // Opens Penumbra's mod browser to this mod's settings page via IPC.
            if (selCount == 1)
            {
                if (ImGui.MenuItem("Open in Penumbra"))
                    _penumbra.OpenModInPenumbra(entry.ModDirectory);
                ImGui.Separator();
            }

            // ── Move to category submenu ─────────────────────────────────────────
            if (ImGui.BeginMenu("Move to category"))
            {
                foreach (var cat in _categories)
                    if (ImGui.MenuItem(cat))
                        SetSelectedModsCategory(cat);
                ImGui.EndMenu();
            }

            // ── Move to group submenu (only when groups exist in this tab) ───────
            var ctxGroups = GetCategoryGroups(category);
            if (ctxGroups.Count > 0 && ImGui.BeginMenu("Move to group"))
            {
                foreach (var grp in ctxGroups)
                    if (ImGui.MenuItem(grp.Name))
                        MoveSelectedModsToGroup(category, grp.Name);
                ImGui.EndMenu();
            }

            // ── Remove from group (single-mod only, when already in a group) ─────
            if (selCount == 1 && groupName != null)
            {
                ImGui.Separator();
                if (ImGui.MenuItem("Remove from group"))
                    RemoveFromGroup(category, groupName, entry.ModDirectory);
            }

            // ── Clear selection (multi-select only) ──────────────────────────────
            if (selCount > 1)
            {
                ImGui.Separator();
                if (ImGui.MenuItem("Clear selection"))
                    ClearSelection();
            }

            // ── Block mod(s) ─────────────────────────────────────────────────────
            // Moves every selected mod to the Unblock tab, preventing all plugin
            // interaction with them. Reverse via right-click → Unblock in the Unblock tab.
            ImGui.Separator();
            var blockLabel = selCount > 1 ? $"Block {selCount} mods" : "Block mod";
            if (ImGui.MenuItem(blockLabel))
                foreach (var dir in _selectedMods.ToList())
                    BlockMod(dir);

            ImGui.EndPopup();
        }

        // --- DL_MOD drop target on the mod name Selectable ---
        // Dropping another mod here reorders within the same section, or moves between sections.
        // AcceptDragDropPayload returns non-null on EVERY hover frame (not just on mouse release).
        // Gate the actual move behind IsMouseReleased so it only fires once, on drop.
        // IMPORTANT: p.IsNull must be checked before accessing any other property.
        // When a DL_GROUP drag hovers over this target, AcceptDragDropPayload("DL_MOD") returns
        // a null-wrapped ImGuiPayloadPtr (IsNull == true). Accessing p.Data on it throws
        // NullReferenceException, which aborts the render loop mid-frame, preventing all rows
        // below from rendering — causing the "rows disappear when dragging" visual bug.
        if (ImGui.BeginDragDropTarget())
        {
            unsafe
            {
                var p = ImGui.AcceptDragDropPayload("DL_MOD");
                if (!p.IsNull
                    && _draggedModDir != null && _draggedModDir != entry.ModDirectory
                    && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    MoveDraggedMod(entry.ModDirectory, category, groupName, rowIndex);
                }
            }
            ImGui.EndDragDropTarget();
        }

        // ── Emote display name or multi-emote expand toggle ───────────────────────
        // Single/child rows: TextDisabled shows the emote display name.
        // Multi-emote parent rows: SmallButton toggle shows "▶ N" / "▼ N" and
        // expands or collapses the per-emote child rows below this parent.
        // SameLine(xEmote) ensures this column (and all columns to its right) are flush-right.
        ImGui.SameLine(xEmote);
        if (isParent)
        {
            var isExpanded = _expandedMods.Contains(entry.ModDirectory);
            // Toggle expand/collapse; rebuild the flat render list immediately so the
            // next frame shows the correct number of child rows.
            if (ImGui.SmallButton($"{(isExpanded ? "▼" : "▶")} {emoteCount}##exp"))
            {
                if (isExpanded) _expandedMods.Remove(entry.ModDirectory);
                else            _expandedMods.Add(entry.ModDirectory);
                RebuildRenderRows();
            }
        }
        else
        {
            ImGui.TextDisabled(entry.EmoteDisplayName);
        }

        // ── Category dropdown button — fixed right-aligned position ───────────────
        ImGui.SameLine(xCat);
        var currentCat = GetModCategory(entry);
        var popupId    = $"cat_popup";
        if (ImGui.SmallButton($"{currentCat}##cat"))
            ImGui.OpenPopup(popupId);

        // --- Category selection popup ---
        if (ImGui.BeginPopup(popupId))
        {
            ImGui.TextDisabled("Category");
            ImGui.Separator();
            foreach (var cat in _categories)
            {
                if (ImGui.Selectable(cat, cat == currentCat))
                {
                    SetModCategory(entry.ModDirectory, cat);
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndPopup();
        }

        // ── Action buttons — fixed right-aligned positions ─────────────────────────

        // --- [Perform] button ---
        // Also clears multi-selection so the green active highlight is always unambiguous.
        ImGui.SameLine(xPerform);
        if (ImGui.SmallButton("Perform##perf"))
        {
            ClearSelection();
            HandleRowClick(entry);
        }

        // --- [Settings] button ---
        // Toggle: clicking while already open for this mod closes the window (re-click to dismiss).
        // Always enabled: even mods with no Penumbra options can still have a star rating set.
        // ModSettingsWindow shows the rating UI for all mods and a "no options" notice if needed.
        ImGui.SameLine(xSettings);
        if (ImGui.SmallButton("Settings##set"))
            _settingsWindow.Toggle(entry);

        // --- [Pnb] button ---
        // Opens Penumbra's mod browser to this mod's detail page via Penumbra IPC.
        ImGui.SameLine(xPnb);
        if (ImGui.SmallButton("Pnb##pnb"))
            _penumbra.OpenModInPenumbra(entry.ModDirectory);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open in Penumbra");

        // --- [Reset] button ---
        // Resets ALL currently active mods plugin-wide. A global reset prevents animation
        // and audio conflicts caused by multiple active mods interfering with each other.
        ImGui.SameLine(xReset);
        if (ImGui.SmallButton("Reset##rst"))
            OnResetClicked(entry);

        ImGui.PopID();
    }

    /// <summary>
    /// Renders one child row for an expanded multi-emote mod.
    ///
    /// Child rows are shown indented below the parent (see <see cref="DrawEntryRow"/>
    /// with isParent=true). They display only the emote display name and a Perform button
    /// — the parent row owns drag handle, star, category, Settings, and Reset.
    ///
    /// The Perform button executes the emote command specific to this child row, so
    /// users can choose exactly which emote the mod activates (e.g., /happy vs /joy).
    /// </summary>
    /// <param name="entry">The emote entry for this child row.</param>
    /// <param name="category">Current category tab (used for unique widget IDs).</param>
    private void DrawChildRow(EmoteModEntry entry, string category)
    {
        // Unique ID: include emote command so sibling children (same mod dir) don't collide.
        ImGui.PushID($"child_{category}_{entry.ModDirectory}_{entry.EmoteCommand}");

        // Cache button widths (idempotent — only runs once per session).
        EnsureRowWidthsCached();

        // Compute the same column positions as the parent row so emote name and Perform
        // align with their respective columns in the header and parent rows.
        var spc       = ImGui.GetStyle().ItemSpacing.X;
        var rightEdge = ImGui.GetContentRegionMax().X;
        const float EmoteColW = 130f;
        var xReset    = rightEdge - _cachedResetW;
        var xPnb      = xReset    - spc - _cachedPnbW;
        var xSettings = xPnb      - spc - _cachedSettingsW;
        var xPerform  = xSettings - spc - _cachedPerformW;
        var xCat      = xPerform  - spc - _cachedCatBtnW;
        var xEmote    = xCat      - spc - EmoteColW;

        // Position the emote name at the Emote column (same X as parent row's emote name).
        // SetCursorPosX jumps to that column without needing a dummy widget first.
        // The "↳" arrow provides a visual tree-child indicator.
        ImGui.SetCursorPosX(xEmote);
        ImGui.TextColored(ColorMuted, $"↳ {entry.EmoteDisplayName}");

        // Perform button at the same column as parent row Perform.
        ImGui.SameLine(xPerform);
        if (ImGui.SmallButton("Perform##cperf"))
            HandleRowClick(entry);

        ImGui.PopID();
    }

    // ── Category Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Gets the effective display category for a mod entry.
    /// Blocked mods always return "Unblock" regardless of any ModCategories entry.
    /// Non-blocked mods return their user-assigned category, defaulting to "Other".
    /// </summary>
    /// <param name="entry">The mod entry to look up.</param>
    /// <returns>Category name: "Unblock", "Dance", "Emote", "NSFW", "Other", or a custom tab name.</returns>
    private string GetModCategory(EmoteModEntry entry)
    {
        // Blocked mods always route to the Unblock tab, regardless of any other assignment.
        if (_config.BlockedMods.Contains(entry.ModDirectory)) return "Unblock";
        return _config.ModCategories.TryGetValue(entry.ModDirectory, out var cat) ? cat : "Other";
    }

    /// <summary>
    /// Saves the user's category choice for a mod to the plugin configuration.
    /// The change is immediately persisted to disk via <see cref="Configuration.Save"/>.
    /// On the next draw frame the mod will appear in its new tab.
    /// </summary>
    /// <param name="modDirectory">The mod's folder name (key in Configuration.ModCategories).</param>
    /// <param name="category">The chosen category: "Dance", "Emote", "NSFW", or "Other".</param>
    private void SetModCategory(string modDirectory, string category)
    {
        _config.ModCategories[modDirectory] = category;
        _config.Save();
        MarkCacheDirty(); // mod moved to a different tab
        _log.Debug($"[DanceLibrary] Category set: {modDirectory} → {category}");
    }

    // ── Move Mode ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles a row click (mod name Selectable or Perform button).
    /// In Move Mode: re-categorizes the mod to the selected target tab.
    /// In normal mode: triggers the Perform action (temp enable + emote execution).
    /// </summary>
    /// <param name="entry">The mod entry that was clicked.</param>
    private void HandleRowClick(EmoteModEntry entry)
    {
        if (_moveModeEnabled)
        {
            // Move Mode: categorize the mod to the selected tab, no emote played.
            var target = _categories[Math.Clamp(_moveModeTargetIdx, 0, _categories.Length - 1)];
            SetModCategory(entry.ModDirectory, target);
            _log.Debug($"[DanceLibrary] Move Mode: {entry.ModDirectory} → {target}");
        }
        else
        {
            OnPerformClicked(entry);
        }
    }

    // ── Group Management ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the list of groups for a category tab, creating an empty list if none exists.
    /// </summary>
    /// <param name="category">The category tab name.</param>
    /// <returns>The mutable list of <see cref="ModGroup"/> objects for this category.</returns>
    private List<ModGroup> GetCategoryGroups(string category)
    {
        if (!_config.CategoryGroups.TryGetValue(category, out var groups))
            _config.CategoryGroups[category] = groups = new List<ModGroup>();
        return groups;
    }

    /// <summary>
    /// Returns entries from the current tab that are NOT assigned to any group.
    /// The set of "in a group" dirs is computed from <see cref="Configuration.CategoryGroups"/>.
    /// </summary>
    /// <param name="tabEntries">All entries in the current category tab.</param>
    /// <param name="category">The current category name.</param>
    /// <returns>Entries that have no group assignment for this category.</returns>
    private List<EmoteModEntry> GetUngroupedEntries(List<EmoteModEntry> tabEntries, string category)
    {
        var groupedDirs = GetCategoryGroups(category)
            .SelectMany(g => g.ModDirectories)
            .ToHashSet();
        return tabEntries.Where(e => !groupedDirs.Contains(e.ModDirectory)).ToList();
    }

    /// <summary>
    /// Returns the stored order list for a section: either <see cref="Configuration.UngroupedOrder"/>
    /// for ungrouped mods, or <see cref="ModGroup.ModDirectories"/> for group contents.
    /// Creates an empty ungrouped list if none exists.
    /// </summary>
    /// <param name="category">The category tab name.</param>
    /// <param name="groupName">Group name, or null for the ungrouped section.</param>
    /// <returns>The mutable order list for that section.</returns>
    private List<string> GetOrderList(string category, string? groupName)
    {
        if (groupName == null)
        {
            if (!_config.UngroupedOrder.TryGetValue(category, out var u))
                _config.UngroupedOrder[category] = u = new List<string>();
            return u;
        }

        // For a group, return its ModDirectories list.
        var group = GetCategoryGroups(category).FirstOrDefault(g => g.Name == groupName);
        return group?.ModDirectories ?? new List<string>();  // fallback (shouldn't happen)
    }

    /// <summary>
    /// Returns entries sorted with favorites at the top, respecting the stored order from
    /// <see cref="GetOrderList"/> for both the favorite and non-favorite sub-groups.
    /// Entries not yet in the stored order list are appended at the end.
    /// </summary>
    /// <param name="entries">The entries to sort (all ungrouped, or all in one group).</param>
    /// <param name="category">The category tab name.</param>
    /// <param name="groupName">Group name, or null for ungrouped.</param>
    /// <returns>Entries sorted: favorites first (in their stored order), then regulars.</returns>
    private List<EmoteModEntry> OrderWithFavoritesFirst(
        List<EmoteModEntry> entries, string category, string? groupName)
    {
        var orderedDirs = GetOrderList(category, groupName);

        // Use ToLookup (not ToDictionary) — one mod directory can appear multiple times
        // in the entry list when a single mod overrides more than one emote animation.
        // ToDictionary would throw ArgumentException on duplicate keys.
        var byDir     = entries.ToLookup(e => e.ModDirectory);
        var knownDirs = new HashSet<string>(orderedDirs);

        // Build list in stored order, expanding each directory to all its emote entries.
        // Distinct() guards against duplicate directories in UngroupedOrder — same
        // pattern as DrawGroupItems. A duplicated directory emits its emote entries
        // twice through SelectMany, producing duplicate rows in the UI.
        var result = orderedDirs
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => byDir.Contains(d))
            .SelectMany(d => byDir[d])
            .ToList();

        // Append any newly scanned mods not yet in orderedDirs.
        result.AddRange(entries.Where(e => !knownDirs.Contains(e.ModDirectory)));

        // Sort: favorites first, then alphabetically within each section.
        // ThenBy(ModDisplayName) gives a predictable, consistent order for mods
        // without requiring the user to manually drag everything into position.
        return result
            .OrderByDescending(e => _config.FavoriteMods.Contains(e.ModDirectory))
            .ThenBy(e => e.ModDisplayName)
            .ToList();
    }

    /// <summary>
    /// Ensures newly discovered ungrouped mods are represented in
    /// <see cref="Configuration.UngroupedOrder"/> for the given category.
    /// Called once per draw frame from <see cref="DrawEntryList"/> (draw thread only).
    /// Saves config only if new mods were appended.
    /// </summary>
    /// <param name="tabEntries">All entries in the current category tab.</param>
    /// <param name="category">The current category name.</param>
    private void UpdateUngroupedOrderForCategory(List<EmoteModEntry> tabEntries, string category)
    {
        var groupedDirs  = GetCategoryGroups(category).SelectMany(g => g.ModDirectories).ToHashSet();
        var ungrouped    = GetOrderList(category, null);
        // HashSet for O(1) lookups — ungrouped list can be 2000+ entries, List.Contains is O(n).
        var ungroupedSet = new HashSet<string>(ungrouped);

        // Deduplicate by directory name first: a mod that overrides N emotes appears N times
        // in tabEntries but should occupy exactly one slot in the order list.
        var newDirs = tabEntries
            .Select(e => e.ModDirectory)
            .Distinct()
            .Where(d => !groupedDirs.Contains(d) && !ungroupedSet.Contains(d))
            .ToList();

        if (newDirs.Count > 0)
        {
            foreach (var d in newDirs)
                ungrouped.Add(d);
            _config.Save();
        }
    }

    /// <summary>
    /// Adds a new empty group to the specified category tab and persists the change.
    /// The new group uses the default name "New Group" and is not collapsed.
    /// </summary>
    /// <param name="category">The category tab to add the group to.</param>
    private void AddGroup(string category)
    {
        GetCategoryGroups(category).Add(new ModGroup());
        _config.Save();
        MarkCacheDirty(); // group list changed
        _log.Debug($"[DanceLibrary] Added new group to {category}");
    }

    /// <summary>
    /// Deletes a group at the given index, moving its mods back to the ungrouped section.
    /// The mods are appended to the end of the ungrouped order list.
    /// </summary>
    /// <param name="category">The category tab containing the group.</param>
    /// <param name="groupIndex">Zero-based index of the group to delete.</param>
    private void DeleteGroup(string category, int groupIndex)
    {
        var groups    = GetCategoryGroups(category);
        if (groupIndex < 0 || groupIndex >= groups.Count) return;

        var groupMods = groups[groupIndex].ModDirectories.ToList();
        groups.RemoveAt(groupIndex);

        // Return the deleted group's mods to ungrouped.
        var ungrouped = GetOrderList(category, null);
        foreach (var d in groupMods)
            if (!ungrouped.Contains(d))
                ungrouped.Add(d);

        _config.Save();
        MarkCacheDirty(); // mods moved from group to ungrouped → _cachedUngroupedOrdered needs rebuild
        _log.Debug($"[DanceLibrary] Deleted group #{groupIndex} in {category}; moved {groupMods.Count} mods to ungrouped");
    }

    /// <summary>
    /// Toggles the favorite status of a mod. Starred mods sort to the top of their section.
    /// </summary>
    /// <param name="modDirectory">The mod directory name to toggle.</param>
    private void ToggleFavorite(string modDirectory)
    {
        if (!_config.FavoriteMods.Remove(modDirectory))
            _config.FavoriteMods.Add(modDirectory);
        _config.Save();
        MarkCacheDirty(); // sort order in _cachedUngroupedOrdered changed
        _log.Debug($"[DanceLibrary] Toggled favorite: {modDirectory} → {_config.FavoriteMods.Contains(modDirectory)}");
    }

    /// <summary>
    /// Returns the star rating (1–5) for a mod, or 0 if unrated.
    /// Reads from <see cref="Configuration.ModStarRatings"/>; returns 0 for any missing entry.
    /// The rating is a personal quality/preference label used only for filtering in the UI.
    /// It has no effect on sort order — that is governed by <see cref="Configuration.FavoriteMods"/>.
    /// </summary>
    /// <param name="modDirectory">The Penumbra mod directory name to look up.</param>
    /// <returns>Integer rating 0–5; 0 means unrated.</returns>
    private int GetStarRating(string modDirectory)
        => _config.ModStarRatings.GetValueOrDefault(modDirectory, 0);

    /// <summary>
    /// Removes a mod from its group and places it back in the ungrouped section of the same tab.
    /// If the mod is not found in the named group, this is a no-op.
    /// The mod is appended to the end of the tab's <see cref="Configuration.UngroupedOrder"/> list.
    /// </summary>
    /// <param name="category">The category tab containing the group.</param>
    /// <param name="groupName">The name of the group the mod belongs to.</param>
    /// <param name="modDirectory">The Penumbra mod directory name to remove from the group.</param>
    private void RemoveFromGroup(string category, string groupName, string modDirectory)
    {
        var group = GetCategoryGroups(category).FirstOrDefault(g => g.Name == groupName);
        if (group == null || !group.ModDirectories.Remove(modDirectory)) return;

        // Return the mod to the ungrouped section (appended at the end).
        var ungrouped = GetOrderList(category, null);
        if (!ungrouped.Contains(modDirectory))
            ungrouped.Add(modDirectory);

        _config.Save();
        MarkCacheDirty(); // mod moved from group to ungrouped
        _log.Debug($"[DanceLibrary] Removed '{modDirectory}' from group '{groupName}' in {category}");
    }

    // ── Drag-and-Drop Handlers ────────────────────────────────────────────────────

    /// <summary>
    /// Handles a DL_MOD drop onto a mod row: reorders within the same section,
    /// or moves the dragged mod to a different group or ungrouped section.
    /// Also updates <see cref="Configuration.ModCategories"/> if the drop crosses tab boundaries.
    /// </summary>
    /// <param name="targetModDir">Directory name of the mod the dragged mod was dropped on.</param>
    /// <param name="targetCategory">Category tab of the drop target.</param>
    /// <param name="targetGroupName">Group name at the drop target, or null if ungrouped.</param>
    /// <param name="targetIndex">Row index of the drop target within its section.</param>
    private void MoveDraggedMod(string targetModDir, string targetCategory,
                                string? targetGroupName, int targetIndex)
    {
        if (_draggedModDir == null) return;

        // Step 1: Remove the dragged mod from its current section.
        var sourceList = GetOrderList(_draggedFromCategory!, _draggedFromGroupName);
        sourceList.Remove(_draggedModDir);

        // Step 2: Insert at the target position, adjusting for same-list reordering.
        var targetList   = GetOrderList(targetCategory, targetGroupName);
        var adjustedIdx  = targetIndex;

        // When reordering within the same list, removing the source shifts indices down.
        // If the source was above the target, decrement the target index by 1.
        if (_draggedFromCategory == targetCategory && _draggedFromGroupName == targetGroupName)
        {
            var srcIdx = targetList.IndexOf(_draggedModDir);
            if (srcIdx >= 0 && srcIdx < targetIndex)
                adjustedIdx--;
        }

        adjustedIdx = Math.Clamp(adjustedIdx, 0, targetList.Count);
        // Guard against duplicate entries in case the drop event fires
        // more than once (e.g. two overlapping drop targets both accepting the payload).
        if (!targetList.Contains(_draggedModDir))
            targetList.Insert(adjustedIdx, _draggedModDir);

        // Step 3: If the drop crosses category tabs, update ModCategories.
        // SetModCategory already calls MarkCacheDirty; call it unconditionally here
        // so same-tab reorders (which don't call SetModCategory) also invalidate the cache.
        if (_draggedFromCategory != targetCategory)
            SetModCategory(_draggedModDir, targetCategory);

        _config.Save();
        MarkCacheDirty(); // group membership / ungrouped order changed
        _log.Debug($"[DanceLibrary] Moved {_draggedModDir} to {targetCategory}/{targetGroupName ?? "ungrouped"} at index {adjustedIdx}");

        _draggedModDir = null;
    }

    /// <summary>
    /// Handles a DL_MOD drop onto a group header Selectable.
    /// Moves the dragged mod into the target group (appended at the end).
    /// Also updates <see cref="Configuration.ModCategories"/> if the mod crosses category tabs.
    /// </summary>
    /// <param name="fromCategory">Category the dragged mod came from.</param>
    /// <param name="fromGroupName">Group the dragged mod came from, or null if ungrouped.</param>
    /// <param name="toCategory">Category of the target group.</param>
    /// <param name="toGroupName">Name of the target group.</param>
    private void MoveToGroup(string fromCategory, string? fromGroupName,
                              string toCategory, string toGroupName)
    {
        if (_draggedModDir == null) return;

        // Remove from source section.
        var sourceList = GetOrderList(fromCategory, fromGroupName);
        sourceList.Remove(_draggedModDir);

        // Append to end of target group.
        var targetList = GetOrderList(toCategory, toGroupName);
        if (!targetList.Contains(_draggedModDir))
            targetList.Add(_draggedModDir);

        // Update tab if needed.
        if (fromCategory != toCategory)
            SetModCategory(_draggedModDir, toCategory);

        _config.Save();
        MarkCacheDirty(); // group membership changed → ungrouped list needs rebuild
        _log.Debug($"[DanceLibrary] Moved {_draggedModDir} into group '{toGroupName}' in {toCategory}");

        _draggedModDir = null;
    }

    // ── Action Handlers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Called when the user clicks a mod's row or Perform button (in normal mode).
    /// Fires an async task to: deactivate all currently active mods in the same category,
    /// apply temporary Penumbra settings to this mod, wait for Penumbra to apply them,
    /// then execute the emote command on the game thread.
    ///
    /// The async task is fire-and-forget: UI remains responsive during the 300ms delay.
    /// </summary>
    /// <param name="entry">The mod entry the user wants to perform.</param>
    private void OnPerformClicked(EmoteModEntry entry)
    {
        // Fire-and-forget: the async task manages its own lifecycle.
        _ = PerformDanceAsync(entry);
    }

    /// <summary>
    /// Called when the user clicks the Reset button on a mod entry.
    ///
    /// Resets ALL currently-active mods plugin-wide, regardless of category, group, or tab.
    /// Multiple active mods can cause animation/audio conflicts (e.g., one dance's music
    /// bleeds into another), so a full reset is always safest.
    ///
    /// Only mods tracked in <see cref="_activeMods"/> are reset — mods that were never
    /// activated via Perform are not touched.
    /// </summary>
    /// <param name="entry">The mod entry whose Reset button was clicked (used for logging).</param>
    private void OnResetClicked(EmoteModEntry entry)
    {
        _log.Info($"[DanceLibrary] Reset clicked: {entry.ModDisplayName} — resetting all {_activeMods.Count} active mod(s)");

        // Reset every active mod, not just the clicked one. Any previously active mod
        // may still be affecting animation or audio, so all must be cleared at once.
        foreach (var dir in _activeMods.ToList())
        {
            _log.Debug($"[DanceLibrary] Resetting active mod: {dir}");
            ResetMod(dir);
        }
    }

    /// <summary>
    /// Fully resets a single mod: removes its temporary Penumbra settings, sets it to
    /// "Inherit Settings" in the player's collection, and removes it from the active set.
    ///
    /// Equivalent to clicking "Remove Temporary" + "Inherit Settings" in Penumbra's UI.
    /// Used by both <see cref="OnResetClicked"/> and <see cref="PerformDanceAsync"/> (to
    /// deactivate all previously active mods before enabling a new one).
    ///
    /// Safe to call on a mod that was never activated — RemoveTemporaryModSettings and
    /// TryInheritMod both return NothingChanged (not an error) in that case.
    /// </summary>
    /// <param name="modDirectory">The mod's folder name to reset.</param>
    private void ResetMod(string modDirectory)
    {
        // Remove temporary Penumbra settings. No-op if none were applied.
        _penumbra.RemoveTemporaryModSettings(modDirectory);

        // Set the mod to "Inherit Settings" so it is fully neutral in the collection.
        var collectionId = _penumbra.GetPlayerCollectionId();
        if (collectionId.HasValue)
        {
            _penumbra.TryInheritMod(collectionId.Value, modDirectory);
            _log.Debug($"[DanceLibrary] Set to inherit: {modDirectory}");
        }
        else
        {
            _log.Warning($"[DanceLibrary] Reset: could not get collection ID to inherit {modDirectory}");
        }

        _activeMods.Remove(modDirectory);
    }

    /// <summary>
    /// Resets every mod in a single category tab: removes temporary Penumbra settings and
    /// sets each mod to "Inherit Settings". Equivalent to pressing the per-row Reset button
    /// for every mod visible in that tab.
    /// Blocked mods (shown in the "Unblock" tab) are always skipped — the plugin never
    /// interacts with them.
    /// </summary>
    /// <param name="cat">The category name whose mods should be reset.</param>
    private void ResetCategory(string cat)
    {
        // Never touch blocked mods — they are inert and shown only in the Unblock tab.
        if (cat == "Unblock") return;
        if (!_cachedTabEntries.TryGetValue(cat, out var entries) || entries.Count == 0) return;
        _log.Info($"[DanceLibrary] Reset Category '{cat}': resetting {entries.Count} mod(s)");
        foreach (var entry in entries)
            ResetMod(entry.ModDirectory);
    }

    /// <summary>
    /// Resets every non-blocked mod the plugin knows about: removes temporary Penumbra
    /// settings and sets each mod to "Inherit Settings".
    /// Blocked mods are always skipped — the plugin never interacts with them.
    /// </summary>
    private void ResetAllMods()
    {
        List<EmoteModEntry> snapshot;
        lock (_lock) { snapshot = _allEntries; }
        var toReset = snapshot.Where(e => !_config.BlockedMods.Contains(e.ModDirectory)).ToList();
        _log.Info($"[DanceLibrary] Reset All: resetting {toReset.Count} mod(s) (skipping {snapshot.Count - toReset.Count} blocked)");
        foreach (var entry in toReset)
            ResetMod(entry.ModDirectory);
    }

    /// <summary>
    /// Blocks a mod from the plugin: hides it from all category tabs, prevents any
    /// plugin interaction with it (Perform, Reset, Settings), and moves it to the
    /// "Unblock" tab. Takes effect immediately without a rescan.
    ///
    /// Any active temporary Penumbra settings for the mod are cleaned up first so
    /// the mod is left in a neutral "Inherit Settings" state.
    /// </summary>
    /// <param name="modDir">The Penumbra mod directory name to block.</param>
    private void BlockMod(string modDir)
    {
        _log.Info($"[DanceLibrary] Blocking mod: {modDir}");

        // Clean up any active Penumbra temp state — blocked mods must be left neutral.
        ResetMod(modDir);

        // Clear from the current multi-selection if present.
        _selectedMods.Remove(modDir);
        if (_selectionAnchorDir == modDir) _selectionAnchorDir = null;

        // Persist the block and rebuild caches so the mod moves to the Unblock tab immediately.
        _config.BlockedMods.Add(modDir);
        _config.Save();
        MarkCacheDirty();
    }

    /// <summary>
    /// Unblocks a mod: removes it from <see cref="Configuration.BlockedMods"/> and
    /// assigns it to "Other" (if it has no existing category assignment). Rebuilds
    /// the tab cache so the mod reappears immediately without needing a Refresh.
    /// </summary>
    /// <param name="modDir">The Penumbra mod directory name to unblock.</param>
    private void UnblockMod(string modDir)
    {
        _log.Info($"[DanceLibrary] Unblocking mod: {modDir}");

        _config.BlockedMods.Remove(modDir);

        // Assign to "Other" if there is no pre-existing category record.
        // (Blocking does not erase ModCategories, so a previously-categorised mod
        // will reappear in its original tab.)
        if (!_config.ModCategories.ContainsKey(modDir))
            _config.ModCategories[modDir] = "Other";

        _config.Save();
        MarkCacheDirty();
    }

    /// <summary>
    /// Async implementation of the Perform action:
    /// 1. Deactivates ALL currently active mods (plugin-wide) to prevent animation/audio conflicts.
    /// 2. Reads the current permanent options for this mod (to preserve user's config).
    /// 3. Applies temporary Penumbra settings: enabled=true, priority=99.
    /// 4. Waits 300ms for Penumbra to apply the changes to the character.
    /// 5. Executes the emote command on the game thread.
    ///
    /// THREADING: This method is called from the ImGui draw thread. The 300ms await
    /// yields to the thread pool. The emote command is re-scheduled via Framework.RunOnTick
    /// to ensure it runs on the game thread.
    /// </summary>
    /// <param name="entry">The mod entry to activate and perform.</param>
    private async Task PerformDanceAsync(EmoteModEntry entry)
    {
        try
        {
            _log.Info($"[DanceLibrary] Performing: {entry.ModDisplayName} ({entry.EmoteCommand})");

            // Step 1: Deactivate ALL currently active mods before enabling the new one.
            // Limiting this to the same category is not enough — active mods from other
            // tabs can still conflict (audio bleeds over, animation layers interfere).
            foreach (var activeDir in _activeMods.ToList())
            {
                ResetMod(activeDir);
                _log.Debug($"[DanceLibrary] Deactivated mod before perform: {activeDir}");
            }

            // Step 2: Get the option selections to use for this mod's temporary settings.
            // Priority: plugin-stored overrides → current Penumbra permanent settings → empty.
            var options = new Dictionary<string, IReadOnlyList<string>>();
            if (_config.ModOptionOverrides.TryGetValue(entry.ModDirectory, out var storedOptions))
            {
                foreach (var (group, selected) in storedOptions)
                {
                    // Skip groups with no option selected. Penumbra's SetTemporaryModSettingsPlayer
                    // rejects empty selection lists and returns a non-zero error code, which would
                    // prevent the mod from being enabled at all. Groups with empty selections
                    // arise when the user opens Settings but doesn't choose any option for a group
                    // (e.g., race-specific animation options left unchecked).
                    if (selected.Count > 0)
                        options[group] = selected;
                }
                _log.Debug($"[DanceLibrary] Perform: using plugin-stored options for {entry.ModDirectory}");
            }
            else
            {
                var collectionId = _penumbra.GetPlayerCollectionId();
                if (collectionId.HasValue)
                {
                    var currentSettings = _penumbra.GetCurrentModSettings(collectionId.Value, entry.ModDirectory);
                    if (currentSettings.HasValue)
                        foreach (var (group, selected) in currentSettings.Value.options)
                            options[group] = selected;
                }
                _log.Debug($"[DanceLibrary] Perform: using Penumbra current settings for {entry.ModDirectory}");
            }

            // Step 3: Apply temporary Penumbra settings (enabled=true, priority=99).
            var success = _penumbra.SetTemporaryModSettings(entry.ModDirectory, options);
            if (!success)
            {
                _log.Warning($"[DanceLibrary] SetTemporaryModSettings failed for: {entry.ModDirectory}");
            }
            else
            {
                _activeMods.Add(entry.ModDirectory);
            }

            // Step 4: Wait 300ms for Penumbra to apply the mod changes to the character.
            await Task.Delay(300);

            // Step 5: Execute the emote command on the game thread.
            // UNSAFE: ChatSender.SendCommand calls a game function — must be on game thread.
            await _framework.RunOnTick(() =>
            {
                _chatSender.SendCommand(entry.EmoteCommand);
            });

            _log.Info($"[DanceLibrary] Dance performed: {entry.EmoteCommand}");
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[DanceLibrary] PerformDanceAsync failed for: {entry.ModDisplayName}");
        }
    }

    // ── Cache Management ─────────────────────────────────────────────────────────

    /// <summary>
    /// Marks the tab content cache as dirty, scheduling a <see cref="RebuildTabCache"/>
    /// call on the next draw frame. Call this after any mutation that changes which entries
    /// appear in which tab / section, or changes their sort order (favorites, group membership,
    /// category assignment).
    /// </summary>
    private void MarkCacheDirty() => _tabCacheDirty = true;

    // ── Multi-Select Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Clears the multi-selection set and resets the range-select anchor.
    /// Called on tab switch, after bulk operations complete, and via "Clear selection".
    /// </summary>
    private void ClearSelection()
    {
        _selectedMods.Clear();
        _selectionAnchorDir = null;
    }

    /// <summary>
    /// Returns the category for a mod given only its directory name.
    /// Defaults to "Other" if unassigned. Used by bulk operations that work
    /// with directory strings rather than full EmoteModEntry objects.
    /// </summary>
    private string GetModCategoryByDir(string modDir) =>
        _config.ModCategories.TryGetValue(modDir, out var cat) ? cat : "Other";

    /// <summary>
    /// Returns the name of the group containing <paramref name="modDir"/>
    /// within <paramref name="category"/>, or null if it is ungrouped there.
    /// Used by MoveSelectedModsToGroup to find each mod's current group before moving.
    /// </summary>
    private string? GetModGroupInCategory(string modDir, string category)
    {
        foreach (var group in GetCategoryGroups(category))
            if (group.ModDirectories.Contains(modDir))
                return group.Name;
        return null;
    }

    /// <summary>
    /// Moves a single mod directory from its current location to a target group
    /// without relying on the drag-drop state field <see cref="_draggedModDir"/>.
    /// Does NOT call <see cref="_config.Save"/> or <see cref="MarkCacheDirty"/> —
    /// the caller batches those after processing all selected mods.
    /// </summary>
    /// <param name="modDir">The mod directory to move.</param>
    /// <param name="fromCategory">The mod's current category.</param>
    /// <param name="fromGroupName">The mod's current group name, or null if ungrouped.</param>
    /// <param name="toCategory">The target category tab.</param>
    /// <param name="toGroupName">The target group name within toCategory.</param>
    private void MoveModToGroup(string modDir, string fromCategory, string? fromGroupName,
                                 string toCategory, string toGroupName)
    {
        // Remove from source location (ungrouped order list or source group's list).
        var sourceList = GetOrderList(fromCategory, fromGroupName);
        sourceList.Remove(modDir);

        // Add to target group's list (append at end, no duplicates).
        var targetList = GetOrderList(toCategory, toGroupName);
        if (!targetList.Contains(modDir))
            targetList.Add(modDir);

        // Update category assignment if moving across tab boundaries.
        if (fromCategory != toCategory)
            _config.ModCategories[modDir] = toCategory;
    }

    /// <summary>
    /// Assigns every mod in <see cref="_selectedMods"/> to <paramref name="targetCategory"/>,
    /// saves config, rebuilds caches, and clears the selection.
    /// Called from the "Move to category" context-menu submenu.
    /// </summary>
    private void SetSelectedModsCategory(string targetCategory)
    {
        foreach (var dir in _selectedMods.ToList())
        {
            _config.ModCategories[dir] = targetCategory;
            _log.Debug($"[DanceLibrary] Bulk-category: {dir} → {targetCategory}");
        }
        _config.Save();
        MarkCacheDirty();
        ClearSelection();
    }

    /// <summary>
    /// Moves every mod in <see cref="_selectedMods"/> into <paramref name="toGroupName"/>
    /// within <paramref name="toCategory"/>, saves config, rebuilds caches, and clears
    /// the selection. Called from the "Move to group" context-menu submenu.
    /// Each mod's current category and group are resolved individually, so the operation
    /// works correctly even when selected mods span different categories or groups.
    /// </summary>
    private void MoveSelectedModsToGroup(string toCategory, string toGroupName)
    {
        foreach (var dir in _selectedMods.ToList())
        {
            var fromCategory  = GetModCategoryByDir(dir);
            var fromGroupName = GetModGroupInCategory(dir, fromCategory);
            MoveModToGroup(dir, fromCategory, fromGroupName, toCategory, toGroupName);
            _log.Debug($"[DanceLibrary] Bulk-group: {dir} → '{toGroupName}' in {toCategory}");
        }
        _config.Save();
        MarkCacheDirty();
        ClearSelection();
    }

    /// <summary>
    /// Rebuilds <see cref="_cachedTabDrawOrder"/>[<paramref name="category"/>] —
    /// an ordered list of mod directories matching the sequence they appear in the rendered UI:
    /// groups first (in group display order, mods within each group in their stored order),
    /// then ungrouped mods (in the order from <see cref="_cachedUngroupedOrdered"/>).
    /// Must be called after both _cachedUngroupedOrdered and group data are ready.
    /// Used by Shift+click range selection.
    /// </summary>
    private void RebuildTabDrawOrder(string category)
    {
        var order = new List<string>();

        // Grouped mods first, in group → mod order.
        foreach (var group in GetCategoryGroups(category))
            foreach (var dir in group.ModDirectories)
                if (!order.Contains(dir))
                    order.Add(dir);

        // Then ungrouped mods, in cached render order.
        if (_cachedUngroupedOrdered.TryGetValue(category, out var ungrouped))
            foreach (var entry in ungrouped)
                if (!order.Contains(entry.ModDirectory))
                    order.Add(entry.ModDirectory);

        _cachedTabDrawOrder[category] = order;
    }

    /// <summary>
    /// Rebuilds <see cref="_categories"/> from <see cref="BuiltInCategories"/> +
    /// <see cref="Configuration.CustomCategories"/> + "Other" (always last).
    /// Also clamps <see cref="_moveModeTargetIdx"/> to the new array bounds and
    /// invalidates the row width cache (a new wide tab name may require a wider category button).
    /// Called from the constructor and after any custom tab add, delete, or rename.
    /// </summary>
    private void RebuildCategories()
    {
        _categories = BuiltInCategories
            .Concat(_config.CustomCategories)
            .Append("Other")
            .ToArray();
        // Keep the Move Mode combo selection in bounds after tab list changes.
        _moveModeTargetIdx = Math.Clamp(_moveModeTargetIdx, 0, _categories.Length - 1);
        // Category button width may need to expand for a long custom tab name.
        _rowWidthsCached = false;
        MarkCacheDirty();
    }

    /// <summary>
    /// Adds a user-created tab to <see cref="Configuration.CustomCategories"/> and
    /// rebuilds <see cref="_categories"/>. No-op if a tab with the same name already exists
    /// (case-insensitive). Safe to call with any non-empty string.
    /// </summary>
    /// <param name="name">The new tab name. Must not be empty or match an existing tab.</param>
    private void AddCustomTab(string name)
    {
        // Guard: no duplicates (case-insensitive), no overwriting built-ins.
        if (Array.Exists(_categories, c => c.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return;

        _config.CustomCategories.Add(name);
        _config.Save();
        RebuildCategories();
        _log.Debug($"[DanceLibrary] Added custom tab: {name}");
    }

    /// <summary>
    /// Deletes a user-created tab from <see cref="Configuration.CustomCategories"/> and
    /// rebuilds <see cref="_categories"/>. Mods assigned to the deleted tab fall back to "Other"
    /// on the next render — their <see cref="Configuration.ModCategories"/> entries are left
    /// intact so re-creating the tab with the same name restores all assignments.
    /// </summary>
    /// <param name="name">The custom tab name to remove.</param>
    private void DeleteCustomTab(string name)
    {
        _config.CustomCategories.Remove(name);
        _config.Save();
        RebuildCategories();
        _log.Debug($"[DanceLibrary] Deleted custom tab: {name}");
    }

    /// <summary>
    /// Renames a user-created tab and migrates all associated config data so mod
    /// assignments, ungrouped order lists, and group definitions follow the new name.
    ///
    /// Migration covers:
    ///   - <see cref="Configuration.ModCategories"/> entries (mods pointing at oldName → newName)
    ///   - <see cref="Configuration.UngroupedOrder"/> dictionary key
    ///   - <see cref="Configuration.CategoryGroups"/> dictionary key
    /// </summary>
    /// <param name="oldName">Current custom tab name.</param>
    /// <param name="newName">New tab name. Must not match any existing tab (case-insensitive).</param>
    private void RenameCustomTab(string oldName, string newName)
    {
        var idx = _config.CustomCategories.IndexOf(oldName);
        if (idx < 0 || string.IsNullOrWhiteSpace(newName)) return;

        _config.CustomCategories[idx] = newName;

        // Migrate all mod category assignments that pointed at the old name.
        foreach (var key in _config.ModCategories.Keys.ToList())
            if (_config.ModCategories[key] == oldName)
                _config.ModCategories[key] = newName;

        // Migrate dictionary keys in UngroupedOrder and CategoryGroups.
        if (_config.UngroupedOrder.Remove(oldName, out var uo))
            _config.UngroupedOrder[newName] = uo;
        if (_config.CategoryGroups.Remove(oldName, out var cg))
            _config.CategoryGroups[newName] = cg;

        _config.Save();
        RebuildCategories();
        _log.Debug($"[DanceLibrary] Renamed tab: {oldName} → {newName}");
    }

    /// <summary>
    /// Rebuilds all tab content caches from the current scan data and configuration.
    /// Called at most once per mutation and once when a new scan reference is detected.
    ///
    /// What this does in a single O(n) pass:
    ///   1. Partitions all entries into per-category lists → <see cref="_cachedTabEntries"/>.
    ///   2. For each category, calls <see cref="UpdateUngroupedOrderForCategory"/> to append
    ///      newly discovered mods to <see cref="Configuration.UngroupedOrder"/> (may save config).
    ///   3. Builds the ready-to-render ordered ungrouped lists → <see cref="_cachedUngroupedOrdered"/>.
    ///   4. Clears <see cref="_tabCacheDirty"/> and stores the new list reference.
    /// </summary>
    /// <param name="allEntries">The current flat list of all scanned emote mod entries.</param>
    private void RebuildTabCache(List<EmoteModEntry> allEntries)
    {
        _log.Debug("[DanceLibrary] RebuildTabCache: rebuilding tab content cache");

        // Step 0: Deduplicate ModDirectories across all groups in every category.
        //
        // Two classes of duplicates are fixed here:
        //
        //   A. Within-group duplicates: the same mod directory appears more than once
        //      inside the same group's list. Old config data from before the add-path
        //      guards were added can contain these.
        //
        //   B. Cross-group duplicates: the same mod directory appears in more than one
        //      group within the same category. DrawGroupItems renders the mod once per
        //      group occurrence, so cross-group duplicates produce duplicate rows too.
        //
        // Fix: use a single per-category seen-set so the first group that claims a
        // directory keeps it; later groups silently lose it.
        var deduped = false;
        foreach (var cat in _categories)
        {
            // One seen-set covers ALL groups in this category — catches both
            // within-group duplicates and cross-group duplicates.
            var seenInCategory = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in GetCategoryGroups(cat))
            {
                var before = group.ModDirectories.Count;
                group.ModDirectories.RemoveAll(d => !seenInCategory.Add(d));
                if (group.ModDirectories.Count != before)
                {
                    var removed = before - group.ModDirectories.Count;
                    _log.Warning($"[DanceLibrary] Removed {removed} duplicate(s) from group '{group.Name}' in category '{cat}'");
                    deduped = true;
                }
            }

            // Also deduplicate UngroupedOrder for this category.
            // Duplicate entries there cause OrderWithFavoritesFirst to expand each
            // mod's emote entries multiple times via SelectMany, producing duplicate rows.
            if (_config.UngroupedOrder.TryGetValue(cat, out var uo) && uo.Count > 1)
            {
                var before = uo.Count;
                var seenUo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                uo.RemoveAll(d => !seenUo.Add(d));
                if (uo.Count != before)
                {
                    _log.Warning($"[DanceLibrary] Removed {before - uo.Count} duplicate(s) from UngroupedOrder for '{cat}'");
                    deduped = true;
                }
            }
        }
        if (deduped) _config.Save();

        // Step 1: Partition entries into per-category lists.
        // Mods absent from ModCategories default to "Other".
        // "Unblock" is a permanent virtual category for blocked mods — not in _categories,
        // but initialized here so GetModCategory can route blocked mods there without
        // falling through to the ContainsKey guard below.
        _cachedTabEntries.Clear();
        _cachedTabEntries["Unblock"] = new List<EmoteModEntry>();
        foreach (var cat in _categories)
            _cachedTabEntries[cat] = new List<EmoteModEntry>();

        foreach (var entry in allEntries)
        {
            var cat = GetModCategory(entry);
            // If cat is unknown (e.g. a deleted custom tab), fall back to "Other".
            // "Unblock" is always initialized above, so blocked mods land there correctly.
            if (!_cachedTabEntries.ContainsKey(cat)) cat = "Other";
            _cachedTabEntries[cat].Add(entry);
        }

        // Step 2 + 3: For each category, sync UngroupedOrder and build the ordered ungrouped list.
        _cachedUngroupedOrdered.Clear();
        foreach (var cat in _categories)
        {
            var tabEntries = _cachedTabEntries[cat];

            // Append any newly discovered mods to UngroupedOrder (may save config once if new mods found).
            UpdateUngroupedOrderForCategory(tabEntries, cat);

            // Build the pre-sorted ordered list for DrawUngroupedSection to virtual-scroll over.
            var ungroupedEntries = GetUngroupedEntries(tabEntries, cat);
            _cachedUngroupedOrdered[cat] = OrderWithFavoritesFirst(ungroupedEntries, cat, groupName: null);
        }

        // Step 4: Mark cache as clean and record which list we built from.
        _cachedEntriesRef = allEntries;
        _tabCacheDirty    = false;

        // Step 5: Build the flat render-row lists for virtual scrolling.
        // This depends on _cachedUngroupedOrdered (built in step 3) so it must run last.
        RebuildRenderRows();
    }

    /// <summary>
    /// Rebuilds the per-category flat render-row lists used by <see cref="DrawUngroupedSection"/>.
    ///
    /// Iterates over each category's <see cref="_cachedUngroupedOrdered"/> list and groups
    /// consecutive entries that share the same <see cref="EmoteModEntry.ModDirectory"/> into
    /// "mod groups" (they are guaranteed to be adjacent because <see cref="OrderWithFavoritesFirst"/>
    /// sorts by <c>ModDisplayName</c>, and all emote entries for a single mod share the same name).
    ///
    /// Each mod group becomes:
    ///   • A <see cref="RowKind.Single"/> row if the mod has exactly one emote override.
    ///   • A <see cref="RowKind.MultiParent"/> row plus zero or more <see cref="RowKind.MultiChild"/>
    ///     rows (when the directory is in <see cref="_expandedMods"/>) if there are multiple overrides.
    ///
    /// Call this after <see cref="RebuildTabCache"/> AND after toggling expand state (user clicks ▶/▼).
    /// It is cheap — no IPC calls or sorts; it only iterates the already-sorted ordered list.
    /// </summary>
    private void RebuildRenderRows()
    {
        _cachedRenderRows.Clear();
        foreach (var cat in _categories)
        {
            if (!_cachedUngroupedOrdered.TryGetValue(cat, out var ordered))
            {
                _cachedRenderRows[cat] = new List<RenderRow>();
                continue;
            }

            var rows = new List<RenderRow>(ordered.Count);
            var i = 0;
            while (i < ordered.Count)
            {
                // Scan the contiguous block of entries sharing the same ModDirectory.
                var dir = ordered[i].ModDirectory;
                var j   = i + 1;
                while (j < ordered.Count && ordered[j].ModDirectory == dir) j++;
                var n = j - i;

                if (n == 1)
                {
                    rows.Add(new RenderRow(ordered[i], RowKind.Single));
                }
                else
                {
                    // Parent row always present; child rows follow only when expanded.
                    rows.Add(new RenderRow(ordered[i], RowKind.MultiParent, n));
                    if (_expandedMods.Contains(dir))
                        for (var k = i; k < j; k++)
                            rows.Add(new RenderRow(ordered[k], RowKind.MultiChild));
                }
                i = j;
            }
            _cachedRenderRows[cat] = rows;
        }

        // Rebuild the draw-order lists used by Shift+click range selection.
        // Must run after _cachedUngroupedOrdered is ready (built in RebuildTabCache).
        foreach (var cat in _categories)
            RebuildTabDrawOrder(cat);

        _log.Debug("[DanceLibrary] RebuildRenderRows: rebuilt render lists for all categories");
    }

    /// <summary>
    /// Caches the pixel widths of all row buttons and the emote column.
    /// Called from <see cref="DrawEntryRow"/> on the first draw frame after construction.
    /// These widths are stable for the plugin session (ImGui style doesn't change at runtime),
    /// so calling <see cref="ImGui.CalcTextSize"/> once and reusing avoids 4+ native calls
    /// per row per frame (previously ~9,320 calls/frame at 2,330 entries).
    /// </summary>
    private void EnsureRowWidthsCached()
    {
        if (_rowWidthsCached) return;

        var fpX = ImGui.GetStyle().FramePadding.X;
        _cachedResetW    = ImGui.CalcTextSize("Reset").X    + fpX * 2;
        _cachedPnbW      = ImGui.CalcTextSize("Pnb").X      + fpX * 2;
        _cachedSettingsW = ImGui.CalcTextSize("Settings").X + fpX * 2;
        _cachedPerformW  = ImGui.CalcTextSize("Perform").X  + fpX * 2;
        // Category button: wide enough for the longest category name.
        _cachedCatBtnW   = _categories.Max(c => ImGui.CalcTextSize(c).X) + fpX * 2 + 4f;
        _rowWidthsCached = true;

        _log.Debug($"[DanceLibrary] Row widths cached: " +
                   $"Reset={_cachedResetW:F0} Pnb={_cachedPnbW:F0} Settings={_cachedSettingsW:F0} " +
                   $"Perform={_cachedPerformW:F0} Cat={_cachedCatBtnW:F0}");
    }

    // ── IDisposable ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Disposes the main window. Clears all active mod state.
    /// Note: does NOT remove temporary Penumbra settings on dispose — they persist
    /// in Penumbra until the game session ends or the user manually resets them.
    /// This is intentional (emote stays active if plugin is temporarily unloaded).
    /// </summary>
    public void Dispose()
    {
        _activeMods.Clear();
        _log.Debug("[DanceLibrary] MainWindow disposed");
    }
}
