# Coop compat modules

Bannerlord Coop support for three mods, as modules you install yourself. Use these if you host or play on a launcher
other than ModderLords. **ModderLords users do not need them:** the launcher already installs the same code.

| Module | For | What it does in Coop |
| --- | --- | --- |
| `ModderLords.Compat` | everything below (required) | The shared layer: settings sync, the server-to-player channel the mods below use, and the general Coop fixes. |
| `BellumCivile.CoopCompat` | Bellum Civile | Peace Parley goes through Coop's peace vote. Treaties for a player's realm become a vote. Kingdoms players rule stay in step. |
| `LivingEconomy.CoopCompat` | Bannerlord Living Economy | Each player's economy actions run on the server as that player. The economy state reaches every player. |
| `Fourberie.CoopCompat` | Fourberie | Each player's books, crimes, prompts and effects run on the server as that player, and are mirrored back to them. |

## Install

1. Copy `ModderLords.Compat` and the modules for the mods you use into the game's `Modules` folder.
2. Enable them in your launcher on the **server and on every player's game**. Coop only lets in players whose
   community modules match the server's.
3. Load order: each `.CoopCompat` module after its mod and after `ModderLords.Compat`. Bannerlord Coop goes last,
   as usual.

Each `.CoopCompat` module has no submodule of its own. `ModderLords.Compat` finds its DLL and loads it once Coop is
loaded. Its log says so: `Coop compat plugin BellumCivile.CoopCompat loaded from ...`. The log is in
`Documents\Mount and Blade II Bannerlord\Configs\ModLogs`.

## What only ModderLords does

Some Bellum Civile support needs ModderLords' launcher. It checks, before the game starts, that every player loads the
same files in the same order, and only then turns on Bellum's player actions, prompts and player clans. Without
ModderLords those parts stay off, and what is listed above still works.

The character editor, revive, family actions, give gold, bans and time control are driven by the ModderLords app.
The code is inside `ModderLords.Compat`, but nothing uses it without the app.
