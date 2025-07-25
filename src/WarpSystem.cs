using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;

namespace WarpMod
{
    public class WarpData
    {
        public Vec3d Position { get; set; }
        public string OwnerName { get; set; }
        public string OwnerUID { get; set; }
        
        public WarpData(Vec3d position, string ownerName, string ownerUID)
        {
            Position = position;
            OwnerName = ownerName;
            OwnerUID = ownerUID;
        }
        
        // Parameterless constructor for JSON deserialization
        public WarpData() { }
    }

    public class WarpModData
    {
        public Dictionary<string, Dictionary<string, WarpData>> PlayerWarps { get; set; }
        public Dictionary<string, bool> PlayerSharingEnabled { get; set; }
        
        public WarpModData()
        {
            PlayerWarps = new Dictionary<string, Dictionary<string, WarpData>>();
            PlayerSharingEnabled = new Dictionary<string, bool>();
        }
    }

    public class WarpSystemMod : ModSystem
    {
        private ICoreServerAPI serverApi;
        private Dictionary<string, Dictionary<string, WarpData>> playerWarps; // playerUID -> warpName -> warpData
        private Dictionary<string, bool> playerSharingEnabled; // playerUID -> sharing enabled/disabled

        public override void StartServerSide(ICoreServerAPI api)
        {
            serverApi = api;
            playerWarps = new Dictionary<string, Dictionary<string, WarpData>>();
            playerSharingEnabled = new Dictionary<string, bool>();

            RegisterCommands();
            LoadWarpData();

            // Save data when server shuts down or world is saved
            serverApi.Event.SaveGameLoaded += () => LoadWarpData();
            serverApi.Event.GameWorldSave += () => SaveWarpData();
        }

        private void RegisterCommands()
        {
            serverApi.ChatCommands.Create("warp")
                .WithDescription("Teleport to a warp point or set a new one")
                .RequiresPlayer()
                .RequiresPrivilege(Privilege.tp) // Optional: require teleport privilege
                .WithArgs(serverApi.ChatCommands.Parsers.OptionalWord("action"), serverApi.ChatCommands.Parsers.OptionalWord("name"), serverApi.ChatCommands.Parsers.OptionalWord("value"))
                .HandleWith(OnWarpCommand);

            serverApi.ChatCommands.Create("warps")
                .WithDescription("List all available waypoints")
                .RequiresPlayer()
                .RequiresPrivilege(Privilege.tp)
                .HandleWith(OnWarpsCommand);
        }

