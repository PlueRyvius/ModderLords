# How ModderLords works

This is the overview: what every part of ModderLords does, and how it does it. It has two parts.

- **[Part 1: Using it](#part-1-using-it)** is for players and hosts: every tab and feature in plain words.
- **[Part 2: How it works underneath](#part-2-how-it-works-underneath)** is for modders and contributors: the pieces
  inside, and why they are built the way they are.

The [README](../README.md) has the full reference for each tab and setting. This page links to it rather than repeat it.

---

# Part 1: Using it

## The big picture

ModderLords does two jobs:

1. **A mod loader.** Pick mods, set their order, and start Bannerlord with exactly those. Your TaleWorlds launcher's
   own list is never read or changed.
2. **A Bannerlord Coop host.** Run the official Coop dedicated server, from the Steam Workshop, with your mods on it.
   ModderLords also makes those mods behave in co-op.

The second job is the hard one. In Coop **the server runs the campaign**. Players' games show what the server tells
them and ask the server when they want to change something. Most mods were written for single player and change the
world directly, so in co-op they either do nothing, or do it only on one player's PC and leave everyone out of step.
Most of ModderLords exists to fix that.

## The tabs

| Tab | What it is for |
|---|---|
| **Mods** | Every mod on this PC. Tick, order, pick a role (where a mod's code runs), see a compatibility verdict. [More](../README.md#mods) |
| **Saves** | Pick the world to host, or name a new one. Shows how a save differs from your mods, and keeps automatic backups. [More](../README.md#saves-host-mode) |
| **Server** | Port, password, Steam visibility, autosave, and the switches for the two ModderLords modules. [More](../README.md#server-host-mode) |
| **Host** | Running the server day to day: campaign time, scheduled restarts, and gameplay rules. [Below](#the-host-tab) |
| **Gameplay** | Coop's own `mod-config.json`: difficulty, fast forward, cheats, and so on. [More](../README.md#gameplay-host-mode) |
| **Mod settings** | Every mod's settings on the server, changed live or ahead of the next launch. [More](../README.md#mod-settings-host-side-live-or-offline) |
| **Console** | The server's output, sorted and filterable, plus a box for server commands. [More](../README.md#console-host-mode) |
| **Characters** | Players, companions and fiefs on the running server: edit, export, import, kick, ban, give fiefs. [Below](#the-characters-tab) |
| **Smoke test** | Checks on its own that this PC can join the server and stay on it. [More](../README.md#smoke-test-host-mode) |
| **Share** | The exact mod list to send players, and a check that a PC matches the server. [More](../README.md#share) |
| **Performance** | Tick rate, frame times, CPU, memory and network, compared with this session's own normal range. [More](../README.md#performance) |
| **Supported mods** | Which popular mods work in co-op, and how ModderLords supports each. [Below](#the-supported-mods-tab) |

## Playing with mods

Tick mods on the **Mods** tab, drag them into order, press **Play**. The right-hand panel shows the order the game will
actually use after dependencies are sorted out. Frameworks such as Harmony and MCM are placed ahead of the game's own
modules, as the TaleWorlds launcher does.

If a mod is installed twice at different versions (a local copy and the Workshop one), both are listed and you pick
which loads. **Profiles** keep named mod sets. A profile can be exported to a file and imported on another PC.

## Hosting

1. Tick the server's mods on **Mods** and save.
2. Pick or name a world on **Saves**.
3. Set a password on **Server** if you want one.
4. Press **Launch server**. The status line reads *SERVING, waiting for clients* when it is ready.
5. Send players the list from **Share**.
6. To play on the same PC, use **Launch client**. It also brings your game's mod list in line with the server's, and
   shows you every change before it makes one.

ModderLords never copies or edits your mods. It links them into the server, so Workshop updates arrive by
themselves. The banner tells you when a mod updated, because the server needs a restart and players need the update too.

### Roles: where a mod's code runs

Each mod on the server has a role:

- **Server + Client**: runs on the server and on players' games. The default for gameplay mods.
- **Client only**: listed on the server so Coop's join check matches, but its code runs on players' games only.
  Frameworks such as Harmony, ButterLib and MCM default to this.
- **Client optional**: not on the server at all, and each player chooses whether to use it. For mods that only change
  what one player sees: overlays, cameras, interface.
- **Mod decides**: the mod's own manifest decides. Only for mods built for dedicated servers.
- **Server only** (experimental): the mod's background logic runs on the server and players' games skip it. See
  [recipes](#recipes-server-only-logic).

### The two ModderLords modules

Two small in-game modules ship with ModderLords:

- **Server guards** (`DedicatedServer.ModderLordsCompat`) runs on the server only. Single-player mods sometimes open
  pop-ups or screens, and a dedicated server has no screen, so it would crash. The guards answer those pop-ups and
  swallow the screens. Players need nothing for it.
- **ModderLords Compat** (`ModderLords.Compat`) runs on the server **and on every player's game**. It powers settings
  sync, recipes, the per-mod support below, the Host tab's gameplay rules and the Characters tab. Turn it on with
  **Settings sync** on the Server tab. **Launch client** installs and updates the player's copy, so nobody copies it
  by hand.

Some mods also get a **standalone co-op compat module** (for now only Bellum Civile: `BellumCivile.CoopCompat`). It
works without ModderLords, so it can be shared with players on other launchers. When the mod is on the server's list,
ModderLords adds its compat module right after it, and installs it on players' games the same way as ModderLords Compat.

## The Host tab

- **Campaign time**: Pause, 1x and 2x buttons that work while the server runs. **Pause while a player is joining**
  holds the world still from the moment someone connects until they reach the map, then goes back to the speed it
  ran at.
- **Server upkeep**: restart the server every N hours. The restart saves the campaign. Players are warned on screen
  10 minutes and 1 minute before.
- **Also export ModderLords.Compat to**: a folder, such as shared Dropbox or OneDrive, that always gets an up-to-date
  copy of the player module for friends to copy into their game.
- **Gameplay rules** (need Settings sync):
  - **Freeze offline players' gold and XP**: an offline player's caravans, workshops and fiefs stop paying them, and
    trading stops raising their Trade skill.
  - **Protect player kingdoms from AI wars**: AI kingdoms never target a kingdom a player belongs to. A war that
    starts anyway is ended with peace. Optionally the same for rebellions. Wars a player starts are allowed.

## The Characters tab

Three lists, read from the running server:

- **Players**: everyone with a character, with whether they are online, in a battle or a siege.
  - **Edit…** opens the character editor (below).
  - **Export…** and **Import…** move a character between worlds as a `.mlchar` file. On import the server cleans the
    file for the world it runs: anything from a mod this world lacks is dropped, values are brought within limits,
    and you see every change first.
  - **Kick**, **Ban** (by Steam id and address) and **Unban**.
- **Companions**: the other heroes of the players' clans, with the same editor.
- **Holdings**: every town and castle. Give one to another clan. Its villages go with it, through the same change the
  game uses for a granted fief.

### The character editor

Its **Character** tab covers:

- **Stats**: name, age, level, gold, health, attributes, skills, focus, traits (modded ones too), and a perk reset.
- **Appearance**: gender, and the face and body "DNA". Copy, paste, export and import it, or copy it from another hero.
- **Face library**: save a look under a name, then use it on any hero, on any server. The library stays on your PC.

Its **Family** tab shows the hero's family as a tree: parents, spouse, children and siblings, including relatives
outside the clan and the dead. Click anyone to open them. From there:

- **Revive** a dead hero. They come back in their home settlement. Death takes a hero's skills, perks and equipment,
  so these are rebuilt the way the game first made them.
- **Change the family**:
  - marry (the game's own wedding, with its rules) or divorce
  - set or remove a father or mother
  - adopt (the hero moves into the parent's clan)
  - move a hero to another clan

  Leave the search box empty to list every living lord and lady.

The server checks every edit against the game's own limits before making it, and Coop's own sync carries the result
to every player. Edits are refused while the hero is in a battle or a siege.

## The Supported mods tab

A card for each mod ModderLords knows: whether it works, works with Settings sync, works partly, or is broken, plus
what works, what to turn on and what does not work. Mods you have installed are listed first. The highlights:

- **The Age of Men (TAOM)**: worlds can be created and hosted, and TAOM's per-player features run on the server for
  each player.
- **Bannerlord Living Economy**: the server runs the economy; each player's towns, gold and menu actions are their own.
- **Fourberie**: each player runs their own criminal empire; a few of its fights are not available in co-op.
- **Bellum Civile**: its votes, peace and treaties go through Coop's kingdom votes.
- **Improved Garrisons**, **Modular Smithing 2**, **CoopMarriage**, **CoopModPatch** and art packs work. Frameworks
  are kept for Coop's join check.
- **TAOM.CoopCompat** is not needed (TAOM support is built in), and **Europe 1100**'s own code still crashes the server.

## Smaller things worth knowing

- **Give gold**: a player can type `modderlords.give <player> <amount>` in their own console to hand gold to another
  player. It only moves gold they have, so it works with cheats off.
- **Save backups**: copies of the hosted save at start, hourly while running, and after stopping. Pick an older one
  on the Saves tab and the next launch loads it.
- **Report a problem…**: builds a redacted support zip of the logs that matter and opens the GitHub bug form. See
  [the support report](#the-support-report).
- **Updates**: a banner offers each new release. **Update now** downloads it, checks it against GitHub's checksum,
  replaces the app and restarts. Profiles are kept.
- **Languages**: English and Simplified Chinese.

---

# Part 2: How it works underneath

## The projects

| Project | What it is |
|---|---|
| `ModderLords.Core` | The mod loader: module catalogue, load order, profiles, the compatibility database, support bundles. No Coop code. |
| `ModderLords.Coop` | Hosting: finding the server, preparing a launch, running and watching the engine, server and gameplay config, saves. |
| `ModderLords.App` | The WPF desktop app. `ModderLords.Cli` is the same engine from a command line. |
| `ModderLords.Hook` | A startup hook loaded into the server engine that finds mods' DLLs. It patches nothing. |
| `ModderLords.Compat` | The server guards module (net472, server only). |
| `ModderLords.CompatSync` | The `ModderLords.Compat` module's entry point. It references nothing of Coop's. |
| `ModderLords.CompatSync.Coop` | The part of `ModderLords.Compat` that talks to Coop. It is loaded by hand once Coop is present. |
| `ModderLords.Operations`, `ModderLords.Analysis` | Operation contracts: the checked, opt-in compatibility adapters ([below](#operation-contracts)). |

`Core` must never reference Coop, so the mod loader builds and runs on a PC without Coop installed.
[DEVELOPMENT.md](DEVELOPMENT.md) has the build details.

## Launching

### Your own game

The game takes its module list on the command line, and that list replaces `LauncherData.xml`, so the TaleWorlds
launcher's selection is never touched. When two copies of a mod exist, or a mod lives in a custom folder, the game is
started from a private folder of links (`client-launches`) that points at exactly the chosen copies.

### The server

`LaunchSession.Prepare` turns a profile into a launch plan:

1. **Select** the modules and resolve the load order. On the server Coop always loads last, as the official host does.
2. **Link** each mod into the server's `Modules` folder with an NTFS junction. No copies, and no admin rights. On
   exFAT or FAT32 drives, which cannot hold junctions, the server runs from a private view instead.
3. **Overlay**: a mod whose manifest says "client only" gets a shadow copy of its `SubModule.xml` so the headless
   server accepts it. Folders a profile keeps off the server ("Folders: server and client") are left out the same way.
   The mod's own files are never changed.
4. **Write config**: `server-config.json`, with a backup each time.
5. **Write the recipe** (below) into the bundled `ModderLords.Compat`.
6. **Start the engine** with `ModderLords.Hook` as a .NET startup hook. The engine looks up every DLL by bare name, so
   the hook resolves each mod's own libraries, and the client-only UI assemblies some mods reference.

The engine's output is read line by line and sorted (engine chatter, module load, Coop, warnings, errors,
milestones). That feeds the Console, the status line, the Performance tab and the player list. The server is tied to
the app's process, so it can never keep running headless after ModderLords closes.

**Map mods** need one exception: the server's settlement distance cache path is hardcoded to the vanilla file, so a
map mod's own cache is swapped in, with a permanent backup of the original. That is the only file ModderLords ever
writes inside the server package. [Why](../README.md#hosting-a-map-replacing-mod).

## Inside ModderLords.Compat

The module the server and every player load. It is split in two because of how the game loads modules:

- **`ModderLords.CompatSync.dll`** is the submodule the game loads. It references only the game, never Coop. The game
  checks a submodule's references when it loads, and Coop loads last, so a Coop reference here would fail.
- **`ModderLords.CompatSync.Coop.dll`** holds everything that touches Coop. `CompatSync` loads it by hand once Coop's
  assemblies are present. Before anything uses a Coop type, `CoopProbe` checks every type and member it needs by
  name. If Coop changed, the adapter stays off with a warning instead of crashing the game.

Inside the adapter:

- **Handlers**: Coop finds message handlers by namespace (`Coop.Core.Server.*`, `Coop.Core.Client.*`) and creates them
  when a session starts. Messages are `[ProtoContract]` types, and a message's wire id is a hash of its full type name.
  **Never rename one**: old and new versions would stop talking.
- **The tick**: every 3 seconds, on both sides, each feature does its periodic work. Each step runs on its own, so one
  that fails does not stop the rest.
- **`GameThread.RunSafe`**: network messages arrive on Coop's thread, and anything that touches the campaign is moved
  onto the game's thread first.

### Settings sync

When a player joins, the server sends the value of every MCM setting it has (toggles, numbers, text, choices). The
player's game applies them in memory for the session, so mod settings match the host's. Changes the host makes
mid-session are sent out again. Players' own settings files are never written.

It also finds settings in mods without MCM: plain `Settings`, `Config` or `Options` classes reached through a static
`Instance`. The **Mod settings** tab is built on the same channel. Edits go to the server, which applies them on its
next tick and saves them where it can. Edits made while the server is stopped become overrides for the next launch.

### Recipes: server-only logic

Many mods do background work: campaign behaviours (map and campaign logic) and mission behaviours (battle and scene
logic). If that runs on every player's PC as well as the server, the world drifts out of step. A **recipe** says, per
mod, which of those should run only on the server.

1. At launch, ModderLords reads each mod's DLLs, without running them (an IL metadata scan), and lists its behaviours.
2. It writes them to `recipes.json` in the bundled `ModderLords.Compat`.
3. A joining player's game asks the server for the recipe before its campaign loads.
4. `BehaviorGate`, a Harmony prefix, stops those behaviours registering on the player's game. Mission behaviours of a
   gated type are dropped there too.

Menus, screens and visuals stay. Only the world-changing background logic is held back. The verification line in each
side's log proves it: counts above zero on the server, zero on the player's game.

### The player channel

Some things cannot be done by Coop's own sync: a mod's button that has to run on the server as *that* player, a pop-up
only one player should see, a mod's saved data that Coop does not sync. For these the adapter has its own channel.
It was built first for TAOM, and Living Economy and Fourberie reuse it:

- **Actions**: the player's game sends "feature, operation, arguments". The server runs it on its own campaign, as
  that player's hero and party, then answers with the outcome and any data. Everything goes through the game's normal
  logic on the server, so Coop syncs the results to everyone.
- **Notices**: messages the server's mod shows while acting for a player are forwarded to that player's screen.
- **State mirror**: a mod's own saved records (a TAOM refuge, Living Economy's books) are sent to players when they
  join and again when they change, so their menus show the right numbers.
- **Relay**: some vanilla actions that Coop does not carry are captured on the player's game and replayed on the
  server.

### Per-mod support

On top of that channel each supported mod has its own layer:

- **TAOM**: world creation for TAOM's map, a joining player's culture, race and career applied on the server, careers,
  special resources, the refuge, field camps and commissions, supply lines, siege defence, and the party components
  TAOM adds.
- **Living Economy**: the economy runs only on the server, and each player's actions and treaties run there as that
  player, with the state mirrored back.
- **Fourberie**: each player's books, crimes, prompts and effects run on the server as that player and are mirrored
  back to them.
- **Bellum Civile**: Peace Parley is replaced by Coop's peace vote, and a treaty for a player's realm becomes a vote.
  Votes Bellum adds reach players, and kingdoms players rule stay in step. The parley, voted peace, treaty vote,
  kingdom founding and thread-safety fixes live in the standalone module `coop-compat/BellumCivileCoopCompat`, built
  to the community Coop Compat repo's rules: no dependency on ModderLords, Coop read by reflection, no messages of its
  own. When it is loaded, ModderLords.Compat leaves its own copy of those patches off (`StandaloneBellum`) and plugs
  its chat notices and the vote deadline into the module's `Hooks` class. Bellum's player actions and prompts stay in
  ModderLords.Compat, since they need the operation plan below.

General co-op fixes that are not tied to one mod live here too:

- **Kingdom decisions** of a mod's own type reach players. Coop's converter only knows vanilla's types.
- **Encounter menus** wait until the server's battle has reached the player, so a click is not lost.
- **The load-menu guard** stops a saved town menu crashing a game that loads with no town.
- **Field battles** skip scenes that would crash players' graphics.
- **The inventory exchange event** is raised in co-op too, so the quests and mods that listen for it work.
- **Client optional mods** are left out of Coop's join check.
- **Offline freeze** and **player kingdom protection** back the Host tab's gameplay rules.

### Admin commands

The Characters tab, the Host tab's time buttons and announcements work through the server's console:

1. The app writes `modderlords.<command> <request id> <arguments>` to the server process's input.
2. The command runs on the server's game thread.
3. The server answers with an `@ML@` line of JSON, which the app reads from the output.
4. Coop's own sync carries the result (a new skill value, a marriage) to every player.

Where Coop misses something, such as a revived hero's rebuilt skills, or a child left in an old parent's list, the
server sends it to players itself.

`@DS@` lines report player events: joining, loading, creating a character, on the map. These drive the Characters
list, pause-on-join and the Performance tab's player count.

## Operation contracts

Some support goes further than recipes: running a mod's player actions on the server, or answering its prompts for a
player. Bellum Civile's player actions are the main case. These **operation contracts** are deliberately strict:

- A contract ships compiled, and only for the exact binaries it was tested against. It activates only if the
  installed mod's files match its fingerprints, and it has passed offline and in-game validation.
- At launch, the app writes an **operation plan**: which contracts are active, the module order, and the hashes of
  the files loaded. The server and each player's game check the plan against what they actually loaded. Any
  difference turns the contract off, with the reason in the log.
- **Automatic validated contracts** on the Mods tab turns them all off.

This is why parts of Bellum's support need ModderLords' launcher: the plan is what proves both sides run the same
thing.

## The compatibility database

The **Compat** column comes from `compat-db.json`: per mod, a verdict (Works, Needs recipe, Broken), the versions it
was tested with, notes, and the defaults ModderLords should use (role, server-only logic, folders to keep off the
server or the game). A mod new to a profile takes those defaults; mods already in it are never changed.

- **Bundled**: shipped with each release.
- **Downloaded**: the latest copy from GitHub, fetched quietly at start. It is used only if it checks out and is newer.
- **Yours**: your own records, laid over the bundled ones field by field.
- **Submit…** turns a working setup into a GitHub issue for the maintainer. A server you hosted with the mod, where a
  player reached the map, counts as proof that it works.

The **Supported mods** tab is separate: written descriptions of how each mod is supported, embedded in the app.

## The support report

**Report a problem…** gathers the launch plan, profile, module list and the recent logs that matter from the launcher,
server, game, Coop, crash folder and mods. It redacts them: folder paths, user and PC names, passwords and keys, IP
addresses and Steam ids. It packs a zip with a manifest of every file and its hash. A save is only added if you pick
one, and goes in a second zip. Nothing is uploaded: you attach the zip to the GitHub form yourself.

## Releases and updates

`scripts/package-release.ps1` builds the self-contained app, the hook, both in-game modules and the database into one
zip. It refuses to package if the version numbers disagree, or if the in-game module's code changed without a version
bump. That bump is what makes installed launchers update players' copies. The app checks GitHub's latest release at
start, downloads the zip on request, checks its SHA-256 against GitHub's digest, and swaps itself in place, keeping
the old files in `.previous`. [RELEASING.md](RELEASING.md) has the steps.

## Ground rules

- **Never copy code** from Bannerlord Coop or HexTool. Both are source-available only. ModderLords runs Coop's
  official binaries and talks to them through their public types.
- **Never edit a mod, a save or the server package**, apart from the one distance cache file. Everything else is
  links, overlays and private copies under `%LOCALAPPDATA%\ModderLords`.
- **Fail safe**: a missing type, member or patch target is logged and skipped. It must never crash the game.
