using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

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

        // Required for JSON deserialization.
        public WarpData() { }
    }

    // Everything persisted per-world, serialized as a single JSON blob.
    public class WarpModData
    {
        public Dictionary<string, Dictionary<string, WarpData>> PlayerWarps { get; set; } = new();
        public Dictionary<string, bool> PlayerSharingEnabled { get; set; } = new();
        public Dictionary<string, bool> PlayerOfflineSharingEnabled { get; set; } = new();
    }

    public class WarpSystemMod : ModSystem
    {
        // Savegame data key (stored INSIDE the .vcdbs, so it's inherently per-world).
        private const string SaveKey = "warpmod_data";

        private ICoreServerAPI sapi;

        // Persisted state.
        private Dictionary<string, Dictionary<string, WarpData>> playerWarps = new();          // uid -> name -> warp
        private Dictionary<string, bool> playerSharingEnabled = new();                          // uid -> group sharing
        private Dictionary<string, bool> playerOfflineSharingEnabled = new();                   // uid -> offline sharing

        // Session-only state (not persisted).
        private readonly Dictionary<string, Vec3d> playerPreviousLocations = new();             // uid -> /back target
        private readonly Dictionary<string, Vec3d> playerLastKnownPositions = new();            // teleport detection
        private readonly Dictionary<string, long> playerLastPositionUpdate = new();

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            RegisterCommands();

            // Load only once the savegame is actually available; persist on each world save.
            sapi.Event.SaveGameLoaded += LoadWarpData;
            sapi.Event.GameWorldSave += SaveWarpData;
            sapi.Event.PlayerDisconnect += OnPlayerDisconnect;

            // Detect teleports from any source (other mods, /tp, etc.) so /back works everywhere.
            sapi.Event.RegisterGameTickListener(_ => CheckForTeleportations(), 200);
            sapi.Logger.Notification("[warpmod] Teleport tracking started.");
        }

        #region Commands

        private void RegisterCommands()
        {
            var parsers = sapi.ChatCommands.Parsers;

            sapi.ChatCommands.Create("warp")
                .WithDescription("Set, delete, share, or teleport to warps.")
                .RequiresPlayer()
                .RequiresPrivilege(Privilege.tp)
                .WithArgs(parsers.OptionalWord("action"), parsers.OptionalWord("name"))
                .HandleWith(OnWarpCommand);

            sapi.ChatCommands.Create("warps")
                .WithDescription("List all warps available to you.")
                .RequiresPlayer()
                .RequiresPrivilege(Privilege.tp)
                .HandleWith(OnWarpsCommand);

            sapi.ChatCommands.Create("back")
                .WithDescription("Teleport back to your location before the last teleport.")
                .RequiresPlayer()
                .RequiresPrivilege(Privilege.tp)
                .HandleWith(OnBackCommand);

            // Import the legacy GLOBAL warp config into THIS world on demand (console-runnable).
            sapi.ChatCommands.Create("warpimport")
                .WithDescription("Import legacy global warp data into the current world (one-time).")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(OnWarpImportCommand);
        }

        private TextCommandResult OnWarpImportCommand(TextCommandCallingArgs args)
        {
            string who = args.Caller.Player?.PlayerName ?? "Console";

            // Only import into a world that has no warps yet, so this can never mix or re-seed data.
            if (playerWarps.Count > 0)
            {
                return TextCommandResult.Error("This world already has warps; import skipped to avoid mixing data.");
            }

            if (!MigrateLegacyGlobalConfig())
            {
                return TextCommandResult.Error("No legacy global warp config found to import.");
            }

            sapi.Logger.Notification($"[warpmod] {who} imported legacy global warps into this world ({playerWarps.Count} player(s)).");
            return TextCommandResult.Success($"Imported legacy warps into THIS world ({playerWarps.Count} player(s)).");
        }

        private TextCommandResult OnWarpCommand(TextCommandCallingArgs args)
        {
            if (args.Caller.Player is not IServerPlayer player)
                return TextCommandResult.Error("This command can only be used by players.");

            string action = (args.Parsers[0].GetValue() as string)?.Trim();
            string name = (args.Parsers[1].GetValue() as string)?.Trim();

            if (string.IsNullOrEmpty(action))
                return Usage();

            switch (action.ToLowerInvariant())
            {
                case "set":          return RequireName(name) ?? SetWarp(player, name);
                case "delete":
                case "remove":       return RequireName(name) ?? DeleteWarp(player, name);
                case "groupshare":   return RequireName(name) ?? SetGroupShare(player, name);
                case "shareoffline": return RequireName(name) ?? SetOfflineShare(player, name);

                default:
                    // No recognized subcommand => treat the first word as a warp to teleport to.
                    return string.IsNullOrEmpty(name) ? TeleportToWarp(player, action) : Usage();
            }
        }

        private TextCommandResult OnWarpsCommand(TextCommandCallingArgs args)
        {
            if (args.Caller.Player is not IServerPlayer player)
                return TextCommandResult.Error("This command can only be used by players.");

            return ListWarps(player);
        }

        private TextCommandResult OnBackCommand(TextCommandCallingArgs args)
        {
            if (args.Caller.Player is not IServerPlayer player)
                return TextCommandResult.Error("This command can only be used by players.");

            string uid = player.PlayerUID;
            if (!playerPreviousLocations.TryGetValue(uid, out var prev))
                return TextCommandResult.Error("No previous location stored. Teleport somewhere first.");

            player.Entity.TeleportToDouble(prev.X, prev.Y, prev.Z);
            playerPreviousLocations.Remove(uid); // one /back per teleport

            return TextCommandResult.Success($"Teleported back to {(int)prev.X}, {(int)prev.Y}, {(int)prev.Z}.");
        }

        private static TextCommandResult RequireName(string name) =>
            string.IsNullOrEmpty(name) ? Usage() : null;

        // NOTE: command results are rendered as VTML, so a literal '<' starts a tag.
        // Writing "<name>" makes the parser treat it as an unclosed tag, which eats the
        // rest of the message AND breaks rendering of the messages after it. Escape as
        // &lt;/&gt; (same convention Vintage Story's own lang files use).
        private static TextCommandResult Usage() => TextCommandResult.Error(
            "Usage: /warp &lt;name&gt; | /warp set &lt;name&gt; | /warp delete &lt;name&gt; | " +
            "/warp groupshare &lt;true|false&gt; | /warp shareoffline &lt;true|false&gt; | /warps | /back");

        #endregion

        #region Warp operations

        private TextCommandResult SetWarp(IServerPlayer player, string warpName)
        {
            string uid = player.PlayerUID;

            // Entity.ServerPos was removed in 1.22; Entity.Pos is the replacement.
            Vec3d pos = player.Entity.Pos.XYZ;

            if (!playerWarps.TryGetValue(uid, out var warps))
            {
                warps = new Dictionary<string, WarpData>();
                playerWarps[uid] = warps;
            }

            string uniqueName = MakeUnique(warpName, warps.ContainsKey);
            warps[uniqueName] = new WarpData(pos, player.PlayerName, uid);

            SaveWarpData();

            string suffix = uniqueName == warpName ? "" : " (renamed due to a name conflict)";
            return TextCommandResult.Success(
                $"Warp '{uniqueName}' set at {(int)pos.X}, {(int)pos.Y}, {(int)pos.Z}{suffix}.");
        }

        private TextCommandResult TeleportToWarp(IServerPlayer player, string warpName)
        {
            var available = GetAvailableWarps(player);
            if (!available.TryGetValue(warpName, out var warp))
                return TextCommandResult.Error($"Warp '{warpName}' not found. Use /warps to see what's available.");

            // Remember where we were so /back works even though the jump is instant.
            playerPreviousLocations[player.PlayerUID] = player.Entity.Pos.XYZ;

            Vec3d p = warp.Position;
            player.Entity.TeleportToDouble(p.X, p.Y, p.Z);

            return TextCommandResult.Success($"Teleported to '{warpName}' (set by {warp.OwnerName}).");
        }

        private TextCommandResult DeleteWarp(IServerPlayer player, string warpName)
        {
            string uid = player.PlayerUID;

            // Players can only delete their OWN warps, by the real (unpooled) name.
            if (!playerWarps.TryGetValue(uid, out var warps) || !warps.Remove(warpName))
                return TextCommandResult.Error($"You don't have a warp named '{warpName}'.");

            if (warps.Count == 0) playerWarps.Remove(uid); // prune empty buckets
            SaveWarpData();

            return TextCommandResult.Success($"Warp '{warpName}' deleted.");
        }

        private TextCommandResult ListWarps(IServerPlayer player)
        {
            var available = GetAvailableWarps(player);
            if (available.Count == 0)
                return TextCommandResult.Success("No warps available. Set one with /warp set &lt;name&gt;.");

            var sb = new StringBuilder("Available warps:\n");
            foreach (var kvp in available.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase))
            {
                Vec3d p = kvp.Value.Position;
                sb.Append($"- {kvp.Key} ({kvp.Value.OwnerName}): {(int)p.X}, {(int)p.Y}, {(int)p.Z}\n");
            }

            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult SetGroupShare(IServerPlayer player, string value)
        {
            if (!TryParseToggle(value, out bool enable))
                return TextCommandResult.Error("Usage: /warp groupshare &lt;true|false&gt;");

            string uid = player.PlayerUID;
            playerSharingEnabled[uid] = enable;
            if (!enable) playerOfflineSharingEnabled[uid] = false; // offline sharing requires group sharing
            SaveWarpData();

            return TextCommandResult.Success(enable
                ? "Group sharing enabled - group members can use your warps while you're online."
                : "Group sharing disabled - your warps are now private.");
        }

        private TextCommandResult SetOfflineShare(IServerPlayer player, string value)
        {
            string uid = player.PlayerUID;

            if (!TryParseToggle(value, out bool enable))
                return TextCommandResult.Error("Usage: /warp shareoffline &lt;true|false&gt;");

            if (enable && !(playerSharingEnabled.TryGetValue(uid, out bool gs) && gs))
                return TextCommandResult.Error("Enable group sharing first: /warp groupshare true");

            playerOfflineSharingEnabled[uid] = enable;
            SaveWarpData();

            return TextCommandResult.Success(enable
                ? "Offline sharing enabled - group members can use your warps even while you're offline."
                : "Offline sharing disabled - your warps are only available while you're online.");
        }

        #endregion

        #region Teleport tracking (powers /back)

        private void CheckForTeleportations()
        {
            try
            {
                var players = sapi.World.AllOnlinePlayers;
                if (players == null) return;

                long now = sapi.World.ElapsedMilliseconds;

                foreach (IServerPlayer player in players)
                {
                    if (player?.Entity?.Pos == null) continue;

                    string uid = player.PlayerUID;
                    Vec3d currentPos = player.Entity.Pos.XYZ;

                    if (!playerLastKnownPositions.TryGetValue(uid, out var lastPos))
                    {
                        playerLastKnownPositions[uid] = currentPos;
                        playerLastPositionUpdate[uid] = now;
                        continue;
                    }

                    double distance = currentPos.DistanceTo(lastPos);
                    if (distance <= 0.1) continue; // hasn't meaningfully moved

                    double timeDiff = now - playerLastPositionUpdate[uid];

                    // Teleport heuristic: a large jump in a short window, faster than running (~4-6 b/s).
                    if (distance > 15.0 && timeDiff > 0 && timeDiff < 1000)
                    {
                        double speed = distance / (timeDiff / 1000.0); // blocks per second
                        if (speed > 15.0)
                        {
                            // Don't clobber a /back target we just set for roughly this same spot.
                            if (!playerPreviousLocations.TryGetValue(uid, out var prev) || prev.DistanceTo(currentPos) > 5.0)
                            {
                                playerPreviousLocations[uid] = lastPos;
                                sapi.Logger.Debug(
                                    $"[warpmod] Teleport detected for {player.PlayerName}: " +
                                    $"{(int)lastPos.X},{(int)lastPos.Y},{(int)lastPos.Z} -> " +
                                    $"{(int)currentPos.X},{(int)currentPos.Y},{(int)currentPos.Z} ({speed:F1} b/s)");
                            }
                        }
                    }

                    playerLastKnownPositions[uid] = currentPos;
                    playerLastPositionUpdate[uid] = now;
                }
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[warpmod] Error in teleport tracking: " + e);
            }
        }

        private void OnPlayerDisconnect(IServerPlayer player)
        {
            // Keep session dictionaries from growing without bound.
            string uid = player.PlayerUID;
            playerLastKnownPositions.Remove(uid);
            playerLastPositionUpdate.Remove(uid);
            playerPreviousLocations.Remove(uid);
        }

        #endregion

        #region Group pooling / sharing

        // Warps visible to a player: their own, plus group members' warps subject to sharing settings.
        private Dictionary<string, WarpData> GetAvailableWarps(IServerPlayer player)
        {
            var pooled = new Dictionary<string, WarpData>();
            var onlineUIDs = sapi.World.AllOnlinePlayers.Select(p => p.PlayerUID).ToHashSet();

            var candidateUIDs = new HashSet<string>(GetGroupMemberUIDs(player));

            // Also include offline owners who share a group with us AND have offline sharing on.
            foreach (var ownerUID in playerWarps.Keys)
            {
                if (candidateUIDs.Contains(ownerUID)) continue;
                if (IsInSameGroup(player, ownerUID)
                    && playerOfflineSharingEnabled.TryGetValue(ownerUID, out bool off) && off)
                {
                    candidateUIDs.Add(ownerUID);
                }
            }

            foreach (var uid in candidateUIDs)
            {
                if (!playerWarps.TryGetValue(uid, out var warps)) continue;

                bool isSelf = uid == player.PlayerUID;
                if (!isSelf)
                {
                    bool groupShare = playerSharingEnabled.TryGetValue(uid, out bool gs) && gs;
                    if (!groupShare) continue;

                    bool online = onlineUIDs.Contains(uid);
                    bool offlineShare = playerOfflineSharingEnabled.TryGetValue(uid, out bool os) && os;
                    if (!online && !offlineShare) continue;
                }

                foreach (var warp in warps)
                    pooled[MakeUnique(warp.Key, pooled.ContainsKey)] = warp.Value;
            }

            return pooled;
        }

        private List<string> GetGroupMemberUIDs(IServerPlayer player)
        {
            var memberUIDs = new List<string> { player.PlayerUID }; // always include self

            try
            {
                var groups = player.GetGroups();
                if (groups == null) return memberUIDs;

                foreach (var membership in groups)
                {
                    if (!sapi.Groups.PlayerGroupsById.TryGetValue(membership.GroupUid, out var fullGroup) || fullGroup == null)
                        continue;

                    foreach (var member in fullGroup.OnlinePlayers)
                        if (!memberUIDs.Contains(member.PlayerUID))
                            memberUIDs.Add(member.PlayerUID);
                }
            }
            catch (Exception e)
            {
                sapi.Logger.Error($"[warpmod] Error resolving group members for {player.PlayerName}: {e.Message}");
                // Fall back to just the player themselves.
            }

            return memberUIDs;
        }

        private bool IsInSameGroup(IServerPlayer player, string otherPlayerUID)
        {
            try
            {
                var groups = player.GetGroups();
                if (groups == null || groups.Length == 0) return false;

                // Resolve the other player's groups from their PLAYER DATA, which exists for
                // OFFLINE players too. (sapi.World.AllPlayers only holds players who connected
                // since the server started, so using it made offline-shared warps disappear
                // after a server restart until that player logged in once.)
                var otherData = sapi.PlayerData.GetPlayerDataByUid(otherPlayerUID);
                var otherGroups = otherData?.PlayerGroupMemberships;
                if (otherGroups == null || otherGroups.Count == 0) return false;

                return groups.Any(g => otherGroups.ContainsKey(g.GroupUid));
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[warpmod] Error checking group membership: " + e.Message);
                return false;
            }
        }

        #endregion

        #region Persistence (per-world)

        private void LoadWarpData()
        {
            try
            {
                byte[] worldData = sapi.WorldManager.SaveGame.GetData(SaveKey);
                if (worldData is { Length: > 0 })
                {
                    var loaded = DeserializeData(worldData);
                    if (loaded != null)
                    {
                        playerWarps = loaded.PlayerWarps ?? new();
                        playerSharingEnabled = loaded.PlayerSharingEnabled ?? new();
                        playerOfflineSharingEnabled = loaded.PlayerOfflineSharingEnabled ?? new();
                        sapi.Logger.Notification($"[warpmod] Loaded warps for {playerWarps.Count} player(s) from this world.");
                        return;
                    }
                }

                // No per-world data yet, so this world simply starts with no warps.
                // Legacy GLOBAL warps are deliberately NOT auto-imported here: that file is shared
                // across every save, so doing it automatically seeded each new world with the same
                // data. Run /warpimport in the world you actually want them in instead.
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[warpmod] Failed to load warp data: " + e);
            }
        }

        private void SaveWarpData()
        {
            try
            {
                var data = new WarpModData
                {
                    PlayerWarps = playerWarps,
                    PlayerSharingEnabled = playerSharingEnabled,
                    PlayerOfflineSharingEnabled = playerOfflineSharingEnabled
                };

                sapi.WorldManager.SaveGame.StoreData(SaveKey, SerializeData(data));
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[warpmod] Failed to save warp data: " + e);
            }
        }

        private bool MigrateLegacyGlobalConfig()
        {
            // 1) v2 wrapper format (warps + sharing flags).
            var v2 = sapi.LoadModConfig<WarpModData>("warpmod_data_v2.json");
            if (v2 != null)
            {
                playerWarps = v2.PlayerWarps ?? new();
                playerSharingEnabled = v2.PlayerSharingEnabled ?? new();
                playerOfflineSharingEnabled = v2.PlayerOfflineSharingEnabled ?? new();
                SaveWarpData();
                sapi.Logger.Notification("[warpmod] Migrated legacy v2 global config into this world.");
                return true;
            }

            // 2) Player-keyed format.
            var perPlayer = sapi.LoadModConfig<Dictionary<string, Dictionary<string, WarpData>>>("warpmod_player_data.json");
            if (perPlayer != null)
            {
                playerWarps = perPlayer;
                foreach (var uid in playerWarps.Keys) playerSharingEnabled[uid] = true; // back-compat default
                SaveWarpData();
                sapi.Logger.Notification("[warpmod] Migrated legacy player-format global config into this world.");
                return true;
            }

            // 3) Oldest group-keyed format -> flatten into per-player collections by OwnerUID.
            var perGroup = sapi.LoadModConfig<Dictionary<string, Dictionary<string, WarpData>>>("warpmod_data.json");
            if (perGroup != null)
            {
                foreach (var group in perGroup.Values)
                {
                    foreach (var entry in group)
                    {
                        string ownerUID = entry.Value?.OwnerUID;
                        if (string.IsNullOrEmpty(ownerUID)) continue;

                        if (!playerWarps.TryGetValue(ownerUID, out var w))
                        {
                            w = new Dictionary<string, WarpData>();
                            playerWarps[ownerUID] = w;
                        }
                        w[entry.Key] = entry.Value;
                    }
                }
                SaveWarpData();
                sapi.Logger.Notification("[warpmod] Migrated legacy group-format global config into this world.");
                return true;
            }

            return false;
        }

        // JsonUtil lives in Vintagestory.API.Common and is Newtonsoft-backed, so no extra reference is needed.
        private static byte[] SerializeData(WarpModData data) => JsonUtil.ToBytes(data);

        private static WarpModData DeserializeData(byte[] bytes) => JsonUtil.FromBytes<WarpModData>(bytes);

        #endregion

        #region Helpers

        // Returns baseName if free, else baseName(1), baseName(2), ... using the supplied existence check.
        // Note: System.Func is qualified because Vintagestory.API.Common also defines a 'Func' delegate.
        private static string MakeUnique(string baseName, System.Func<string, bool> exists)
        {
            if (!exists(baseName)) return baseName;

            int n = 1;
            string candidate;
            do { candidate = $"{baseName}({n++})"; } while (exists(candidate));
            return candidate;
        }

        private static bool TryParseToggle(string s, out bool value)
        {
            value = false;
            if (string.IsNullOrWhiteSpace(s)) return false;

            switch (s.Trim().ToLowerInvariant())
            {
                case "true": case "on": case "yes": case "1": value = true; return true;
                case "false": case "off": case "no": case "0": value = false; return true;
                default: return false;
            }
        }

        #endregion
    }
}