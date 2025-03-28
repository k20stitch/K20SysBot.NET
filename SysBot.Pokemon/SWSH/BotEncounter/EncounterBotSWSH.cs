using PKHeX.Core;
using SysBot.Base;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;
using static SysBot.Base.SwitchStick;

namespace SysBot.Pokemon;

public abstract class EncounterBotSWSH : PokeRoutineExecutor8SWSH, IEncounterBot
{
    protected readonly PokeTradeHub<PK8> Hub;
    private readonly IDumper DumpSetting;
    private readonly EncounterSettings Settings;
    private readonly int[] DesiredMinIVs;
    private readonly int[] DesiredMaxIVs;
    public ICountSettings Counts => Settings;
    public readonly IReadOnlyList<string> UnwantedMarks;

    protected EncounterBotSWSH(PokeBotState Config, PokeTradeHub<PK8> hub) : base(Config)
    {
        Hub = hub;
        Settings = Hub.Config.EncounterSWSH;
        DumpSetting = Hub.Config.Folder;
        StopConditionSettings.InitializeTargetIVs(Hub.Config, out DesiredMinIVs, out DesiredMaxIVs);
        StopConditionSettings.ReadUnwantedMarks(Hub.Config.StopConditions, out UnwantedMarks);
    }

    // Cached offsets that stay the same per session.
    protected ulong OverworldOffset;

    protected int encounterCount;

    public override async Task MainLoop(CancellationToken token)
    {
        var settings = Hub.Config.EncounterSWSH;
        Log("Identifying trainer data of the host console.");
        var sav = await IdentifyTrainer(token).ConfigureAwait(false);
        await InitializeHardware(settings, token).ConfigureAwait(false);

        OverworldOffset = await SwitchConnection.PointerAll(Offsets.OverworldPointer, token).ConfigureAwait(false);

        try
        {
            Log($"Starting main {GetType().Name} loop.");
            Config.IterateNextRoutine();

            // Clear out any residual stick weirdness.
            await ResetStick(token).ConfigureAwait(false);
            await EncounterLoop(sav, token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log(e.Message);
        }

        Log($"Ending {GetType().Name} loop.");
    }

    public override async Task HardStop()
    {
        await ResetStick(CancellationToken.None).ConfigureAwait(false);
        await CleanExit(CancellationToken.None).ConfigureAwait(false);
    }

    protected abstract Task EncounterLoop(SAV8SWSH sav, CancellationToken token);

    // return true if breaking loop
    protected async Task<bool> HandleEncounter(PK8 pk, CancellationToken token)
    {
        encounterCount++;
        var print = StopConditionSettings.GetPrintName(pk);
        Log($"Encounter: {encounterCount}{Environment.NewLine}{print}{Environment.NewLine}");

        var folder = IncrementAndGetDumpFolder(pk);
        if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
            DumpPokemon(DumpSetting.DumpFolder, folder, pk);

        if (!StopConditionSettings.EncounterFound(pk, DesiredMinIVs, DesiredMaxIVs, Hub.Config.StopConditions, UnwantedMarks))
            return false;

        if (Hub.Config.StopConditions.CaptureVideoClip)
        {
            await Task.Delay(Hub.Config.StopConditions.ExtraTimeWaitCaptureVideo, token).ConfigureAwait(false);
            await PressAndHold(CAPTURE, 2_000, 0, token).ConfigureAwait(false);
        }

        var mode = Settings.ContinueAfterMatch;
        var msg = $"Result found!\n{print}\n" + GetModeMessage(mode);

        if (!string.IsNullOrWhiteSpace(Hub.Config.StopConditions.MatchFoundEchoMention))
            msg = $"{Hub.Config.StopConditions.MatchFoundEchoMention} {msg}";
        EchoUtil.Echo(msg);

        if (mode == ContinueAfterMatch.StopExit)
            return true;
        if (mode == ContinueAfterMatch.Continue)
            return false;

        IsWaiting = true;
        while (IsWaiting)
            await Task.Delay(1_000, token).ConfigureAwait(false);
        return false;
    }

    private static string GetModeMessage(ContinueAfterMatch mode) => mode switch
    {
        ContinueAfterMatch.Continue             => "Continuing...",
        ContinueAfterMatch.PauseWaitAcknowledge => "Waiting for instructions to continue.",
        ContinueAfterMatch.StopExit             => "Stopping routine execution; restart the bot to search again.",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Match result type was invalid."),
    };

    private string IncrementAndGetDumpFolder(PK8 pk)
    {
        var legendary = SpeciesCategory.IsLegendary(pk.Species) || SpeciesCategory.IsMythical(pk.Species) || SpeciesCategory.IsSubLegendary(pk.Species);
        if (legendary)
        {
            Settings.AddCompletedLegends();
            return "legends";
        }

        if (pk.IsEgg)
        {
            Settings.AddCompletedEggs();
            return "egg";
        }

        if (pk.Species is >= (int)Species.Dracozolt and <= (int)Species.Arctovish)
        {
            Settings.AddCompletedFossils();
            return "fossil";
        }

        Settings.AddCompletedEncounters();
        return "encounters";
    }

    private bool IsWaiting;
    public void Acknowledge() => IsWaiting = false;

    protected Task ResetStick(CancellationToken token)
    {
        // If aborting the sequence, we might have the stick set at some position. Clear it just in case.
        return SetStick(LEFT, 0, 0, 0_500, token); // reset
    }

    protected async Task FleeToOverworld(CancellationToken token)
    {
        // This routine will always escape a battle.
        await Click(DUP, 0_200, token).ConfigureAwait(false);
        await Click(A, 1_000, token).ConfigureAwait(false);

        while (await IsInBattle(token).ConfigureAwait(false))
        {
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(B, 1_000, token).ConfigureAwait(false);
            await Click(DUP, 0_200, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);
        }
    }
}
