# WarpMod for Vintagestory

A mod that adds personal waypoint/warp functionality to Vintagestory, allowing players to set and share custom waypoints!

## Features

- Personal Warp Creation: Save any location with a custom name.
- Instant Teleportation: Travel instantly to any of your saved warps.
- Warp Management: View a complete list of all your currently saved warp points.
- Privacy Control: Choose whether to keep your warps private or share them with your group.
- Group-Based Warp Sharing: Players in the same group can access and use each other's shared warps.
- Automatic Warp Name Conflict Resolution:The system automatically handles duplicate warp names to prevent conflicts.
- `/back` Funcitonality: Automatically detect when a player teleports, registers their previous position, and allows you to teleport back.

## Commands

- `/warp <name>` - Teleport to a waypoint
- `/warp set <name>` - Set a new waypoint at your current location
- `/warp delete <name>` - Delete one of your waypoints
- `/warp groupshare TRUE` - Enable sharing your waypoints with group members
- `/warp groupshare FALSE` - Disable sharing (keep waypoints private)
- `/warps` - List available waypoints (including group-shared warps)
- `/back` - Teleports you to your previous position.

## Privacy Control

By default, waypoints are shared with group members. You can control this with:

- **Enable sharing**: `/warp groupshare TRUE` - Group members can use your waypoints
- **Disable sharing**: `/warp groupshare FALSE` - Your waypoints become private

- Players always have access to their own waypoints regardless of sharing settings.
- Additionally, when a players is not present in the server, their warps will not be display for their group members.
- If a player leaves a group, their warps leave with them.

## Permissions

Players need the `tp` (teleport) privilege to use warp commands.

## License

This project is open source. Feel free to contribute or modify as needed.
