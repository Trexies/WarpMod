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
        private Dictionary<string, Vec3d> playerPreviousLocations; // playerUID -> previous location before warp

        public override void StartServerSide(ICoreServerAPI api)
        {
            serverApi = api;
            playerWarps = new Dictionary<string, Dictionary<string, WarpData>>();
            playerSharingEnabled = new Dictionary<string, bool>();
            playerPreviousLocations = new Dictionary<string, Vec3d>();

            RegisterCommands();
            LoadWarpData();

            // Save data when server shuts down or world is saved
            serverApi.Event.SaveGameLoaded += () => LoadWarpData();
            serverApi.Event.GameWorldSave += () => SaveWarpData();

            // Start universal teleport tracking system
            StartTeleportTracking();
        }

        private Dictionary<string, Vec3d> playerLastKnownPositions; // Track last known positions for teleport detection
        private Dictionary<string, long> playerLastPositionUpdate; // Track when position was last updated

        private void StartTeleportTracking()
        {
            playerLastKnownPositions = new Dictionary<string, Vec3d>();
            playerLastPositionUpdate = new Dictionary<string, long>();

            // Start a repeating timer to check for teleportation every 200ms
            serverApi.Event.RegisterGameTickListener((dt) =>
            {
                CheckForTeleportations();
            }, 200);

            serverApi.Logger.Debug("Universal teleport tracking system started");
        }

        private void CheckForTeleportations()
        {
            try
            {
                var onlinePlayers = serverApi.World.AllOnlinePlayers;
                if (onlinePlayers == null) return;

                foreach (IServerPlayer player in onlinePlayers)
                {
                    if (player?.Entity?.ServerPos == null) continue;

                    string playerUID = player.PlayerUID;
                    Vec3d currentPos = player.Entity.ServerPos.XYZ;
                    long currentTime = serverApi.World.ElapsedMilliseconds;

                    // Skip if player just joined or respawned
                    if (!playerLastKnownPositions.ContainsKey(playerUID))
                    {
                        playerLastKnownPositions[playerUID] = currentPos;
                        playerLastPositionUpdate[playerUID] = currentTime;
                        continue;
                    }

                    Vec3d lastPos = playerLastKnownPositions[playerUID];
                    long lastUpdate = playerLastPositionUpdate[playerUID];
                    double timeDiff = currentTime - lastUpdate;

                    // Calculate distance moved
                    double distance = currentPos.DistanceTo(lastPos);

                    // Detect teleportation: significant distance (>15 blocks) in short time (<500ms)
                    // This filters out normal movement but catches teleports
                    if (distance > 15.0 && timeDiff < 500)
                    {
                        // Check if this wasn't a /back command (avoid storing position during /back)
                        if (!playerPreviousLocations.ContainsKey(playerUID) || 
                            playerPreviousLocations[playerUID].DistanceTo(currentPos) > 5.0)
                        {
                            // Store the previous position for /back
                            playerPreviousLocations[playerUID] = lastPos;
                            serverApi.Logger.Debug($"Detected teleportation for {player.PlayerName}: {(int)lastPos.X}, {(int)lastPos.Y}, {(int)lastPos.Z} -> {(int)currentPos.X}, {(int)currentPos.Y}, {(int)currentPos.Z}");
                        }
                    }

                    // Update tracking data
                    playerLastKnownPositions[playerUID] = currentPos;
                    playerLastPositionUpdate[playerUID] = currentTime;
                }
            }
            catch (Exception e)
            {
                serverApi.Logger.Error($"Error in teleport tracking: {e.Message}");
            }
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

            serverApi.ChatCommands.Create("back")
                .WithDescription("Teleport back to your location prior to the last teleportation")
                .RequiresPlayer()
                .RequiresPrivilege(Privilege.tp)
                .HandleWith(OnBackCommand);
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

        private TextCommandResult OnBackCommand(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Command can only be used by players");

            string playerUID = player.PlayerUID;

            // Check if player has a previous location stored
            if (!playerPreviousLocations.ContainsKey(playerUID))
            {
                return TextCommandResult.Error("No previous location available. Use any teleport command first.");
            }

            Vec3d previousPos = playerPreviousLocations[playerUID];

            // Teleport the player to their previous location
            player.Entity.TeleportToDouble(previousPos.X, previousPos.Y, previousPos.Z);

            // Clear the previous location so /back can only be used once per warp
            playerPreviousLocations.Remove(playerUID);

            return TextCommandResult.Success($"Teleported back to your previous location: {(int)previousPos.X}, {(int)previousPos.Y}, {(int)previousPos.Z}");
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
            return TextCommandResult.Error("Usage: /warp [warp_name], /warp set [warp_name], /warp delete [warp_name], /warp groupshare [TRUE|FALSE], /back (return to previous location), or use /warps to list waypoints");
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

            return TextCommandResult.Error("Usage: /warp [warp_name], /warp set [warp_name], /warp delete [warp_name], /warp groupshare [TRUE|FALSE], /back (return to previous location), or use /warps to list waypoints");
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

            // Store current location for /back command
            string playerUID = player.PlayerUID;
            Vec3d currentPos = player.Entity.ServerPos.XYZ;
            playerPreviousLocations[playerUID] = currentPos;

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
                // Try to load world-specific data from world save file
                byte[] worldData = serverApi.WorldManager.SaveGame.GetData("warpmod_data");
                if (worldData != null)
                {
                    try
                    {
                        string jsonData = System.Text.Encoding.UTF8.GetString(worldData);
                        // Use a simple approach - leverage VS's existing JSON handling through a temporary mod config
                        string tempFileName = $"warpmod_temp_load_{Guid.NewGuid():N}.json";
                        System.IO.File.WriteAllText(System.IO.Path.Combine(serverApi.GetOrCreateDataPath("ModConfig"), tempFileName), jsonData);
                        var loadedWarpModData = serverApi.LoadModConfig<WarpModData>(tempFileName);
                        System.IO.File.Delete(System.IO.Path.Combine(serverApi.GetOrCreateDataPath("ModConfig"), tempFileName));
                        
                        if (loadedWarpModData != null)
                        {
                            playerWarps = loadedWarpModData.PlayerWarps ?? new Dictionary<string, Dictionary<string, WarpData>>();
                            playerSharingEnabled = loadedWarpModData.PlayerSharingEnabled ?? new Dictionary<string, bool>();
                            serverApi.Logger.Debug("Loaded world-specific warp data successfully");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        serverApi.Logger.Error($"Failed to deserialize world-specific warp data: {ex.Message}");
                    }
                }

                // Check if this world has already been migrated by looking for a migration marker
                byte[] migrationMarker = serverApi.WorldManager.SaveGame.GetData("warpmod_migrated");
                if (migrationMarker == null)
                {
                    // Migration: Check for existing global mod config data and migrate it to world-specific storage
                    var globalWarpModData = serverApi.LoadModConfig<WarpModData>("warpmod_data_v2.json");
                    if (globalWarpModData != null)
                    {
                        playerWarps = globalWarpModData.PlayerWarps ?? new Dictionary<string, Dictionary<string, WarpData>>();
                        playerSharingEnabled = globalWarpModData.PlayerSharingEnabled ?? new Dictionary<string, bool>();
                        SaveWarpData(); // Save to world-specific storage
                        // Mark this world as migrated
                        serverApi.WorldManager.SaveGame.StoreData("warpmod_migrated", System.Text.Encoding.UTF8.GetBytes("true"));
                        serverApi.Logger.Warning("Migrated global warp data to world-specific storage");
                        return;
                    }

                    // Fallback: Try to migrate from old player-based format
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
                        SaveWarpData(); // Save in world-specific format
                        // Mark this world as migrated
                        serverApi.WorldManager.SaveGame.StoreData("warpmod_migrated", System.Text.Encoding.UTF8.GetBytes("true"));
                        serverApi.Logger.Warning("Migrated warp data from old player-based format to world-specific storage");
                        return;
                    }

                    // Fallback: Try to migrate from old group-based format
                    var loadedGroupData = serverApi.LoadModConfig<Dictionary<string, Dictionary<string, WarpData>>>("warpmod_data.json");
                    if (loadedGroupData != null)
                    {
                        MigrateFromGroupFormat(loadedGroupData);
                        SaveWarpData(); // Save in world-specific format
                        // Mark this world as migrated
                        serverApi.WorldManager.SaveGame.StoreData("warpmod_migrated", System.Text.Encoding.UTF8.GetBytes("true"));
                        serverApi.Logger.Warning("Migrated warp data from old group-based format to world-specific storage");
                        return;
                    }

                    // No data to migrate, mark as migrated anyway to prevent future migration attempts
                    serverApi.WorldManager.SaveGame.StoreData("warpmod_migrated", System.Text.Encoding.UTF8.GetBytes("true"));
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

                // Save to world-specific storage using world data APIs
                string tempFileName = $"warpmod_temp_save_{Guid.NewGuid():N}.json";
                string tempFilePath = System.IO.Path.Combine(serverApi.GetOrCreateDataPath("ModConfig"), tempFileName);
                
                // Use VS's StoreModConfig to generate proper JSON, then read it back
                serverApi.StoreModConfig(warpModData, tempFileName);
                string jsonData = System.IO.File.ReadAllText(tempFilePath);
                System.IO.File.Delete(tempFilePath);
                
                // Store in world-specific storage
                byte[] dataBytes = System.Text.Encoding.UTF8.GetBytes(jsonData);
                serverApi.WorldManager.SaveGame.StoreData("warpmod_data", dataBytes);
                serverApi.Logger.Debug("Saved warp data to world-specific storage");
            }
            catch (Exception e)
            {
                serverApi.Logger.Error("Failed to save warp data: " + e.Message);
            }
        }
    }
}
