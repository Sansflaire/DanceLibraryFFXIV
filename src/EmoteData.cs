/// <summary>
/// Static data about FFXIV emote slash commands, specifically dance emotes.
///
/// Responsibilities:
///   - Provides a dictionary mapping known dance emote commands to display names.
///   - Classifies any emote command as a dance or non-dance.
///   - Normalizes emote command strings (lowercase, slash-prefixed).
///   - Resolves Penumbra's reported emote keys to actual game commands via
///     <see cref="GameCommandOverrides"/> (Penumbra sometimes reports display-name
///     forms like "/manderville dance" instead of the real command "/mdance").
///
/// When a new dance emote is added to FFXIV:
///   1. Add its primary command and all known aliases to <see cref="DanceEmotes"/>.
///   2. Document where the emote comes from (quest, MGP, Mog Station, tribal, etc.).
///   3. If it has multiple slash command aliases, add all of them mapping to
///      the same display name.
///   4. If Penumbra reports the emote under a different key than the actual command,
///      add an entry to <see cref="GameCommandOverrides"/>.
/// </summary>

using System;
using System.Collections.Generic;

namespace DanceLibraryFFXIV;

/// <summary>
/// Static repository of known FFXIV dance emote commands and their display names.
/// </summary>
public static class EmoteData
{
    /// <summary>
    /// Dictionary of all known dance emote commands mapped to their display names.
    /// Keys are slash commands (lowercase, with leading slash, e.g. "/harvestdance").
    /// Multiple keys can map to the same display name when the emote has aliases.
    ///
    /// Also includes Penumbra-reported key variants (e.g. "/manderville dance") so that
    /// display names resolve correctly even before execute-command remapping.
    ///
    /// Sources: FFXIV Console Games Wiki, Eorzea Database, Lodestone.
    /// Last updated: 2026-02-23
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DanceEmotes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Base game / quest rewards ────────────────────────────────────────

            // Default dance available to all players from the start.
            { "/dance",              "Dance" },

            // Obtained from quest "Good for What Ales You" in Limsa Lominsa.
            { "/stepdance",          "Step Dance" },

            // Obtained from quest "Saw that One Coming" in New Gridania.
            // Both /harvestdance and /hdance are recognized in-game.
            { "/harvestdance",       "Harvest Dance" },
            { "/hdance",             "Harvest Dance" },

            // Obtained from quest "Help Me, Lord of the Dance" in Ul'dah.
            { "/balldance",          "Ball Dance" },

            // Obtained via the Hildibrand questline (A Realm Reborn).
            // Both /mdance and /mandervilledance are recognized in-game. /mdance is shorter.
            { "/mdance",             "Manderville Dance" },
            { "/mandervilledance",   "Manderville Dance" },

            // Obtained via the Hildibrand questline (Stormblood).
            // Both /mmambo and /mandervillemambo are recognized.
            { "/mmambo",             "Manderville Mambo" },
            { "/mandervillemambo",   "Manderville Mambo" },

            // ── Allied / Tribal quest rewards ────────────────────────────────────

            // Obtained at Vanu Vanu rank 5 (Heavensward).
            // Both /sundropdance and /sundance are recognized.
            { "/sundropdance",       "Sundrop Dance" },
            { "/sundance",           "Sundrop Dance" },

            // Obtained at Moogle tribe rank 6 (Heavensward).
            { "/mogdance",           "Moogle Dance" },

            // Obtained after maxing all Heavensward tribes ("Eternity, Loyalty, Honesty").
            { "/moonlift",           "Moonlift Dance" },

            // Obtained at Namazu rank 6 (Stormblood).
            { "/yoldance",           "Yol Dance" },

            // Obtained at Dwarf tribe rank 8 (Shadowbringers).
            // Both /lalihop and /laliho are recognized.
            { "/lalihop",            "Lali Hop" },
            { "/laliho",             "Lali Hop" },

            // Obtained after maxing all Endwalker tribes (Loporrit questline).
            { "/lophop",             "Lop Hop" },

            // ── Gold Saucer (MGP purchases, 80,000 MGP each) ────────────────────

            // Available from the Gold Saucer prize vendor.
            // Both /thavdance and /tdance are recognized in-game; /tdance is the shorter alias.
            { "/thavdance",          "Thavnairian Dance" },
            { "/tdance",             "Thavnairian Dance" },

