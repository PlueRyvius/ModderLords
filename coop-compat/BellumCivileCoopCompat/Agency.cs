using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace BellumCivileCoopCompat
{
    /// <summary>How the module's network part (BellumCivile.CoopCompat.Net.dll, built against Coop) carries a message.</summary>
    public interface IAgencyChannel
    {
        /// <summary>Player's PC: sends a request to the server.</summary>
        void ToServer(string kind, string[] args);

        /// <summary>Server: sends a message to one player's PC. False when that player is not connected.</summary>
        bool ToPlayer(Hero hero, string kind, string[] args);
    }

    // Player agency (docs/BELLUM-COOP-COVERAGE.md, stage 3): a player's PC and the server talk about Bellum.
    //
    // The module itself never references Coop, so it loads in single player and against a changed Coop. Its network
    // part is a second assembly, BellumCivile.CoopCompat.Net.dll, built against Coop's own messaging: it is loaded once
    // Coop is, and sets Channel. Messages are two generic envelopes (a request to the server, a message to a player),
    // each a kind and a list of strings; what each kind means is decided here, on the game thread.
    //
    // Server: Handle(kind, (sender, args) => ...) answers a player's request; the sender is the hero Coop says sent it,
    // never one named in the message. Player's PC: Listen(kind, args => ...) receives what the server sends.
    public static class Agency
    {
        /// <summary>Goes up when a message's meaning changes; a request from a PC with another protocol is refused.</summary>
        public const int Protocol = 1;

        public const string NetAssembly = "BellumCivile.CoopCompat.Net.dll";
        public const string NetEntry = "BellumCivileCoopCompat.Net.AgencyNet";

        private static readonly ConcurrentQueue<Action> Inbox = new ConcurrentQueue<Action>();
        private static readonly Dictionary<string, Action<Hero, string[]>> ServerHandlers = new Dictionary<string, Action<Hero, string[]>>();
        private static readonly Dictionary<string, Action<string[]>> PlayerHandlers = new Dictionary<string, Action<string[]>>();
        private static readonly List<string> Received = new List<string>();
        private static bool _loadTried, _loaded;

        /// <summary>Set by the network part when Coop's session has built it; null without Coop or the network part.</summary>
        public static IAgencyChannel Channel { get; set; }

        public static bool NetLoaded => _loaded;

        /// <summary>The last messages this side received ("kind arg|arg"), newest last, at most 100, for tests.</summary>
        public static List<string> Recent
        {
            get { lock (Received) return Received.ToList(); }
        }

        public static void Handle(string kind, Action<Hero, string[]> handler) => ServerHandlers[kind] = handler;

        public static void Listen(string kind, Action<string[]> handler) => PlayerHandlers[kind] = handler;

        public static void Install(Harmony harmony)
        {
            Handle("echo", (sender, args) => Channel?.ToPlayer(sender, "echo", new[] { sender.Name?.ToString() ?? "" }.Concat(args).ToArray()));
            Listen("echo", args => { });
            Listen("notice", args => { if (args.Length > 0) SubModule.Tell(args[0]); });
            AsPlayer.Resolve();
            SubModule.Log("Agency: player agency is " + (File.Exists(NetPath()) ? "ready to load with Coop" : "off (no " + NetAssembly + ")") + "; "
                + (AsPlayer.Available ? "the server can act as a player." : "the server cannot act as a player in this Coop."));
        }

        /// <summary>Player's PC: a request to the server. False without a channel (no Coop session or no network part).</summary>
        public static bool ToServer(string kind, params string[] args)
        {
            var channel = Channel;
            if (channel == null || !Coop.OnPlayerPc)
                return false;
            try { channel.ToServer(kind, args ?? new string[0]); return true; }
            catch (Exception ex) { SubModule.Log("Agency: could not send " + kind + ": " + ex.GetBaseException().Message); return false; }
        }

        /// <summary>Server: a message to one player's PC. False when they are not connected or there is no channel.</summary>
        public static bool ToPlayer(Hero hero, string kind, params string[] args)
        {
            var channel = Channel;
            if (channel == null || hero == null || !Coop.OnServer)
                return false;
            try { return channel.ToPlayer(hero, kind, args ?? new string[0]); }
            catch (Exception ex) { SubModule.Log("Agency: could not send " + kind + " to " + hero.StringId + ": " + ex.GetBaseException().Message); return false; }
        }

        // ---- called by the network part, on Coop's network thread ----

        public static void FromPlayer(Hero sender, int protocol, string kind, string[] args)
        {
            Inbox.Enqueue(() =>
            {
                Note("from " + (sender?.StringId ?? "?") + ": " + kind, args);
                if (protocol != Protocol)
                {
                    SubModule.Log("Agency: a player's PC runs another version of this module (protocol " + protocol + ", here " + Protocol + "); its " + kind + " is refused.");
                    if (sender != null) ToPlayer(sender, "notice", "Your Bellum Civile Coop Compat differs from the server's; update it to use Bellum's actions.");
                    return;
                }
                if (sender == null || !ServerHandlers.TryGetValue(kind, out var handler))
                {
                    SubModule.Log("Agency: a request of an unknown kind (" + kind + ") or from an unknown sender was ignored.");
                    return;
                }
                handler(sender, args ?? new string[0]);
            });
        }

        public static void FromServer(int protocol, string kind, string[] args)
        {
            Inbox.Enqueue(() =>
            {
                Note(kind, args);
                if (PlayerHandlers.TryGetValue(kind, out var handler))
                    handler(args ?? new string[0]);
            });
        }

        private static void Note(string what, string[] args)
        {
            lock (Received)
            {
                if (Received.Count >= 100) Received.RemoveAt(0);
                Received.Add(what + " " + string.Join("|", args ?? new string[0]));
            }
        }

        /// <summary>Every frame, on the game thread: loads the network part once Coop is there, and runs what arrived.</summary>
        public static void Tick()
        {
            if (!_loadTried)
                TryLoadNet();
            while (Inbox.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { SubModule.Log("Agency: a message failed: " + ex.GetBaseException().Message); }
            }
        }

        private static string NetPath()
        {
            var dir = Path.GetDirectoryName(typeof(Agency).Assembly.Location) ?? "";
            return Path.Combine(dir, NetAssembly);
        }

        private static void TryLoadNet()
        {
            if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => { try { return a.GetName().Name == "GameInterface"; } catch { return false; } }))
                return;
            _loadTried = true;
            var path = NetPath();
            if (!File.Exists(path))
                return;
            try
            {
                var assembly = Assembly.LoadFrom(path);
                assembly.GetTypes();
                assembly.GetType(NetEntry, true).GetMethod("Init", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                _loaded = true;
                SubModule.Log("Agency: the network part is loaded; player agency is on when a session starts.");
            }
            catch (Exception ex)
            {
                var inner = ex is ReflectionTypeLoadException rtle ? rtle.LoaderExceptions.FirstOrDefault() ?? ex : ex.GetBaseException();
                SubModule.Log("Agency: the network part could not be loaded (" + inner.Message + "); player agency is off.");
            }
        }
    }

    // Server: runs Bellum's own code as one player, so its "is this the player?" tests mean that player.
    //
    // Coop has a scope for this (GameInterface.Services.Heroes.Patches.MainHeroSubstitutionScope): for its lifetime the
    // game's player hero, party and faction are the given player's, on the server, and Coop's own patches follow it.
    // The module creates it by reflection, for one call on the main thread at a time. While it is open, PlayersNotAi
    // lets Bellum treat that player as the player (that is the point), and Current names who it is.
    public static class AsPlayer
    {
        public const string Scope = "GameInterface.Services.Heroes.Patches.MainHeroSubstitutionScope";

        private static ConstructorInfo _scope;
        private static bool _resolved;

        [ThreadStatic] private static Hero _current;

        /// <summary>The player the server is acting as, or null.</summary>
        public static Hero Current => _current;

        public static bool Available => _scope != null;

        public static void Resolve()
        {
            if (_resolved && _scope != null)
                return;
            _resolved = true;
            var type = AccessTools.TypeByName(Scope);
            _scope = type == null ? null : AccessTools.Constructor(type, new[] { typeof(Hero), typeof(MobileParty) });
        }

        /// <summary>Runs <paramref name="action"/> with the game's player being <paramref name="hero"/>. False if it could not.</summary>
        public static bool Run(Hero hero, Action action)
        {
            if (action == null)
                return false;
            var scope = Enter(hero);
            if (scope == null)
                return false;
            try { action(); }
            finally { scope.Dispose(); }
            return true;
        }

        /// <summary>
        /// Opens the scope until the returned handle is disposed (on the same frame, main thread), or null if it could
        /// not. For a Harmony prefix and finalizer around a Bellum method.
        /// </summary>
        public static IDisposable Enter(Hero hero)
        {
            Resolve();
            if (hero == null || !Coop.OnServer)
                return null;
            if (_scope == null)
            {
                SubModule.Log("Agency: Coop's player scope was not found; the server cannot act as " + hero.StringId + ".");
                return null;
            }
            IDisposable scope;
            try { scope = (IDisposable)_scope.Invoke(new object[] { hero, hero.PartyBelongedTo }); }
            catch (Exception ex)
            {
                SubModule.Log("Agency: could not act as " + hero.StringId + ": " + ex.GetBaseException().Message);
                return null;
            }
            var outer = _current;
            _current = hero;
            return new Exit(hero, outer, scope);
        }

        private sealed class Exit : IDisposable
        {
            private readonly Hero _hero, _outer;
            private IDisposable _scope;

            public Exit(Hero hero, Hero outer, IDisposable scope) { _hero = hero; _outer = outer; _scope = scope; }

            // Coop's scope restores whoever was the player before it, so scopes nest (one player's answer asking another).
            public void Dispose()
            {
                if (_scope == null)
                    return;
                _current = _outer;
                try { _scope.Dispose(); }
                catch (Exception ex) { SubModule.Log("Agency: could not leave " + _hero.StringId + "'s scope: " + ex.GetBaseException().Message); }
                _scope = null;
            }
        }
    }
}
