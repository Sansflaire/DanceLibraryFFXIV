/// <summary>
/// Scans all Penumbra mods and builds the list of <see cref="EmoteModEntry"/> objects
/// displayed in the Dance Library UI.
///
/// Responsibilities:
///   - Calls <see cref="PenumbraIpc.GetModList"/> to enumerate all installed mods.
///   - For each mod, calls <see cref="PenumbraIpc.GetChangedItems"/> to find emote overrides.
///   - Parses Penumbra's "Emote: /command" changed-item keys to extract emote commands.
///   - Classifies each emote as a dance or non-dance via <see cref="EmoteData.IsDance"/>.
///   - Checks if each mod has configurable options via <see cref="PenumbraIpc.GetAvailableModSettings"/>.
///   - Returns one <see cref="EmoteModEntry"/> per (mod × emote) pair.
///
/// Performance note:
///   ScanMods() makes 1-2 IPC calls per mod. For 500 mods this is ~500-1000
///   in-process IPC calls, typically completing in under a second. The method
///   is designed to be run on a background thread (see MainWindow.StartScan).
/// </summary>

using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace DanceLibraryFFXIV;

/// <summary>
/// Scans Penumbra mods for emote animation overrides and produces
/// a flat list of <see cref="EmoteModEntry"/> objects for the UI.
/// </summary>
public sealed class ModScanner
{
    // ── Dependencies ─────────────────────────────────────────────────────────────

    /// <summary>Penumbra IPC bridge used for all mod data queries.</summary>
    private readonly PenumbraIpc _penumbra;

    /// <summary>Logger for scan progress and errors.</summary>
    private readonly IPluginLog _log;

    // ── Constructor ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new ModScanner.
    /// </summary>
    /// <param name="penumbra">Penumbra IPC bridge used to query mod data.</param>
    /// <param name="log">Plugin logger for scan progress and error reporting.</param>
    public ModScanner(PenumbraIpc penumbra, IPluginLog log)
    {
        _penumbra = penumbra;
        _log      = log;
    }

    // ── Scanning ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans all Penumbra mods and returns entries for every mod that overrides
    /// at least one in-game emote animation.
    ///
    /// The result is a flat list: if a mod overrides multiple emotes, it appears
    /// multiple times — once per emote. This allows the user to perform each
    /// dance variant independently.
    ///
    /// THREADING: Safe to call from a background thread — all Penumbra IPC calls
    /// are in-process and thread-safe. The caller (MainWindow.StartScan) runs this
    /// on a Task thread and then updates the UI lists from the result.
    /// </summary>
    /// <returns>
    /// List of <see cref="EmoteModEntry"/> objects, one per (mod, emote) pair.
    /// Returns an empty list if Penumbra is unavailable or no emote mods are found.
    /// </returns>
    public List<EmoteModEntry> ScanMods()
    {
        var results = new List<EmoteModEntry>();

        // Guard: Penumbra must be available to scan.
        if (!_penumbra.IsAvailable)
        {
            _log.Warning("[DanceLibrary] ModScanner: Penumbra not available — skipping scan");
            return results;
        }

        // Step 1: Get all installed Penumbra mods.
        // Returns (directoryName → displayName) pairs.
        var modList = _penumbra.GetModList();
        if (modList == null || modList.Count == 0)
        {
            _log.Info("[DanceLibrary] ModScanner: no mods found");
            return results;
        }

        _log.Info($"[DanceLibrary] ModScanner: scanning {modList.Count} mods...");
        var emoteModCount = 0;

        foreach (var (modDirectory, displayName) in modList)
        {
            // Step 2: Get the items this mod changes.
            // We look for keys in the format "Emote: /command".
            var changedItems = _penumbra.GetChangedItems(modDirectory);
            if (changedItems == null || changedItems.Count == 0)
                continue; // Mod has no declared changed items — skip.

            // Step 3: Find all emote changes in this mod.
            var emoteEntries = ExtractEmoteEntries(modDirectory, displayName, changedItems);
            if (emoteEntries.Count == 0)
                continue; // No emote changes in this mod.

            // Step 4: Check if the mod has configurable Penumbra option groups.
            // This determines whether the Settings button is enabled in the UI.
            var availableOptions = _penumbra.GetAvailableModSettings(modDirectory);
            var hasOptions = availableOptions != null && availableOptions.Count > 0;

            // Step 5: Build EmoteModEntry objects for each emote this mod affects.
            foreach (var (emoteCommand, emoteDisplayName, isDance) in emoteEntries)
            {
                results.Add(new EmoteModEntry
                {
                    ModDirectory      = modDirectory,
                    ModDisplayName    = displayName,
                    EmoteCommand      = emoteCommand,
                    EmoteDisplayName  = emoteDisplayName,
                    IsDance           = isDance,
                    HasOptions        = hasOptions,
                    IsActive          = false, // Always starts inactive; tracked by MainWindow
                });
                emoteModCount++;
            }
        }

        _log.Info($"[DanceLibrary] ModScanner: found {emoteModCount} emote mod entries " +
                  $"({results.FindAll(e => e.IsDance).Count} dances, " +
                  $"{results.FindAll(e => !e.IsDance).Count} other)");

        return results;
    }