            // Available from the Gold Saucer prize vendor.
            // Both /golddance and /gdance are recognized.
            { "/golddance",          "Gold Dance" },
            { "/gdance",             "Gold Dance" },

            // Available from the Gold Saucer prize vendor.
            // Penumbra may report the emote key as "/bees knees" (with space); both forms map here.
            { "/beesknees",          "Bee's Knees" },
            { "/bees knees",         "Bee's Knees" },

            // ── Mog Station / Online Store purchases ────────────────────────────

            // From "Ballroom Etiquette - Modern Dance" on Mog Station.
            { "/songbird",           "Songbird" },

            // Available from Mog Station.
            // Both /edance and /easterndance are recognized.
            { "/edance",             "Eastern Dance" },
            { "/easterndance",       "Eastern Dance" },

            // Available from Mog Station.
            { "/bombdance",          "Bomb Dance" },

            // Available from Mog Station.
            { "/boxstep",            "Box Step" },

            // Available from Mog Station.
            { "/sidestep",           "Side Step" },

            // From "Ballroom Etiquette - Get Fantasy" on Mog Station.
            { "/getfantasy",         "Get Fantasy" },

            // Available from Mog Station.
            { "/popotostep",         "Popoto Step" },

            // Available from Mog Station / Make It Rain seasonal campaign.
            { "/sabotender",         "Senor Sabotender" },

            // From "Ballroom Etiquette - The Heel Toe" on Mog Station (Patch 5.2).
            { "/heeltoe",            "Heel Toe" },

            // From "Ballroom Etiquette - The Mystery Machine" / "Goobbue Do" on Mog Station.
            // Both /goobbuedo and /mysterymachine are recognized.
            { "/goobbuedo",          "Goobbue Do" },
            { "/mysterymachine",     "Goobbue Do" },

            // Available from Mog Station.
            { "/flamedance",         "Flame Dance" },

            // From "Ballroom Etiquette - Fan Fare" / Little Ladies' Day event (Patch 6.3).
            // Both /ladance and /littleladiesdance are recognized.
            { "/ladance",            "Little Ladies' Dance" },
            { "/littleladiesdance",  "Little Ladies' Dance" },

            // Available from Mog Station.
            { "/crimsonlotus",       "Crimson Lotus" },

            // Available from Mog Station / seasonal events.
            { "/uchiwasshoi",        "Uchiwasshoi" },

            // Seasonal event reward.
            { "/wasshoi",            "Wasshoi" },

