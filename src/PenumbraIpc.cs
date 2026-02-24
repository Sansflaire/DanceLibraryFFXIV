/// <summary>
/// Bridge to Penumbra's Dalamud IPC endpoints.
///
/// Responsibilities:
///   - Wraps every Penumbra IPC call in try/catch so callers never crash.
///   - Exposes a clean, strongly-typed API so other classes don't touch IPC directly.
///   - Tracks <see cref="IsAvailable"/> by probing the Penumbra API version on init.
///
/// Requires Penumbra v5 (breaking API = 5) to be loaded.
/// All methods return null/false/0 if Penumbra is not available.
///
/// IPC notes:
///   - <c>GetIpcSubscriber&lt;...&gt;</c> always succeeds even if Penumbra is unloaded.
///   - Actual failures only surface when <c>InvokeFunc()</c> is called.
///   - That's why every invocation is wrapped in try/catch.
///   - "modDirectory" in Penumbra's API = folder name from GetModList, NOT a full path.
///   - "modName" can be "" (empty) to use directory-based lookup in all calls.
/// </summary>

using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DanceLibraryFFXIV;

/// <summary>
/// Wraps all Penumbra Dalamud IPC calls used by DanceLibraryFFXIV.
/// Dispose this class when the plugin unloads to clean up event subscriptions.
/// </summary>
public sealed class PenumbraIpc : IDisposable
{
    // ── Dependencies ────────────────────────────────────────────────────────────

    /// <summary>Dalamud plugin interface used to create IPC gate subscribers.</summary>
    private readonly IDalamudPluginInterface _pi;

    /// <summary>Logger for IPC errors and debug output.</summary>
    private readonly IPluginLog _log;

    // ── IPC Subscribers ─────────────────────────────────────────────────────────
    // These are created once in the constructor and reused on every call.
    // GetIpcSubscriber always succeeds — failure only surfaces on InvokeFunc/InvokeAction.

    /// <summary>
    /// IPC: "Penumbra.ApiVersion.V5" | () → (int breaking, int features)
    /// Returns Penumbra's current API version. We expect breaking == 5.
    /// </summary>
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<(int, int)> _apiVersion;

    /// <summary>
    /// IPC: "Penumbra.GetModList" | () → Dictionary&lt;string, string&gt;
    /// Returns all installed Penumbra mods as (directoryName → displayName) pairs.
    /// The directoryName is the key used in all other API calls.
    /// </summary>
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<Dictionary<string, string>> _getModList;

    /// <summary>
    /// IPC: "Penumbra.GetChangedItems.V5" | (string modDir, string modName) → Dictionary&lt;string, object?&gt;
    /// Returns the named items a mod changes. Keys for emote overrides look like "Emote: /sit".
    /// Values are game objects (or null for unknown paths). We only need the keys.
    /// </summary>
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<string, string, Dictionary<string, object?>> _getChangedItems;

    /// <summary>
    /// IPC: "Penumbra.GetAvailableModSettings.V5" | (string modDir, string modName) → IReadOnlyDictionary&lt;string, (string[], int)&gt;?
    /// Returns option groups for a mod. Key = group name; value = (option names[], groupType).
    /// groupType: 0 = single-select (radio buttons), 1 = multi-select (checkboxes).
    /// Returns null if the mod has no option groups.
    /// </summary>
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<string, string, IReadOnlyDictionary<string, (string[], int)>?> _getAvailableModSettings;

    /// <summary>
    /// IPC: "Penumbra.GetCurrentModSettings.V5"
    ///   | (Guid collectionId, string modDir, string modName, bool ignoreInheritance)
    ///   → (int errorCode, (bool enabled, int priority, Dictionary&lt;string, List&lt;string&gt;&gt; options, bool isInherited)?)
    /// Returns the current settings for a mod in a collection.
    /// errorCode == 0 on success. The inner tuple is null if the mod is not in the collection.
    /// </summary>
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<Guid, string, string, bool,
        (int, (bool, int, Dictionary<string, List<string>>, bool)?)> _getCurrentModSettings;

