using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using ModderLords.CompatSync.Coop.Taom;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace ModderLords.CompatSync.Coop.Fourberie;

/// <summary>
/// fourb-prompts (docs/FOURBERIE-LAYER-PLAN.md). Fourberie's ticks ask the player things: a contract offer, a
/// blackmail demand, which fief to give up. On the server those calls used to reach the headless guard, which says yes
/// to everything, so the server accepted demands and wars on players' behalf. Now, while a player's Fourberie runs on the
/// server, a prompt goes to that player's game; its callbacks wait on the server and run, as that player with their
/// book, when the answer comes back.
///
/// No answer within 10 minutes means the AI decides, as it would for a lord (maintainer decision 2026-09-30, shared with
/// the Bellum layer): the AI can still act for an away player. An offline player is no special case: their prompt
/// waits, reaches them if they reconnect in time, and otherwise the AI decides at the same deadline. See AiAnswer for
/// what the AI picks and why.
///
/// Map notices and the contract's map conversation go to the player's game too, sent after the book rows the same run
/// changed, so the dialog finds the contract it is about. Plain messages are forwarded by the shared notice forwarder.
/// </summary>
internal static class FbPrompts
{
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromMinutes(10);

    private sealed class Pending
    {
        public string Key = "";
        public InquiryData? Inquiry;
        public MultiSelectionInquiryData? Multi;
        public DateTime Expires;
        /// <summary>The message for the player's game; sent when they are (or come back) online.</summary>
        public List<string> Message = new List<string>();
        public bool Sent;
    }

    private static readonly Dictionary<int, Pending> Waiting = new Dictionary<int, Pending>();
    private static readonly Dictionary<string, List<List<string>>> Outbox = new Dictionary<string, List<List<string>>>(StringComparer.Ordinal);
    private static readonly Dictionary<string, Dictionary<string, string>> TextVariables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
    private static int _nextId;
    private static long _sent, _answered, _aiDecided;
    private static DateTime _nextExpiryCheck = DateTime.MinValue;

    // ---- server: capture ------------------------------------------------------------------------------------------

    /// <summary>The player whose Fourberie is running on the server right now, or null outside any player's run.</summary>
    private static string? Running => FourberieLayer.IsServer ? FbBooks.Current : null;

    /// <summary>Called by FbBooks when a player's book is installed afresh: text variables from an earlier run are stale.</summary>
    internal static void BookEntered(string key) => TextVariables.Remove(key);

    internal static bool InquiryPrefix(InquiryData __0)
    {
        if (Running is not { } key || __0 == null) return true;
        var (affirmativeEnabled, affirmativeHint) = Evaluate(__0.GetIsAffirmativeOptionEnabled);
        var (negativeEnabled, negativeHint) = Evaluate(__0.GetIsNegativeOptionEnabled);
        var id = ++_nextId;
        Hold(id, new Pending
        {
            Key = key, Inquiry = __0,
            Message = FbPromptWire.Pack(new FbInquiry
            {
                Id = id, Title = __0.TitleText ?? "", Text = __0.Text ?? "", AffirmativeText = __0.AffirmativeText ?? "", NegativeText = __0.NegativeText ?? "",
                AffirmativeShown = __0.IsAffirmativeOptionShown, NegativeShown = __0.IsNegativeOptionShown,
                AffirmativeEnabled = affirmativeEnabled, AffirmativeHint = affirmativeHint, NegativeEnabled = negativeEnabled, NegativeHint = negativeHint,
            }),
        });
        return false;
    }

    internal static bool MultiPrefix(MultiSelectionInquiryData __0)
    {
        if (Running is not { } key || __0 == null) return true;
        var id = ++_nextId;
        var q = new FbMultiInquiry
        {
            Id = id, Title = __0.TitleText ?? "", Description = __0.DescriptionText ?? "", AffirmativeText = __0.AffirmativeText ?? "",
            NegativeText = __0.NegativeText ?? "", ExitShown = __0.IsExitShown, Min = __0.MinSelectableOptionCount, Max = __0.MaxSelectableOptionCount,
        };
        foreach (var e in __0.InquiryElements ?? new List<InquiryElement>()) q.Elements.Add((e.Title ?? "", e.Hint ?? "", e.IsEnabled));
        Hold(id, new Pending { Key = key, Multi = __0, Message = FbPromptWire.Pack(q) });
        return false;
    }

    internal static bool MapNoticePrefix(InformationData __0)
    {
        if (Running is not { } key || __0 == null || __0.GetType().FullName != "Fourberie.MapNotifGrudgeData") return true;
        if (IsConnected(key))
        {
            var t = Traverse.Create(__0);
            Queue(key, FbPromptWire.Pack(new FbMapNotice
            {
                NotifType = t.Property("NotifType").GetValue<string>() ?? "",
                ActionType = t.Property("ActionType").GetValue<string>() ?? "",
                ActionStringId = t.Property("ActionStringId").GetValue<string>() ?? "",
                Description = __0.DescriptionText?.ToString() ?? "",
            }));
        }
        return false;   // the server keeps no notices of its own
    }

