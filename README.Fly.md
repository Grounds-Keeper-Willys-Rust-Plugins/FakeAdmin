# Fly — user guide

`/fly` lets players fly freely around the map, like admin noclip, **without** being given any
admin or moderator rights. Version 0.8.0.

1. [Installing, and where the files are](#1-installing-and-where-the-files-are)
2. [How the plugin works, and its settings](#2-how-the-plugin-works-and-its-settings)
3. [Permissions](#3-permissions)
   - 3.1 [Custom rules and permission setups](#31-custom-rules-and-permission-setups)
4. [Commands for users](#4-commands-for-users)
5. [Commands for admins](#5-commands-for-admins)
6. [Troubleshooting](#6-troubleshooting)

---

## 1. Installing, and where the files are

**Requirements:** a Rust server running Oxide (uMod). The plugin uses Harmony, which ships with
Oxide, so there is nothing else to install. It has only been tested on Oxide; Carbon is untested.

**Install**

1. Copy `Fly.cs` into the server's `oxide/plugins/` folder. Make sure the account the server runs
   as can read the file.
2. Oxide compiles and loads it automatically within a few seconds. To force it, run
   `oxide.reload Fly` in the server console or over RCON.
3. Check the console for `Loaded plugin Fly v0.8.0`.
4. **Grant permissions.** Nobody can fly until you do — see [section 3](#3-permissions). The usual
   starting point is:
   ```
   oxide.grant group default fly.user
   ```

**Files the plugin creates**

| File | What it holds | Edit it? |
|------|---------------|----------|
| `oxide/config/Fly.json` | All server settings (section 2). `/fly speed` and `/fly hz` also save to it. | Yes — then run `oxide.reload Fly` |
| `oxide/lang/en/Fly.json` | Every chat message players see | Yes, to reword or translate messages |
| `oxide/data/Fly.json` | Each player's clip setting, and whether they or an admin chose it | Not normally |

Changes to the config file do nothing until the plugin is reloaded. Reloading lands anyone who is
currently flying, so they need to run `/fly` again.

Using `/fly speed` or `/fly hz` in game saves the whole config file straight away. If you've edited
the file but not reloaded yet, those edits are overwritten — reload first.

To uninstall, delete `Fly.cs` (anyone flying is landed safely), then delete the three files above
if you don't want to keep the settings.

---

## 2. How the plugin works, and its settings

### What players experience

A player types `/fly` and lifts off from where they stand. They steer with the normal movement
keys, move in whichever direction they are looking, can look all the way around, and land by
typing `/fly` again. When they land they are set down safely on whatever is below them.

While flying, by default they:

- cannot be hurt,
- cannot attack, loot, gather, pick things up or build,
- fly straight through terrain, rocks and buildings (unless clip is on — see below),
- are landed automatically if they fly into a base whose tool cupboard they are not authorised on.

These defaults make `/fly` a way to look around, not a way to cheat. All of them can be changed.

### How it works

Rust's own `noclip` command only works for players the game treats as admins or moderators, and
that can only be set when a player connects. So this plugin does not use it, and never changes a
player's rights.

Instead, the player is seated in an invisible seat, and the server flies the seat. That has a few
effects worth knowing about:

- **Other players see a seated figure floating in the air.** The seat is invisible, the player is
  not.
- **The player's body always faces the direction they took off in.** Their head turns freely and
  they fly wherever they look; only the body stays put. (Turning the seat with the player's view
  makes the player spin in circles, so the seat never turns.)
- **Looking up is limited to about 55°.** That limit comes from the seat itself, not the plugin.
  Use Space to climb straight up.
- **Rust's anti-cheat is switched off for a player while they are in the air, and back on the
  moment they land.** Without this, flying through terrain gets the player killed and kicked. If
  you run another anti-cheat plugin, check it doesn't punish flying players.
- **At high speed the view can stutter, and the world may load in late.** Rust only updates what
  a moving player can see every few seconds, so a very fast flyer can outrun it and see terrain
  and buildings appear late. The defaults (2x speed, 60 updates a second) were chosen as smooth
  enough. Beyond about 3x, expect problems.

### Settings (`oxide/config/Fly.json`)

**General**

| Setting | Default | What it does |
|---------|---------|--------------|
| Chat command | `fly` | The command name. Change it to `flight` and every command becomes `/flight`, `/flight clip`, and so on. |
| Seat prefab | `…/submarineduopassengerseat.prefab` | The invisible seat players ride. Leave this alone unless you know why you're changing it — the seat must allow looking all the way around, or players can't turn. The plugin warns in the console at startup if it doesn't. `ptboatpassenger.prefab` and `smallboatpassenger.prefab` also work, with slightly less upward look. |

**Movement**

| Setting | Default | What it does |
|---------|---------|--------------|
| Horizontal speed (m/s) | `12` | Base forward and sideways speed. |
| Vertical speed (m/s) | `8` | Base speed going up (Space) and down (Ctrl). |
| Sprint multiplier | `3` | How much faster holding Shift makes you. |
| Acceleration (m/s²) | `60` | How quickly you reach full speed and stop. Higher is snappier, lower is floatier. |
| Speed multiplier | `2` | Applied on top of all the speeds above, and to acceleration, for everyone. At the defaults a player flies at 24 m/s, or 72 m/s holding Shift. Admins can change it in game with `/fly speed`. |
| Position updates per second | `60` | How often a flying player's position is sent to them, for everyone. Higher is smoother but uses more network traffic. It can't go above the server's frame rate. Admins can change it in game with `/fly hz`. |

**Limits**

| Setting | Default | What it does |
|---------|---------|--------------|
| Maximum height above terrain | `0` | Stops players climbing higher than this many metres above the ground. `0` = no limit. |
| Maximum flight time in seconds | `0` | Lands players automatically after this long. `0` = no limit. |
| Cooldown between flights in seconds | `0` | How long after landing before a player can take off again. |
| Cannot take off … after dealing or taking damage | `0` | Stops players taking off within this many seconds of a fight. `0` = can take off any time, even mid-fight. Set it to something like `30` on a PvP server so `/fly` isn't an escape from a losing fight. |
| Refuse to take off while building blocked | `true` | Players can't take off from inside someone else's base. |
| Land when flying into a building privilege you are not authorised on | `true` | Lands players who fly into someone else's base. Stops `/fly` being used to scout or get into bases. |

**While flying**

| Setting | Default | What it does |
|---------|---------|--------------|
| Invulnerable | `true` | Flying players can't be hurt. If turned off they can be shot down, but flying through terrain still never kills them. |
| Block attacking | `true` | Flying players can't hit or shoot anything. |
| Block looting | `true` | Flying players can't open boxes, corpses, dropped loot or other players. |
| Block gathering and pickups | `true` | Flying players can't harvest, pick up items or pick up deployables. |
| Block building | `true` | Flying players can't place building parts. |
| Set down on the ground below when flight ends | `true` | When a player lands they're placed safely on the first clear spot below them. If `false`, they drop from wherever they are — and can take fall damage. |
| Clip by default | `false` | `false` = players fly through terrain and buildings. `true` = they stop at them and slide along them. Players with `fly.cansetclip` can choose for themselves, and admins can set it for any player. |
| Clip radius in metres | `0.5` | How close to a surface a player can get while clip is on. |

---

## 3. Permissions

There are three permissions. None are granted automatically.

| Permission | Who it's for | What it allows |
|------------|--------------|----------------|
| `fly.user` | **Users** | Using `/fly` to fly and land. Required to fly at all. |
| `fly.cansetclip` | **Users** you trust with it | Using `/fly clip on\|off` to choose whether they fly through terrain and buildings. Without it, they get the server's **Clip by default** setting. |
| `fly.admin` | **Admins** | Everything above, plus: `/fly <player>` to launch or land someone else, `/fly clip … <player>` to set someone else's clip, `/fly speed` and `/fly hz` to change speed and update rate for everyone, and skipping the takeoff checks (combat block, cooldown, building blocked) for their own flights and for anyone they launch. |

A few things to know:

- **`fly.admin` includes the other two.** Admins don't also need `fly.user` or `fly.cansetclip`.
- **Being a Rust admin or moderator does not give you `fly.admin` by itself.** Plugin permissions
  are separate from the server's admin list. The easy way to give it to all server owners is
  `oxide.grant group admin fly.admin` — Oxide puts server owners into its `admin` group
  automatically.
- **`fly.admin` does not skip rules that apply while flying.** Admins are still landed by the
  flight time limit and when they enter someone else's base, are still held to the height limit,
  and still can't attack, loot, gather or build if those are blocked.
- **A player's own clip choice is kept if the permission is taken away, but not used.** A player
  who loses `fly.cansetclip` flies with the server default, and their choice comes back if the
  permission is granted again.
- **A clip setting made by an admin always applies**, whether or not the player has
  `fly.cansetclip`. If the player does have it, they can change it back themselves.

**Granting and removing** (server console or RCON):

```
oxide.grant group default fly.user            everyone can fly
oxide.grant user "Some Player" fly.cansetclip one player can choose clip
oxide.grant user 76561197960000000 fly.admin  an admin, by Steam ID
oxide.revoke group default fly.user           take it away again
oxide.show perm fly.user                      who has it
oxide.show user "Some Player"                 everything one player has
```

### 3.1 Custom rules and permission setups

Oxide permissions can be given to **groups** (like `default`, which every player is in, or your own
groups such as `vip`) or to **individual players**. That's how you decide who can use what.

**Only some players can fly**

Give `fly.user` to a group instead of `default`:

```
oxide.group add vip
oxide.usergroup add "Some Player" vip
oxide.grant group vip fly.user
```

**Everyone flies with clip on, trusted builders can turn it off**

Useful on a build server where you don't want players seeing inside other people's bases.
Set **Clip by default** to `true`, reload, then:

```
oxide.grant group default fly.user
oxide.grant group builders fly.cansetclip
```

**One player keeps flying through bases**

Turn their clip on without changing anyone else's:

```
/fly clip on "Some Player"
```

This applies even though they don't have `fly.cansetclip`. Don't give them `fly.cansetclip`, or they
can turn it straight back off. To undo it, use `/fly clip default "Some Player"`.

**Helpers who can rescue stuck players, without full access**

There is no separate "rescue" permission — launching another player needs `fly.admin`. Give
`fly.admin` only to people you'd trust with everything it allows.

**Recommended starting points**

| Server type | Suggested setup |
|-------------|-----------------|
| PvE / building server | `fly.user` for `default`. Leave the "while flying" blocks on. Clip off is fine. |
| PvP server | `fly.user` for a small trusted group, or not at all. Set the combat block to about `30`. Set **Clip by default** to `true` and don't hand out `fly.cansetclip`, so nobody can fly through walls. Leave "land when flying into a building privilege" on. |
| Events / admin-only | Don't grant `fly.user`. Give `fly.admin` to event staff; they can launch players with `/fly <player>` when needed. |

**What permissions can't do**

The config settings — speed, update rate, limits and the "while flying" blocks — apply to
**everyone**. You can't give one group a different speed, a longer flight time or permission to loot
while flying. `/fly speed` and `/fly hz` change the value for the whole server, not for the admin
using them. The only per-player setting is clip. If you need per-group rules, the plugin would need
changing.

---

## 4. Commands for users

**Controls while flying**

| Key | What it does |
|-----|--------------|
| W / S | Fly forward / back, in the direction you're looking (so looking down and pressing W flies you downward) |
| A / D | Move sideways |
| Space | Go up |
| Ctrl | Go down |
| Shift | Fly faster |

The normal "get out of seat" key does nothing while flying. Type `/fly` to land.

**Commands**

| Command | Permission | What it does |
|---------|------------|--------------|
| `/fly` | `fly.user` | Take off. Type it again to land. |
| `/fly clip` | `fly.cansetclip` | Shows whether clip is on or off for you. |
| `/fly clip on` | `fly.cansetclip` | You stop at terrain and buildings, and slide along them. |
| `/fly clip off` | `fly.cansetclip` | You fly straight through terrain and buildings. |
| `/fly clip default` | `fly.cansetclip` | Go back to whatever the server has set. |

- Clip changes work straight away, even mid-flight, and are remembered next time you play.
- An admin can also set your clip. You'll see a message in chat when they do. If you have
  `fly.cansetclip` you can change it back.
- `true`/`false` and `yes`/`no` also work in place of `on`/`off`.
- **"Clip on" means you bump into things.** It's the opposite of noclip. (The old `/fly noclip`
  command has been replaced; typing it tells you to use `/fly clip` instead.)

**Messages you might see**

| Message | Why |
|---------|-----|
| You don't have permission to fly. | You don't have `fly.user`. Ask an admin. |
| You were in combat recently — wait Ns before flying. | You dealt or took damage too recently. |
| Flight is on cooldown for another Ns. | You landed too recently. |
| You cannot take off while building blocked. | You're inside someone else's base. |
| Get off what you are riding first. | You're in a vehicle or seat. |
| You cannot do that while flying. | Attacking, looting, gathering or building is blocked while flying. |
| Landed: you flew into someone else's building privilege. | You entered someone else's base, so you were landed. |
| Flight time is up — landing. | The server has a flight time limit. |

---

## 5. Commands for admins

All of these need `fly.admin`. Admins can also use every command in [section 4](#4-commands-for-users).

**In-game commands**

| Command | What it does |
|---------|--------------|
| `/fly <player>` | Launches that player, or lands them if they're already flying. Use a name, part of a name, or a Steam ID. Skips the takeoff checks. |
| `/fly clip <player>` | Shows whether that player's clip is on or off, and whether it comes from the server default, their own choice, or an admin. |
| `/fly clip on <player>` | Turns that player's clip on — they stop at terrain and buildings. |
| `/fly clip off <player>` | Turns that player's clip off — they fly through terrain and buildings. |
| `/fly clip default <player>` | Clears that player's setting, so they get the server default again. |
| `/fly speed` | Shows the flight speed. |
| `/fly speed 3x` | Sets the flight speed **for everyone** to three times the base speeds in the config. `3` and `2.5x` also work. |
| `/fly speed default` | Puts the speed back to the plugin default (2x) for everyone. |
| `/fly hz` | Shows the update rate. |
| `/fly hz 90` | Sets the update rate **for everyone**, at least 5 per second. Higher is smoother, up to the server's frame rate. |
| `/fly hz default` | Puts the update rate back to the plugin default (60) for everyone. |

About these commands:

- **Speed and update rate apply to the whole server.** They take effect immediately for everyone,
  including players already in the air, and are saved to `oxide/config/Fly.json`, so they're
  kept after a reload or restart.
- **Clip for other players**: the player can be named with a name, part of a name, or a Steam ID.
  Names with spaces work — `/fly clip on Grounds Keeper Willy` — or put the name in quotes. A Steam
  ID also works for players who are offline; the setting applies next time they fly. The setting
  takes effect immediately if they're already flying, and they get a chat message if they're online.
- If a name matches more than one online player, nothing happens and you'll see "No single online
  player matches". Use more of the name, or the Steam ID.

**Freeing a stuck player:** if a player is stuck inside a rock or building, run `/fly <name>` to
lift them out, fly them somewhere open, then run `/fly <name>` again to set them down. It works
even if they don't have `fly.user`.

**Server console / RCON commands**

| Command | What it does |
|---------|--------------|
| `oxide.reload Fly` | Reloads the plugin and re-reads the config. Anyone flying is landed. |
| `oxide.unload Fly` | Turns the plugin off. Anyone flying is landed. |
| `oxide.load Fly` | Turns it back on. |
| `oxide.grant` / `oxide.revoke` / `oxide.show` | Manage permissions — see [section 3](#3-permissions). |

---

## 6. Troubleshooting

| Problem | What to do |
|---------|------------|
| `/fly` says "Unknown command" | The plugin isn't loaded. Check the console for a compile error, then run `oxide.reload Fly`. |
| Nobody can fly | `fly.user` hasn't been granted. Run `oxide.show perm fly.user`. |
| Speed or update rate changed for everyone | That's expected — `/fly speed` and `/fly hz` are server-wide. Use `/fly speed default` and `/fly hz default` to undo. |
| An admin can't use `/fly speed` | They need `fly.admin` — being a Rust admin isn't enough on its own. Grant it to them, or run `oxide.grant group admin fly.admin`. |
| Players can't turn around while flying | The **Seat prefab** setting has been changed to a seat that limits looking around. Check the console for a warning, and put it back to the default. |
| Flight looks jumpy | Raise **Position updates per second**, or lower the speed. Some stutter at high speed is normal. |
| Buildings and terrain appear late at speed | The player is flying faster than the world can load. Lower the speed multiplier. |
| A player got kicked while flying through terrain | Another anti-cheat plugin may be kicking them. Fly's own protection only covers Rust's built-in anti-cheat. |
| A player is stuck after landing | Use `/fly <name>` twice to move them (see section 5). |
| "Flight could not be started" | Check the server console. Usually the **Seat prefab** setting is wrong. |