    /// <summary>
    /// IPC: "Penumbra.GetCollectionForObject.V5" | (int objectIndex) → (bool objectValid, bool individualSet, (Guid id, string name))
    /// Returns the effective Penumbra collection for a game object.
    /// objectIndex 0 = local player character.
    /// objectValid = false if the object doesn't exist in the object table.
    /// </summary>
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<int, (bool, bool, (Guid, string))> _getCollectionForObject;

    /// <summary>
    /// IPC: "Penumbra.SetTemporaryModSettingsPlayer.V5"
    ///   | (int objectIndex, string modDir, string modName,
    ///      (bool inherit, bool enabled, int priority, IReadOnlyDictionary&lt;string, IReadOnlyList&lt;string&gt;&gt; options),
    ///      string source, int key)
    ///   → int (PenumbraApiEc)
    /// Applies temporary mod settings to the collection assigned to a game object.
    /// objectIndex 0 = local player. source = plugin name shown to user. key = 0 (no lock).
    /// Returns 0 (Success) or 1 (NothingChanged) on success.
    /// Temporary settings are removed when RemoveTemporaryModSettingsPlayer is called
    /// or when Dalamud/the game session ends.
    /// </summary>
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<
        int, string, string,
        (bool, bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>),
        string, int,
        int> _setTemporaryModSettingsPlayer;

    /// <summary>
    /// IPC: "Penumbra.RemoveTemporaryModSettingsPlayer.V5"
    ///   | (int objectIndex, string modDir, string modName, int key) → int (PenumbraApiEc)
    /// Removes temporary mod settings previously set by SetTemporaryModSettingsPlayer.
    /// objectIndex 0 = local player. key must match the key used when setting (0 = no lock).
    /// The mod reverts to its permanent collection settings.
    /// </summary>
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<int, string, string, int, int>
        _removeTemporaryModSettingsPlayer;

    /// <summary>
    /// IPC: "Penumbra.TrySetModSetting.V5"
    ///   | (Guid collectionId, string modDir, string modName, string groupName, string optionName)
    ///   → int (PenumbraApiEc)
    /// Permanently sets a single option within a mod's option group in a collection.
    /// For single-select groups: replaces the current selection.
    /// For multi-select groups: toggles one option on. Call once per option to set multiple.
    /// Returns 0 (Success) or 1 (NothingChanged) on success.
    /// </summary>
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<Guid, string, string, string, string, int>
        _trySetModSetting;

    /// <summary>
    /// IPC: "Penumbra.TryInheritMod.V5"
    ///   | (Guid collectionId, string modDir, string modName, bool inherit) → int (PenumbraApiEc)
    /// Sets a mod's inheritance flag in a collection. When inherit=true, the mod uses
    /// the settings from the parent/default collection rather than explicit settings.
    /// This is equivalent to clicking "Inherit Settings" in Penumbra's collection editor.
    /// Call after RemoveTemporaryModSettings to fully revert a mod to its neutral state.
    /// Returns 0 (Success) or 1 (NothingChanged) on success.
    /// </summary>
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<Guid, string, string, bool, int>
        _tryInheritMod;

    /// <summary>
    /// IPC: "Penumbra.OpenMainWindow.V5"
    ///   | (int tab, string modDir, string modName) → int (PenumbraApiEc)
    /// Opens Penumbra's main window and navigates to the given mod's page.
    /// tab = 1 (TabType.Mods) opens directly to the mod list with the mod selected.
    /// If tab is not TabType.Mods, the mod will not be selected regardless of other parameters.
    /// Passing "" for modName uses directory-based lookup.
    /// </summary>
    private readonly Dalamud.Plugin.Ipc.ICallGateSubscriber<int, string, string, int>
        _openMainWindow;

    // ── Public State ─────────────────────────────────────────────────────────────

    /// <summary>
    /// True if Penumbra is currently loaded and reports API breaking version == 5.
    /// Checked on construction and can be rechecked at any time via <see cref="CheckAvailability"/>.
    /// All IPC methods check this and return null/false/0 when unavailable.
    /// </summary>
    public bool IsAvailable { get; private set; }

