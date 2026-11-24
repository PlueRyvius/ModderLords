Bellum Civile Coop Compat v1.5.0 (by ModderLords)

Makes Bellum Civile safe in Bannerlord Coop.

In co-op the server runs the campaign. Players' PCs can't change the world
themselves: they ask the server through the game's normal buttons and votes.
Bellum runs its politics on every PC, so a PC's own rebellions and treaties
only changed that PC (empty kingdoms, treaties nobody else saw, peace votes
that kept coming back).

Only while a co-op game is running (single player is untouched):
- Everywhere: Bellum's council and title-name lookups are made safe for
  Coop's parallel party updates, so parties stop skipping moves.
- Players' PCs: Bellum's politics are the server's. Bellum's daily and
  hourly politics don't run on a player's PC, and nothing Bellum does there
  reaches the server (votes, prisoner releases, alliances, new clans). War
  and peace of client kingdoms show as the server has them, and relations
  are the server's. Bellum doesn't found kingdoms there (the server's show
  up through Coop).
- Players' PCs: Bellum's Peace Parley is off; peace for your realm is the
  kingdom vote, like vanilla. Bellum's own actions (factions, ultimatums,
  feud petitions, crown actions, the privy council, titles, succession law)
  say that in co-op the server decides, and Bellum's dialogue lines don't
  show. Vanilla's votes, proposals, diplomacy and marriage work as in any
  co-op game.
- Server: a peace your kingdom votes for isn't blocked by Bellum any more
  (unless an ally's call to arms forbids a separate peace). When Bellum
  drafts a treaty for a realm a player rules, that realm gets a peace vote
  with the treaty's tribute on it. If the vote passes, Bellum signs its
  treaty with all its terms; if not, the war goes on and the realm isn't
  asked again about that war for 14 days. A draft Bellum would cancel anyway
  is never put to a vote. Bellum no longer drops a treaty because it thinks
  your own lord wouldn't take it: you decide, as in single player.
- Server: civil wars, rival claimants' wars and claim feuds end, landless
  realms retire and disputed elections go on. Bellum 1.5 does those only
  while a map screen is open, and a dedicated server has none.

What you lose: writing a treaty's terms yourself in the parley screen, and
for now Bellum's own player actions (above). What you see in Bellum's own
screens (factions, titles, councils) is what the server had when you
joined.

- Server: Bellum's AI leaves players alone. It doesn't accept or refuse an
  ultimatum for your realm, purge your house for treason (a passed vote
  expels you, as in vanilla), marry off your family, put your clan under a
  regent or split it, drag your realm into pacts or court wars, spend your
  gold on spies, or press claims for you. Until co-op players get Bellum's
  own choices, those simply don't happen to them.
- Server: when a ruler dies, Bellum's succession law picks the heir and the
  crown changes once. Coop's own handling had given it to a random house
  and opened a king vote.

- Player agency (new in 1.4.0): when Bellum would ask you something, the
  server asks you. First: a court faction's ultimatum to a realm you rule
  comes to you as a question; accept or refuse, and Bellum carries it out.
  Not answered in 10 minutes: refused (you keep the crown). Needs the
  BellumCivile.CoopCompat.Net.dll that comes with this module (it talks to
  Coop); without it, everything else above still works.

- Bellum's Factions, Hierarchy and title screens show the server's state
  (sent each game day and after your actions).
- The Factions tab works in co-op: join or leave, found a faction, issue
  an ultimatum, abandon or surrender. The server carries out the button
  as you, with Bellum's own checks and costs; Bellum's confirmations come
  to you as questions.

- More of Bellum's questions come to you (new in 1.5.0):
  - a call to join a rebellion in your realm (your liege rises, or your
    faction's ultimatum was refused);
  - an accused lord you are close to asks you to stand with him;
  - after a ruler's death, a faction you lead may strike now or wait;
  - claim feuds: the ruler's ruling on a feud over your fief or your
    claim, the judgment of a feud in a realm you rule (pick one), a call
    to support a feud, and a lord's demand that you surrender a title.
  Not answered in 10 minutes: nothing happens, except that a feud ruling
  is accepted and a feud you don't judge is judged as a lord would.

Coming next: Bellum's other screens (claim feuds, titles, succession law,
the privy council) and votes on Bellum's own decisions.

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
