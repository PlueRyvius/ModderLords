using System.Reflection;
using HarmonyLib;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.Localization;

namespace ModderLords.CompatSync.Coop.Taom;

/// <summary>
/// TAOM's named companions (Aragorn, Legolas, Gimli and the rest; named_companions.xml) are heroes from TAOM's
/// heroes.xml, not made from a wanderer template, so their <c>Hero.Template</c> is null on every peer. The game's
/// wanderer introduction ("Tell me about yourself") reads <c>Template.StringId</c> for the backstory texts and throws.
/// In single player TAOM marks them met at campaign start, so the introduction never runs; in co-op a joining player
/// has not met them, the condition throws inside the conversation, and the conversation stops where it is: the player
/// report of 2026-11-18, "every time someone tries to talk to a companion to hire them the game freezes".
/// Reproduced by the taom/hire-wanderer suite (NullReferenceException in
/// <c>conversation_wanderer_preintroduction_on_condition</c> on the player's game).
///
/// With no template there is no backstory to tell (TAOM ships backstory texts only for its spc_wanderer_* templates),
/// so such a wanderer gives the game's own short answer instead (the <c>wanderer_backstory_generic</c> line, which
/// already exists for this state) and the conversation goes on to the usual options, hiring included.
/// </summary>
internal sealed class WandererIntroductionComponent : ITaomComponent
{
    private static MethodInfo? _preintroduction;
    private static MethodInfo? _introduction;

    public string Id => "wanderer-introduction";

    public string? SkipReason(TaomContext context)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        _preintroduction = typeof(LordConversationsCampaignBehavior).GetMethod("conversation_wanderer_preintroduction_on_condition", flags);
        _introduction = typeof(LordConversationsCampaignBehavior).GetMethod("conversation_wanderer_introduction_on_condition", flags);
        if (_preintroduction == null || _introduction == null)
            return "the game changed; not found: LordConversationsCampaignBehavior.conversation_wanderer_(pre)introduction_on_condition";
        return null;
    }

    public string Install(TaomContext context)
    {
        var harmony = new Harmony("ModderLords.Taom.WandererIntroduction");
        harmony.Patch(_preintroduction!, prefix: new HarmonyMethod(typeof(WandererIntroductionComponent), nameof(PreintroductionPrefix)));
        harmony.Patch(_introduction!, prefix: new HarmonyMethod(typeof(WandererIntroductionComponent), nameof(IntroductionPrefix)));
        return "a wanderer with no template (TAOM's named companions) gives the short introduction instead of throwing";
    }

    /// <summary>True when the wanderer in this conversation has no template, so the game's backstory lines cannot run.</summary>
    internal static bool HasNoTemplate(Hero? hero) => hero != null && hero.Template == null;

    private static bool PreintroductionPrefix(ref bool __result)
    {
        var hero = Hero.OneToOneConversationHero;
        if (!HasNoTemplate(hero)) return true;
        // The game's own words for a wanderer who will not tell their story; the next line for this state shows them.
        MBTextManager.SetTextVariable("WANDERER_GENERIC_BACKSTORY",
            new TextObject("{=pBb6sevv}It is enough to say that I am looking for a new employer, and I will serve loyally so long as I am treated well and paid well."));
        Log.Info($"TAOM wanderer introduction: {hero!.StringId} has no template; giving the short introduction");
        __result = false;
        return false;
    }

    private static bool IntroductionPrefix(ref bool __result)
    {
        if (!HasNoTemplate(Hero.OneToOneConversationHero)) return true;
        __result = false;
        return false;
    }
}
