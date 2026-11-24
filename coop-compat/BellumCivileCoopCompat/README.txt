Bellum Civile Coop Compat v1.1.0 (by ModderLords)

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
  vote, like vanilla. Civil wars and feuds are ended by the server.
- Server: a peace your kingdom votes for isn't blocked by Bellum any more
  (unless an ally's call to arms forbids a separate peace). When Bellum
  drafts a treaty for a realm a player rules, that realm gets a peace vote
  with the treaty's tribute on it. If the vote passes, Bellum signs its
  treaty with all its terms; if not, the war goes on and the realm isn't
  asked again about that war for 14 days. A draft Bellum would cancel anyway
  is never put to a vote.
- Server: civil wars, rival claimants' wars and claim feuds end. Bellum 1.5
  ends them only while a map screen is open, and a dedicated server has none.

What you lose: writing a treaty's terms yourself in the parley screen.

Not covered yet: what Bellum 1.5 added after 1.3 (defensive and hostage
pacts, court agendas, succession, lordships, voluntary pacts). They run on
every PC as in single player, and may differ between players.

Install (server and every player):
1. Bellum Civile and its requirements (Harmony).
2. Put the BellumCivile.CoopCompat folder in Modules (players: the game's
   Modules folder; host: the dedicated server's Modules folder too).
3. Load order: BellumCivile.CoopCompat goes right after Bellum Civile.
4. Everyone needs the same mods, versions and order, like any other coop mod.

ModderLords installs and orders this for you, and also tells your realm a
treaty's terms in chat.

Made for Bellum Civile 1.5.1 (game 1.5.7) and the Bannerlord Coop nightly
for 1.5.7. Version 1.0.0 was for Bellum Civile 1.3.1 (game 1.4.8) and
Coop 0.1.6.