    // ── Constructor ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates all IPC subscribers and checks if Penumbra is available.
    /// </summary>
    /// <param name="pi">Dalamud plugin interface for creating IPC gate subscribers.</param>
    /// <param name="log">Plugin logger for error reporting.</param>
    public PenumbraIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        _pi  = pi;
        _log = log;

        // Create all IPC subscribers.
        // Note: GetIpcSubscriber<> ALWAYS succeeds — it does not verify Penumbra is loaded.
        // Actual failures only happen on InvokeFunc() calls below.

        // IPC: "Penumbra.ApiVersion.V5" | () → (int, int)
        _apiVersion = pi.GetIpcSubscriber<(int, int)>("Penumbra.ApiVersion.V5");

        // IPC: "Penumbra.GetModList" | () → Dictionary<string, string>
        _getModList = pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");

        // IPC: "Penumbra.GetChangedItems.V5" | (string, string) → Dictionary<string, object?>
        _getChangedItems = pi.GetIpcSubscriber<string, string, Dictionary<string, object?>>(
            "Penumbra.GetChangedItems.V5");

        // IPC: "Penumbra.GetAvailableModSettings.V5" | (string, string) → IReadOnlyDictionary<string, (string[], int)>?
        _getAvailableModSettings = pi.GetIpcSubscriber<string, string,
            IReadOnlyDictionary<string, (string[], int)>?>(
            "Penumbra.GetAvailableModSettings.V5");

        // IPC: "Penumbra.GetCurrentModSettings.V5"
        //   | (Guid, string, string, bool) → (int, (bool, int, Dictionary<string, List<string>>, bool)?)
        _getCurrentModSettings = pi.GetIpcSubscriber<
            Guid, string, string, bool,
            (int, (bool, int, Dictionary<string, List<string>>, bool)?)>(
            "Penumbra.GetCurrentModSettings.V5");

        // IPC: "Penumbra.GetCollectionForObject.V5" | (int) → (bool, bool, (Guid, string))
        _getCollectionForObject = pi.GetIpcSubscriber<int, (bool, bool, (Guid, string))>(
            "Penumbra.GetCollectionForObject.V5");

        // IPC: "Penumbra.SetTemporaryModSettingsPlayer.V5"
        //   | (int, string, string, (bool, bool, int, IRODict<string, IROList<string>>), string, int) → int
        _setTemporaryModSettingsPlayer = pi.GetIpcSubscriber<
            int, string, string,
            (bool, bool, int, IReadOnlyDictionary<string, IReadOnlyList<string>>),
            string, int,
            int>("Penumbra.SetTemporaryModSettingsPlayer.V5");

        // IPC: "Penumbra.RemoveTemporaryModSettingsPlayer.V5" | (int, string, string, int) → int
        _removeTemporaryModSettingsPlayer = pi.GetIpcSubscriber<int, string, string, int, int>(
            "Penumbra.RemoveTemporaryModSettingsPlayer.V5");

        // IPC: "Penumbra.TrySetModSetting.V5" | (Guid, string, string, string, string) → int
        _trySetModSetting = pi.GetIpcSubscriber<Guid, string, string, string, string, int>(
            "Penumbra.TrySetModSetting.V5");

        // IPC: "Penumbra.TryInheritMod.V5" | (Guid, string, string, bool) → int
        _tryInheritMod = pi.GetIpcSubscriber<Guid, string, string, bool, int>(
            "Penumbra.TryInheritMod.V5");

        // IPC: "Penumbra.OpenMainWindow.V5" | (int tab, string modDir, string modName) → int
        _openMainWindow = pi.GetIpcSubscriber<int, string, string, int>(
            "Penumbra.OpenMainWindow.V5");

