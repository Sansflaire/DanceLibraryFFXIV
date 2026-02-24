/// <summary>
/// ImGui popup window for editing a Penumbra mod's option groups.
///
/// Responsibilities:
///   - Opens as a floating popup (not docked) when the user clicks the Settings button.
///   - Reads available option groups from Penumbra via <see cref="PenumbraIpc"/>.
///   - Populates initial selections from <see cref="Configuration.ModOptionOverrides"/>
///     (falling back to Penumbra's current permanent settings if no override is stored).
///   - Renders single-select groups as radio buttons; multi-select groups as checkboxes.
///   - Saves the user's selections to <see cref="Configuration.ModOptionOverrides"/>
///     when the user clicks Apply (does NOT write to Penumbra's permanent collection).
///   - Saved options are applied the next time the user clicks Perform in MainWindow.
///
/// Design rationale:
///   Saving to the plugin config (not Penumbra) avoids the multi-select toggle bug
///   where calling TrySetModSetting on an already-enabled option would toggle it OFF.
///   It also allows the user to configure "what options to use when I perform this dance"
///   without permanently altering their Penumbra collection.
///
/// Dependencies:
///   - <see cref="Configuration"/> for storing option overrides.
///   - <see cref="PenumbraIpc"/> for reading available and current options.
/// </summary>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace DanceLibraryFFXIV.Windows;

/// <summary>
/// Floating ImGui window for editing the Penumbra option groups of a specific mod.
/// </summary>
public sealed class ModSettingsWindow : IDisposable
{
    // ── Dependencies ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Plugin configuration that stores per-mod option overrides.
    /// Apply writes selections here; Open reads from here first.
    /// </summary>
    private readonly Configuration _config;

    /// <summary>Penumbra IPC bridge for reading available and current options.</summary>
    private readonly PenumbraIpc _penumbra;

    /// <summary>Logger for settings changes and errors.</summary>
    private readonly IPluginLog _log;

    // ── Window State ─────────────────────────────────────────────────────────────

    /// <summary>
    /// True when the window should be rendered. Set by <see cref="Open"/> and
    /// cleared when the user clicks Cancel, Apply, or closes the window.
    /// </summary>
    public bool IsVisible { get; private set; }

    /// <summary>
    /// The mod entry currently being edited. Set when <see cref="Open"/> is called.
    /// Null when the window is not visible.
    /// </summary>
    private EmoteModEntry? _entry;

    /// <summary>
    /// Available option groups for the current mod, loaded from Penumbra when Open() is called.
    /// Key = group name, Value = (optionNames[], groupType: 0=single-select, 1=multi-select).
    /// Null if the mod has no options or loading failed.
    /// </summary>
    private IReadOnlyDictionary<string, (string[] Options, int GroupType)>? _availableOptions;

    /// <summary>
    /// Working copy of the user's option selections. Modified by radio/checkbox interactions
    /// before Apply is clicked. Maps group name to list of selected option names.
    /// For single-select groups: at most one option. For multi-select: zero or more.
    /// </summary>
    private Dictionary<string, List<string>> _pendingSelections = new();

    /// <summary>
    /// Current player collection ID, fetched when the window opens.
    /// Required for TrySetModSetting (permanent changes) and GetCurrentModSettings.
    /// Null if the player object is not valid or Penumbra is unavailable.
    /// </summary>
    private Guid? _collectionId;

    /// <summary>
    /// Status message shown below the option groups (e.g., "Settings applied!").
    /// Cleared when the window opens or closes.
    /// </summary>
    private string _statusMessage = string.Empty;

    /// <summary>
    /// Color for the status message. Green for success, red for error.
    /// </summary>
    private Vector4 _statusColor = Vector4.One;

    // ── Colors ───────────────────────────────────────────────────────────────────

    /// <summary>Green color used for success status messages.</summary>
    private static readonly Vector4 ColorSuccess = new(0.4f, 1f, 0.4f, 1f);

    /// <summary>Red/orange color used for error status messages.</summary>
    private static readonly Vector4 ColorError = new(1f, 0.4f, 0.3f, 1f);

    /// <summary>Yellow/gold color for filled star rating buttons.</summary>
    private static readonly Vector4 ColorStarFilled = new(1f, 0.85f, 0.3f, 1f);

    /// <summary>Muted grey for unfilled star rating buttons.</summary>
    private static readonly Vector4 ColorStarEmpty = new(0.45f, 0.45f, 0.45f, 1f);

