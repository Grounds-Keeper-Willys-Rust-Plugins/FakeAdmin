# Instacraft — User Guide

Version 0.1.8

Instacraft lets players craft items instantly. When instant crafting is on, a
player can also give themselves items and spawn vehicles from the F1 console.
Every give and every spawn is written to a log file, and can also be announced
in chat if you turn that on.

---

## 1. Installing

1. Copy `instacraft.cs` into the `oxide/plugins/` folder on the server.
2. Make sure the file can be read by the server:
   ```
   chmod 666 oxide/plugins/instacraft.cs
   ```
   If the file is not readable, Oxide can stop compiling plugins until the
   server is restarted.
3. The plugin loads by itself. To load it by hand, run this in the server
   console or RCON:
   ```
   oxide.reload instacraft
   ```
   You should see `Loaded plugin instacraft v0.1.8 by Lucien`.

### Configuration file

The plugin makes its own file the first time it loads:

```
oxide/config/instacraft.json
```

It looks like this:

```json
{
  "Announce gives and spawns in chat": false,
  "Announce rejected requests in chat": false
}
```

Both are **off** by default, so nothing is said in chat. Change a `false` to
`true` and run `oxide.reload instacraft` to switch the messages on. Everything
else about the plugin is built in and cannot be changed (see section 2).

### Log file

Every give, spawn and rejected request is written to:

```
oxide/logs/instacraft/instacraft_give-YYYY-MM-DD.txt
```

The same lines also appear in the server console and in
`oxide/logs/oxide_YYYY-MM-DD.txt`.

---

## 2. What the plugin does

### Instant crafting

A player types `/craft on`. From then on, everything they craft finishes
straight away. Ingredients are still used up as normal. The crafting queue
on screen still works.

`/craft off` turns it back to normal crafting speed.

Instant crafting is **per player**. Turning it on for yourself does not
change anyone else.

It turns off by itself when:
- the player disconnects, or
- the player loses the `instacraft.user` permission, or
- the plugin is reloaded or the server restarts.

### Giving yourself items (only while `/craft on`)

Normally only admins can use the F1 give commands. With `/craft on`, a player
can use the give commands that **only give to themselves**:

| Command | Allowed with `/craft on`? |
|---|---|
| `inventory.give` (short: `give`) | Yes |
| `inventory.giveid` (short: `giveid`) | Yes |
| `inventory.givearm` (short: `givearm`) | Yes |
| `inventory.giveequip` (short: `giveequip`) | Yes |
| `inventory.giveto` | **No** — can give to other players |
| `inventory.giveall` | **No** — gives to every player |

Example, typed in the F1 console:
```
inventory.give wood 1000
```

### Spawning vehicles (only while `/craft on`)

With `/craft on`, a player can use `entity.spawn` (short: `spawn`) — but
**only for vehicles**.

- **Allowed:** minicopter, scrap helicopter, attack helicopter, boats, kayaks,
  modular cars, snowmobiles, horses, trains, submarines, hot air balloon, and
  other vehicles.
- **Blocked:** everything else, for example cargo ship, patrol helicopter,
  Bradley, and NPCs such as scientists.

Example, typed in the F1 console:
```
spawn minicopter.entity
```

### Chat announcements (off by default)

These messages are only sent if you turn them on in the config file. Gives and
spawns are always written to the log file either way.

With `"Announce gives and spawns in chat": true`, everyone on the server sees a
message when:

- **An item is given** — orange message:
  `[instacraft] GIVE PlayerName [steamid] (F1, auth 0): inventory.give wood 1000`
- **A vehicle is spawned** — orange message:
  `[instacraft] SPAWN PlayerName [steamid] (F1, auth 0): entity.spawn minicopter.entity at (x, y, z)`
With `"Announce rejected requests in chat": true`, everyone also sees a red
message when a give or spawn is refused:

`[instacraft] Rejected request: PlayerName [steamid] (auth 0): inventory.giveto ...`

Gives and spawns done by admins or from RCON are announced too.

### Settings

These two can be changed in `oxide/config/instacraft.json`:

| Setting | Default | What it does |
|---|---|---|
| `Announce gives and spawns in chat` | `false` | Tells everyone in chat when a player gives themselves an item or spawns a vehicle. |
| `Announce rejected requests in chat` | `false` | Tells everyone in chat when a give or spawn is refused. |

