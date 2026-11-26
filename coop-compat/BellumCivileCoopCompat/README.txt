Bellum Civile Coop Compat v1.13.0 (by ModderLords)

Makes Bellum Civile work in Bannerlord Coop.

In co-op the server runs the campaign. Players' PCs can't change the world
themselves: they ask the server, and the server's changes reach everyone.
Bellum runs its politics on every PC, so without this module each player
had their own rebellions, councils and treaties (empty kingdoms, treaties
nobody else saw, peace votes that kept coming back).

Everything below happens only while a co-op game is running. Single player
is untouched.

The server runs Bellum
- Bellum's politics run on the server only. On a player's PC they don't
  run, and nothing Bellum does there reaches the server. Bellum doesn't
  found kingdoms there; the server's show up through Coop.
- Civil wars, rival claimants' wars and claim feuds end, landless realms
  retire and disputed elections go on. Bellum 1.5 does those only while a
  map screen is open, and a dedicated server has none.
- When a ruler dies, Bellum's succession law picks the heir and the crown
  changes once.
- Bellum's AI leaves players alone. It doesn't answer an ultimatum for your
  realm, purge your house, marry off your family, put your clan under a
  regent, drag your realm into pacts or court wars, spend your gold or press
  claims for you. Those choices are yours (below).
- Bellum's council and title-name lookups are made safe for Coop's
  parallel party updates, so parties stop skipping moves.

Peace
- Peace for your realm is the kingdom vote, like vanilla. Bellum's Peace
  Parley is off.
- A peace your kingdom votes for isn't blocked by Bellum (unless an ally's
  call to arms forbids a separate peace).
- When Bellum drafts a treaty for a realm a player rules, that realm gets a
  peace vote with the treaty's tribute on it. If it passes, Bellum signs
  its treaty with all its terms. If not, the war goes on and the realm
  isn't asked again about that war for 14 days.

You still play Bellum
- Bellum's Factions, Hierarchy and title screens show the server's state,
  sent each game day and after your actions.
- Bellum's own actions work: the Factions tab (join, leave, found,
  ultimatum, abandon, surrender), titles, lordships, claim feud petitions,
  succession law, your vote in an elective succession, the Crown panel's
  court business and council motions, and dismissing a councillor. The
  server carries each one out as you, with Bellum's own checks and costs.
- When Bellum would ask you something, the server asks you: an ultimatum
  to a realm you rule, a call to join a rebellion, an accused lord asking
  for your support, a chance to strike after a ruler's death, claim feud
  rulings and judgments, court business awaiting you, succession pledges,
  claim fabrication setbacks, and your lordships' questions. Not answered
  in 10 minutes: an ultimatum is refused (you keep the crown), a feud
  ruling is accepted, a feud you don't judge is judged as a lord would,
  and anything else just doesn't happen.
- You vote on Bellum's own realm decisions (a defensive pact's call, a
  voluntary pact, a mandate reform, a privy council appointment) like any
  other kingdom decision.
- In conversation: grant or take back estates, retainers' indentures,
  returning an estate to your liege, releasing a client kingdom, lobbying
  or bribing a lord for a vote, buying a house into or out of a rebel
  faction, freeing a captured member of your house at a ransom broker,
  asking a lord to join the army you lead, as a ruler, releasing or
  executing a treaty hostage in your keeping, as a vassal, granting a
  barony to a companion who founds a house under you, and proposing a
  marriage between members of your house and a lord's, in the house you
  choose. The conversation
  runs on your PC, and the server makes the deal and moves the gold or
  influence; a lord who agrees joins your army there, a hostage's fate is
  carried out there, the companion's new house is founded there, and the
  marriage is concluded there on the terms you agreed.

Not in co-op yet
- Writing a treaty's terms yourself in the parley screen.
- Bellum's other dialogue lines (spymasters, exile asylum,
  mercenary bands and the like). They stay hidden on players' PCs.

Install (server and every player)
1. Bellum Civile and its requirements (Harmony).
2. Put the BellumCivile.CoopCompat folder in Modules (players: the game's
   Modules folder; host: the dedicated server's Modules folder too).
3. Load order: BellumCivile.CoopCompat goes right after Bellum Civile.
4. Everyone needs the same mods, versions and order, like any other co-op
   mod.

BellumCivile.CoopCompat.Net.dll comes with this module and carries your
actions and Bellum's questions between your PC and the server. Without it,
the server still runs Bellum and peace works, but Bellum's own actions and
questions don't reach you.

ModderLords installs and orders this for you, and also tells your realm a
treaty's terms in chat.

Made for Bellum Civile 1.5.1 on game 1.5.7, with Bannerlord Coop 0.1.8.
Not for game 1.4.8: version 1.0.0 was for Bellum Civile 1.3.1 (game 1.4.8)
and Coop 0.1.6.