        // Probe availability now so callers know if Penumbra is ready.
        CheckAvailability();
    }

    // ── Availability ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Probes Penumbra by calling <c>Penumbra.ApiVersion.V5</c>.
    /// Sets <see cref="IsAvailable"/> to true if the call succeeds and
    /// <c>breaking == 5</c> (the API version this plugin is built against).
    /// Call this again if you suspect Penumbra has been reloaded.
    /// </summary>
    public void CheckAvailability()
    {
        try
        {
            // IPC: "Penumbra.ApiVersion.V5" | () → (int breaking, int features)
            var (breaking, _) = _apiVersion.InvokeFunc();
            IsAvailable = breaking == 5;

            if (IsAvailable)
                _log.Info("[DanceLibrary] Penumbra available — API V5");
            else
                _log.Warning($"[DanceLibrary] Penumbra API version mismatch: breaking={breaking}, expected 5");
        }
        catch
        {
            // Penumbra is not loaded or has thrown — treat as unavailable.
            IsAvailable = false;
            _log.Debug("[DanceLibrary] Penumbra not available (ApiVersion probe failed)");
        }
    }

    // ── Mod Enumeration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all Penumbra mods as a dictionary of (directoryName → displayName).
    /// The directoryName is the key used in all other Penumbra IPC calls.
    /// Returns null if Penumbra is unavailable or the call fails.
    /// </summary>
    /// <returns>
    /// Dictionary where key = Penumbra mod folder name and value = display name,
    /// or null on failure.
    /// </returns>
    public Dictionary<string, string>? GetModList()
    {
        if (!IsAvailable) return null;
        try
        {
            // IPC: "Penumbra.GetModList" | () → Dictionary<string, string>
            return _getModList.InvokeFunc();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[DanceLibrary] GetModList IPC failed");
            return null;
        }
    }

    /// <summary>
    /// Returns the named items changed by a specific Penumbra mod.
    /// Keys follow Penumbra's format: "Emote: /sit", "Equipment: Hempen Camise", etc.
    /// Only keys starting with "Emote: " are relevant to DanceLibraryFFXIV.
    /// Returns null if Penumbra is unavailable or the call fails.
    /// </summary>
    /// <param name="modDirectory">
    /// The mod's folder name as returned by <see cref="GetModList"/>.
    /// </param>
    /// <returns>
    /// Dictionary of changed item descriptions, or null on failure.
    /// </returns>
    public Dictionary<string, object?>? GetChangedItems(string modDirectory)
    {
        if (!IsAvailable) return null;
        try
        {
            // IPC: "Penumbra.GetChangedItems.V5" | (string modDir, string modName) → Dictionary<string, object?>
            // Passing "" for modName uses directory-based lookup (modName is optional).
            return _getChangedItems.InvokeFunc(modDirectory, "");
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[DanceLibrary] GetChangedItems IPC failed for: {modDirectory}");
            return null;
        }
    }

    /// <summary>
    /// Returns the option groups available for a Penumbra mod.
    /// Used to determine if a mod has configurable settings (HasOptions) and
    /// to populate the <see cref="Windows.ModSettingsWindow"/> UI.
    /// Returns null if the mod has no options or if Penumbra is unavailable.
    /// </summary>
    /// <param name="modDirectory">
    /// The mod's folder name as returned by <see cref="GetModList"/>.
    /// </param>
    /// <returns>
    /// Dictionary where key = group name and value = (optionNames[], groupType).
    /// groupType 0 = single-select, 1 = multi-select.
    /// Returns null if no option groups or on failure.
    /// </returns>
    public IReadOnlyDictionary<string, (string[], int)>? GetAvailableModSettings(string modDirectory)
    {
        if (!IsAvailable) return null;
        try
        {
            // IPC: "Penumbra.GetAvailableModSettings.V5"
            //   | (string modDir, string modName) → IReadOnlyDictionary<string, (string[], int)>?
            // Passing "" for modName uses directory-based lookup.
            return _getAvailableModSettings.InvokeFunc(modDirectory, "");
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[DanceLibrary] GetAvailableModSettings IPC failed for: {modDirectory}");
            return null;
        }
    }

    // ── Collection Access ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the Penumbra collection ID assigned to the local player character.
    /// This is needed for TrySetModSetting (permanent settings) and GetCurrentModSettings.
    /// Returns null if the local player object is invalid or Penumbra is unavailable.
    /// </summary>
    /// <returns>
    /// The Guid of the effective Penumbra collection for the local player, or null.
    /// </returns>
    public Guid? GetPlayerCollectionId()
    {
        if (!IsAvailable) return null;
        try
        {
            // IPC: "Penumbra.GetCollectionForObject.V5"
            //   | (int objectIndex) → (bool objectValid, bool individualSet, (Guid id, string name))
            // objectIndex 0 = local player (game object table index 0).
            var (objectValid, _, effectiveCollection) = _getCollectionForObject.InvokeFunc(0);

            if (!objectValid)
            {
                _log.Debug("[DanceLibrary] GetCollectionForObject: local player object not valid");
                return null;
            }

            // Extract the collection GUID from the tuple.
            return effectiveCollection.Item1;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[DanceLibrary] GetCollectionForObject IPC failed");
            return null;
        }
    }

    /// <summary>
    /// Returns the current permanent settings for a mod in a collection.
    /// Used before applying temporary settings to preserve the user's configured options.
    /// Returns null if the mod is not in the collection or on failure.
    /// </summary>
    /// <param name="collectionId">
    /// The collection GUID, typically from <see cref="GetPlayerCollectionId"/>.
    /// </param>
    /// <param name="modDirectory">
    /// The mod's folder name as returned by <see cref="GetModList"/>.
    /// </param>
    /// <returns>
    /// A tuple of (enabled, priority, options) where options maps group names to
    /// lists of selected option names. Returns null if the mod is absent or on failure.
    /// </returns>
    public (bool enabled, int priority, Dictionary<string, List<string>> options)?
        GetCurrentModSettings(Guid collectionId, string modDirectory)
    {
        if (!IsAvailable) return null;
        try
        {
            // IPC: "Penumbra.GetCurrentModSettings.V5"
            //   | (Guid, string modDir, string modName, bool ignoreInheritance)
            //   → (int errorCode, (bool enabled, int priority, Dictionary<string, List<string>> options, bool isInherited)?)
            // Passing false for ignoreInheritance so we see the effective settings.
            var (errorCode, settingsTuple) = _getCurrentModSettings.InvokeFunc(
                collectionId, modDirectory, "", false);

            // errorCode 0 = success, non-zero = various failures (mod missing, etc.)
            if (errorCode != 0 || !settingsTuple.HasValue)
            {
                _log.Debug($"[DanceLibrary] GetCurrentModSettings returned errorCode={errorCode} for: {modDirectory}");
                return null;
            }

            var (enabled, priority, options, _) = settingsTuple.Value;
            return (enabled, priority, options);
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[DanceLibrary] GetCurrentModSettings IPC failed for: {modDirectory}");
            return null;
        }
    }

    // ── Temporary Settings ───────────────────────────────────────────────────────

    /// <summary>
    /// Applies temporary Penumbra settings to the local player's collection.
    /// Sets the mod to enabled=true with priority=99, preserving the provided options.
    /// The temporary settings overlay the permanent collection settings and are
    /// removed when <see cref="RemoveTemporaryModSettings"/> is called or the game session ends.
    /// </summary>
    /// <param name="modDirectory">
    /// The mod's folder name as returned by <see cref="GetModList"/>.
    /// </param>
    /// <param name="options">
    /// Option selections to apply. Typically the current permanent options, fetched via
    /// <see cref="GetCurrentModSettings"/>, to avoid overwriting the user's preferences.
    /// Pass an empty dictionary to use the mod's defaults.
    /// </param>
    /// <returns>
    /// True if the call succeeded (Penumbra returned 0=Success or 1=NothingChanged).
    /// False if Penumbra is unavailable or the call failed.
    /// </returns>
    public bool SetTemporaryModSettings(
        string modDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<string>> options)
    {
        if (!IsAvailable) return false;
        try
        {
            // IPC: "Penumbra.SetTemporaryModSettingsPlayer.V5"
            //   | (int objectIndex, string modDir, string modName,
            //      (bool inherit, bool enabled, int priority, IRODict<string, IROList<string>> options),
            //      string source, int key)
            //   → int (PenumbraApiEc)
            //
            // Parameters:
            //   objectIndex = 0 (local player)
            //   inherit = false (we want to override, not inherit permanent settings)
            //   enabled = true (activate the mod)
            //   priority = 99 (high priority so this mod wins conflicts)
            //   source = "DanceLibraryFFXIV" (shown in Penumbra's UI for this override)
            //   key = 0 (no lock, so the temp settings can be removed by anyone)
            var ec = _setTemporaryModSettingsPlayer.InvokeFunc(
                0,                          // objectIndex: local player
                modDirectory,               // modDir
                "",                         // modName: "" = use directory-based lookup
                (false, true, 99, options), // (inherit, enabled, priority, options)
                "DanceLibraryFFXIV",        // source label shown in Penumbra UI
                0);                         // key: 0 = no lock

            // PenumbraApiEc: 0=Success, 1=NothingChanged (both are fine)
            var success = ec == 0 || ec == 1;
            if (!success)
                _log.Warning($"[DanceLibrary] SetTemporaryModSettings returned ec={ec} for: {modDirectory}");
            return success;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[DanceLibrary] SetTemporaryModSettings IPC failed for: {modDirectory}");
            return false;
        }
    }

    /// <summary>
    /// Removes temporary Penumbra settings previously applied by <see cref="SetTemporaryModSettings"/>.
    /// The mod reverts to its permanent state in the collection (which may be disabled
    /// or enabled at normal priority, depending on the user's Penumbra settings).
    /// </summary>
    /// <param name="modDirectory">
    /// The mod's folder name as returned by <see cref="GetModList"/>.
    /// </param>
    /// <returns>
    /// True if the call succeeded. False if Penumbra is unavailable or the call failed.
    /// </returns>
    public bool RemoveTemporaryModSettings(string modDirectory)
    {
        if (!IsAvailable) return false;
        try
        {
            // IPC: "Penumbra.RemoveTemporaryModSettingsPlayer.V5"
            //   | (int objectIndex, string modDir, string modName, int key) → int (PenumbraApiEc)
            // key=0 must match the key used when setting (we used 0 = no lock).
            var ec = _removeTemporaryModSettingsPlayer.InvokeFunc(
                0,            // objectIndex: local player
                modDirectory, // modDir
                "",           // modName: "" = directory-based lookup
                0);           // key: 0 = no lock

            var success = ec == 0 || ec == 1;
            if (!success)
                _log.Warning($"[DanceLibrary] RemoveTemporaryModSettings returned ec={ec} for: {modDirectory}");
            return success;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[DanceLibrary] RemoveTemporaryModSettings IPC failed for: {modDirectory}");
            return false;
        }
    }

    // ── Permanent Settings ───────────────────────────────────────────────────────

    /// <summary>
    /// Permanently changes a single option within a mod's option group in a collection.
    /// For single-select groups: replaces the current selection with <paramref name="optionName"/>.
    /// For multi-select groups: toggles <paramref name="optionName"/> on (call multiple times
    /// for multiple selections).
    /// This writes to the user's permanent Penumbra collection — not temporary.
    /// </summary>
    /// <param name="collectionId">
    /// The collection GUID, typically from <see cref="GetPlayerCollectionId"/>.
    /// </param>
    /// <param name="modDirectory">
    /// The mod's folder name as returned by <see cref="GetModList"/>.
    /// </param>
    /// <param name="groupName">
    /// Option group name, exactly as returned by <see cref="GetAvailableModSettings"/>.
    /// Example: "Style".
    /// </param>
    /// <param name="optionName">
    /// Option name within the group, exactly as returned by <see cref="GetAvailableModSettings"/>.
    /// Example: "Sparkle".
    /// </param>
    /// <returns>
    /// True if the call succeeded. False if Penumbra is unavailable or the call failed.
    /// </returns>
    public bool TrySetModSetting(Guid collectionId, string modDirectory, string groupName, string optionName)
    {
        if (!IsAvailable) return false;
        try
        {
            // IPC: "Penumbra.TrySetModSetting.V5"
            //   | (Guid collectionId, string modDir, string modName, string groupName, string optionName)
            //   → int (PenumbraApiEc)
            var ec = _trySetModSetting.InvokeFunc(
                collectionId, // which collection to write to
                modDirectory, // modDir
                "",           // modName: "" = directory-based lookup
                groupName,    // option group (e.g., "Style")
                optionName);  // selected option (e.g., "Sparkle")

            var success = ec == 0 || ec == 1;
            if (!success)
                _log.Warning($"[DanceLibrary] TrySetModSetting returned ec={ec} for: {modDirectory}/{groupName}/{optionName}");
            return success;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[DanceLibrary] TrySetModSetting IPC failed for: {modDirectory}/{groupName}/{optionName}");
            return false;
        }
    }

    /// <summary>
    /// Sets a mod to "Inherit Settings" in the local player's collection.
    /// This is equivalent to clicking "Inherit Settings" in Penumbra's collection UI.
    /// The mod will use whatever the parent/default collection says, which typically
    /// means it is disabled if the user has not permanently enabled it elsewhere.
    ///
    /// Call this after <see cref="RemoveTemporaryModSettings"/> when resetting a mod
    /// to ensure it is fully neutral (not enabled with explicit settings).
    /// </summary>
    /// <param name="collectionId">
    /// The collection GUID, typically from <see cref="GetPlayerCollectionId"/>.
    /// </param>
    /// <param name="modDirectory">
    /// The mod's folder name as returned by <see cref="GetModList"/>.
    /// </param>
    /// <returns>
    /// True if the call succeeded. False if Penumbra is unavailable or the call failed.
    /// </returns>
    public bool TryInheritMod(Guid collectionId, string modDirectory)
    {
        if (!IsAvailable) return false;
        try
        {
            // IPC: "Penumbra.TryInheritMod.V5"
            //   | (Guid collectionId, string modDir, string modName, bool inherit) → int
            // inherit=true means "use inherited settings" (not explicit enabled/disabled).
            var ec = _tryInheritMod.InvokeFunc(
                collectionId,  // which collection to update
                modDirectory,  // modDir
                "",            // modName: "" = directory-based lookup
                true);         // inherit = true (use parent collection settings)

            // PenumbraApiEc: 0=Success, 1=NothingChanged (both are fine)
            var success = ec == 0 || ec == 1;
            if (!success)
                _log.Warning($"[DanceLibrary] TryInheritMod returned ec={ec} for: {modDirectory}");
            return success;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[DanceLibrary] TryInheritMod IPC failed for: {modDirectory}");
            return false;
        }
    }

    // ── UI Navigation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens Penumbra's main window and navigates to the given mod's page in the Mods tab.
    /// This is the equivalent of the user opening Penumbra and clicking on the mod in the list.
    /// </summary>
    /// <param name="modDirectory">
    /// The mod's folder name as returned by <see cref="GetModList"/>.
    /// </param>
    /// <returns>
    /// True if the call succeeded. False if Penumbra is unavailable or the call failed.
    /// </returns>
    public bool OpenModInPenumbra(string modDirectory)
    {
        if (!IsAvailable) return false;
        try
        {
            // IPC: "Penumbra.OpenMainWindow.V5"
            //   | (int tab, string modDir, string modName) → int (PenumbraApiEc)
            // tab = 1 = TabType.Mods: opens directly to the Mods tab for this mod.
            // Passing "" for modName uses directory-based lookup.
            var ec = _openMainWindow.InvokeFunc(1, modDirectory, "");

            var success = ec == 0 || ec == 1;
            if (!success)
                _log.Warning($"[DanceLibrary] OpenMainWindow returned ec={ec} for: {modDirectory}");
            return success;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[DanceLibrary] OpenMainWindow IPC failed for: {modDirectory}");
            return false;
        }
    }

    // ── IDisposable ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Cleans up this IPC bridge. Currently no event subscriptions to remove,
    /// but Dispose is implemented for proper IDisposable compliance.
    /// </summary>
    public void Dispose()
    {
        // No Penumbra event subscriptions to clean up (we skipped init/dispose events
        // for simplicity — see CLAUDE.md for rationale).
        _log.Debug("[DanceLibrary] PenumbraIpc disposed");
    }
}
