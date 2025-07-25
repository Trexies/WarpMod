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
