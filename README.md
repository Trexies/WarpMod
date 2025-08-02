# WarpMod for Vintagestory

A mod that adds personal waypoint/warp functionality to Vintagestory, allowing players to set and share custom waypoints!

## Features

- Personal Warp Creation: Save any location with a custom name.
- Group-Based Warp Sharing: Players in the same group can access and use each other's shared warps.
- Instant Teleportation: Travel instantly to any of your saved warps.
- Warp Management: View a complete list of all your currently saved warp points.
- Privacy Control: Choose whether to keep your warps private or share them with your group.
- Automatic Warp Name Conflict Resolution:The system automatically handles duplicate warp names to prevent conflicts.
- /back Funcitonality: Automatically detect when a player teleports, registers their previous position, and allows you to teleport back.

## Commands

- `/warp <name>` - Teleport to a waypoint
- `/warp set <name>` - Set a new waypoint at your current location
- `/warp delete <name>` - Delete one of your waypoints
- `/warp groupshare (True|False)` - Enable or disable sharing your waypoints with group members
- `/warp shareoffline (True|False)` - Enable or disable sharing your waypoints with group members while offline (must have groupshare set to true first)
- `/warps` - List available waypoints (including group-shared warps)
- `/back` - Teleports you to your previous position.

## Privacy Control

By default, waypoints are shared with group members. You can control this with:
- If a player leaves a group, their warps leave with them.
- groupshare is **enabled** by default
- shareoffline is **disabled** by default
- If a warp already exists, and the user tries to create a duplicate, it will add a (1), (2), (3)... etc after it

## Permissions

Players need the `tp` (teleport) privilege to use warp commands.

## License

This project is open source. Feel free to contribute or modify as needed.