    // ── Constructor ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new ModSettingsWindow.
    /// </summary>
    /// <param name="config">Plugin configuration for storing per-mod option overrides.</param>
    /// <param name="penumbra">Penumbra IPC bridge for reading available and current options.</param>
    /// <param name="log">Plugin logger.</param>
    public ModSettingsWindow(Configuration config, PenumbraIpc penumbra, IPluginLog log)
    {
        _config   = config;
        _penumbra = penumbra;
        _log      = log;
    }

    // ── Public API ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the settings window for the given mod entry.
    /// Loads the mod's available options and current selections from Penumbra.
    /// If the mod has no options, the window still opens but shows a message.
    /// </summary>
    /// <param name="entry">The mod entry to edit settings for.</param>
    public void Open(EmoteModEntry entry)
    {
        _entry          = entry;
        _statusMessage  = string.Empty;
        _pendingSelections.Clear();

        // Load available option groups from Penumbra.
        // Cast to the concrete tuple type we'll use internally.
        var raw = _penumbra.GetAvailableModSettings(entry.ModDirectory);
        _availableOptions = raw != null
            ? new Dictionary<string, (string[], int)>(raw)
            : null;

        // Load current collection ID (needed to read Penumbra's current settings as fallback).
        _collectionId = _penumbra.GetPlayerCollectionId();

        // Load initial selections for the UI.
        // Priority: plugin-stored overrides → Penumbra current permanent settings → empty.
        if (_config.ModOptionOverrides.TryGetValue(entry.ModDirectory, out var storedOptions))
        {
            // User has previously configured options for this mod via this window.
            // Start with those so their saved choices are already checked.
            _pendingSelections = storedOptions.ToDictionary(
                kvp => kvp.Key,
                kvp => new List<string>(kvp.Value)); // defensive copy
            _log.Debug($"[DanceLibrary] Settings window: loaded plugin-stored options for {entry.ModDisplayName}");
        }
        else if (_collectionId.HasValue)
        {
            // No stored overrides yet — fall back to whatever Penumbra currently has
            // for this mod so the UI shows a sensible starting state.
            var current = _penumbra.GetCurrentModSettings(_collectionId.Value, entry.ModDirectory);
            if (current.HasValue)
            {
                _pendingSelections = current.Value.options
                    .ToDictionary(
                        kvp => kvp.Key,
                        kvp => new List<string>(kvp.Value)); // defensive copy
            }
            _log.Debug($"[DanceLibrary] Settings window: loaded Penumbra current options for {entry.ModDisplayName}");
        }

        IsVisible = true;
        _log.Debug($"[DanceLibrary] Settings window opened for: {entry.ModDisplayName}");
    }

    /// <summary>
    /// Closes the settings window without applying changes.
    /// </summary>
    public void Close()
    {
        IsVisible      = false;
        _entry         = null;
        _statusMessage = string.Empty;
    }

    /// <summary>
    /// Toggles the settings window for the given mod entry.
    /// If the window is already open for the same mod (matched by mod directory), it closes.
    /// Otherwise, it opens for the given mod — replacing any previously open window.
    /// This lets the Settings button act as a toggle so users can dismiss the window
    /// without moving their mouse to the window's X button.
    /// </summary>
    /// <param name="entry">The mod entry whose settings to toggle.</param>
    public void Toggle(EmoteModEntry entry)
    {
        if (IsVisible && _entry?.ModDirectory == entry.ModDirectory)
            Close();
        else
            Open(entry);
    }

    // ── ImGui Draw ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the settings window. Call this from the plugin's Draw event every frame.
    /// The window is only rendered when <see cref="IsVisible"/> is true.
    /// </summary>
    public void Draw()
    {
        // Guard: only draw when visible and we have a valid entry.
        if (!IsVisible || _entry == null) return;

        // --- Set window size constraints before Begin ---
        // Min: 350×200, Max: 600×600 (resize allowed by user)
        ImGui.SetNextWindowSizeConstraints(new Vector2(350, 200), new Vector2(600, 600));
        ImGui.SetNextWindowSize(new Vector2(420, 320), ImGuiCond.FirstUseEver);

        // --- Main window ---
        // Title includes the mod name for context. "###DLSettings" is the stable ImGui ID.
        var title = $"{_entry.ModDisplayName} — Settings###DLSettings";
        bool open = true;
        if (!ImGui.Begin(title, ref open))
        {
            ImGui.End();
            if (!open) Close();
            return;
        }

        try
        {
            DrawWindowContents();
        }
        finally
        {
            // Always call End() to match Begin(), even if an exception occurs.
            ImGui.End();
        }

        // Close if the user clicked the X button on the window.
        if (!open) Close();
    }

