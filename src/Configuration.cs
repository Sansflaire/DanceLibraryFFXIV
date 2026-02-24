/// <summary>
/// Persisted plugin configuration for DanceLibraryFFXIV.
/// Stored at: %APPDATA%\XIVLauncher\pluginConfigs\DanceLibraryFFXIV.json
///
/// Only holds UI state — scan results are never persisted (always fresh on scan).
/// If new settings are added in future versions, increment Version and add a
/// MigrateIfNeeded() call in the constructor.
/// </summary>

using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace DanceLibraryFFXIV;

/// <summary>
/// A user-created collapsible group within a category tab.
///
/// Responsibilities:
///   - Holds an ordered list of mod directory names assigned to this group.
///   - Tracks whether the group is currently collapsed in the UI.
///   - Serialized as part of <see cref="Configuration.CategoryGroups"/>.
///
/// Favorites within a group are not stored separately; they sort to the top
/// at render time based on <see cref="Configuration.FavoriteMods"/>.
/// </summary>
[Serializable]
public class ModGroup
{
    /// <summary>User-assigned display name for this group. Editable inline via the ✎ button.</summary>
    public string Name { get; set; } = "New Group";

    /// <summary>Whether this group is currently collapsed in the UI. Persisted across reloads.</summary>
    public bool IsCollapsed { get; set; } = false;

    /// <summary>
    /// Ordered list of mod directory names (as returned by Penumbra GetModList) in this group.
    /// The display order follows this list, with favorites sorted to the top at render time.
    /// </summary>
    public List<string> ModDirectories { get; set; } = new();
}

/// <summary>
/// Plugin configuration data, serialized to JSON by Dalamud.
/// Implements <see cref="IPluginConfiguration"/> so Dalamud can load/save it.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>
    /// Config file version. Increment this when adding fields that require
    /// migration logic. Currently at 1 (initial release).
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Whether the main Dance Library window is currently visible.
    /// Persisted so the window stays open/closed across plugin reloads.
    /// </summary>
    public bool IsMainWindowVisible { get; set; } = false;

    /// <summary>
    /// Per-mod option selections saved by the user via the Settings window.
    /// Key = Penumbra mod directory name (as returned by GetModList).
    /// Value = Dictionary of (groupName → list of selected option names).
    ///
    /// These are applied as the option overrides when temporarily activating a mod
    /// via Perform. If no entry exists for a mod, the current Penumbra permanent
    /// settings are used as a fallback.
    ///
    /// These are NOT written to Penumbra's permanent collection — they are the
    /// plugin's own per-mod option preferences.
    /// </summary>
    public Dictionary<string, Dictionary<string, List<string>>> ModOptionOverrides { get; set; } = new();

    /// <summary>
    /// User-assigned category for each mod, set via the category dropdown in the main window.
    /// Key = Penumbra mod directory name (as returned by GetModList).
    /// Value = category name: one of "Dance", "Emote", "NSFW", or "Other".
    ///
    /// Mods not present in this dictionary are displayed under the "Other" tab by default.
    /// Persisted across plugin reloads and game launches so categorizations are remembered.
    /// </summary>
    public Dictionary<string, string> ModCategories { get; set; } = new();

    /// <summary>
    /// Set of mod directory names the user has starred as favorites.
    /// Starred mods are sorted to the top of their section (group or ungrouped) within their
    /// category tab at render time. Starred state persists across reloads.
    /// </summary>
    public HashSet<string> FavoriteMods { get; set; } = new();

    /// <summary>
    /// User-created named groups per category tab.
    /// Key = tab name ("Dance", "Emote", "NSFW", or "Other").
    /// Value = ordered list of <see cref="ModGroup"/> objects for that tab.
    ///
    /// Each mod can appear in at most one group (or be ungrouped). Groups are collapsible,
    /// renameable, and reorderable via drag-and-drop in the UI.
    /// </summary>
    public Dictionary<string, List<ModGroup>> CategoryGroups { get; set; } = new();

    /// <summary>
    /// Render order for ungrouped mods within each category tab.
    /// Key = tab name ("Dance", "Emote", "NSFW", or "Other").
    /// Value = ordered list of mod directory names for mods NOT assigned to any group.
    ///
    /// Newly discovered mods (found in scan but absent from this list) are appended at
    /// the end. Mods absent from the current scan are skipped at render time but not
    /// removed (the user may just have Penumbra temporarily disabled).
    /// </summary>
    public Dictionary<string, List<string>> UngroupedOrder { get; set; } = new();

    /// <summary>
    /// User-created tab names, in display order.
    /// These appear as additional tabs between "NSFW" and "Other" in the Dance Library window.
    /// Built-in names ("Dance", "Emote", "NSFW", "Other") must never appear here.
    ///
    /// Mods assigned to a deleted custom tab automatically fall back to "Other" on the next
    /// render — GetModCategory returns "Other" for unknown category strings.
    /// This means deleting a tab is always safe (no manual migration needed).
    /// Re-creating a tab with the same name restores all previous mod assignments.
    /// </summary>
    public List<string> CustomCategories { get; set; } = new();

    /// <summary>
    /// Per-mod star ratings assigned by the user (0 = unrated, 1–5 stars).
    /// Key = Penumbra mod directory name (as returned by GetModList).
    ///
    /// Stars are a personal quality/preference rating used only for filtering in the main window.
    /// They do NOT affect sort order (that is governed by <see cref="FavoriteMods"/>).
    /// Entries with value 0 are functionally absent — <c>GetValueOrDefault(dir, 0)</c> is always safe.
    /// Stale entries for deleted mods are harmless and are never cleaned up automatically.
    /// </summary>
    public Dictionary<string, int> ModStarRatings { get; set; } = new();

    /// <summary>
    /// Set of mod directory names the user has blocked from the plugin.
    /// Blocked mods are invisible to the plugin: they do not appear in any category list,
    /// cannot be interacted with, and are skipped by all Reset operations.
    /// Blocking survives Refresh — the scan filters them out before populating _allEntries.
    /// Unblocking triggers a new scan so the mod reappears automatically.
    /// </summary>
    public HashSet<string> BlockedMods { get; set; } = new();

    /// <summary>
    /// Saves the current configuration to disk via Dalamud's config system.
    /// Call this after any mutation to persist the change.
    /// </summary>
    public void Save()
    {
        // Plugin.PluginInterface is the static reference injected at startup.
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
