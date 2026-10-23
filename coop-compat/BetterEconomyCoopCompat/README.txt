Living Economy Coop Compat v1.0.0 (by ModderLords)

Makes Bannerlord Living Economy (by ISKL) work in Bannerlord Coop.

In co-op the server runs the campaign. Living Economy runs on every PC and
keeps its own records, which Coop never shares, so each player's economy
drifted away from everyone else's, and clicks in its menus only changed
that player's own screen.

Only while a co-op game is running (single player is untouched):
- The server runs the economy. Players' PCs stop simulating it.
- On the server, a town a player owns is run as that player, not by the
  AI with the player's gold. Lord wealth limits leave players' gold alone,
  and the AI signs no trade treaties for kingdoms players lead.
- Clicks in Living Economy's menus (invest, contribute, set policy or tax,
  start projects, armory and training camp, troop training, estate
  actions, market access, trade treaties) are carried out by the server,
  as you. You see the same messages you would alone.
- Living Economy's records follow the server to every player, so menus,
  prices and the ledger show the real economy.
- Messages about your towns (payouts, deliveries, finished projects)
  reach you.
- Players can't reload Living Economy's settings files during co-op; the
  host's settings are used.

What it needs from the network: Living Economy's records and your clicks
can't reach anyone through Coop on their own, so this module sends its own
messages. When you join, your game and the server compare versions first.
If they differ, or Coop has changed, those parts stay off and your game
plays as if this module weren't there.

Install (server and every player):
1. Living Economy and its requirements (Harmony).
2. Put the BetterEconomy.CoopCompat folder in Modules (players: the game's
   Modules folder; host: the dedicated server's Modules folder too).
3. Load order: BetterEconomy.CoopCompat goes right after Living Economy.
4. Everyone needs the same mods, versions and order, like any other coop
   mod. Living Economy's settings must match the host's too: copy the
   host's settings files to the players.

ModderLords installs and orders this for you, and syncs the settings.

Made for Living Economy v1.4.6 (game 1.4.8) and Coop 0.1.6.
