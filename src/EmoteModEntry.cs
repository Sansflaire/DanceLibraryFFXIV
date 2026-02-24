/// <summary>
/// Data model representing a single row in the Dance Library UI.
///
/// One EmoteModEntry corresponds to a (Penumbra mod, emote command) pair.
/// If a single Penumbra mod overrides multiple emotes, it produces multiple
/// EmoteModEntry instances — one per emote. This allows the user to perform
/// each dance variant independently, even though the underlying mod (and its
/// Penumbra settings) are shared.
///
/// The <see cref="IsActive"/> flag is NOT persisted — it is runtime state only.
/// Active mods are tracked by <see cref="Windows.MainWindow"/> across scans.
/// </summary>

namespace DanceLibraryFFXIV;

/// <summary>
/// Immutable data record describing a Penumbra mod that overrides one emote animation.
/// Created by <see cref="ModScanner"/> and displayed in <see cref="Windows.MainWindow"/>.
/// </summary>
public sealed class EmoteModEntry
{
    /// <summary>
    /// The Penumbra mod's internal directory name (the folder name within the
    /// Penumbra mods root, as returned by <c>Penumbra.GetModList</c>).
    /// This is the identifier used in all subsequent Penumbra IPC calls.
    /// Example: "My Harvest Dance Mod" (not a full path, just the folder name).
    /// </summary>
    public string ModDirectory { get; init; } = string.Empty;

    /// <summary>
    /// The human-readable display name for the mod, as shown in the Penumbra UI.
    /// This is the value from <c>Penumbra.GetModList</c>'s dictionary.
    /// Example: "Trist's Sparkly Harvest Dance".
    /// </summary>
    public string ModDisplayName { get; init; } = string.Empty;

    /// <summary>
    /// The emote slash command that this mod overrides, normalized to lowercase
    /// with a leading slash. Used both for display and for executing the emote
    /// via <see cref="ChatSender"/>.
    /// Example: "/harvestdance".
    /// </summary>
    public string EmoteCommand { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable display name for the emote, derived from <see cref="EmoteData"/>.
    /// Example: "Harvest Dance".
    /// Falls back to the raw command if the emote is not in EmoteData's dictionary.
    /// </summary>
    public string EmoteDisplayName { get; init; } = string.Empty;

    /// <summary>
    /// True if this emote is classified as a dance (found in <see cref="EmoteData.DanceEmotes"/>).
    /// Determines which tab (Dances vs Other Emotes) this entry appears in.
    /// </summary>
    public bool IsDance { get; init; }

    /// <summary>
    /// True if the mod has configurable option groups in Penumbra
    /// (e.g., "Style: Classic / Sparkle / Dark").
    /// Controls whether the Settings button is enabled in the UI.
    /// Determined by <see cref="PenumbraIpc.GetAvailableModSettings"/> during scan.
    /// </summary>
    public bool HasOptions { get; init; }

    /// <summary>
    /// True when this mod currently has temporary Penumbra settings applied
    /// (enabled with priority 99 via <see cref="PenumbraIpc.SetTemporaryModSettings"/>).
    /// This is runtime-only state, tracked by <see cref="Windows.MainWindow"/>.
    /// NOT stored in Configuration and resets on plugin reload.
    /// </summary>
    public bool IsActive { get; set; }
}