    // ── Private Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the changed-items dictionary from Penumbra and extracts emote entries.
    /// </summary>
    /// <param name="modDirectory">Mod folder name (for logging).</param>
    /// <param name="displayName">Mod display name (for logging).</param>
    /// <param name="changedItems">
    /// Raw changed items dictionary from <c>Penumbra.GetChangedItems.V5</c>.
    /// Keys look like "Emote: /sit", "Equipment: Hempen Camise", etc.
    /// </param>
    /// <returns>
    /// List of (emoteCommand, emoteDisplayName, isDance) tuples for each emote
    /// this mod overrides. May be empty if the mod doesn't change any emotes.
    /// </returns>
    private List<(string command, string displayName, bool isDance)> ExtractEmoteEntries(
        string modDirectory,
        string displayName,
        Dictionary<string, object?> changedItems)
    {
        var entries = new List<(string, string, bool)>();

        // Prefix used by Penumbra for emote changed items.
        // Example key: "Emote: /harvestdance"
        const string emotePrefix = "Emote: ";

        // Guard against duplicate emote entries within the same mod.
        //
        // There are two classes of duplicates to handle:
        //
        //   1. Same execute command: Penumbra reports the same emote under multiple key
        //      formats (e.g. "Emote: /bow" and "Emote: Bow"). Both normalize to "/bow".
        //
        //   2. Same display name (alias pairs): Penumbra reports both command aliases for
        //      emotes that have short forms (e.g. "Emote: /golddance" AND "Emote: /gdance",
        //      "Emote: /harvestdance" AND "Emote: /hdance"). These produce different execute
        //      commands but the same display name ("Gold Dance", "Harvest Dance"). Without
        //      dedup both aliases produce separate rows in the UI.
        //
        // Deduplicating by display name covers both cases: two entries with the same
        // display name in the same mod are the same emote regardless of which alias
        // Penumbra happened to report.
        var seenDisplayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, _) in changedItems)
        {
            // Filter to only emote-related changes.
            if (!key.StartsWith(emotePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Extract the raw command part after "Emote: ".
            var rawCommand = key.Substring(emotePrefix.Length).Trim();
            if (string.IsNullOrEmpty(rawCommand))
            {
                _log.Debug($"[DanceLibrary] Skipping empty emote key in mod: {displayName}");
                continue;
            }

            // Normalize to lowercase with leading slash (e.g., "/harvestdance").
            // EmoteData.NormalizeCommand handles both "/cmd" and "cmd" formats.
            var normalizedCommand = EmoteData.NormalizeCommand(rawCommand);

            // Resolve the actual game command to execute.
            // Penumbra reports emote keys using display-name format with spaces
            // (e.g. "Emote: /gold dance"). GetExecuteCommand strips spaces and
            // applies any explicit overrides to get the real command ("/golddance").
            var executeCommand = EmoteData.GetExecuteCommand(normalizedCommand);

            // Compute the display name before the dedup check so we can dedup by name.
            // Get the display name from the normalized Penumbra key (which may contain spaces)
            // so that space-separated forms like "/sit on ground" → "Sit On Ground"
            // without needing dictionary entries for every possible emote.
            var emoteName = EmoteData.GetDisplayName(normalizedCommand);

            // Skip if this display name was already added for this mod.
            // This catches both:
            //   - Same execute command (e.g. "Emote: /bow" and "Emote: Bow" both → "/bow")
            //   - Alias pairs (e.g. "Emote: /golddance" and "Emote: /gdance" both → "Gold Dance")
            if (!seenDisplayNames.Add(emoteName))
            {
                _log.Debug($"[DanceLibrary] Skipping duplicate emote '{emoteName}' (execute='{executeCommand}') in mod: {displayName} (Penumbra key: {key})");
                continue;
            }

            // Classify as dance using the execute command (canonical no-space form).
            var isDance = EmoteData.IsDance(executeCommand);

            _log.Debug($"[DanceLibrary] Found emote mod: [{displayName}] → penumbra={normalizedCommand}, execute={executeCommand}, display={emoteName}, isDance={isDance}");

            entries.Add((executeCommand, emoteName, isDance));
        }

        return entries;
    }
}