    /// <summary>
    /// Draws the interior contents of the settings window (mod name header,
    /// option groups, status message, and Apply/Cancel buttons).
    /// Called only within a Begin/End block.
    /// </summary>
    private void DrawWindowContents()
    {
        if (_entry == null) return;

        // --- Header: mod name and emote ---
        ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.5f, 1f), _entry.ModDisplayName);
        ImGui.SameLine();
        ImGui.TextDisabled($"({_entry.EmoteDisplayName})");
        ImGui.Separator();

        // --- Star rating section ---
        // 1–5 clickable star buttons. Clicking the current highest-filled star clears
        // the rating back to 0; clicking any other star sets the rating to that value.
        // Stars are stored in Configuration.ModStarRatings and used only for filtering
        // in the main window — they do NOT affect sort order.
        ImGui.Spacing();
        ImGui.TextDisabled("Rating:");
        DrawStarRating(_entry.ModDirectory);
        ImGui.Spacing();
        ImGui.Separator();

        // --- Guard: no options available ---
        if (_availableOptions == null || _availableOptions.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped("This mod has no configurable option groups in Penumbra.");
            ImGui.Spacing();
            DrawCloseButton();
            return;
        }

        // --- Guard: collection not found ---
        if (!_collectionId.HasValue)
        {
            ImGui.Spacing();
            ImGui.TextColored(ColorError, "Could not determine your Penumbra collection.");
            ImGui.TextWrapped("Make sure your character is loaded and Penumbra is active.");
            ImGui.Spacing();
            DrawCloseButton();
            return;
        }

        // --- Option groups ---
        // Render each option group as a labelled section.
        ImGui.Spacing();
        DrawOptionGroups();
        ImGui.Spacing();

        // --- Status message (success/error from last Apply) ---
        if (!string.IsNullOrEmpty(_statusMessage))
        {
            ImGui.TextColored(_statusColor, _statusMessage);
            ImGui.Spacing();
        }

        // --- Note explaining how Apply works ---
        // Settings are stored in the plugin config (not written to Penumbra directly).
        // They are applied to the mod the next time the user clicks Perform.
        ImGui.TextDisabled("Saved to plugin. Click Perform to apply these options in-game.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // --- Apply / Close buttons ---
        DrawApplyButton();
        ImGui.SameLine();
        DrawCloseButton();
    }

    /// <summary>
    /// Renders all option groups from <see cref="_availableOptions"/>.
    /// Single-select groups render as radio buttons (one selection per group).
    /// Multi-select groups render as checkboxes (zero or more selections).
    /// </summary>
    private void DrawOptionGroups()
    {
        if (_availableOptions == null) return;

        var groupIndex = 0;
        foreach (var (groupName, (options, groupType)) in _availableOptions)
        {
            // --- Group header (bold-style via TextColored) ---
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), groupName);

            // Get current selections for this group (may be empty if never set).
            if (!_pendingSelections.TryGetValue(groupName, out var selected))
            {
                selected = new List<string>();
                _pendingSelections[groupName] = selected;
            }

            // --- Render options based on group type ---
            if (groupType == 0)
            {
                // Single-select group: render as radio buttons.
                DrawSingleSelectGroup(groupName, options, selected, groupIndex);
            }
            else
            {
                // Multi-select group: render as checkboxes.
                DrawMultiSelectGroup(groupName, options, selected, groupIndex);
                ImGui.TextDisabled("  (multi-select)");
            }

            ImGui.Spacing();
            groupIndex++;
        }
    }

    /// <summary>
    /// Renders a single-select option group as inline radio buttons.
    /// Clicking one option deselects all others in the group.
    /// </summary>
    /// <param name="groupName">Display name of the option group.</param>
    /// <param name="options">Array of option names in this group.</param>
    /// <param name="selected">Current selection list (modified in-place on click).</param>
    /// <param name="groupIndex">Index used to make ImGui IDs unique per group.</param>
    private void DrawSingleSelectGroup(
        string groupName,
        string[] options,
        List<string> selected,
        int groupIndex)
    {
        for (var i = 0; i < options.Length; i++)
        {
            var option    = options[i];
            var isChecked = selected.Count > 0 && selected[0] == option;

            // Radio button: unique ID = ##radio_group_option to avoid ImGui ID conflicts.
            if (ImGui.RadioButton($"{option}##radio_{groupIndex}_{i}", isChecked))
            {
                // User clicked this option — update the selection (single-select).
                selected.Clear();
                selected.Add(option);
            }

            // Keep options on the same line for compact display (if they fit).
            // We always use NewLine-style layout (one per line) for clarity.
        }
    }

    /// <summary>
    /// Renders a multi-select option group as checkboxes.
    /// Each option can be independently toggled.
    /// </summary>
    /// <param name="groupName">Display name of the option group.</param>
    /// <param name="options">Array of option names in this group.</param>
    /// <param name="selected">Current selection list (modified in-place on toggle).</param>
    /// <param name="groupIndex">Index used to make ImGui IDs unique per group.</param>
    private void DrawMultiSelectGroup(
        string groupName,
        string[] options,
        List<string> selected,
        int groupIndex)
    {
        for (var i = 0; i < options.Length; i++)
        {
            var option    = options[i];
            var isChecked = selected.Contains(option);

            // Checkbox: unique ID = ##cb_group_option to avoid ImGui ID conflicts.
            if (ImGui.Checkbox($"{option}##cb_{groupIndex}_{i}", ref isChecked))
            {
                // User toggled this option — update the multi-select list.
                if (isChecked)
                {
                    if (!selected.Contains(option)) selected.Add(option);
                }
                else
                {
                    selected.Remove(option);
                }
            }
        }
    }

    /// <summary>
    /// Renders 5 inline star SmallButtons for assigning a 1–5 rating to the given mod.
    /// Clicking star N when the current rating is already N clears the rating to 0.
    /// Clicking any other star sets the rating to that value.
    /// The rating is saved immediately to <see cref="Configuration.ModStarRatings"/> on click.
    /// </summary>
    /// <param name="modDirectory">The Penumbra mod directory name for the mod being rated.</param>
    private void DrawStarRating(string modDirectory)
    {
        var current = _config.ModStarRatings.GetValueOrDefault(modDirectory, 0);

        // Render 5 star buttons inline, colour-coded by fill state.
        for (var s = 1; s <= 5; s++)
        {
            ImGui.SameLine();
            var filled = s <= current;
            ImGui.PushStyleColor(ImGuiCol.Text, filled ? ColorStarFilled : ColorStarEmpty);

            // Unique ID keeps ImGui from collapsing the 5 buttons into one.
            if (ImGui.SmallButton($"{(filled ? "★" : "☆")}##star{s}"))
            {
                // Clicking the current top-filled star clears the rating; any lower/higher sets it.
                var newRating = s == current ? 0 : s;
                _config.ModStarRatings[modDirectory] = newRating;
                _config.Save();
                _log.Debug($"[DanceLibrary] Star rating set: {modDirectory} → {newRating}★");
            }

            ImGui.PopStyleColor();
        }
    }

    /// <summary>
    /// Renders the Apply button. When clicked, saves the pending option selections
    /// to <see cref="Configuration.ModOptionOverrides"/> for this mod.
    ///
    /// These saved options are used the next time the user clicks Perform:
    /// MainWindow reads them and passes them as the temporary mod settings, so the
    /// mod is activated with exactly these option choices.
    ///
    /// Note: this does NOT write to Penumbra's permanent collection. That design
    /// choice avoids the multi-select toggle bug (TrySetModSetting toggles options
    /// for multi-select groups, which could accidentally turn off already-enabled options).
    /// </summary>
    private void DrawApplyButton()
    {
        if (!ImGui.Button("Apply"))
            return;

        // Guard: need an entry and available options to save.
        if (_entry == null || _availableOptions == null)
        {
            _statusMessage = "Cannot save — mod data not available.";
            _statusColor   = ColorError;
            return;
        }

        // Save the pending selections to the plugin configuration.
        // Copy the dictionary so the stored snapshot is independent of _pendingSelections.
        var snapshot = _pendingSelections.ToDictionary(
            kvp => kvp.Key,
            kvp => new List<string>(kvp.Value)); // defensive copy per group

        _config.ModOptionOverrides[_entry.ModDirectory] = snapshot;
        _config.Save();

        _statusMessage = "Settings saved! Click Perform to apply them in-game.";
        _statusColor   = ColorSuccess;
        _log.Info($"[DanceLibrary] Option overrides saved for: {_entry.ModDisplayName} " +
                  $"({snapshot.Count} groups)");
    }

    /// <summary>
    /// Renders the Close (or Cancel) button, which closes the window without
    /// reverting any already-applied changes.
    /// </summary>
    private void DrawCloseButton()
    {
        if (ImGui.Button("Close"))
            Close();
    }

    // ── IDisposable ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Disposes the settings window. Clears any open state.
    /// </summary>
    public void Dispose()
    {
        IsVisible = false;
        _entry    = null;
    }
}
