using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using HarmonyLib;

namespace Oxide.Plugins
{
    [Info("instacraft", "Lucien", "0.1.8")]
    [Description("Test plugin: /craft on|off toggles instant crafting per player; logs every F1 give-style command")]
    class instacraft : RustPlugin
    {
        private const string HarmonyId = "instacraft";
        private const string PermUser = "instacraft.user";

        private static readonly HashSet<ulong> Enabled = new HashSet<ulong>();
        private Harmony _harmony;
        private static instacraft Instance;

        private void Init()
        {
            permission.RegisterPermission(PermUser, this);
        }

        // ---------------------------------------------------------------- config

        private Configuration _config;

        private class Configuration
        {
            [JsonProperty("Announce gives and spawns in chat")]
            public bool AnnounceInChat = false;

            [JsonProperty("Announce rejected requests in chat")]
            public bool AnnounceRejectedInChat = false;
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>() ?? new Configuration();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        // Losing the permission switches /craft off, which also closes the give/spawn unlock.
        private void OnUserPermissionRevoked(string id, string perm) => RecheckPermission(id);
        private void OnUserRemovedFromGroup(string id, string group) => RecheckPermission(id);
        private void OnGroupPermissionRevoked(string group, string perm)
        {
            foreach (var userId in new List<ulong>(Enabled)) RecheckPermission(userId.ToString());
        }

        private void RecheckPermission(string id)
        {
            if (ulong.TryParse(id, out var userId) && Enabled.Contains(userId)
                && !permission.UserHasPermission(id, PermUser))
                Enabled.Remove(userId);
        }

        // Patch after startup, never in Init() (static ctors vs +server.identity).
        private void OnServerInitialized()
        {
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(AccessTools.Method(typeof(ItemCrafter), nameof(ItemCrafter.ServerUpdate)),
                postfix: new HarmonyMethod(typeof(instacraft), nameof(ServerUpdatePostfix)));

            Instance = this;
            var givePrefix = new HarmonyMethod(typeof(instacraft), nameof(GivePrefix));
            foreach (var name in GiveCommands)
            {
                var target = AccessTools.Method(typeof(ConVar.Inventory), name, new[] { typeof(ConsoleSystem.Arg) });
                if (target == null) PrintWarning($"inventory.{name} not found - not logged");
                else _harmony.Patch(target, prefix: givePrefix);
            }

            _harmony.Patch(AccessTools.Method(typeof(ConVar.Entity), nameof(ConVar.Entity.svspawn)),
                prefix: new HarmonyMethod(typeof(instacraft), nameof(SpawnPrefix)));

            // Runs after any other plugin's HasPermission patch (PowerPlayer) so it sees the final verdict.
            _harmony.Patch(AccessTools.Method(typeof(ConsoleSystem.Arg), "HasPermission"),
                postfix: new HarmonyMethod(typeof(instacraft), nameof(HasPermissionPostfix)) { priority = Priority.Last });
        }

        private void Unload()
        {
            _harmony?.UnpatchAll(HarmonyId);
            Enabled.Clear();
            Instance = null;
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason) => Enabled.Remove(player.userID);

        // ServerUpdate stamps endTime = now + craft duration when a task starts (and sends
        // note.craft_start). Pulling endTime back to now makes the next update finish it -
        // same trick the vanilla craft.instant admin path uses, without needing IsAdmin.
        private static void ServerUpdatePostfix(ItemCrafter __instance)
        {
            var owner = __instance.owner;
            if (owner == null || __instance.queue.Count == 0 || !Enabled.Contains(owner.userID)) return;
            var task = __instance.queue.First.Value;
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (task.endTime > now) task.endTime = now;
        }

        [ChatCommand("craft")]
        private void CmdCraft(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermUser))
            {
                SendReply(player, $"You need the {PermUser} permission to use /craft.");
                return;
            }

            var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (mode == "on") Enabled.Add(player.userID);
            else if (mode == "off") Enabled.Remove(player.userID);
            else
            {
                SendReply(player, $"Usage: /craft on|off (currently {(Enabled.Contains(player.userID) ? "ON" : "OFF")})");
                return;
            }
            SendReply(player, $"Instant crafting {(Enabled.Contains(player.userID) ? "ON" : "OFF")}");
        }

        // ---------------------------------------------------------------- give logging

        private static readonly string[] GiveCommands = { "give", "giveid", "givearm", "giveto", "giveall", "giveequip" };

        // OnServerCommand never fires for client-sent commands on this build, so hook the
        // ServerVar methods directly. They only run once the command passed the permission gate.
        private static void GivePrefix(ConsoleSystem.Arg arg, MethodBase __originalMethod)
        {
            var inst = Instance;
            if (inst == null || arg == null) return;

            var player = arg.Connection?.player as BasePlayer;
            var source = player != null
                ? $"{player.displayName} [{player.UserIDString}] (F1, auth {arg.Connection.authLevel})"
                : "server/RCON";
            var line = $"GIVE {source}: inventory.{__originalMethod.Name} {arg.FullString}";

            inst.Puts(line);
            inst.LogToFile("give", line, inst);
            if (inst._config.AnnounceInChat) inst.Server.Broadcast($"<color=#ffb347>[instacraft]</color> {line}");
        }

        private static readonly Dictionary<ulong, float> LastRejected = new Dictionary<ulong, float>();

