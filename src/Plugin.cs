/// <summary>
/// Entry point for the DanceLibraryFFXIV Dalamud plugin.
///
/// Responsibilities:
///   - Declares all Dalamud service injections via [PluginService] attributes.
///   - Constructs all plugin components in dependency order.
///   - Registers ImGui draw callbacks with Dalamud's UiBuilder.
///   - Registers slash commands /dl and /dancelibrary.
///   - Disposes all components in reverse construction order on unload.
///
/// Architecture:
///   Plugin.cs (this file) → MainWindow → ModScanner → PenumbraIpc
///                                     → ChatSender
///                                     → ModSettingsWindow → PenumbraIpc
///
/// Commands:
///   /dl          — Toggle the Dance Library window
///   /dancelibrary — Same as /dl (longer alias)
///
/// Services injected (static properties, filled by Dalamud before constructor):
///   PluginInterface, Log, CommandManager, ChatGui, Framework
/// </summary>

using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DanceLibraryFFXIV.Windows;

namespace DanceLibraryFFXIV;

/// <summary>
/// The Dalamud plugin entry point. Dalamud instantiates exactly one instance of this class.
/// All plugin logic is delegated to the component classes (MainWindow, PenumbraIpc, etc.).
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    // ── Dalamud Service Injections ────────────────────────────────────────────────
    // These are filled by Dalamud BEFORE the constructor runs.
    // All are declared as static so they can be accessed by components without
    // threading the reference through every constructor chain.

    /// <summary>
    /// Dalamud plugin interface: config save/load, IPC gate, UiBuilder, plugin metadata.
    /// Used by Configuration.Save(), PenumbraIpc (for GetIpcSubscriber), and OpenMainUi.
    /// </summary>
    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    /// <summary>
    /// Plugin logger: routes to dalamud.log at %APPDATA%\XIVLauncher\dalamud.log.
    /// Used by all components for Info/Debug/Warning/Error output.
    /// </summary>
    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    /// <summary>
    /// Command manager: registers and removes slash commands.
    /// Used to register /dl and /dancelibrary.
    /// </summary>
    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    /// <summary>
    /// Chat GUI: print messages to the game chat window.
    /// Not currently used for output, but available for future chat notifications.
    /// </summary>
    [PluginService]
    internal static IChatGui ChatGui { get; private set; } = null!;

    /// <summary>
    /// Dalamud framework: provides RunOnTick for scheduling actions on the game thread.
    /// Used by MainWindow.PerformDanceAsync to safely call ChatSender from async context.
    /// </summary>
    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    // ── Plugin Component Instances ────────────────────────────────────────────────

    /// <summary>Persisted plugin configuration (window visibility).</summary>
    private readonly Configuration _config;

    /// <summary>Penumbra IPC bridge for all mod-related Penumbra calls.</summary>
    private readonly PenumbraIpc _penumbraIpc;

    /// <summary>Executes in-game slash commands via unsafe game function call.</summary>
    private readonly ChatSender _chatSender;

    /// <summary>Scans Penumbra mods for emote overrides.</summary>
    private readonly ModScanner _scanner;

    /// <summary>Settings popup window for editing mod option groups.</summary>
    private readonly ModSettingsWindow _settingsWindow;

    /// <summary>Main Dance Library window with Dances and Other Emotes tabs.</summary>
    private readonly MainWindow _mainWindow;

    // ── Constructor ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Plugin constructor. Dalamud calls this after injecting all [PluginService] properties.
    /// Constructs all components in dependency order, registers commands and draw callbacks.
    /// </summary>
    public Plugin()
    {
        // Load or create configuration from disk.
        // GetPluginConfig() deserializes %APPDATA%\XIVLauncher\pluginConfigs\DanceLibraryFFXIV.json.
        _config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Construct Penumbra IPC bridge first — many other components depend on it.
        _penumbraIpc = new PenumbraIpc(PluginInterface, Log);

        // Construct command sender (no dependencies beyond the logger).
        _chatSender = new ChatSender(Log);

        // Construct the mod scanner (depends on PenumbraIpc).
        _scanner = new ModScanner(_penumbraIpc, Log);

        // Construct the settings popup (depends on Configuration + PenumbraIpc).
        _settingsWindow = new ModSettingsWindow(_config, _penumbraIpc, Log);

        // Construct the main window (depends on everything above + Framework).
        _mainWindow = new MainWindow(
            _config,
            _scanner,
            _penumbraIpc,
            _chatSender,
            _settingsWindow,
            Framework,
            Log);

        // Register the ImGui draw callback with Dalamud's UiBuilder.
        // This is called every game frame when the UI is being rendered.
        PluginInterface.UiBuilder.Draw += OnDraw;

        // Register the "Open Main UI" callback, called when the user clicks
        // the plugin's entry in /xlplugins or uses the Dalamud plugin manager UI.
        PluginInterface.UiBuilder.OpenMainUi += OnOpenMainUi;

        // Register the "Open Config UI" callback so Dalamud's settings gear icon works
        // and the plugin passes validation. Reuses the same handler as OpenMainUi.
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenMainUi;

        // Register slash commands for the Dance Library.
        // /dl — short alias, primary command
        CommandManager.AddHandler("/dl", new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Dance Library window (/dl or /dancelibrary)"
        });

        // /dancelibrary — longer full-name alias
        CommandManager.AddHandler("/dancelibrary", new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Dance Library window (/dl or /dancelibrary)"
        });

        Log.Info("[DanceLibrary] Plugin loaded. Use /dl to open.");
    }

    // ── Dispose ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Disposes the plugin. Dalamud calls this when the plugin is disabled or Dalamud unloads.
    /// Cleans up all resources in reverse construction order to avoid dependency issues.
    /// Failing to clean up here causes ghost handlers, duplicate UI, and crashes on reload.
    /// </summary>
    public void Dispose()
    {
        // Remove slash commands first so they stop responding immediately.
        CommandManager.RemoveHandler("/dl");
        CommandManager.RemoveHandler("/dancelibrary");

        // Unregister UI callbacks to stop ImGui draw calls.
        PluginInterface.UiBuilder.Draw       -= OnDraw;
        PluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenMainUi;

        // Dispose components in reverse construction order.
        _mainWindow.Dispose();
        _settingsWindow.Dispose();
        // _scanner, _chatSender: no IDisposable (no resources to clean up)
        _penumbraIpc.Dispose();

        // Save config on dispose to persist any last state changes.
        _config.Save();

        Log.Info("[DanceLibrary] Plugin unloaded.");
    }

    // ── Event Handlers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called every game frame by Dalamud's UiBuilder.Draw event.
    /// Delegates to MainWindow.Draw() which renders the main window and the settings popup.
    /// </summary>
    private void OnDraw()
    {
        _mainWindow.Draw();
    }

    /// <summary>
    /// Called when the user clicks the plugin entry in /xlplugins or triggers OpenMainUi.
    /// Always opens the main window (does not toggle closed if already open).
    /// Triggers the first scan if the window hasn't been scanned yet.
    /// </summary>
    private void OnOpenMainUi()
    {
        // Open() sets IsVisible=true and starts the scan if NotScanned.
        // Unlike Toggle(), Open() never closes the window.
        _mainWindow.Open();
    }

    /// <summary>
    /// Called when the user types /dl or /dancelibrary in the game chat.
    /// Toggles the main window visibility (open → close → open).
    /// </summary>
    /// <param name="command">The slash command that was typed (e.g., "/dl").</param>
    /// <param name="args">Any arguments after the command (ignored — no subcommands).</param>
    private void OnCommand(string command, string args)
    {
        _mainWindow.Toggle();
    }
}
