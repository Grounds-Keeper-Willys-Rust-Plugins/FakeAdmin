# FakeAdmin

Lets ordinary players fly, use god mode, and craft or spawn items instantly, for Rust servers running
Oxide/uMod. They get the fun parts of being an admin without being made one: their auth level never
changes, so kick, ban, server settings and the other admin commands stay closed to them.

Three plugins together cover the three things admins usually hand out:

| Admin ability | Plugin | Command | Permission |
|---|---|---|---|
| Noclip (flying through the map) | **Fly** 0.8.0 (this repo) | `/fly` | `fly.user` |
| God mode (`god true`) | **Godmode** (download it, see below) | `/god` | `godmode.toggle` + `godmode.invulnerable` |
| F1 item creation and instant crafting | **instacraft** 0.1.8 (this repo) | `/craft on` | `instacraft.user` |

Use any of them on their own, or all three. Players need nothing installed.

## Fly: noclip without admin

`/fly` lifts the player off and flies them wherever they look; `/fly` again lands them safely. Rust's
own noclip only works for admins, so the plugin seats the player in an invisible seat and flies the
seat instead. By default flying players cannot be hurt, cannot attack, loot, gather or build, and are
landed if they fly into someone else's base, so it is for looking around, not cheating. Speed,
flight time, height and combat limits, and whether players pass through walls, are all settings.

Full guide: [README.Fly.md](README.Fly.md)

## Godmode: invulnerability

Godmode is a separate, widely used plugin by Wulf and others. It is not included here: download it
from uMod (<https://umod.org/plugins/godmode>) and copy `Godmode.cs` into `oxide/plugins`.

`/god` toggles it for the player. Players need `godmode.toggle` to use the command and
`godmode.invulnerable` to actually take no damage while it is on. Its other permissions (no hunger
or thirst, protection from looting, and so on) are listed on its uMod page.

## instacraft: F1 items and instant crafting

`/craft on` makes everything the player crafts finish straight away. While it is on, the player can
also type the admin give commands in the F1 console, such as `give wood 1000`, and `spawn` vehicles
such as `spawn minicopter.entity`. Only gives to themselves are allowed (not `giveto` or `giveall`),
and only vehicles can be spawned (not cargo ships, patrol helicopters, Bradleys or NPCs). Every
give and spawn is logged to `oxide/logs/instacraft/`, and can be announced in chat.

The F1 **Items** and **Spawn** screens may do nothing for a non-admin, because the game client hides
or does not send them; typing the command in the F1 console always works.

Full guide: [README.Instacraft.md](README.Instacraft.md)

## Install

1. Copy `Fly.cs` and `instacraft.cs` from this repo, and `Godmode.cs` from uMod, into the server's
   `oxide/plugins` folder. They load within a few seconds.
2. Grant the permissions to the players or group you want, for example a `vip` group:
   ```
   oxide.group add vip
   oxide.grant group vip fly.user
   oxide.grant group vip godmode.toggle
   oxide.grant group vip godmode.invulnerable
   oxide.grant group vip instacraft.user
   oxide.usergroup add "Some Player" vip
   ```
   Granting to `default` gives them to everyone.

Anyone with `instacraft.user` can give themselves any item, so grant it only to players you trust.

Fly and instacraft use Harmony, which comes with Oxide. They have been tested on Oxide only; Carbon
is untested.
