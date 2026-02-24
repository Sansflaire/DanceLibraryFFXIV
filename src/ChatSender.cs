/// <summary>
/// Executes in-game slash commands by calling a game function directly.
///
/// Responsibilities:
///   - Provides <see cref="SendCommand"/> which sends any slash command as if the
///     player typed it in the chat box (e.g., "/harvestdance").
///   - Wraps the unsafe game call in a try/catch with logging.
///
/// THREADING: <see cref="SendCommand"/> MUST be called on the game thread.
/// Use <c>Plugin.Framework.RunOnTick(() =&gt; chatSender.SendCommand(...))</c>.
///
/// UNSAFE: This class uses unsafe code to call into the FFXIV game process via
/// FFXIVClientStructs. Specifically, it calls:
///   UIModule::ProcessChatBoxEntry(Utf8String* message)
///
/// Method location: FFXIVClientStructs.FFXIV.Client.UI.UIModule.ProcessChatBoxEntry
/// This is the same function the game uses to process player-typed chat input.
///
/// NOTE: RaptureShellModule does NOT have a general command-dispatch method.
/// The correct entry point is UIModule.ProcessChatBoxEntry.
///
/// If ProcessChatBoxEntry breaks after a game patch, check the FFXIVClientStructs
/// GitHub (aers/FFXIVClientStructs) for updated member function signatures.
/// </summary>

using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace DanceLibraryFFXIV;

/// <summary>
/// Sends slash commands to the game as if the player typed them in chat.
/// </summary>
public sealed class ChatSender
{
    /// <summary>Logger for command execution and error reporting.</summary>
    private readonly IPluginLog _log;

    /// <summary>
    /// Creates a new ChatSender.
    /// </summary>
    /// <param name="log">Plugin logger for debug output and errors.</param>
    public ChatSender(IPluginLog log)
    {
        _log = log;
    }

    /// <summary>
    /// Executes a slash command in-game as if the player typed it in the chat box.
    ///
    /// Examples:
    ///   SendCommand("/harvestdance") — makes the player perform the Harvest Dance emote.
    ///   SendCommand("/sit")          — makes the player perform the /sit emote.
    ///   SendCommand("/say Hello!")   — sends "Hello!" to say chat.
    ///
    /// UNSAFE: Calls UIModule::ProcessChatBoxEntry via FFXIVClientStructs.
    ///         ProcessChatBoxEntry is the game function that processes any text typed
    ///         into the chat input box. It applies the same restrictions as typing in chat
    ///         (length limits, content filter, etc.).
    ///
    ///         The function takes a Utf8String* — a game-managed UTF-8 string struct.
    ///         We allocate one with Utf8String.FromString(), use it, then free it via Dtor(true).
    ///
    /// THREADING: Must be called on the game (framework) thread.
    ///            Use: await Plugin.Framework.RunOnTick(() => chatSender.SendCommand(cmd))
    /// </summary>
    /// <param name="command">
    /// The full slash command to execute, including the leading slash.
    /// Example: "/harvestdance" — NOT "harvestdance" without slash.
    /// </param>
    public unsafe void SendCommand(string command)
    {
        try
        {
            // UNSAFE: Get the UIModule singleton via FFXIVClientStructs.
            // UIModule.Instance() returns a pointer to the main game UI module.
            var uiModule = UIModule.Instance();
            if (uiModule == null)
            {
                _log.Error("[DanceLibrary] UIModule.Instance() returned null — cannot send command");
                return;
            }

            // UNSAFE: Allocate a Utf8String on the game's default heap.
            // Utf8String.FromString converts the managed C# string to the game's native string format.
            // This allocates memory in the game's memory space (using IMemorySpace default allocator).
            var str = Utf8String.FromString(command);
            if (str == null)
            {
                _log.Error($"[DanceLibrary] Utf8String.FromString returned null for command: {command}");
                return;
            }

            try
            {
                // UNSAFE: Call the game's chat input processing function.
                // ProcessChatBoxEntry is the same function called when a player presses Enter
                // in the chat input box. It parses the text and executes commands.
                //
                // Parameters:
                //   Utf8String* message  — the command text (e.g., "/harvestdance")
                //   nint a4              — 0 (unused second argument, default)
                //   bool saveToHistory   — false (don't add to chat history, default)
                uiModule->ProcessChatBoxEntry(str);
            }
            finally
            {
                // UNSAFE: Free the Utf8String by calling its destructor with free=true.
                // This runs the destructor (releases any internal buffers) AND frees the pointer
                // itself from the game heap. Always use try/finally to guarantee cleanup.
                str->Dtor(true);
            }

            _log.Debug($"[DanceLibrary] Sent command: {command}");
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[DanceLibrary] Failed to send command: {command}");
        }
    }
}
