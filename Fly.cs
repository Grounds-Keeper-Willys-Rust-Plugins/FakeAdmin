using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using System.Reflection;
using HarmonyLib;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Fly", "Lucien Murray-Pitts", "0.8.0")]
    [Description("Noclip-style free flight for ordinary players, with no change to their auth level")]
    internal class Fly : RustPlugin
    {
        // Rust's `noclip` is a CLIENT command. The client only accepts it when its own connection
        // reports authLevel >= 1, and the client learns that level once, in the connection approval
        // packet -- so the only way to hand a player real noclip is to make them a moderator and
        // reconnect them. That is exactly what this plugin must not do.
        //
        // Instead the player is mounted on an invisible seat that the server flies. Mounted players
        // are exempt from movement antihack (BasePlayer.FinalizeTick returns early for them, see
        // BasePlayer.cs:13429), the seat has no rigidbody so it passes through terrain and buildings,
        // and BaseMountable.MountedPlayerSync glues the player to it every frame. The player's auth
        // level is never touched.

        #region Constants

        private const string PermUse = "fly.user";          // may fly
        private const string PermSetClip = "fly.cansetclip"; // may choose their own clip on/off
        private const string PermAdmin = "fly.admin";        // everything above, plus speed/hz/others

        // What a downward landing probe is allowed to hit. Layer names contain spaces on this build
        // ("Vehicle Detailed", not "Vehicle_Detailed") -- a typo silently drops out of the mask.
        private static readonly int LandMask = LayerMask.GetMask(
            "Terrain", "World", "Construction", "Deployed", "Water", "Vehicle Large");

        // Same set minus Water, for "is there room for a player here" tests and for solid flight.
        private static readonly int SolidMask = LayerMask.GetMask(
            "Terrain", "World", "Construction", "Deployed", "Vehicle Large", "Vehicle Detailed");

        #endregion

        #region Fields

        private Configuration _config;

        private readonly Dictionary<ulong, Flight> _flights = new Dictionary<ulong, Flight>();

        // userID -> Time.realtimeSinceStartup after which they may take off again.
        private readonly Dictionary<ulong, float> _cooldownUntil = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float> _combatUntil = new Dictionary<ulong, float>();

        // Per-player choices, for anyone who has made one. A null field means "use the config".
        // Kept in oxide/data/Fly.json so preferences survive a restart.
        private Dictionary<ulong, Preferences> _prefs = new Dictionary<ulong, Preferences>();

        private class Preferences
        {
            // true = stop at terrain and buildings. (Stored under a new name so choices saved by the
            // old /fly noclip command, which meant the opposite, are not read back inverted.)
            [JsonProperty("clip")]
            public bool? Clip;

            // Set when an admin chose this player's clip with /fly clip <state> <player>. An
            // admin's choice applies whether or not the player holds fly.cansetclip; the player's
            // own choice only applies while they do.
            [JsonProperty("clipSetByAdmin")]
            public bool ClipSetByAdmin;
        }

        private Timer _watchdog;

        private Harmony _harmony;
        private const string HarmonyId = "com.lucienmp.fly";

        // Set for the patches below, which are static and have no other route to plugin state.
        private static Fly Instance;

        #endregion

        #region Lifecycle

        private void Init()
        {
            Instance = this;

            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermSetClip, this);
            permission.RegisterPermission(PermAdmin, this);

            cmd.AddChatCommand(_config.Command, this, nameof(CmdFly));

            LoadData();
        }

        private void OnServerInitialized()
        {
            var prefab = GameManager.server.FindPrefab(_config.MountPrefab);
            var mountable = prefab == null ? null : prefab.GetComponent<BaseMountable>();

            if (prefab == null)
            {
                PrintError($"Seat prefab '{_config.MountPrefab}' does not exist -- /{_config.Command} will refuse to take off.");
            }
            else if (mountable == null || mountable.mountAnchor == null)
            {
                PrintError($"Seat prefab '{_config.MountPrefab}' is not a mountable with an anchor -- /{_config.Command} will refuse to take off.");
            }
            else if (mountable.yawClamp.y - mountable.yawClamp.x < 340f)
            {
                // Worth shouting about: the seat still flies, but the rider is stuck looking within
                // the clamp of whichever way they happened to be facing at takeoff.
                PrintWarning($"Seat prefab '{_config.MountPrefab}' clamps yaw to {mountable.yawClamp} -- riders will not be able to turn around. "
                    + "Use a seat with an unlimited yaw clamp (the default) instead.");
            }

            // AntiHack.ValidateAgainstTerrain is NOT part of the movement validation that mounted
            // players skip -- it runs off the player's world position on its own timeslice, so a
            // rider inside a hill racks up InsideTerrain violations until they are kicked. That is
            // the "clipped into terrain, got disconnected" report, and the patches below are the fix.
            // Patched here rather than in Init(): Harmony's PatchAll touches types whose static
            // constructors are not safe to run that early in server startup.
            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            // Cheap per-second checks (height cap, flight timer, building privilege) live here rather
            // than in the per-frame flight loop.
            _watchdog = timer.Every(1f, Watchdog);
        }

        private void OnServerSave() => SaveData();

        private void LoadData()
        {
            try
            {
                _prefs = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, Preferences>>(Name)
                    ?? new Dictionary<ulong, Preferences>();
            }
            catch (Exception ex)
            {
                // Includes the 0.4.x file, which stored a bare bool per player. One wiped preference
                // is not worth a migration path.
                PrintWarning($"Could not read oxide/data/{Name}.json, starting with no saved choices: {ex.Message}");
                _prefs = new Dictionary<ulong, Preferences>();
            }
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, _prefs);

        private Preferences PrefsFor(ulong userId)
        {
            Preferences prefs;
            if (!_prefs.TryGetValue(userId, out prefs)) _prefs[userId] = prefs = new Preferences();
            return prefs;
        }

        private void Unload()
        {
            SaveData();
            _watchdog?.Destroy();
            _harmony?.UnpatchAll(HarmonyId);

            foreach (var id in new List<ulong>(_flights.Keys))
                StopFlight(_flights[id], "Unload");
            _flights.Clear();

            Instance = null;
        }

        #endregion

        #region Commands

        private bool IsAdmin(BasePlayer player)
            => player != null && permission.UserHasPermission(player.UserIDString, PermAdmin);

        private bool CanFly(BasePlayer player)
            => IsAdmin(player) || (player != null && permission.UserHasPermission(player.UserIDString, PermUse));

        private bool CanSetClip(BasePlayer player)
            => IsAdmin(player) || (player != null && permission.UserHasPermission(player.UserIDString, PermSetClip));

        private void CmdFly(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (args.Length > 0)
            {
                // Sub-commands are checked before the admin player lookup below, or "clip" would be
                // read as a player name.
                switch (args[0].ToLowerInvariant())
                {
                    case "clip":
                    {
                        var value = args.Length > 1 ? args[1] : null;

                        // /fly clip on|off|default <player> -- the name may contain spaces.
                        if (args.Length > 2)
                        {
                            SetClipForOther(player, value, string.Join(" ", args, 2, args.Length - 2));
                            return;
                        }

                        // /fly clip <player> -- an admin asking what someone else is on.
                        bool ignored;
                        if (value != null && IsAdmin(player) && !IsReset(value) && !TryParseToggle(value, out ignored))
                        {
                            ShowClipForOther(player, value);
                            return;
                        }

                        SetClip(player, value);
                        return;
                    }

                    case "speed":
                        SetSpeed(player, args.Length > 1 ? args[1] : null);
                        return;

                    case "hz":
                        SetUpdateRate(player, args.Length > 1 ? args[1] : null);
                        return;

                    // Renamed, and its meaning is inverted (noclip true = clip off), so point people
                    // at the new spelling rather than guessing what they meant.
                    case "noclip":
                        Reply(player, "NoclipRenamed", "{command}", _config.Command);
                        return;
                }

                if (!IsAdmin(player))
                {
                    Reply(player, "NoPermissionOthers");
                    return;
                }

                var target = FindPlayer(args[0]);
                if (target == null)
                {
                    Reply(player, "PlayerNotFound", "{name}", args[0]);
                    return;
                }

                Toggle(target, player);
                return;
            }

            if (!CanFly(player))
            {
                Reply(player, "NoPermission");
                return;
            }

            Toggle(player, null);
        }

        // `by` is the admin who ran /fly <player>, or null when a player toggled their own flight.
        private void Toggle(BasePlayer player, BasePlayer by)
        {
            if (_flights.ContainsKey(player.userID))
            {
                StopFlight(_flights[player.userID], "Command");
                Reply(player, "FlightOff");
                if (by != null && by != player) Reply(by, "FlightOffOther", "{name}", player.displayName);
                return;
            }

            // fly.admin skips the takeoff limits (combat block, cooldown, building blocked) both for
            // their own flight and for anyone they launch with /fly <player>.
            string error;
            if (!StartFlight(player, IsAdmin(player) || IsAdmin(by), out error))
            {
                if (by != null && by != player) Reply(by, "CannotFlyOther", "{name}", player.displayName, "{reason}", error);
                else SendReply(player, error);
                return;
            }

            Reply(player, "FlightOn");
            if (CanSetClip(player))
                Reply(player, ClipFor(player) ? "FlightOnClipOn" : "FlightOnClipOff", "{command}", _config.Command);
            if (by != null && by != player) Reply(by, "FlightOnOther", "{name}", player.displayName);
        }

        // ---- /fly clip on|off ---------------------------------------------------------------

        // value == null just reports the current setting.
        private void SetClip(BasePlayer player, string value)
        {
            if (!CanSetClip(player))
            {
                Reply(player, "NoPermissionClip");
                return;
            }

            if (string.IsNullOrEmpty(value))
            {
                Reply(player, ClipFor(player) ? "ClipStateOn" : "ClipStateOff");
                return;
            }

            if (IsReset(value))
            {
                var cleared = PrefsFor(player.userID);
                cleared.Clip = null;
                cleared.ClipSetByAdmin = false;
                SaveData();
                Reply(player, ClipFor(player) ? "ClipResetOn" : "ClipResetOff");
                return;
            }

            bool wanted;
            if (!TryParseToggle(value, out wanted))
            {
                Reply(player, "ClipUsage", "{command}", _config.Command);
                return;
            }

            // A player with the permission can change a setting an admin made for them.
            var prefs = PrefsFor(player.userID);
            prefs.Clip = wanted;
            prefs.ClipSetByAdmin = false;
            SaveData();

            // Read fresh by the flight loop every frame, so this lands mid-flight.
            Reply(player, wanted ? "ClipSetOn" : "ClipSetOff");
        }

        // True = stop at terrain and buildings.
        private bool ClipFor(BasePlayer player)
            => player != null ? ClipFor(player.userID) : _config.Restrictions.ClipByDefault;

        // Works for offline players too, which is what lets an admin set someone's clip by Steam ID.
        // A choice an admin made always counts; a player's own only while they hold the permission.
        private bool ClipFor(ulong userId)
        {
            Preferences prefs;
            if (_prefs.TryGetValue(userId, out prefs) && prefs.Clip.HasValue)
            {
                var id = userId.ToString();
                if (prefs.ClipSetByAdmin
                    || permission.UserHasPermission(id, PermSetClip)
                    || permission.UserHasPermission(id, PermAdmin))
                    return prefs.Clip.Value;
            }

            return _config.Restrictions.ClipByDefault;
        }

        // /fly clip on|off|default <player>
        private void SetClipForOther(BasePlayer admin, string value, string needle)
        {
            if (!IsAdmin(admin))
            {
                Reply(admin, "NoPermissionClipOthers");
                return;
            }

            ulong userId;
            string name;
            BasePlayer online;
            if (!ResolveTarget(needle, out userId, out name, out online))
            {
                Reply(admin, "PlayerNotFound", "{name}", needle);
                return;
            }

            var prefs = PrefsFor(userId);

            if (IsReset(value))
            {
                prefs.Clip = null;
                prefs.ClipSetByAdmin = false;
                SaveData();

                var now = ClipFor(userId) ? "on" : "off";
                Reply(admin, "ClipOtherReset", "{name}", name, "{state}", now);
                if (online != null && online != admin)
                    Reply(online, ClipFor(userId) ? "ClipResetOn" : "ClipResetOff");
                return;
            }

            bool wanted;
            if (!TryParseToggle(value, out wanted))
            {
                Reply(admin, "ClipOtherUsage", "{command}", _config.Command);
                return;
            }

            prefs.Clip = wanted;
            prefs.ClipSetByAdmin = true;
            SaveData();

            // Read fresh by the flight loop every frame, so this lands on them mid-flight too.
            Reply(admin, "ClipOtherSet", "{name}", name, "{state}", wanted ? "on" : "off");
            if (online != null && online != admin)
                Reply(online, wanted ? "ClipSetOnByAdmin" : "ClipSetOffByAdmin");
        }

        // /fly clip <player>
        private void ShowClipForOther(BasePlayer admin, string needle)
        {
            ulong userId;
            string name;
            BasePlayer online;
            if (!ResolveTarget(needle, out userId, out name, out online))
            {
                Reply(admin, "PlayerNotFound", "{name}", needle);
                return;
            }

            Preferences prefs;
            var id = userId.ToString();
            string source;
            if (!_prefs.TryGetValue(userId, out prefs) || !prefs.Clip.HasValue)
                source = "server default";
            else if (prefs.ClipSetByAdmin)
                source = "set by an admin";
            else if (permission.UserHasPermission(id, PermSetClip) || permission.UserHasPermission(id, PermAdmin))
                source = "their own choice";
            else
                source = "server default - their own saved choice is ignored without fly.cansetclip";

            Reply(admin, "ClipOtherState", "{name}", name, "{state}", ClipFor(userId) ? "on" : "off", "{source}", source);
        }

        // An online player by name, partial name or Steam ID; or any Steam ID, online or not.
        private bool ResolveTarget(string needle, out ulong userId, out string name, out BasePlayer online)
        {
            online = FindPlayer(needle);
            if (online != null)
            {
                userId = online.userID;
                name = online.displayName;
                return true;
            }

            if (ulong.TryParse(needle, out userId) && userId > 76561197960265728UL)
            {
                name = needle;
                return true;
            }

            userId = 0;
            name = null;
            return false;
        }

        // ---- /fly speed <n>x  (fly.admin, applies to everyone) ------------------------------

        // Both of these change the server-wide value and write it to the config file, so it
        // survives a reload. They land mid-flight for everyone already in the air.
        private void SetSpeed(BasePlayer player, string value)
        {
            if (!IsAdmin(player))
            {
                Reply(player, "AdminOnly");
                return;
            }

            if (string.IsNullOrEmpty(value))
            {
                ReplySpeed(player, "SpeedState");
                return;
            }

            float wanted;
            if (IsReset(value))
            {
                wanted = new MovementSettings().DefaultSpeedMultiplier;
            }
            else if (!TryParseNumber(value, "x", out wanted) || wanted <= 0f)
            {
                Reply(player, "SpeedUsage", "{command}", _config.Command);
                return;
            }

            _config.Movement.DefaultSpeedMultiplier = wanted;
            SaveConfig();
            ReplySpeed(player, IsReset(value) ? "SpeedReset" : "SpeedSet");
        }

        private void ReplySpeed(BasePlayer player, string key)
        {
            var movement = _config.Movement;
            var multiplier = SpeedMultiplier;
            Reply(player, key,
                "{speed}", Fmt(multiplier),
                "{ms}", Fmt(movement.HorizontalSpeed * multiplier),
                "{sprint}", Fmt(movement.HorizontalSpeed * movement.SprintMultiplier * multiplier));
        }

        // Scales everything the flight loop computes, for everyone.
        private float SpeedMultiplier => Mathf.Max(0.1f, _config.Movement.DefaultSpeedMultiplier);

        // ---- /fly hz <n>  (fly.admin, applies to everyone) ----------------------------------

        private const float MinUpdateRate = 5f;

        private void SetUpdateRate(BasePlayer player, string value)
        {
            if (!IsAdmin(player))
            {
                Reply(player, "AdminOnly");
                return;
            }

            if (string.IsNullOrEmpty(value))
            {
                Reply(player, "HzState", "{hz}", Fmt(UpdateRate));
                return;
            }

            float wanted;
            if (IsReset(value))
            {
                wanted = new MovementSettings().DefaultUpdateRate;
            }
            else if (!TryParseNumber(value, "hz", out wanted) || wanted < MinUpdateRate)
            {
                Reply(player, "HzUsage", "{command}", _config.Command, "{min}", Fmt(MinUpdateRate));
                return;
            }

            _config.Movement.DefaultUpdateRate = wanted;
            SaveConfig();
            Reply(player, IsReset(value) ? "HzReset" : "HzSet", "{hz}", Fmt(UpdateRate));
        }

        // Pushes happen at most once per server frame, so the server FPS is a ceiling on top of this.
        private float UpdateRate => Mathf.Max(MinUpdateRate, _config.Movement.DefaultUpdateRate);

        // ---- parsing ------------------------------------------------------------------------

        private static bool IsReset(string value)
        {
            var v = value.ToLowerInvariant();
            return v == "default" || v == "reset" || v == "auto";
        }

        // Accepts "2", "2x", "2.5x" / "60", "60hz".
        private static bool TryParseNumber(string value, string suffix, out float result)
        {
            var text = value.Trim();
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                text = text.Substring(0, text.Length - suffix.Length);

            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                && !float.IsNaN(result) && !float.IsInfinity(result);
        }

        private static string Fmt(float value)
            => value.ToString(Mathf.Approximately(value, Mathf.Round(value)) ? "0" : "0.0", CultureInfo.InvariantCulture);

        private static bool TryParseToggle(string value, out bool result)
        {
            switch (value.ToLowerInvariant())
            {
                case "on": case "true": case "1": case "yes": case "y":
                    result = true; return true;
                case "off": case "false": case "0": case "no": case "n":
                    result = false; return true;
                default:
                    result = false; return false;
            }
        }

        #endregion

        #region Takeoff and landing

        private bool StartFlight(BasePlayer player, bool bypassLimits, out string error)
        {
            error = null;

            if (player == null || !player.IsConnected || player.IsDead() || player.IsSleeping())
            {
                error = Lang("CannotFlyNow", player?.UserIDString);
                return false;
            }

            if (player.IsWounded())
            {
                error = Lang("CannotFlyWounded", player.UserIDString);
                return false;
            }

            if (player.isMounted || player.GetParentEntity() != null)
            {
                error = Lang("CannotFlyMounted", player.UserIDString);
                return false;
            }

            if (!bypassLimits)
            {
                float until;
                if (_combatUntil.TryGetValue(player.userID, out until) && until > Time.realtimeSinceStartup)
                {
                    error = Lang("CombatBlocked", player.UserIDString)
                        .Replace("{seconds}", Mathf.CeilToInt(until - Time.realtimeSinceStartup).ToString());
                    return false;
                }

                if (_cooldownUntil.TryGetValue(player.userID, out until) && until > Time.realtimeSinceStartup)
                {
                    error = Lang("Cooldown", player.UserIDString)
                        .Replace("{seconds}", Mathf.CeilToInt(until - Time.realtimeSinceStartup).ToString());
                    return false;
                }

                if (_config.Limits.RefuseTakeoffWhenBuildingBlocked && player.IsBuildingBlocked(false))
                {
                    error = Lang("BuildingBlocked", player.UserIDString);
                    return false;
                }
            }

            var seat = SpawnSeat(player);
            if (seat == null)
            {
                error = Lang("SeatFailed", player.UserIDString);
                return false;
            }

            seat.MountPlayer(player);
            if (seat.GetMounted() != player)
            {
                seat.Kill();
                error = Lang("SeatFailed", player.UserIDString);
                return false;
            }

            var flight = new Flight
            {
                UserId = player.userID,
                Player = player,
                Seat = seat,
                Started = Time.realtimeSinceStartup,
                TakeoffPosition = player.transform.position,
            };

            var controller = seat.gameObject.AddComponent<FlightController>();
            controller.Bind(this, flight);
            flight.Controller = controller;

            _flights[player.userID] = flight;
            return true;
        }

        private BaseMountable SpawnSeat(BasePlayer player)
        {
            var entity = GameManager.server.CreateEntity(
                _config.MountPrefab, player.transform.position, Quaternion.Euler(0f, player.viewAngles.y, 0f));

            if (entity == null) return null;

            var seat = entity as BaseMountable;
            if (seat == null)
            {
                entity.Kill();
                return null;
            }

            entity.OwnerID = player.userID;
            entity.enableSaving = false;

            // syncPosition has to be set before Spawn -- ServerInit reads it once to arm the 10 Hz
            // NetworkPositionTick invoke (and to create NetworkPosTickCallback, which the flight loop
            // calls directly to push position faster than 10 Hz).
            entity.syncPosition = true;

            // isMobile before Spawn too: ServerInit uses it to register the seat for the mountable
            // fixed-update cycle, and BasePlayer's tick only leaves a mounted player's rotation alone
            // when the seat it is on is mobile (BasePlayer.cs:13673).
            seat.isMobile = true;

            // The default seat is a vehicle mount point with nothing above it. VehicleParent() already
            // copes with that, but saying so outright keeps it from ever walking a parent chain if the
            // seat somehow ends up inside one.
            var mountPoint = seat as BaseVehicleMountPoint;
            if (mountPoint != null) mountPoint.ignoreVehicleParent = true;

            // Nothing holds the seat up, so strip the components that delete deployables floating in
            // mid-air before a physics event notices.
            DestroyComponent<GroundWatch>(entity);
            DestroyComponent<DestroyOnGroundMissing>(entity);

            entity.Spawn();

            // Server-side colliders off: the seat should never stop a bullet, block a raycast or
            // shove anything as it flies past.
            foreach (var collider in entity.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;

            // The mount anchor is offset from the seat origin; shift the seat so the anchor -- which
            // is where the player ends up -- lands exactly where the player is standing.
            if (seat.mountAnchor != null)
            {
                var offset = seat.mountAnchor.position - entity.transform.position;
                entity.transform.position = player.transform.position - offset;
                entity.transform.hasChanged = true;
            }

            return seat;
        }

        private static void DestroyComponent<T>(BaseEntity entity) where T : Component
        {
            var component = entity.GetComponent<T>();
            if (component != null) UnityEngine.Object.DestroyImmediate(component);
        }

        private void StopFlight(Flight flight, string reason)
        {
            if (flight == null || flight.Landing) return;
            flight.Landing = true;

            var player = flight.Player;
            var seat = flight.Seat;

            if (flight.Controller != null)
            {
                flight.Controller.Detach();
                UnityEngine.Object.Destroy(flight.Controller);
            }

            _flights.Remove(flight.UserId);

            var landing = player != null ? player.transform.position : Vector3.zero;
            if (player != null && _config.Restrictions.LandOnGroundBelow)
                landing = ResolveLanding(player, landing);

            if (seat != null && !seat.IsDestroyed)
            {
                // Lite dismount only. The full path looks for a legal dismount point and, when it
                // cannot find one -- which is every time you are 200m up -- kills the player.
                if (player != null && seat.GetMounted() == player)
                    seat.DismountPlayer(player, true);
                seat.Kill();
            }

            if (player == null || !player.IsConnected) return;

            player.EnsureDismounted();
            player.MovePosition(landing);
            player.ClientRPC(RpcTarget.Player("ForcePositionTo", player), landing);
            player.SendModelState(true);
            player.SendNetworkUpdateImmediate();
            player.ForceUpdateTriggers();

            // The drop onto the landing point is a teleport as far as antihack is concerned.
            player.PauseFlyHackDetection(10f);
            player.PauseVehicleNoClipDetection(10f);
            player.PauseSpeedHackDetection(10f);

            if (_config.Limits.CooldownSeconds > 0f && reason != "Unload")
                _cooldownUntil[flight.UserId] = Time.realtimeSinceStartup + _config.Limits.CooldownSeconds;
        }

        // Straight down from where flight ended, so the player is set down on whatever was under them
        // rather than dropped from altitude. Every candidate is clearance-tested before it is used:
        // the first thing under you is often the *inside* of a monument's roof lattice, and dropping
        // someone there wedges them in geometry they cannot walk out of.
        private Vector3 ResolveLanding(BasePlayer player, Vector3 from)
        {
            var radius = BasePlayer.GetRadius();
            var height = player.GetHeight();

            RaycastHit hit;
            if (Physics.Raycast(from + Vector3.up * 0.5f, Vector3.down, out hit, 1000f, LandMask, QueryTriggerInteraction.Ignore))
            {
                var onSurface = hit.point + Vector3.up * 0.1f;
                var clear = FirstClearSpot(onSurface, radius, height);
                if (clear.HasValue) return clear.Value;
            }

            // Nothing under them at all (a gap in the terrain collider, a hole in a monument): fall
            // back to the heightmap. GetHeight(Vector3) takes world coordinates -- the two-float
            // overload takes normalised ones and quietly returns the ocean floor.
            if (TerrainMeta.HeightMap != null)
            {
                var ground = TerrainMeta.HeightMap.GetHeight(from);
                if (TerrainMeta.WaterMap != null)
                    ground = Mathf.Max(ground, TerrainMeta.WaterMap.GetHeight(from));

                var onTerrain = FirstClearSpot(new Vector3(from.x, ground, from.z), radius, height);
                if (onTerrain.HasValue) return onTerrain.Value;
            }

            // Everything below is solid. Better to leave them in the open air where they stopped --
            // they take a fall, but they are not welded inside a wall.
            return from;
        }

        // Walk upward from a candidate until a player actually fits.
        private static Vector3? FirstClearSpot(Vector3 candidate, float radius, float height)
        {
            for (var lift = 0f; lift <= 6f; lift += 0.5f)
            {
                var spot = candidate + Vector3.up * lift;
                if (HasRoom(spot, radius, height)) return spot;
            }

            return null;
        }

        private static bool HasRoom(Vector3 feet, float radius, float height)
        {
            var bottom = feet + Vector3.up * radius;
            var top = feet + Vector3.up * Mathf.Max(height - radius, radius);
            return !Physics.CheckCapsule(bottom, top, radius, SolidMask, QueryTriggerInteraction.Ignore);
        }

        #endregion

        #region Watchdog

        // One pass a second over everyone in the air: the per-frame loop only moves the seat.
        private void Watchdog()
        {
            if (_flights.Count == 0) return;

            foreach (var id in new List<ulong>(_flights.Keys))
            {
                Flight flight;
                if (!_flights.TryGetValue(id, out flight)) continue;

                var player = flight.Player;

                if (player == null || !player.IsConnected || player.IsDead() || player.IsSleeping()
                    || flight.Seat == null || flight.Seat.IsDestroyed || player.GetMounted() != flight.Seat)
                {
                    StopFlight(flight, "Invalid");
                    continue;
                }

                if (_config.Limits.MaxFlightSeconds > 0f
                    && Time.realtimeSinceStartup - flight.Started >= _config.Limits.MaxFlightSeconds)
                {
                    StopFlight(flight, "TimeLimit");
                    Reply(player, "TimeLimitReached");
                    continue;
                }

                if (_config.Limits.LandWhenBuildingBlocked && player.IsBuildingBlocked(false))
                {
                    StopFlight(flight, "BuildingBlocked");
                    Reply(player, "LandedBuildingBlocked");
                }
            }
        }

        #endregion

        #region Hooks

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            Flight flight;
            if (player != null && _flights.TryGetValue(player.userID, out flight))
                StopFlight(flight, "Disconnect");
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            Flight flight;
            if (player != null && _flights.TryGetValue(player.userID, out flight))
                StopFlight(flight, "Death");
        }

        // Something other than this plugin killing the seat -- a cleanup sweep, a server wipe of
        // unsaved entities -- would reach BaseMountable.DismountAllPlayers, which takes the full
        // dismount path and kills a rider it cannot find a legal spot for. Land them first instead.
        // OnEntityKill runs ahead of the entity's own teardown.
        private void OnEntityKill(BaseNetworkable entity)
        {
            if (_flights.Count == 0) return;

            var seat = entity as BaseMountable;
            if (seat == null) return;

            Flight found = null;
            foreach (var flight in _flights.Values)
            {
                if (flight.Seat != seat) continue;
                found = flight;
                break;
            }

            if (found != null) StopFlight(found, "SeatDestroyed");
        }

        // Vanilla dismount (space / E on a chair) would drop the player at altitude and, failing to
        // find a legal spot, kill them. Flight ends through the command instead.
        private object CanDismountEntity(BasePlayer player, BaseMountable entity)
        {
            return IsFlightSeat(entity) ? (object)false : null;
        }

        private object CanMountEntity(BasePlayer player, BaseMountable entity)
        {
            if (!IsFlightSeat(entity)) return null;
            return entity.OwnerID == player.userID ? null : (object)false;
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return null;

            // The seat is scenery, not a target.
            var seat = entity as BaseMountable;
            if (seat != null && IsFlightSeat(seat)) return true;

            var victim = entity as BasePlayer;
            if (victim == null || victim.IsNpc) return null;

            var attacker = info?.InitiatorPlayer;
            if (attacker != null && !attacker.IsNpc && attacker != victim)
            {
                MarkCombat(attacker);
                MarkCombat(victim);
            }
            else
            {
                MarkCombat(victim);
            }

            if (!_flights.ContainsKey(victim.userID)) return null;

            // AntiHack.ValidateAgainstTerrain hurts for 1000 with DamageType.Suicide when it finds a
            // player inside terrain (antihack.terrain_kill). A rider passing through a hill is not
            // cheating, so that one is blocked even when Invulnerable is off.
            if (info != null && info.damageTypes != null && info.damageTypes.Has(Rust.DamageType.Suicide))
                return true;

            return _config.Restrictions.Invulnerable ? (object)true : null;
        }

        private object OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (!_config.Restrictions.BlockAttacking || attacker == null) return null;
            if (!_flights.ContainsKey(attacker.userID)) return null;

            Reply(attacker, "BlockedWhileFlying");
            return true;
        }

        private object CanLootEntity(BasePlayer player, StorageContainer container) => BlockLoot(player);
        private object CanLootEntity(BasePlayer player, LootableCorpse corpse) => BlockLoot(player);
        private object CanLootEntity(BasePlayer player, DroppedItemContainer container) => BlockLoot(player);
        private object CanLootEntity(BasePlayer player, ContainerIOEntity container) => BlockLoot(player);
        private object CanLootPlayer(BasePlayer target, BasePlayer looter) => BlockLoot(looter);

        private object BlockLoot(BasePlayer player)
        {
            if (!_config.Restrictions.BlockLooting || player == null) return null;
            if (!_flights.ContainsKey(player.userID)) return null;

            Reply(player, "BlockedWhileFlying");
            return false;
        }

        // Signatures below are the ones GatherProbe.cs confirmed actually fire on this build --
        // the plausible-looking variants (OnDispenserBonus with a BasePlayer, two-argument
        // OnGrowableGather) never get called and fail silently.
        private object OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
            => BlockGather(entity as BasePlayer);

        private object OnDispenserBonus(ResourceDispenser dispenser, BaseEntity entity, Item item)
            => BlockGather(entity as BasePlayer);

        private object OnCollectiblePickup(CollectibleEntity collectible, BasePlayer player)
            => BlockGather(player);

        private object OnGrowableGather(GrowableEntity growable, Item item, BasePlayer player)
            => BlockGather(player);

        private object BlockGather(BasePlayer player)
        {
            if (!_config.Restrictions.BlockGathering || player == null) return null;
            if (!_flights.ContainsKey(player.userID)) return null;

            Reply(player, "BlockedWhileFlying");
            return true;
        }

        private object CanBuild(Planner planner, Construction construction, Construction.Target target)
        {
            if (!_config.Restrictions.BlockBuilding || planner == null) return null;

            var player = planner.GetOwnerPlayer();
            if (player == null || !_flights.ContainsKey(player.userID)) return null;

            Reply(player, "BlockedWhileFlying");
            return false;
        }

        private object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            if (!_config.Restrictions.BlockGathering || player == null) return null;
            if (!_flights.ContainsKey(player.userID)) return null;

            Reply(player, "BlockedWhileFlying");
            return false;
        }

        // A rider is not cheating -- the server is the thing moving them. AddViolation is the single
        // funnel every antihack type goes through, and EnforceViolations is what turns a level into a
        // kick; skipping both for anyone currently in the air is narrower than exempting them from
        // antihack wholesale, and it reverts the moment they land.
        private static bool IsAirborne(BasePlayer player)
        {
            var instance = Instance;
            return instance != null && player != null && instance._flights.ContainsKey(player.userID);
        }

        [HarmonyPatch(typeof(AntiHack), nameof(AntiHack.AddViolation))]
        internal static class AddViolationPatch
        {
            private static bool Prefix(BasePlayer ply) => !IsAirborne(ply);
        }

        [HarmonyPatch(typeof(AntiHack), nameof(AntiHack.EnforceViolations))]
        internal static class EnforceViolationsPatch
        {
            private static bool Prefix(BasePlayer ply, ref bool __result)
            {
                if (!IsAirborne(ply)) return true;

                __result = false;
                return false;
            }
        }

        private bool IsFlightSeat(BaseMountable entity)
        {
            if (entity == null) return false;

            foreach (var flight in _flights.Values)
                if (flight.Seat == entity) return true;

            return false;
        }

        private void MarkCombat(BasePlayer player)
        {
            if (player == null || _config.Limits.CombatBlockSeconds <= 0f) return;
            _combatUntil[player.userID] = Time.realtimeSinceStartup + _config.Limits.CombatBlockSeconds;
        }

        #endregion

        #region Flight loop

        private class Flight
        {
            public ulong UserId;
            public bool Landing;
            public BasePlayer Player;
            public BaseMountable Seat;
            public FlightController Controller;
            public float Started;
            public Vector3 TakeoffPosition;
        }

        // Lives on the seat's GameObject, so it dies with the seat. Runs in Update (server frame
        // rate) rather than FixedUpdate so the ride is as smooth as the server is.
        private class FlightController : MonoBehaviour
        {
            private Fly _plugin;
            private Flight _flight;
            private Vector3 _velocity;
            private float _nextNetworkPush;

            public void Bind(Fly plugin, Flight flight)
            {
                _plugin = plugin;
                _flight = flight;
            }

            public void Detach()
            {
                _plugin = null;
                _flight = null;
                enabled = false;
            }

            private void Update()
            {
                if (_plugin == null || _flight == null) return;

                var player = _flight.Player;
                var seat = _flight.Seat;
                if (player == null || seat == null || seat.IsDestroyed) return;

                var movement = _plugin._config.Movement;
                var input = player.serverInput;
                var aim = input != null && input.current != null ? input.current.aimAngles : player.viewAngles;
                var look = Quaternion.Euler(aim);

                var forward = look * Vector3.forward;
                var right = look * Vector3.right;
                right.y = 0f;
                right = right.sqrMagnitude > 0.0001f ? right.normalized : Vector3.zero;

                var wish = Vector3.zero;
                if (input != null)
                {
                    if (input.IsDown(BUTTON.FORWARD)) wish += forward;
                    if (input.IsDown(BUTTON.BACKWARD)) wish -= forward;
                    if (input.IsDown(BUTTON.LEFT)) wish -= right;
                    if (input.IsDown(BUTTON.RIGHT)) wish += right;
                }

                if (wish.sqrMagnitude > 1f) wish.Normalize();
                wish *= movement.HorizontalSpeed;

                if (input != null)
                {
                    var vertical = 0f;
                    if (input.IsDown(BUTTON.JUMP)) vertical += 1f;
                    if (input.IsDown(BUTTON.DUCK)) vertical -= 1f;
                    wish += Vector3.up * (vertical * movement.VerticalSpeed);

                    if (input.IsDown(BUTTON.SPRINT)) wish *= movement.SprintMultiplier;
                }

                // One multiplier over everything the player asked for, sprint included, so /fly speed
                // reads as "my flying is N times faster" rather than only affecting one key.
                var speedScale = _plugin.SpeedMultiplier;
                wish *= speedScale;

                var dt = Time.deltaTime;

                // Acceleration scales with it too, or a fast rider spends seconds drifting up to
                // speed and the same again overshooting whatever they aimed at.
                _velocity = Vector3.MoveTowards(_velocity, wish, Mathf.Max(1f, movement.Acceleration) * speedScale * dt);

                var next = transform.position + _velocity * dt;

                // Clip on: stop at whatever is in the way and slide along it, instead of going
                // through. The sweep is a sphere so the rider cannot squeeze through a wall seam.
                if (_plugin.ClipFor(player))
                {
                    var step = next - transform.position;
                    var distance = step.magnitude;
                    if (distance > 0.0001f)
                    {
                        var radius = Mathf.Max(0.1f, _plugin._config.Restrictions.SolidRadius);
                        RaycastHit blocker;
                        if (Physics.SphereCast(transform.position, radius, step / distance, out blocker,
                                distance + radius, SolidMask, QueryTriggerInteraction.Ignore))
                        {
                            next = transform.position + step / distance * Mathf.Max(0f, blocker.distance - radius);
                            _velocity = Vector3.ProjectOnPlane(_velocity, blocker.normal);
                        }
                    }
                }

                var ceiling = _plugin._config.Limits.MaxHeightAboveTerrain;
                if (ceiling > 0f && TerrainMeta.HeightMap != null)
                {
                    var cap = TerrainMeta.HeightMap.GetHeight(next) + ceiling;
                    if (next.y > cap)
                    {
                        next.y = cap;
                        if (_velocity.y > 0f) _velocity.y = 0f;
                    }
                }

                // Leaving the playable area makes the server treat the seat as invalidly positioned
                // and delete it out from under the rider, so stop at the edge instead.
                if (!ValidBounds.Test(seat, next))
                {
                    _velocity = Vector3.zero;
                    return;
                }

                // The seat's rotation is deliberately never touched after takeoff. A mounted client
                // clamps its view *relative to the seat*, so the world aim it reports back is
                // seatYaw + mouseYaw. Feeding that straight back into the seat's yaw adds mouseYaw
                // again every frame and the player spins on the spot for as long as they are not
                // looking exactly down the seat's nose. The seat prefab's yaw clamp is what gives
                // the rider a full turn instead -- see the config note on the prefab.
                transform.position = next;
                transform.hasChanged = true;


                // The engine only networks a moved entity at 10 Hz. The rider's camera hangs off the
                // seat's transform, so push it faster by hand -- NetworkPosTickCallback is the public
                // handle on the same private position update the engine's own tick calls.
                if (Time.realtimeSinceStartup >= _nextNetworkPush)
                {
                    _nextNetworkPush = Time.realtimeSinceStartup + 1f / _plugin.UpdateRate;
                    var push = seat.NetworkPosTickCallback;
                    if (push != null) push();
                }
            }
        }

        #endregion

        #region Helpers

        private BasePlayer FindPlayer(string needle)
        {
            ulong steamId;
            if (ulong.TryParse(needle, out steamId) && steamId > 76561197960265728UL)
            {
                foreach (var candidate in BasePlayer.activePlayerList)
                    if (candidate.userID == steamId) return candidate;
                return null;
            }

            BasePlayer partial = null;
            foreach (var candidate in BasePlayer.activePlayerList)
            {
                if (candidate.displayName.Equals(needle, StringComparison.OrdinalIgnoreCase)) return candidate;
                if (candidate.displayName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (partial != null) return null; // ambiguous
                    partial = candidate;
                }
            }

            return partial;
        }

        private void Reply(BasePlayer player, string key, params string[] replacements)
        {
            if (player == null) return;

            var message = Lang(key, player.UserIDString);
            for (var i = 0; i + 1 < replacements.Length; i += 2)
                message = message.Replace(replacements[i], replacements[i + 1]);

            SendReply(player, message);
        }

        private string Lang(string key, string userId = null) => lang.GetMessage(key, this, userId);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You don't have permission to fly.",
                ["NoPermissionOthers"] = "You don't have permission to toggle flight for other players.",
                ["NoPermissionClip"] = "You don't have permission to change clip.",
                ["NoPermissionClipOthers"] = "You don't have permission to change clip for other players.",
                ["AdminOnly"] = "Only admins can use that.",
                ["PlayerNotFound"] = "No single online player matches '{name}'.",
                ["FlightOn"] = "<color=#8ac6f2>Flight on.</color> W/A/S/D to move, space up, ctrl down, shift for speed. Use the command again to land.",
                ["FlightOnClipOn"] = "Clip is on - you stop at terrain and buildings. /{command} clip off to fly through them.",
                ["FlightOnClipOff"] = "Clip is off - you fly through terrain and buildings. /{command} clip on to stop at them.",
                ["FlightOff"] = "<color=#8ac6f2>Flight off.</color>",
                ["FlightOnOther"] = "Flight enabled for {name}.",
                ["FlightOffOther"] = "Flight disabled for {name}.",
                ["CannotFlyOther"] = "{name} cannot fly right now: {reason}",
                ["CannotFlyNow"] = "You cannot take off right now.",
                ["CannotFlyWounded"] = "You cannot take off while wounded.",
                ["CannotFlyMounted"] = "Get off what you are riding first.",
                ["CombatBlocked"] = "You were in combat recently - wait {seconds}s before flying.",
                ["Cooldown"] = "Flight is on cooldown for another {seconds}s.",
                ["BuildingBlocked"] = "You cannot take off while building blocked.",
                ["SeatFailed"] = "Flight could not be started - check the server console.",
                ["TimeLimitReached"] = "<color=#8ac6f2>Flight time is up - landing.</color>",
                ["LandedBuildingBlocked"] = "<color=#8ac6f2>Landed:</color> you flew into someone else's building privilege.",
                ["BlockedWhileFlying"] = "You cannot do that while flying.",
                ["NoclipRenamed"] = "That command is now /{command} clip on|off - note 'clip on' means you stop at terrain.",
                ["ClipSetOn"] = "<color=#8ac6f2>Clip on.</color> You will stop at terrain and buildings.",
                ["ClipSetOff"] = "<color=#8ac6f2>Clip off.</color> You will fly through terrain and buildings.",
                ["ClipStateOn"] = "Clip is <color=#8ac6f2>on</color> - you stop at terrain and buildings.",
                ["ClipStateOff"] = "Clip is <color=#8ac6f2>off</color> - you fly through terrain and buildings.",
                ["ClipResetOn"] = "Clip back to the server default: <color=#8ac6f2>on</color>.",
                ["ClipResetOff"] = "Clip back to the server default: <color=#8ac6f2>off</color>.",
                ["ClipUsage"] = "Usage: /{command} clip on|off|default  (on = stop at terrain and buildings)",
                ["ClipOtherUsage"] = "Usage: /{command} clip on|off|default <player>",
                ["ClipOtherSet"] = "Clip for {name} is now <color=#8ac6f2>{state}</color>.",
                ["ClipOtherReset"] = "Clip for {name} is back to the server default: <color=#8ac6f2>{state}</color>.",
                ["ClipOtherState"] = "Clip for {name} is <color=#8ac6f2>{state}</color> ({source}).",
                ["ClipSetOnByAdmin"] = "<color=#8ac6f2>An admin turned your clip on.</color> You will stop at terrain and buildings.",
                ["ClipSetOffByAdmin"] = "<color=#8ac6f2>An admin turned your clip off.</color> You will fly through terrain and buildings.",
                ["SpeedSet"] = "<color=#8ac6f2>Flight speed for everyone is now {speed}x.</color> {ms} m/s, {sprint} m/s holding shift.",
                ["SpeedReset"] = "Flight speed for everyone is back to the default: <color=#8ac6f2>{speed}x</color> - {ms} m/s, {sprint} m/s holding shift.",
                ["SpeedState"] = "Flight speed for everyone is <color=#8ac6f2>{speed}x</color> - {ms} m/s, {sprint} m/s holding shift.",
                ["SpeedUsage"] = "Usage: /{command} speed 2x  or  /{command} speed default",
                ["HzSet"] = "<color=#8ac6f2>Update rate for everyone is now {hz} Hz.</color>",
                ["HzReset"] = "Update rate for everyone is back to the default: <color=#8ac6f2>{hz} Hz</color>.",
                ["HzState"] = "Update rate for everyone is <color=#8ac6f2>{hz} Hz</color>.",
                ["HzUsage"] = "Usage: /{command} hz 60  (at least {min})  or  /{command} hz default",
            }, this);
        }

        #endregion

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Chat command")]
            public string Command = "fly";

            // Look clamps are authored in the prefab and applied by the CLIENT, so they cannot be
            // widened from here -- the prefab choice IS the look freedom. This one is the only shape
            // of seat in the game with an unlimited yaw clamp and a usable pitch range, which is what
            // lets the rider turn a full circle while the seat itself stays put.
            [JsonProperty("Seat prefab the player rides (invisible; must have an unlimited yaw clamp or the rider cannot turn around)")]
            public string MountPrefab = "assets/prefabs/vehicle/seats/submarineduopassengerseat.prefab";

            [JsonProperty("Movement")]
            public MovementSettings Movement = new MovementSettings();

            [JsonProperty("Limits")]
            public LimitSettings Limits = new LimitSettings();

            [JsonProperty("While flying")]
            public RestrictionSettings Restrictions = new RestrictionSettings();
        }

        private class MovementSettings
        {
            [JsonProperty("Horizontal speed (m/s)")]
            public float HorizontalSpeed = 12f;

            [JsonProperty("Vertical speed (m/s)")]
            public float VerticalSpeed = 8f;

            [JsonProperty("Sprint multiplier")]
            public float SprintMultiplier = 3f;

            [JsonProperty("Acceleration (m/s^2, higher is twitchier)")]
            public float Acceleration = 60f;

            // Applied on top of the speeds above, sprint included, and to acceleration.
            [JsonProperty("Speed multiplier (fly.admin can change it in game with /<command> speed)")]
            public float DefaultSpeedMultiplier = 2f;

            // How often the seat's position is sent to the client. Higher is smoother; the server
            // FPS is a hard ceiling because updates go out at most once per frame.
            [JsonProperty("Position updates per second (fly.admin can change it in game with /<command> hz)")]
            public float DefaultUpdateRate = 60f;
        }

        private class LimitSettings
        {
            [JsonProperty("Maximum height above terrain (0 = unlimited)")]
            public float MaxHeightAboveTerrain = 0f;

            [JsonProperty("Maximum flight time in seconds (0 = unlimited)")]
            public float MaxFlightSeconds = 0f;

            [JsonProperty("Cooldown between flights in seconds")]
            public float CooldownSeconds = 0f;

            // 0 = take off whenever you like, including mid-fight. Raise it to stop /fly being an
            // escape hatch from a losing gunfight.
            [JsonProperty("Cannot take off for this many seconds after dealing or taking damage (0 = no restriction)")]
            public float CombatBlockSeconds = 0f;

            [JsonProperty("Refuse to take off while building blocked")]
            public bool RefuseTakeoffWhenBuildingBlocked = true;

            [JsonProperty("Land when flying into a building privilege you are not authorised on")]
            public bool LandWhenBuildingBlocked = true;
        }

        private class RestrictionSettings
        {
            [JsonProperty("Invulnerable")]
            public bool Invulnerable = true;

            [JsonProperty("Block attacking")]
            public bool BlockAttacking = true;

            [JsonProperty("Block looting")]
            public bool BlockLooting = true;

            [JsonProperty("Block gathering and pickups")]
            public bool BlockGathering = true;

            [JsonProperty("Block building")]
            public bool BlockBuilding = true;

            [JsonProperty("Set down on the ground below when flight ends")]
            public bool LandOnGroundBelow = true;

            // Players with fly.cansetclip can override this for themselves with /<command> clip on|off.
            [JsonProperty("Clip by default (true = stop at terrain and buildings, false = fly through them)")]
            public bool ClipByDefault = false;

            [JsonProperty("Clip radius in metres (only used while clip is on)")]
            public float SolidRadius = 0.5f;
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<Configuration>() ?? new Configuration();
            }
            catch (Exception ex)
            {
                PrintError($"Configuration file is invalid, using defaults: {ex.Message}");
                _config = new Configuration();
            }

            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion
    }
}