            // ── Dawntrail-era "Paint It" dance emotes ───────────────────────────────────
            // Available from Gold Saucer / events. Penumbra may report these with spaces
            // (e.g. "Emote: /paint it black"); GameCommandOverrides maps them to these commands.
            { "/paintblack",         "Paint It Black"  },
            { "/paintblue",          "Paint It Blue"   },
            { "/paintred",           "Paint It Red"    },
            { "/paintyellow",        "Paint It Yellow" },
        };

    /// <summary>
    /// Maps Penumbra-reported emote key variants to the actual in-game slash commands,
    /// for cases where simply stripping spaces from the Penumbra key would still give
    /// the wrong command.
    ///
    /// Penumbra reports emote changed-items using the emote's display name with spaces
    /// (e.g. "Emote: /gold dance"). <see cref="GetExecuteCommand"/> handles this in two steps:
    ///   1. Check this dict for an explicit override.
    ///   2. If no override, strip spaces from the normalized key (e.g. "/gold dance" → "/golddance").
    ///
    /// Only add entries here when space-stripping alone gives the wrong answer.
    /// Example: "/manderville dance" stripped → "/mandervilledance" (wrong); override → "/mdance" (correct).
    ///
    /// Keys are normalized (lowercase, slash-prefixed), same as <see cref="DanceEmotes"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> GameCommandOverrides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Add entries here only when space-stripping gives the wrong command.
            // Keys are normalized Penumbra keys (lowercase, slash-prefixed, with spaces).
            // Values are the actual in-game slash commands.

            // Penumbra reports "/thavnairian dance"; stripping spaces → "/thavnairiandance" (wrong).
            // The real short command is "/tdance".
            { "/thavnairian dance", "/tdance" },

            // Penumbra reports "/sit on ground"; stripping spaces → "/sitonground" (wrong).
            // The actual game command is "/groundsit".
            { "/sit on ground", "/groundsit" },

            // "Paint It" emotes: Penumbra reports "/paint it black" etc.
            // Stripping spaces gives "/paintitblack" (wrong); the real commands omit "it".
            { "/paint it black",  "/paintblack"  },
            { "/paint it blue",   "/paintblue"   },
            { "/paint it red",    "/paintred"    },
            { "/paint it yellow", "/paintyellow" },

            // Penumbra reports "/bee's knees" (with apostrophe + space).
            // Stripping spaces gives "/bee'sknees" (wrong); the real command is "/beesknees".
            { "/bee's knees", "/beesknees" },

            // Penumbra reports "/push-ups" (with hyphen, no spaces).
            // Space-stripping leaves the hyphen in place, giving "/push-ups" (wrong).
            // The actual game command has no hyphen: "/pushups".
            { "/push-ups", "/pushups" },

            // Penumbra reports "/sit-ups" (with hyphen, no spaces).
            // Same pattern as push-ups: the real game command is "/situps" (no hyphen).
            { "/sit-ups", "/situps" },

            // Penumbra reports the emote as "Greeting" or "/greeting".
            // The actual game command is "/greet" (the longer form doesn't exist).
            { "/greeting", "/greet" },

            // Penumbra reports "/confused"; the actual game command is "/disturbed".
            { "/confused", "/disturbed" },

            // Penumbra reports cheer variant emotes with descriptive names that include colons.
            // The actual game commands use a compact form:
            // "/cheer" + action letter (w=wave, o=on, j=jump) + modifier first letter.
            // Modifier letters: y=yellow, v=violet, g=green, r=red, b=blue, w=bright (bright=white).
            //
            // Penumbra may include a space between "cheer" and the action word for jump variants
            // (e.g. "/cheer jump:red" rather than "/cheerjump:red"). Both forms are listed here
            // so the override fires regardless of which format Penumbra uses.
            // Penumbra's "Changed Items" tab shows these as e.g. "Emote: Cheer Jump: Red"
            // (no slash, title case, space after colon). NormalizeCommand lowercases and
            // prepends "/" giving "/cheer jump: red". All slash-prefixed variants are also
            // listed as fallback in case a mod uses the command form directly.
            { "/cheer wave: yellow",  "/cheerwy" },
            { "/cheerwave: yellow",   "/cheerwy" },
            { "/cheerwave:yellow",    "/cheerwy" },
            { "/cheer wave: violet",  "/cheerwv" },
            { "/cheerwave: violet",   "/cheerwv" },
            { "/cheerwave:violet",    "/cheerwv" },
            { "/cheer on: bright",    "/cheerow" },
            { "/cheeron: bright",     "/cheerow" },
            { "/cheeron:bright",      "/cheerow" },
            { "/cheer on: blue",      "/cheerob" },
            { "/cheeron: blue",       "/cheerob" },
            { "/cheeron:blue",        "/cheerob" },
            { "/cheer jump: green",   "/cheerjg" },
            { "/cheerjump: green",    "/cheerjg" },
            { "/cheer jump:green",    "/cheerjg" },
            { "/cheerjump:green",     "/cheerjg" },
            { "/cheer jump: red",     "/cheerjr" },
            { "/cheerjump: red",      "/cheerjr" },
            { "/cheer jump:red",      "/cheerjr" },
            { "/cheerjump:red",       "/cheerjr" },
        };

    /// <summary>
    /// Returns true if the given emote slash command is a known dance emote.
    /// The comparison is case-insensitive and handles slash prefix normalization.
    /// Also recognizes Penumbra-reported key variants listed in <see cref="DanceEmotes"/>.
    /// </summary>
    /// <param name="command">
    /// Emote slash command to check. May include or omit the leading slash.
    /// Example: "/harvestdance" or "harvestdance" — both return true.
    /// </param>
    /// <returns>
    /// True if the command is in <see cref="DanceEmotes"/>; false otherwise.
    /// </returns>
    public static bool IsDance(string command)
    {
        // Normalize to always have a leading slash for dictionary lookup.
        var normalized = NormalizeCommand(command);
        return DanceEmotes.ContainsKey(normalized);
    }

    /// <summary>
    /// Returns the human-readable display name for a given emote command.
    ///
    /// Resolution order:
    ///   1. Look up in <see cref="DanceEmotes"/> (handles known dance emotes).
    ///   2. Fall back to stripping the leading slash and title-casing each space-separated word.
    ///      Penumbra reports emote keys with spaces, e.g. "/sit on ground" → "Sit On Ground".
    ///      Commands with no spaces get only the first letter capitalized: "/wave" → "Wave".
    ///
    /// Callers should pass <c>normalizedCommand</c> (before space-stripping) rather than
    /// <c>executeCommand</c> so that space-containing Penumbra keys like "/gold dance"
    /// produce readable display names even when they're not in the dictionary.
    /// </summary>
    /// <param name="command">
    /// Emote slash command (normalized or raw). May contain spaces (Penumbra form).
    /// Examples: "/golddance" → "Gold Dance" (dict hit); "/sit on ground" → "Sit On Ground" (fallback).
    /// </param>
    /// <returns>
    /// Display name from <see cref="DanceEmotes"/>, or a title-cased version of the
    /// command (without leading slash) if the emote is not in the dictionary.
    /// </returns>
    public static string GetDisplayName(string command)
    {
        var normalized = NormalizeCommand(command);
        if (DanceEmotes.TryGetValue(normalized, out var name)) return name;

        // Fallback: strip the leading slash and title-case each space-separated word.
        // "/sit on ground" → "Sit On Ground", "/wave" → "Wave", "/sitonground" → "Sitonground".
        var stripped = normalized.TrimStart('/');
        if (stripped.Length == 0) return normalized;

        var words = stripped.Split(' ');
        for (var i = 0; i < words.Length; i++)
            if (words[i].Length > 0)
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
        return string.Join(' ', words);
    }

    /// <summary>
    /// Returns the actual in-game slash command to send for the given emote key.
    ///
    /// This is the command that gets passed to <c>ChatSender.SendCommand</c>, and
    /// the canonical form used for <see cref="IsDance"/> and <see cref="GetDisplayName"/>
    /// lookups in <see cref="ModScanner"/>.
    ///
    /// Resolution order:
    ///   1. Normalize the input (lowercase, leading slash).
    ///   2. Check <see cref="GameCommandOverrides"/> for an explicit mapping.
    ///   3. If no override: strip all spaces from the normalized key.
    ///      Penumbra reports emotes as "/gold dance", "/step dance", etc.; stripping
    ///      spaces gives the correct game command in most cases ("/golddance", "/stepdance").
    ///
    /// Use this on every Penumbra emote key before doing anything with it.
    /// </summary>
    /// <param name="command">
    /// Emote key as reported by Penumbra (e.g. "/gold dance", "/manderville dance")
    /// or any recognized game command.
    /// </param>
    /// <returns>
    /// The actual slash command to send to the game, e.g. "/golddance" or "/mdance".
    /// </returns>
    public static string GetExecuteCommand(string command)
    {
        var normalized = NormalizeCommand(command);

        // Step 1: Check explicit overrides for commands where stripping spaces
        // alone would give the wrong result (e.g. "/manderville dance" → "/mdance").
        if (GameCommandOverrides.TryGetValue(normalized, out var actual))
            return actual;

        // Step 2: Strip spaces. Penumbra reports emote keys using display-name
        // formatting with spaces (e.g. "/gold dance", "/step dance"). The real
        // game commands have no spaces ("/golddance", "/stepdance").
        return normalized.Replace(" ", "");
    }

    /// <summary>
    /// Normalizes an emote command string to lowercase with a leading slash.
    /// This ensures consistent dictionary lookup regardless of how Penumbra
    /// formats the command in its changed-items metadata.
    /// </summary>
    /// <param name="command">Raw command string, e.g. "HarvestDance" or "/harvestdance".</param>
    /// <returns>Normalized command, e.g. "/harvestdance".</returns>
    public static string NormalizeCommand(string command)
    {
        // Trim whitespace that might come from parsing Penumbra metadata.
        var trimmed = command.Trim().ToLowerInvariant();

        // Ensure the slash prefix is present.
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }
}
