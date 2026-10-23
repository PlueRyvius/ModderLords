using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Taom;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// A player's game, in a co-op session: its Fourberie statics are this player's book. The server's copy arrives at join
/// and then row by row as the server's ticks change it; rows this game's menus, screens and missions change are reported
/// back every couple of seconds (FbBookSync). Single-player is untouched: nothing here runs without a co-op session.
/// </summary>
internal static class FbMirrorClient
{
    private const double ReportEverySeconds = 2;
    private const double AskAgainSeconds = 10;

    private static FieldInfo[]? _fields;
    private static IReadOnlyList<(string Name, Type Type)>? _shape;
    private static readonly FbClientLedger Ledger = new FbClientLedger();
    private static bool _campaignReady;
    private static DateTime _nextReport = DateTime.MinValue, _nextAsk = DateTime.MinValue;
    private static long _received, _reported, _asked;
    private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

    internal static void Bind(IEnumerable<FieldInfo> persisted)
    {
        _fields = persisted.ToArray();
        _shape = _fields.Select(f => (f.Name, f.FieldType)).ToList();
    }

    /// <summary>Called by the TAOM client handler when Coop says the joined campaign is ready (both layers share that message).</summary>
    internal static void CampaignReady()
    {
        _campaignReady = true;
        Ledger.Forget();
        FbLedgers.Reset();
        _nextAsk = DateTime.MinValue;
    }

    internal static void SessionEnded()
    {
        _campaignReady = false;
        Ledger.Forget();
    }

    /// <summary>Client, every frame (Bridge.Tick).</summary>
    internal static void ClientTick()
    {
        if (_fields == null || !_campaignReady || !FourberieLayer.IsCoopClient || Campaign.Current == null) return;
        var now = DateTime.UtcNow;
        try
        {
            if (!Ledger.HasBook)
            {
                if (now < _nextAsk) return;
                _nextAsk = now.AddSeconds(AskAgainSeconds);
                TaomActions.Send?.Invoke(FourberieLayer.Feature, "full", Array.Empty<string>());
                _asked++;
                return;
            }
            if (now < _nextReport) return;
            _nextReport = now.AddSeconds(ReportEverySeconds);
            var payload = Ledger.Report(Local());
            if (payload == null) return;
            TaomActions.Send?.Invoke(FourberieLayer.Feature, "report", new[] { payload });
            _reported++;
        }
        catch (Exception ex)
        {
            if (Warned.Add("tick")) Log.Warn(FourberieLayer.Tag + "mirror: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>Client, game thread: the server's book (whole, or the rows it changed).</summary>
    internal static void Apply(IList<string> data)
    {
        if (_fields == null || data.Count != 1 || data[0].Length > FbBooks.MaxPayload) return;
        var install = Ledger.Receive(data[0], Local(), out var needFull);
        if (needFull)
        {
            Log.Info(FourberieLayer.Tag + "mirror: a server message was missed; asking for the whole book");
            _nextAsk = DateTime.MinValue;
            return;
        }
        if (install == null) return;
        var problems = new List<string>();
        var values = FbBookCodec.Decode(install, _shape!, FbGameRefs.Instance, problems, i => _fields[i].GetValue(null));
        for (var i = 0; i < _fields.Length; i++) _fields[i].SetValue(null, values[i]);
        if (Hero.MainHero is { } me) FbLedgers.Apply(me.StringId, install[FbLedgers.Section], problems);
        FbCrime.ApplyOnClient(install[FbCrime.Section]);
        if (problems.Count > 0 && Warned.Add("decode|" + problems[0]))
            Log.Warn($"{FourberieLayer.Tag}mirror: {problems.Count} part(s) of the server's book could not be matched on this game: {FourberieLayer.Some(problems, 3)}");
        if (_received++ == 0) Log.Info(FourberieLayer.Tag + "mirror: this player's Fourberie book arrived from the server");
    }

    private static JObject Local()
    {
        var problems = new List<string>();
        var json = FbBookCodec.Encode(_fields!.Select(f => (f.Name, f.FieldType, f.GetValue(null))), FbGameRefs.Instance, problems);
        if (problems.Count > 0 && Warned.Add("encode|" + problems[0]))
            Log.Warn($"{FourberieLayer.Tag}mirror: {problems.Count} part(s) of this player's book cannot be sent: {FourberieLayer.Some(problems, 3)}");
        if (Hero.MainHero is { } me) json[FbLedgers.Section] = FbLedgers.Capture(me.StringId);
        json[FbCrime.Section] = FbCrime.CaptureOnClient();
        return json;
    }

    internal static string Summary() => $"book {(Ledger.HasBook ? "in step" : "not received")}, server messages {_received}, reports {_reported}, asked {_asked}";
}

/// <summary>The client half of keeping a player's book in step with the server.</summary>
internal sealed class FbMirrorComponent : IFbComponent
{
    public string Id => "mirror";

    public string? SkipReason(FbContext context)
    {
        if (context.IsServer) return "server (books)";
        if (context.Fields == null) return "Fourberie changed: " + context.FieldsProblem;
        return FbGameRefs.Problem;
    }

    public string Install(FbContext context)
    {
        FbGameRefs.IsShared = obj =>
            !GameInterface.ContainerProvider.TryResolve<GameInterface.Services.ObjectManager.IObjectManager>(out var objects) || objects.Contains(obj);
        FbMirrorClient.Bind(context.Fields!.Where(f => FbFields.Persisted.Contains(f.Name) && f.DeclaringType?.FullName == FbFields.Behavior));
        TaomActions.RegisterClientApply(FourberieLayer.Feature, FbMirrorClient.Apply);
        return "this player's book follows the server's, and this game's changes are reported back";
    }
}