These are fixed in the plugin and cannot be changed:

| Setting | Value |
|---|---|
| Permission needed for `/craft` | `instacraft.user` |
| Give commands a `/craft on` player may use | `give`, `giveid`, `givearm`, `giveequip` |
| Spawns a `/craft on` player may use | vehicles only |
| Log file | `oxide/logs/instacraft/instacraft_give-DATE.txt` |

### Good to know

- **Typing the command works best.** The F1 **Items** and **Spawn** screens may
  do nothing for a player who is not an admin, because the game itself may
  hide or not send those buttons. Typing the command in the F1 console always
  works.
- **Anyone with `instacraft.user` can spawn items and vehicles.** Only give the
  permission to players you trust. Turn the chat messages on, or read the log
  file, to see who is doing what.

---

## 3. Permissions

### USER

| Permission | What it allows |
|---|---|
| `instacraft.user` | Use `/craft on` and `/craft off`. While on: instant crafting, giving yourself items, and spawning vehicles. |

Without this permission, `/craft` replies:
`You need the instacraft.user permission to use /craft.`

Admins need this permission too — there is no admin shortcut.

### ADMIN

The admin console commands (section 5) need **server owner level (auth 2)**
when used in game. From the server console or RCON they always work. There is
no Oxide permission for them.

### Giving and removing the permission

Run these in the server console or RCON.

Give it to one player:
```
oxide.grant user <steamid or name> instacraft.user
```

Give it to every player:
```
oxide.grant group default instacraft.user
```

Take it away from one player:
```
oxide.revoke user <steamid or name> instacraft.user
```

Take it away from everyone:
```
oxide.revoke group default instacraft.user
```

When the permission is taken away, that player's `/craft` is switched off
straight away.

See who has it:
```
oxide.show perm instacraft.user
```

---

## 4. Commands for USERS

Type these in chat.

| Command | What it does |
|---|---|
| `/craft on` | Turn on instant crafting. Also lets you give yourself items and spawn vehicles. |
| `/craft off` | Turn it off. |
| `/craft` | Shows whether it is ON or OFF. |

While `/craft on` is set, type these in the **F1 console**:

| Command | What it does |
|---|---|
| `give <item> [amount]` | Give yourself an item by name. Example: `give wood 1000` |
| `giveid <itemid> [amount]` | Give yourself an item by item ID. |
| `givearm <itemid> [amount]` | Give an item straight to your belt. |
| `giveequip <item>` | Give yourself clothing or armour and put it on. |
| `spawn <vehicle>` | Spawn a vehicle where you are looking. Example: `spawn minicopter.entity` |

---

## 5. Commands for ADMIN

Type these in the server console, RCON, or the F1 console (auth 2 needed in
game).

| Command | What it does |
|---|---|
| `oxide.reload instacraft` | Reload the plugin, and load any change made to `oxide/config/instacraft.json`. Everyone's `/craft` is switched off. |
| `instacraft.status` | Shows that the plugin is hooked into all give commands, and lists online players with their auth level and admin flag. |
| `instacraft.vehicle <name>` | Checks if a `/craft on` player would be allowed to spawn this. Nothing is spawned. Example: `instacraft.vehicle minicopter.entity` → `ALLOWED`, `instacraft.vehicle cargoshiptest` → `blocked` |
| `oxide.grant user <player> instacraft.user` | Let a player use `/craft`. |
| `oxide.revoke user <player> instacraft.user` | Stop a player using `/craft`. |
| `oxide.show perm instacraft.user` | List who has the permission. |

### Reading the log

Open `oxide/logs/instacraft/instacraft_give-YYYY-MM-DD.txt`. Lines start with:

| Starts with | Meaning |
|---|---|
| `GIVE` | An item was given. |
| `SPAWN` | An entity was spawned. |
| `Rejected request` | A give or spawn was refused. |
| `CLIENT CMD` | A player's game sent a give or spawn command to the server. If you see this but no `GIVE`/`SPAWN`/`Rejected` line, check the command was typed correctly. If a player says the F1 screen does nothing and there is **no** `CLIENT CMD` line, their game never sent it — ask them to type the command instead. |
