# Dance Library

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for Final Fantasy XIV that turns your [Penumbra](https://github.com/xivdev/Penumbra) emote mods into a one-click dance library.

---

## What It Does

Dance Library scans all of your installed Penumbra mods and finds every mod that overrides an emote animation. It organizes them into two tabs — **Dances** and **Other Emotes** — and lets you activate any mod and perform its emote with a single click.

**Clicking Perform on a mod:**

1. Temporarily enables the mod in Penumbra at high priority (99), so it wins any conflicts
2. Waits 300ms for Penumbra to apply the file replacements
3. Executes the emote command in-game automatically

**Clicking Reset** reverts the mod back to its normal Penumbra state.

**Clicking Settings** opens a per-mod option editor, letting you switch between any option groups the mod defines (e.g. different color variants).

---

## Requirements

- **Final Fantasy XIV** (any patch)
- **[XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher)** with Dalamud (API Level 14)
- **[Penumbra](https://github.com/xivdev/Penumbra)** installed and loaded (API V5)
- At least one Penumbra mod that overrides an emote animation

---

## Installation

Dance Library is not on the official Dalamud plugin repository. You'll need to build it from source and load it as a dev plugin.

### 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or newer
- Dalamud installed via XIVLauncher (the reference DLLs must be present at `%APPDATA%\XIVLauncher\addon\Hooks\dev\`)

### 2. Build

```bash
git clone https://github.com/YOUR_USERNAME/DanceLibraryFFXIV.git
cd DanceLibraryFFXIV/src
dotnet build
```

The build will produce `DanceLibraryFFXIV.dll` in the `src/bin/x64/Debug/` folder.

### 3. Register as a Dev Plugin

1. Copy `DanceLibraryFFXIV.dll` and `DanceLibraryFFXIV.json` into a folder, e.g.:
   `%APPDATA%\XIVLauncher\devPlugins\DanceLibraryFFXIV\`

2. Launch FFXIV through XIVLauncher.

3. In-game, open `/xlsettings` → **Experimental** → **Dev Plugin Locations**.

4. Click `+` and add the full path to the DLL:
   `C:\Users\YOU\AppData\Roaming\XIVLauncher\devPlugins\DanceLibraryFFXIV\DanceLibraryFFXIV.dll`

5. Save, then open `/xlplugins`, find **Dance Library**, and click **Enable**.

---

## Usage

| Command | Effect |
|---|---|
| `/dl` | Open / close the Dance Library window |
| `/dancelibrary` | Same as `/dl` |

### First Use

1. Open the window with `/dl`.
2. Click **Refresh** to scan your Penumbra mods. This takes under a second for most collections.
3. Browse the **Dances** and **Other Emotes** tabs.
4. Click any row (or the **Perform** button) to activate the mod and perform the emote.
5. Click **Reset** when you want to deactivate that mod's temporary override.

### Notes

- **Temporary settings** are session-only. They survive plugin reloads but are cleared when you log out or the game closes.
- **Settings** (the per-mod option editor) writes changes permanently to your Penumbra collection. After changing options, click Reset and then Perform again to see them take effect.
- If a mod does not appear in the list, it may not declare emote overrides in its Penumbra metadata. This is a mod authoring issue, not a plugin bug.

---

## How It Finds Mods

Penumbra mods can declare "changed items" — metadata describing what game assets they modify. Dance Library looks for entries in the form `"Emote: /harvestdance"` and uses those to build the list. Only mods that properly declare emote changes will appear.

Recognized dance emotes (Harvest Dance, Ball Dance, Manderville Dance, Gold Dance, and 25+ more) go to the **Dances** tab. All other emote overrides go to **Other Emotes**.

---

## License

Copyright Sansflaire
