# WarpMod for Vintage Story

A server-side mod that adds personal waypoint/warp functionality to Vintage Story, letting players set, share, and instantly travel to custom locations, with a `/back` command to undo a teleport.

## Features

- **Personal warp creation** — save any location under a custom name.
- **Group-based sharing** — players in the same group can use each other's warps when sharing is enabled.
- **Instant teleportation** — travel to any warp available to you.
- **`/back`** — teleports are tracked automatically (from this mod *and* other sources), so you can return to where you were.
- **Warp listing** — see every warp available to you, including shared ones, with coordinates and owner.
- **Privacy control** — keep your warps private, share them with your group, and optionally keep sharing them while offline.
- **Automatic name-conflict resolution** — duplicate names are suffixed with `(1)`, `(2)`, `(3)`… automatically.
- **Per-world storage** — warps are saved inside each world's savegame, so different worlds keep separate warps.

## Commands

| Command | Description |
| --- | --- |
| `/warp <name>` | Teleport to a warp available to you. |
| `/warp set <name>` | Save a warp at your current location. |
| `/warp delete <name>` | Delete one of your own warps (`remove` also works). |
| `/warp groupshare <true\|false>` | Enable or disable sharing your warps with group members. |
| `/warp shareoffline <true\|false>` | Share your warps with group members even while you're offline (requires `groupshare` to be on). |
| `/warps` | List all warps available to you, including group-shared ones. |
| `/back` | Return to your location before the last teleport. |
| `/warpimport` | *(Admin / server console)* Import legacy global warp data into the current world — one-time. |

Toggle values are case-insensitive and also accept `on`/`off`, `yes`/`no`, and `1`/`0`.

## Sharing & privacy

- **Sharing is off by default.** Your warps stay private until you run `/warp groupshare true`. *(Warps migrated from older versions of the mod are treated as shared, for backward compatibility.)*
- **Offline sharing is off by default**, and requires group sharing to be enabled first. Turning group sharing off also turns offline sharing off.
- Sharing follows your **current** group membership, if you leave a group, that group's members can no longer see your warps.
- When two available warps share a name (e.g. yours and a group member's), the duplicate is shown with a `(1)`, `(2)`… suffix.

## Storage

Warp data is stored **per-world**, inside each world's savegame, so every world has its own independent set of warps. A brand-new world starts with **no** warps — nothing is copied in from other worlds.

Warps from older versions of the mod lived in shared global files under `ModConfig` (`warpmod_data_v2.json`, `warpmod_player_data.json`, `warpmod_data.json`). These are **no longer imported automatically**. To bring them into a specific world, open that world and run:

```
/warpimport
```

It imports the legacy data into the **current** world only, and refuses to run if that world already has warps, so it can never mix or re-seed data across worlds.

## Permissions

Players need the `tp` (teleport) privilege to use the warp commands.


## License

This project is open source. Feel free to contribute or modify as needed.