    internal static bool ConversationPrefix(ConversationCharacterData __1)
    {
        if (Running is not { } key) return true;
        if (IsConnected(key) && __1.Character is { } partner)
        {
            var c = new FbConversation { CharacterId = partner.StringId };
            if (TextVariables.TryGetValue(key, out var vars)) c.Variables.AddRange(vars.Select(p => (p.Key, p.Value)));
            Queue(key, FbPromptWire.Pack(c));
        }
        return false;   // there is nobody on the server to talk to
    }

    /// <summary>Records the text variables a player's run sets; their dialog lines read them on the player's game.</summary>
    internal static void TextVariablePostfix(object[] __args)
    {
        if (Running is not { } key || __args.Length < 2 || __args[0] is not string name || __args[1] is not { } value) return;
        if (!TextVariables.TryGetValue(key, out var vars)) TextVariables[key] = vars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (vars.Count < FbPromptWire.MaxVariables || vars.ContainsKey(name)) vars[name] = value.ToString() ?? "";
    }

    private static (bool, string) Evaluate(Func<(bool, string)>? check)
    {
        if (check == null) return (true, "");
        try { var (ok, hint) = check(); return (ok, hint ?? ""); }
        catch { return (true, ""); }
    }

    private static void Queue(string key, List<string> message)
    {
        if (!Outbox.TryGetValue(key, out var list)) Outbox[key] = list = new List<List<string>>();
        list.Add(message);
        FbBooks.SendSoon();
    }

    private static bool IsConnected(string key) => FbBooks.Connected(withGame: true).Any(p => p.Hero.StringId == key);

    // ---- server: defaults, answers, sending ----------------------------------------------------------------------

    /// <summary>Holds a prompt's callbacks until the player answers or the deadline passes; sends it now if they are online.</summary>
    private static void Hold(int id, Pending pending)
    {
        pending.Expires = DateTime.UtcNow + AnswerWithin;
        Waiting[id] = pending;
        if (!IsConnected(pending.Key)) return;   // sent if they come back before the deadline
        Queue(pending.Key, pending.Message);
        pending.Sent = true;
    }

    /// <summary>
    /// What the AI picks when the player does not answer: the affirmative option when it is shown and enabled, else the
    /// negative one; from a list, the first enabled options (as many as it must take, at least one). Reviewed against
    /// every prompt Fourberie can raise from a server-run handler (FourberieSurfaceTests pins that list), this is what
    /// Fourberie's own NPCs do in each case: a blackmailed clan pays (FSchBlackSu makes NPC victims pay what they can),
    /// a bribe the player arranged gets paid, influence is spent to soften being uncovered when there is enough, a clan
    /// leaving a kingdom keeps its holdings as vanilla AI clans do, and a notice is acknowledged. Fourberie's own
    /// callbacks still apply their checks (not enough gold).
    /// </summary>
    private static void AiAnswer(InquiryData q)
    {
        _aiDecided++;
        var (affirmativeEnabled, _) = Evaluate(q.GetIsAffirmativeOptionEnabled);
        var (negativeEnabled, _) = Evaluate(q.GetIsNegativeOptionEnabled);
        if (q.IsAffirmativeOptionShown && affirmativeEnabled) q.AffirmativeAction?.Invoke();
        else if (q.IsNegativeOptionShown && negativeEnabled) q.NegativeAction?.Invoke();
        else (q.NegativeAction ?? q.AffirmativeAction)?.Invoke();
    }

    private static void AiAnswer(MultiSelectionInquiryData q)
    {
        _aiDecided++;
        var chosen = (q.InquiryElements ?? new List<InquiryElement>()).Where(e => e.IsEnabled).Take(Math.Max(1, q.MinSelectableOptionCount)).ToList();
        if (chosen.Count > 0 && q.AffirmativeAction != null) q.AffirmativeAction(chosen);
        else q.NegativeAction?.Invoke(new List<InquiryElement>());
    }

