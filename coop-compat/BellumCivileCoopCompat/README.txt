Bellum Civile Coop Compat v1.0.0 (by ModderLords)

Makes Bellum Civile safe in Bannerlord Coop.

In co-op the server runs the campaign. Players' PCs can't change the world
themselves: they ask the server through the game's normal buttons and votes.
Bellum runs its politics on every PC, so a PC's own rebellions and treaties
only changed that PC (empty kingdoms, treaties nobody else saw, peace votes
that kept coming back).

Only while a co-op game is running (single player is untouched):
- Everywhere: Bellum's council lookups are made safe for Coop's parallel
  party updates, so parties stop skipping moves.
- Players' PCs: Bellum doesn't found kingdoms (the server's show up through
  Coop), and its Peace Parley is off. Peace for your realm is the kingdom
  vote, like vanilla.
- Server: a peace your kingdom votes for isn't blocked by Bellum any more
  (Bellum's minimum war length still applies). When Bellum drafts a treaty
  for a realm a player rules, that realm gets a peace vote with the treaty's
  tribute on it. If the vote passes, Bellum signs its treaty with all its
  terms; if not, the war goes on and the realm isn't asked again about that
  war for 14 days.

What you lose: writing a treaty's terms yourself in the parley screen.

Install (server and every player):
1. Bellum Civile and its requirements (Harmony).
2. Put the BellumCivile.CoopCompat folder in Modules (players: the game's
   Modules folder; host: the dedicated server's Modules folder too).
3. Load order: BellumCivile.CoopCompat goes right after Bellum Civile.
4. Everyone needs the same mods, versions and order, like any other coop mod.

ModderLords installs and orders this for you, and also tells your realm a
treaty's terms in chat.

Made for Bellum Civile 1.3.1 (game 1.4.8) and Coop 0.1.6.
