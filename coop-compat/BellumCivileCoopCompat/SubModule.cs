using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BellumCivileCoopCompat
{
    // Makes Bellum Civile safe in Bannerlord Coop (built against Bellum Civile 1.5.1 on game 1.5.7,
    // with Coop 0.1.8 for 1.5.7; earlier it was 1.3.1 with Coop 0.1.6).
    //
    // How Coop works: the server runs the campaign. A player's PC asks the server through vanilla
    // paths (kingdom votes, proposals) and gets the results back. Bellum runs its politics on
    // every PC, so a PC's own copy of a rebellion or treaty changed only that PC.
    //
    // Only while a co-op session is running:
    // - Both sides: Bellum's council and title-name caches are locked so Coop's parallel party tick can read them.
    // - Players' PCs: Bellum's politics are the server's. Its ticks don't run there, nothing it does there reaches the
    //   server (decisions, prisoner releases, alliances, new clans), Coop's replays of the server's diplomacy get past
    //   its client-kingdom rules, and relations read as Coop sends them. Bellum founds no kingdoms there. Its Peace
    //   Parley is off (peace is the kingdom vote), and so are its own player actions and dialogue lines; vanilla's
    //   proposals go to the server as in any co-op game.
    // - Server: a peace voted in a players' kingdom gets past Bellum's guard, a treaty Bellum drafts for a realm a
    //   connected player rules is put to that realm as a kingdom vote (and is no longer dropped beforehand because
    //   Bellum's AI thinks the player's hero would refuse it), and Bellum's processors that wait for a map
    //   screen (ending civil wars and feuds, retiring landless realms, disputed elections) run.
    //
    // - Server: Bellum's AI leaves real players' clans, heroes and realms alone (no ultimatum, treason purge, marriage,
    //   regency, partition, pact, espionage, feud or court business decided for them), and a ruler's death is settled
    //   once, by Bellum's succession law, instead of Coop's random ruling clan and king vote.
    //
    // Next (docs/BELLUM-COOP-COVERAGE.md, stage 3): players get Bellum's own actions back, carried out by the server.
    // Single player and the main menu are untouched. No network messages of its own: everything
    // runs on the server and reaches players through Coop's own sync.
    public sealed class SubModule : MBSubModuleBase
    {
        internal const string HarmonyId = "modderlords.bellumcivile.coopcompat";
        private static bool _patched;
        private static DateTime _nextTick;

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            Setup();
        }

        // Coop 0.1.7's dedicated server never sets an initial screen, so the first tick does the setup there.
        private static void Setup()
        {
            Coop.ModulesLoaded();
            if (_patched)
                return;
            _patched = true;
            if (AccessTools.TypeByName(BellumPeaceTargets.Treaty) == null)
            {
                Log("Bellum Civile is not loaded; nothing to do.");
                return;
            }

            var harmony = new Harmony(HarmonyId);
            foreach (var install in new Action<Harmony>[]
                     {
                         ThreadSafety.Install, ClientKingdoms.Install, PeaceInCoop.Install, VotePeace.Install, TreatyVote.Install, InternalPeace.Install,
                         ServerDecides.Install, PlayerActions.Install, ServerScreens.Install, TreatyDraftGate.Install,
                         PlayersNotAi.Install, CrownAccession.Install, Agency.Install, Feuds.Install, Prompts.Install, Calls.Install, StateMirror.Install, FactionActions.Install, RemoteCalls.Install, Bribes.Install, Ransoms.Install, Summons.Install, Hostages.Install, Pollers.Install,
                     })
            {
                try { install(harmony); }
                catch (Exception ex) { Log(install.Method.DeclaringType?.Name + " not installed: " + ex.GetBaseException().Message); }
            }
            // Coop compares the decision types both sides sync when a player joins, before any campaign tick.
            try { DecisionSync.TryRegister(); }
            catch (Exception ex) { Log("Votes not set up: " + ex.GetBaseException().Message); }
        }

        // Every 3 seconds, on the main thread, while a campaign runs.
        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            if (!_patched) Setup();
            try { Agency.Tick(); }
            catch (Exception ex) { Log("Agency tick failed: " + ex.GetBaseException().Message); }
            if (Campaign.Current == null || DateTime.UtcNow < _nextTick)
                return;
            _nextTick = DateTime.UtcNow.AddSeconds(3);
            try
            {
                if (Coop.OnPlayerPc) ClientKingdoms.Tick();
                if (Coop.OnServer) TreatyVote.Tick();
                if (Coop.OnServer) Prompts.Tick();
                if (Coop.OnServer) StateMirror.Tick();
                CrownAccession.TryHookCoop();
                DecisionSync.TryRegister();
            }
            catch (Exception ex) { Log("Tick failed: " + ex.GetBaseException().Message); }
        }

        /// <summary>What the module has logged this run, newest last (at most 200 lines), for a test harness to read.</summary>
        internal static readonly System.Collections.Generic.List<string> Logged = new System.Collections.Generic.List<string>();

        internal static void Log(string text)
        {
            lock (Logged)
            {
                if (Logged.Count >= 200) Logged.RemoveAt(0);
                Logged.Add(text);
            }
            try { Debug.Print("[BellumCivile.CoopCompat] " + text); }
            catch { }
        }

        internal static void Tell(string text)
        {
            try { InformationManager.DisplayMessage(new InformationMessage(text)); }
            catch { }
        }
    }

    // Reads Coop's own state by reflection, so this module never compiles against one Coop version:
    // - GameInterface.ContainerProvider._lifetimeScope (Coop 0.1.6) or _active (0.1.7) is set while a co-op session runs.
    // - Common.ModInformation.IsServer tells the server from a player.
    // - GameInterface.Services.Heroes.Extensions.HeroExtensions.IsPlayerHero(Hero) and
    //   IPlayerManager.IsOwnerOfHeroDisconnected(Hero) answer "is this a player, and are they here?".
    // If a future Coop renames the session state, this assumes a session on a player's PC, the
    // safe side. If it renames the player checks, the server treats every hero as a lord's, which
    // is Bellum's own behaviour.
    internal static class Coop
    {
        private static bool _resolved, _modulesLoaded, _coopLoaded;
        private static FieldInfo _scope;
        private static PropertyInfo _isServer;
        private static MethodInfo _isPlayerHero, _resolve;
        private static Type _playerManager;
        private static MethodInfo _isDisconnected;

        public static bool InSession
        {
            get
            {
                Resolve();
                if (!_coopLoaded)
                    return false;
                if (_scope == null)
                    return true;
                try { return _scope.GetValue(null) != null; }
                catch { return true; }
            }
        }

        public static bool IsServer
        {
            get
            {
                Resolve();
                if (_isServer == null)
                    return false;
                try { return (bool)_isServer.GetValue(null, null); }
                catch { return false; }
            }
        }

        public static bool OnPlayerPc => InSession && !IsServer;
        public static bool OnServer => InSession && IsServer;

        public static void ModulesLoaded()
        {
            _modulesLoaded = true;
            Resolve();
        }

        public static bool IsPlayerHero(Hero hero)
        {
            Resolve();
            if (hero == null || _isPlayerHero == null)
                return false;
            try { return (bool)_isPlayerHero.Invoke(null, new object[] { hero }); }
            catch { return false; }
        }

        public static bool IsPlayerClan(Clan clan)
        {
            if (clan == null)
                return false;
            try { return clan.Heroes.Any(IsPlayerHero); }
            catch { return false; }
        }

        /// <summary>A player hero whose player is connected right now.</summary>
        public static bool IsConnectedPlayer(Hero hero)
        {
            if (!IsPlayerHero(hero))
                return false;
            var players = PlayerManager();
            if (players == null || _isDisconnected == null)
                return true;
            try { return !(bool)_isDisconnected.Invoke(players, new object[] { hero }); }
            catch { return true; }
        }

        private static object PlayerManager()
        {
            if (_resolve == null || _playerManager == null)
                return null;
            try
            {
                var args = new object[] { null };
                return (bool)_resolve.MakeGenericMethod(_playerManager).Invoke(null, args) ? args[0] : null;
            }
            catch { return null; }
        }

        private static void Resolve()
        {
            if (_resolved)
                return;

            Type container = null, info = null, heroes = null;
            var coop = false;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = assembly.GetName().Name ?? ""; }
                catch { continue; }
                if (name == "GameInterface")
                {
                    coop = true;
                    container = container ?? assembly.GetType("GameInterface.ContainerProvider", false);
                    heroes = heroes ?? assembly.GetType("GameInterface.Services.Heroes.Extensions.HeroExtensions", false);
                    _playerManager = _playerManager ?? assembly.GetType("GameInterface.Services.Players.IPlayerManager", false);
                }
                else if (name == "Coop.Core" || (name == "Coop" && assembly.GetType("Coop.CoopMod", false) != null))
                {
                    coop = true;
                }
                info = info ?? assembly.GetType("Common.ModInformation", false);
            }

            if (!coop)
            {
                // Before the main menu Coop may not be loaded yet; look again later. Coop 0.1.7's dedicated server runs
                // the setup on its first tick, which can come before Coop has loaded, so "no Coop" is final only once a
                // campaign exists.
                if (_modulesLoaded && Campaign.Current != null)
                    _resolved = true;
                return;
            }

            _coopLoaded = true;
            _scope = container == null ? null : AccessTools.Field(container, "_lifetimeScope") ?? AccessTools.Field(container, "_active");
            _resolve = container?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "TryResolve" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            _isServer = info == null ? null : AccessTools.Property(info, "IsServer");
            _isPlayerHero = heroes == null ? null : AccessTools.Method(heroes, "IsPlayerHero", new[] { typeof(Hero) });
            _isDisconnected = _playerManager == null ? null : AccessTools.Method(_playerManager, "IsOwnerOfHeroDisconnected", new[] { typeof(Hero) });
            if (_scope == null || _isServer == null)
                SubModule.Log("Could not read Coop's session state; assuming a player's PC in a session.");
            if (_isPlayerHero == null)
                SubModule.Log("Could not read Coop's player list; the server treats every realm as a lord's.");
            _resolved = true;
        }
    }
}