    /// <summary>Server, game thread, inside TaomActions.Run: a player's answer to one of their prompts.</summary>
    internal static TaomActionOutcome Answer(Hero hero, MobileParty? party, IList<string> args)
    {
        if (args.Count == 0 || !int.TryParse(args[0], out var id) || !Waiting.TryGetValue(id, out var pending) || pending.Key != hero.StringId)
            return TaomActionOutcome.Fail("");
        var multi = pending.Multi;
        if (!FbPromptWire.TryReadAnswer(args, out _, out var yes, out var picked,
                multi?.InquiryElements?.Count ?? 0, multi?.MinSelectableOptionCount ?? 0, multi?.MaxSelectableOptionCount ?? 0))
            return TaomActionOutcome.Fail("");
        Waiting.Remove(id);
        Run(hero, party, () =>
        {
            if (pending.Inquiry is { } q)
            {
                var (affirmativeEnabled, _) = Evaluate(q.GetIsAffirmativeOptionEnabled);
                if (yes && q.IsAffirmativeOptionShown && affirmativeEnabled) q.AffirmativeAction?.Invoke();
                else if (!yes) q.NegativeAction?.Invoke();
            }
            else if (multi != null)
            {
                if (picked != null)
                {
                    var elements = multi.InquiryElements ?? new List<InquiryElement>();
                    var chosen = picked.Select(i => elements[i]).Where(e => e.IsEnabled).ToList();
                    if (chosen.Count >= multi.MinSelectableOptionCount) multi.AffirmativeAction?.Invoke(chosen);
                    else AiAnswer(multi);
                }
                else multi.NegativeAction?.Invoke(new List<InquiryElement>());
            }
        });
        _answered++;
        return new TaomActionOutcome(true, "");
    }

    /// <summary>Runs a held callback as the player with their book. Messages it shows are forwarded like any tick's.</summary>
    private static void Run(Hero hero, MobileParty? party, Action action)
    {
        var running = TaomActions.Running;
        TaomActions.Running = false;   // the answer carries no message; let the notice forwarder send what the callback shows
        try
        {
            using var scope = FbBooks.Enter(hero, party);
            action();
        }
        catch (Exception ex) { Log.Warn($"{FourberieLayer.Tag}prompt callback failed for {hero.Name}: {ex.GetBaseException().Message}"); }
        finally { TaomActions.Running = running; }
    }

    /// <summary>Server: sends this player's queued prompts (after FbBooks sent their book rows), and defaults expired ones.</summary>
    internal static void Flush(Hero hero)
    {
        if (!Outbox.TryGetValue(hero.StringId, out var list) || list.Count == 0 || TaomActions.Push == null) return;
        Outbox.Remove(hero.StringId);
        foreach (var message in list) { TaomActions.Push(hero, FbPromptWire.Feature, message); _sent++; }
    }

    /// <summary>Server: sends prompts held for players who came back, and lets the AI answer the ones past the deadline.</summary>
    internal static void ServerTick()
    {
        if (!FourberieLayer.IsServer || DateTime.UtcNow < _nextExpiryCheck) return;
        _nextExpiryCheck = DateTime.UtcNow.AddSeconds(5);
        Expire(DateTime.UtcNow);
        foreach (var pending in Waiting.Values.Where(p => !p.Sent).ToList())
        {
            if (!IsConnected(pending.Key)) continue;
            Queue(pending.Key, pending.Message);
            pending.Sent = true;
        }
    }

    /// <summary>The AI answers every prompt whose deadline is at or before <paramref name="now"/>, as its player.</summary>
    internal static int Expire(DateTime now)
    {
        var due = Waiting.Where(p => p.Value.Expires <= now).ToList();
        foreach (var pair in due)
        {
            Waiting.Remove(pair.Key);
            var hero = FbBooks.HeroFor(pair.Value.Key);
            if (hero == null) continue;
            Run(hero, hero.PartyBelongedTo, () =>
            {
                if (pair.Value.Inquiry is { } q) AiAnswer(q);
                else if (pair.Value.Multi is { } m) AiAnswer(m);
            });
            Log.Info($"{FourberieLayer.Tag}prompts: no answer from {hero.Name} in time; the AI decided");
        }
        return due.Count;
    }

    /// <summary>Self-test: how many prompts are waiting for a player.</summary>
    internal static int WaitingFor(string key) => Waiting.Values.Count(p => p.Key == key);

    internal static string Summary() => $"prompts sent {_sent}, answered {_answered}, decided by the AI {_aiDecided}, waiting {Waiting.Count}";

    // ---- client ----------------------------------------------------------------------------------------------------

