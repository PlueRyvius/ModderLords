using System;
using System.Linq;
using ModderLords.CompatSync;
using TaleWorlds.CampaignSystem.CharacterCreationContent;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace ModderLords.CompatSync.Coop;

/// <summary>
/// Player's game started by the smoke test only (launch argument <see cref="Flag"/>): when the world has no character
/// for this player, Coop sends the game into character creation, which needs a person. This walks it instead, one step
/// per tick, choosing at random: a culture, a story option in every menu (only the ones the game allows), the default
/// face and banner, and a name "Smoke NNNN". Finishing goes through the game's own FinalizeCharacterCreationState, which
/// Coop patches to send the new hero to the server, so the join carries on exactly as after a person's choices.
/// </summary>
internal static class SmokeCharacterCreation
{
    internal const string Flag = "/modderlords-smoke";
    // The smoke test matches these prefixes (ModderLords.Core.Smoke.SmokeSignals); keep them stable.
    internal const string StartedLine = "smoke test: creating a character automatically";
    internal const string DoneLine = "smoke test: character created: ";
    internal const string StuckLine = "smoke test: character creation is stuck at ";

    private static readonly bool Enabled = Environment.GetCommandLineArgs().Any(a => a.Equals(Flag, StringComparison.OrdinalIgnoreCase));
    private static readonly Random Random = new Random();
    private static bool _started, _finished, _warned;
    private static string _culture = "", _name = "";
    private static object? _lastStage;
    private static int _sameStageTicks;

    /// <summary>Player's game, every tick (Bridge.Tick, 3 s): one step of character creation, if one is open.</summary>
    internal static void ClientTick()
    {
        if (!Enabled || _finished) return;
        var state = GameStateManager.Current?.ActiveState;

        // Before creation proper the campaign intro video plays; it waits for a click.
        if (state is VideoPlaybackState video && (video.VideoPath ?? "").Contains("campaign_intro"))
        {
            video.OnVideoFinished();
            return;
        }
        if (state is not CharacterCreationState creation)
        {
            if (_started && !_finished)
            {
                _finished = true;
                Log.Info($"{DoneLine}{_name} ({_culture})");
            }
            return;
        }
        var manager = creation.CharacterCreationManager;
        var stage = manager.CurrentStage;
        if (stage == null) return;
        if (!_started)
        {
            _started = true;
            Log.Info(StartedLine);
        }

        // A stage that does not move on after many steps (a mod's own stage that needs input) is reported once.
        _sameStageTicks = ReferenceEquals(stage, _lastStage) ? _sameStageTicks + 1 : 0;
        _lastStage = stage;
        if (_sameStageTicks > 20 && !_warned)
        {
            _warned = true;
            Log.Warn(StuckLine + stage.GetType().Name);
        }

        var content = manager.CharacterCreationContent;
        switch (stage)
        {
            case CharacterCreationCultureStage:
                var cultures = content.GetCultures().ToList();
                if (cultures.Count > 0)
                {
                    var culture = cultures[Random.Next(cultures.Count)];
                    content.SetSelectedCulture(culture, manager);
                    _culture = culture.Name?.ToString() ?? culture.StringId;
                }
                manager.NextStage();
                break;

            case CharacterCreationNarrativeStage:
                if (manager.CurrentMenu == null) manager.StartNarrativeStage();
                var options = manager.CurrentMenu == null ? new System.Collections.Generic.List<NarrativeMenuOption>()
                    : manager.GetSuitableNarrativeMenuOptions().ToList();
                if (options.Count > 0) manager.OnNarrativeMenuOptionSelected(options[Random.Next(options.Count)]);
                // As the screen's Next does: the following menu, or the next stage after the last one.
                if (options.Count == 0 || !manager.TrySwitchToNextMenu()) manager.NextStage();
                break;

            case CharacterCreationClanNamingStage:
                _name = "Smoke " + Random.Next(1000, 10000);
                content.SetMainCharacterName(_name);
                manager.NextStage();
                break;

            default:
                // Face, banner, review and options keep their defaults; a stage a mod adds gets the same treatment.
                manager.NextStage();
                break;
        }
    }
}
