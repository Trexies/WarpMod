# WarpMod for Vintagestory

A mod that adds personal waypoint/warp functionality to Vintagestory, allowing players to set and share custom waypoints!

## Features

- Set personal waypoints with `/warp set <name>`
- Teleport to waypoints with `/warp <name>`
- List available waypoints with `/warps`
- Delete waypoints with `/warp delete <name>`
- **Privacy Control**: Toggle waypoint sharing with `/warp groupshare [TRUE|FALSE]`
- Group-based waypoint sharing (players in the same group can use each other's waypoints)
- Automatic waypoint name conflict resolution

## Installation

1. Download the latest release
2. Extract `WarpMod.dll` to your Vintagestory mods folder
3. Restart your Vintagestory server

## Building from Source

### Standard Setup (Recommended)

For most users with a standard Vintagestory installation:

1. Clone this repository
2. Run `dotnet build` or build through Visual Studio
3. The project will automatically find Vintagestory in `%APPDATA%\Vintagestory`

### Custom Installation Paths

If you have Vintagestory installed in a custom location:

1. Copy `WarpMod.props.example` to `WarpMod.props`
2. Edit `WarpMod.props` and update the `VintagestoryPath` to your installation directory
3. Build the project

## Commands

- `/warp <name>` - Teleport to a waypoint
- `/warp set <name>` - Set a new waypoint at your current location
- `/warp delete <name>` - Delete one of your waypoints
- `/warp groupshare TRUE` - Enable sharing your waypoints with group members
- `/warp groupshare FALSE` - Disable sharing (keep waypoints private)
- `/warps` - List available waypoints

## Privacy Control

By default, waypoints are shared with group members. You can control this with:

- **Enable sharing**: `/warp groupshare TRUE` - Group members can use your waypoints
- **Disable sharing**: `/warp groupshare FALSE` - Your waypoints become private

Players always have access to their own waypoints regardless of sharing settings.

## Permissions

Players need the `tp` (teleport) privilege to use warp commands.

## License

This project is open source. Feel free to contribute or modify as needed.