    /// <summary>Client, game thread: a prompt, notice or conversation the server raised for this player.</summary>
    internal static void ClientApply(IList<string> data)
    {
        switch (FbPromptWire.Unpack(data))
        {
            case FbInquiry q:
                InformationManager.ShowInquiry(new InquiryData(q.Title, q.Text, q.AffirmativeShown, q.NegativeShown, q.AffirmativeText, q.NegativeText,
                    () => Reply(FbPromptWire.Answer(q.Id, true)), () => Reply(FbPromptWire.Answer(q.Id, false)),
                    isAffirmativeOptionEnabled: () => (q.AffirmativeEnabled, q.AffirmativeHint),
                    isNegativeOptionEnabled: () => (q.NegativeEnabled, q.NegativeHint)));
                break;
            case FbMultiInquiry m:
                var elements = m.Elements.Select((e, i) => new InquiryElement(i, e.Title, null, e.Enabled, e.Hint)).ToList();
                MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(m.Title, m.Description, elements, m.ExitShown, m.Min, m.Max,
                    m.AffirmativeText, m.NegativeText,
                    chosen => Reply(FbPromptWire.AnswerPicked(m.Id, chosen.Select(c => (int)c.Identifier))),
                    _ => Reply(FbPromptWire.Answer(m.Id, false))));
                break;
            case FbMapNotice n:
                ShowMapNotice(n);
                break;
            case FbConversation c:
                OpenConversation(c);
                break;
            default:
                Log.Warn(FourberieLayer.Tag + "prompts: a server message could not be read");
                break;
        }
    }

    private static void Reply(List<string> answer) => TaomActions.Send?.Invoke(FourberieLayer.Feature, "answer", answer);

    private static void ShowMapNotice(FbMapNotice n)
    {
        var type = TaomLayer.Loaded(FourberieLayer.AssemblyName)?.GetType("Fourberie.MapNotifGrudgeData", false);
        if (type == null || Campaign.Current == null) return;
        var data = Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            new object[] { n.NotifType, n.ActionType, n.ActionStringId, new TextObject("{=!}" + n.Description) }, null) as InformationData;
        if (data != null) Campaign.Current.CampaignInformationManager.NewMapNoticeAdded(data);
    }

    private static void OpenConversation(FbConversation c)
    {
        var partner = MBObjectManager.Instance.GetObject<CharacterObject>(c.CharacterId);
        if (partner == null || Campaign.Current == null) { Log.Warn(FourberieLayer.Tag + "prompts: conversation partner '" + c.CharacterId + "' not found"); return; }
        foreach (var (name, value) in c.Variables) MBTextManager.SetTextVariable(name, new TextObject("{=!}" + value));
        CampaignMapConversation.OpenConversation(new ConversationCharacterData(CharacterObject.PlayerCharacter), new ConversationCharacterData(partner));
    }
}

/// <summary>fourb-prompts: prompts, notices and the contract conversation reach the player they are for.</summary>
internal sealed class FbPromptsComponent : IFbComponent
{
    private const string Owner = "ModderLords.Fourberie.Prompts";
    /// <summary>Above the headless guard and the server core's own auto-accept, so a player's prompt is never answered for them.</summary>
    private const int First = Priority.First + 200;

    public string Id => "prompts";

    public string? SkipReason(FbContext context) => context.Fields == null ? "no books: " + context.FieldsProblem : null;

    public string Install(FbContext context)
    {
        if (!context.IsServer)
        {
            TaomActions.RegisterClientApply(FbPromptWire.Feature, FbPrompts.ClientApply);
            return "this player's game shows the prompts the server raises for them";
        }
        var h = new Harmony(Owner);
        void Prefix(MethodBase? target, string name)
        {
            if (target == null) throw new MissingMethodException(name + " not found");
            h.Patch(target, prefix: new HarmonyMethod(typeof(FbPrompts), name) { priority = First });
        }
        Prefix(AccessTools.Method(typeof(InformationManager), nameof(InformationManager.ShowInquiry)), nameof(FbPrompts.InquiryPrefix));
        Prefix(AccessTools.Method(typeof(MBInformationManager), nameof(MBInformationManager.ShowMultiSelectionInquiry)), nameof(FbPrompts.MultiPrefix));
        Prefix(AccessTools.Method(typeof(CampaignInformationManager), nameof(CampaignInformationManager.NewMapNoticeAdded)), nameof(FbPrompts.MapNoticePrefix));
        Prefix(AccessTools.Method(typeof(CampaignMapConversation), nameof(CampaignMapConversation.OpenConversation)), nameof(FbPrompts.ConversationPrefix));
        // Every SetTextVariable overload except (name, arrayIndex, content), whose second argument is an index.
        var variables = typeof(MBTextManager).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(MBTextManager.SetTextVariable))
            .Where(m => m.GetParameters() is { Length: >= 2 } p && !(p.Length == 3 && p[1].ParameterType == typeof(int)))
            .ToList();
        foreach (var m in variables) h.Patch(m, postfix: new HarmonyMethod(typeof(FbPrompts), nameof(FbPrompts.TextVariablePostfix)));
        var notices = NoticeComponent.EnsureInstalled();
        return $"prompts, map notices and conversations raised in a player's run go to that player; {variables.Count} text-variable setter(s) recorded; {notices}";
    }
}