        // Gives that only ever target the caller. giveall/giveto can hit other players, so they stay admin-only.
        private static readonly HashSet<string> SelfGiveCommands = new HashSet<string>
        {
            "inventory.give", "inventory.giveid", "inventory.givearm", "inventory.giveequip",
        };

        private static void HasPermissionPostfix(ConsoleSystem.Arg __instance, ref bool __result)
        {
            if (__result) return;
            var inst = Instance;
            var cmd = __instance?.cmd;
            if (inst == null || cmd == null || __instance.Connection == null) return;
            var isGive = cmd.FullName.StartsWith("inventory.give");
            var isSpawn = cmd.FullName == "entity.spawn";
            if (!isGive && !isSpawn) return;

            // /craft on unlocks giving to yourself and spawning vehicles; the give/spawn itself is
            // then logged by GivePrefix/SpawnPrefix
            if (Enabled.Contains(__instance.Connection.userid)
                && (isGive ? SelfGiveCommands.Contains(cmd.FullName) : IsVehiclePrefab(__instance.GetString(0))))
            {
                __result = true;
                return;
            }

            // HasPermission can be asked more than once per command - one message per request
            var conn = __instance.Connection;
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (LastRejected.TryGetValue(conn.userid, out var last) && now - last < 0.25f) return;
            LastRejected[conn.userid] = now;

            var player = conn.player as BasePlayer;
            var name = player != null ? player.displayName : conn.username;
            var line = $"Rejected request: {name} [{conn.userid}] (auth {conn.authLevel}): {cmd.FullName} {__instance.FullString}";
            inst.Puts(line);
            inst.LogToFile("give", line, inst);
            if (inst._config.AnnounceRejectedInChat) inst.Server.Broadcast($"<color=#ff6b6b>[instacraft]</color> {line}");
        }

        private static readonly Dictionary<string, bool> VehiclePrefabCache = new Dictionary<string, bool>();

        // entity.spawn takes any prefab (cargo ship, patrol heli, NPCs) - only vehicles are unlocked.
        // IVehicleSpawnUser covers every BaseVehicle plus the hot air balloon.
        private static bool IsVehiclePrefab(string name)
        {
            var req = ConVar.Entity.GetSpawnEntityFromName(name);
            if (!req.Valid) return false;
            if (VehiclePrefabCache.TryGetValue(req.PrefabName, out var cached)) return cached;
            var prefab = GameManager.server.FindPrefab(req.PrefabName);
            var result = prefab != null && prefab.GetComponent<BaseEntity>() is VehicleSpawner.IVehicleSpawnUser;
            VehiclePrefabCache[req.PrefabName] = result;
            return result;
        }

        private static void SpawnPrefix(string name, UnityEngine.Vector3 pos)
        {
            var inst = Instance;
            if (inst == null) return;
            var arg = ConsoleSystem.CurrentArgs;
            var player = arg?.Connection?.player as BasePlayer;
            var source = player != null
                ? $"{player.displayName} [{player.UserIDString}] (F1, auth {arg.Connection.authLevel})"
                : "server/RCON";
            var line = $"SPAWN {source}: entity.spawn {name} at {pos}";
            inst.Puts(line);
            inst.LogToFile("give", line, inst);
            if (inst._config.AnnounceInChat) inst.Server.Broadcast($"<color=#ffb347>[instacraft]</color> {line}");
        }

        // ---------------------------------------------------------------- diagnostics

        // Raw client command as it arrives, before the permission gate - tells us whether an F1
        // give reaches the server at all.
        private void OnClientCommand(Network.Connection conn, string command)
        {
            if (command == null || command.IndexOf("give", System.StringComparison.OrdinalIgnoreCase) < 0
                && command.IndexOf("spawn", System.StringComparison.OrdinalIgnoreCase) < 0) return;
            var line = $"CLIENT CMD from {conn?.username} [{conn?.userid}] auth {conn?.authLevel}: {command}";
            Puts(line);
            LogToFile("give", line, this);
        }

        // instacraft.vehicle <name> - would /craft on allow entity.spawn of this?
        [ConsoleCommand("instacraft.vehicle")]
        private void CmdVehicle(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 2) return;
            var name = arg.GetString(0);
            var req = ConVar.Entity.GetSpawnEntityFromName(name);
            arg.ReplyWith(req.Valid ? $"{name} -> {req.PrefabName}: {(IsVehiclePrefab(name) ? "ALLOWED" : "blocked")}" : $"{name}: {req.Error}");
        }

        [ConsoleCommand("instacraft.status")]
        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 2) return;
            var sb = new System.Text.StringBuilder();
            foreach (var name in GiveCommands)
            {
                var m = AccessTools.Method(typeof(ConVar.Inventory), name, new[] { typeof(ConsoleSystem.Arg) });
                var info = m == null ? null : Harmony.GetPatchInfo(m);
                sb.AppendLine($"inventory.{name}: {(m == null ? "MISSING" : info == null ? "not patched" : $"{info.Prefixes.Count} prefix(es)")}");
            }
            foreach (var p in BasePlayer.activePlayerList)
                sb.AppendLine($"{p.displayName} [{p.UserIDString}] auth {p.net?.connection?.authLevel} IsAdmin {p.IsAdmin}");
            arg.ReplyWith(sb.ToString());
        }
    }
}
