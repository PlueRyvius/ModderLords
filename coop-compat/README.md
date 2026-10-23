# Standalone Coop compat modules

Each folder here is a normal Bannerlord module that makes one mod work in Bannerlord Coop **without ModderLords**.
They follow the rules of the shared community repo (Coodsz's Coop Compat), so a folder can be copied there unchanged:

- Its own `SubModule.xml` and entry point. No dependency on ModderLords.Compat or any other loader.
- References only the game and Harmony. Coop is read by reflection, and a missing Coop type logs once and turns
  that patch off.
- No network messages of its own. The server does the work and Coop's own sync carries the result.
- Stays out of Coop's join check and connection flow.
- Source only: `bin/` and `obj/` are never committed. `Build.cmd` builds and zips it into `artifacts/compat`.

ModderLords keeps working with or without them. When a module is loaded, ModderLords.Compat leaves its own copy of
those patches off and plugs the launcher-only extras (such as chat notices) into the module's public `Hooks` class.

They are not part of the ModderLords solution or CI: each builds against the local game install, like the community
repo's modules. Test every change in a real Coop game (dedicated server plus a joined client) before calling it done.