        private List<string> GetGroupMemberUIDs(IServerPlayer player)
        {
            var memberUIDs = new List<string> { player.PlayerUID }; // Always include self
            
            try
            {
                var groups = player.GetGroups();
                if (groups != null && groups.Length > 0)
                {
                    // For each group the player is in, get all members from the group manager
                    foreach (var groupMembership in groups)
                    {
                        // Try to get the full group from the group manager
                        if (serverApi.Groups.PlayerGroupsById.ContainsKey(groupMembership.GroupUid))
                        {
                            var fullGroup = serverApi.Groups.PlayerGroupsById[groupMembership.GroupUid];
                            if (fullGroup != null)
                            {
                                // Add all online players from this group
                                foreach (var member in fullGroup.OnlinePlayers)
                                {
                                    if (!memberUIDs.Contains(member.PlayerUID))
                                        memberUIDs.Add(member.PlayerUID);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                serverApi.Logger.Error($"Error getting group members for player {player.PlayerName}: {e.Message}");
                // Fall back to just the player themselves
            }
            
            return memberUIDs;
        }

        private Dictionary<string, WarpData> GetAvailableWarps(IServerPlayer player)
        {
            var pooledWarps = new Dictionary<string, WarpData>();
            var memberUIDs = GetGroupMemberUIDs(player);
            
            // Collect warps from group members, respecting sharing preferences
            foreach (var memberUID in memberUIDs)
            {
                if (playerWarps.ContainsKey(memberUID))
                {
                    // Always include own warps, only include others' warps if they have sharing enabled
                    bool includeWarps = (memberUID == player.PlayerUID) || 
                                      (!playerSharingEnabled.ContainsKey(memberUID) || playerSharingEnabled[memberUID]);
                    
                    if (includeWarps)
                    {
                        foreach (var warp in playerWarps[memberUID])
                        {
                            string uniqueName = GetUniquePoolName(pooledWarps, warp.Key);
                            pooledWarps[uniqueName] = warp.Value;
                        }
                    }
                }
            }
            
            return pooledWarps;
        }

        private string GetUniquePoolName(Dictionary<string, WarpData> existingWarps, string baseName)
        {
            if (!existingWarps.ContainsKey(baseName))
                return baseName;
                
            int counter = 1;
            string uniqueName;
            do 
            {
                uniqueName = $"{baseName}({counter})";
                counter++;
            } while (existingWarps.ContainsKey(uniqueName));
            
            return uniqueName;
        }

        private string GetUniquePlayerWarpName(string playerUID, string baseName)
        {
            if (!playerWarps.ContainsKey(playerUID))
                return baseName;

            var playerWarpDict = playerWarps[playerUID];
            if (!playerWarpDict.ContainsKey(baseName))
                return baseName;

            // Find next available name with increment
            int counter = 1;
            string uniqueName;
            do
            {
                uniqueName = $"{baseName}({counter})";
                counter++;
            } while (playerWarpDict.ContainsKey(uniqueName));

            return uniqueName;
        }

        private TextCommandResult OnWarpsCommand(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Command can only be used by players");

            return ListWarps(player);
        }

        private TextCommandResult OnWarpCommand(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Command can only be used by players");

            // Parse arguments using Vintage Story's argument system
            string action = (string)args.Parsers[0].GetValue();
            string name = (string)args.Parsers[1].GetValue();
            string value = (string)args.Parsers[2].GetValue();
            
            if (string.IsNullOrEmpty(action))
            {
                return TextCommandResult.Error("Usage: /warp [warp_name], /warp set [warp_name], /warp delete [warp_name], /warp groupshare [TRUE|FALSE], or use /warps to list waypoints");
            }

            if (action.ToLower() == "set" && !string.IsNullOrEmpty(name))
            {
                return SetWarp(player, name);
            }
            else if (action.ToLower() == "delete" && !string.IsNullOrEmpty(name))
            {
                return DeleteWarp(player, name);
            }
            else if (action.ToLower() == "groupshare" && !string.IsNullOrEmpty(name))
            {
                return SetGroupShare(player, name);
            }
            else if (!string.IsNullOrEmpty(action) && string.IsNullOrEmpty(name))
            {
                return TeleportToWarp(player, action);
            }

            return TextCommandResult.Error("Usage: /warp [warp_name], /warp set [warp_name], /warp delete [warp_name], /warp groupshare [TRUE|FALSE], or use /warps to list waypoints");
        }

        private TextCommandResult SetWarp(IServerPlayer player, string warpName)
        {
            string playerUID = player.PlayerUID;
            Vec3d playerPos = player.Entity.ServerPos.XYZ;

            // Initialize player warp dictionary if it doesn't exist
            if (!playerWarps.ContainsKey(playerUID))
            {
                playerWarps[playerUID] = new Dictionary<string, WarpData>();
            }

            // Get unique warp name to handle conflicts within player's collection
            string uniqueName = GetUniquePlayerWarpName(playerUID, warpName);

            // Save the warp point with owner information
            var warpData = new WarpData(playerPos, player.PlayerName, player.PlayerUID);
            playerWarps[playerUID][uniqueName] = warpData;

            SaveWarpData();

            string message = uniqueName == warpName 
                ? $"Personal waypoint '{uniqueName}' set at coordinates: {(int)playerPos.X}, {(int)playerPos.Y}, {(int)playerPos.Z}"
                : $"Personal waypoint '{uniqueName}' set at coordinates: {(int)playerPos.X}, {(int)playerPos.Y}, {(int)playerPos.Z} (renamed due to conflict)";

            return TextCommandResult.Success(message);
        }

        private TextCommandResult TeleportToWarp(IServerPlayer player, string warpName)
        {
            var availableWarps = GetAvailableWarps(player);

            if (!availableWarps.ContainsKey(warpName))
            {
                return TextCommandResult.Error($"Waypoint '{warpName}' not found in your available waypoints");
            }

            WarpData warpData = availableWarps[warpName];
            Vec3d warpPos = warpData.Position;

            // Teleport the player
            player.Entity.TeleportToDouble(warpPos.X, warpPos.Y, warpPos.Z);

            return TextCommandResult.Success($"Teleported to waypoint '{warpName}' (created by {warpData.OwnerName})");
        }

        private TextCommandResult ListWarps(IServerPlayer player)
        {
            var availableWarps = GetAvailableWarps(player);

            if (availableWarps.Count == 0)
            {
                return TextCommandResult.Success("No waypoints available");
            }

            string warpList = "Available waypoints:\n";
            foreach (var kvp in availableWarps.OrderBy(w => w.Key))
            {
                WarpData warpData = kvp.Value;
                Vec3d pos = warpData.Position;
                warpList += $"- {kvp.Key} ({warpData.OwnerName}): {(int)pos.X}, {(int)pos.Y}, {(int)pos.Z}\n";
            }

            return TextCommandResult.Success(warpList);
        }

        private TextCommandResult DeleteWarp(IServerPlayer player, string warpName)
        {
            string playerUID = player.PlayerUID;

            if (!playerWarps.ContainsKey(playerUID) || !playerWarps[playerUID].ContainsKey(warpName))
            {
                return TextCommandResult.Error($"You don't have a personal waypoint named '{warpName}'");
            }

            playerWarps[playerUID].Remove(warpName);

            SaveWarpData();

            return TextCommandResult.Success($"Your personal waypoint '{warpName}' deleted");
        }

        private TextCommandResult SetGroupShare(IServerPlayer player, string value)
        {
            string playerUID = player.PlayerUID;

            if (string.IsNullOrEmpty(value))
            {
                return TextCommandResult.Error("Usage: /warp groupshare [TRUE|FALSE]");
            }

            bool enableSharing;
            if (value.ToUpper() == "TRUE")
            {
                enableSharing = true;
            }
            else if (value.ToUpper() == "FALSE")
            {
                enableSharing = false;
            }
            else
            {
                return TextCommandResult.Error("Usage: /warp groupshare [TRUE|FALSE]");
            }

            playerSharingEnabled[playerUID] = enableSharing;
            SaveWarpData();

            string message = enableSharing 
                ? "Waypoint sharing enabled. Group members can use your waypoints."
                : "Waypoint sharing disabled. Your waypoints are now private.";

            return TextCommandResult.Success(message);
        }

        private void LoadWarpData()
        {
            try
            {
                // Try to load new combined format first
                var loadedWarpModData = serverApi.LoadModConfig<WarpModData>("warpmod_data_v2.json");
                if (loadedWarpModData != null)
                {
                    playerWarps = loadedWarpModData.PlayerWarps ?? new Dictionary<string, Dictionary<string, WarpData>>();
                    playerSharingEnabled = loadedWarpModData.PlayerSharingEnabled ?? new Dictionary<string, bool>();
                    return;
                }

                // Fallback: Try to load old player-based format
                var loadedPlayerData = serverApi.LoadModConfig<Dictionary<string, Dictionary<string, WarpData>>>("warpmod_player_data.json");
                if (loadedPlayerData != null)
                {
                    playerWarps = loadedPlayerData;
                    // Default all existing players to sharing enabled for backward compatibility
                    foreach (var playerUID in playerWarps.Keys)
                    {
                        if (!playerSharingEnabled.ContainsKey(playerUID))
                        {
                            playerSharingEnabled[playerUID] = true;
                        }
                    }
                    SaveWarpData(); // Save in new format
                    serverApi.Logger.Warning("Migrated warp data from old player-based format to new combined format");
                    return;
                }

                // Fallback: Try to migrate from old group-based format
                var loadedGroupData = serverApi.LoadModConfig<Dictionary<string, Dictionary<string, WarpData>>>("warpmod_data.json");
                if (loadedGroupData != null)
                {
                    MigrateFromGroupFormat(loadedGroupData);
                    SaveWarpData(); // Save in new format
                    serverApi.Logger.Warning("Migrated warp data from old group-based format to new combined format");
                }
            }
            catch (Exception e)
            {
                serverApi.Logger.Error("Failed to load warp data: " + e.Message);
            }
        }

        private void MigrateFromGroupFormat(Dictionary<string, Dictionary<string, WarpData>> oldGroupData)
        {
            foreach (var groupEntry in oldGroupData)
            {
                foreach (var warpEntry in groupEntry.Value)
                {
                    string ownerUID = warpEntry.Value.OwnerUID;
                    
                    // Initialize player collection if needed
                    if (!playerWarps.ContainsKey(ownerUID))
                    {
                        playerWarps[ownerUID] = new Dictionary<string, WarpData>();
                    }
                    
                    // Add warp to owner's personal collection
                    playerWarps[ownerUID][warpEntry.Key] = warpEntry.Value;
                }
            }
        }

        private void SaveWarpData()
        {
            try
            {
                var warpModData = new WarpModData
                {
                    PlayerWarps = playerWarps,
                    PlayerSharingEnabled = playerSharingEnabled
                };
                serverApi.StoreModConfig(warpModData, "warpmod_data_v2.json");
            }
            catch (Exception e)
            {
                serverApi.Logger.Error("Failed to save warp data: " + e.Message);
            }
        }
    }
}
