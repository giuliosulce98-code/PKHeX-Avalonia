using System;
using System.Collections.Generic;
using System.Threading;
using PKHeX.Application.Abstractions;
using PKHeX.Application.UseCases;
using PKHeX.Core;
using PKHeX.Core.AutoMod;

namespace PKHeX.Infrastructure.AutoLegality;

public sealed class LivingDexService : ILivingDexService
{
    private readonly LivingDexVerificationUseCase _verification = new();

    public LivingDexGenerationResult Generate(SaveFile sav, LivingDexOptions options, IProgress<LivingDexGenerationProgress>? progress = null, CancellationToken cancellationToken = default, int? maxSpeciesId = null)
    {
        ArgumentNullException.ThrowIfNull(sav);

        if (cancellationToken.IsCancellationRequested)
            return LivingDexGenerationResult.Cancel();

        var tr = (ITrainerInfo)sav;
        var personal = sav.Personal;
        var context = sav.Context;
        var generation = sav.Generation;
        var strings = GameInfo.Strings;

        var maxSpecies = maxSpeciesId is { } cap ? Math.Min(cap, personal.MaxSpeciesID) : personal.MaxSpeciesID;
        var candidates = new List<PKM>();
        var failedNames = new List<string>();
        var completed = 0;

        try
        {
            for (ushort s = 1; s <= maxSpecies; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (personal.IsSpeciesInGame(s))
                    ProcessSpecies(tr, personal, strings, context, generation, s, options, candidates, failedNames, cancellationToken);

                completed++;
                progress?.Report(new LivingDexGenerationProgress(completed, maxSpecies));
            }
        }
        catch (OperationCanceledException)
        {
            return LivingDexGenerationResult.Cancel();
        }

        var verified = _verification.Verify(candidates);
        var allSkipped = new List<string>(failedNames.Count + verified.SkippedSpeciesNames.Count);
        allSkipped.AddRange(failedNames);
        allSkipped.AddRange(verified.SkippedSpeciesNames);
        return LivingDexGenerationResult.Ok(verified.Accepted, allSkipped);
    }

    private static void ProcessSpecies(ITrainerInfo tr, IPersonalTable personal, GameStrings strings, EntityContext context, byte generation, ushort species, LivingDexOptions options, List<PKM> candidates, List<string> failedNames, CancellationToken cancellationToken)
    {
        var numForms = personal[species].FormCount;
        if (numForms == 1 && options.IncludeForms)
            numForms = (byte)FormConverter.GetFormList(species, strings.types, strings.forms, GameInfo.GenderSymbolUnicode, context).Length;

        for (byte f = 0; f < numForms; f++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var form = options.IncludeForms ? f : ModLogic.GetBaseForm((Species)species, f, tr);

            if (!personal.IsPresentInGame(species, form)
                || FormInfo.IsLordForm(species, form, context)
                || FormInfo.IsBattleOnlyForm(species, form, generation)
                || FormInfo.IsFusedForm(species, form, generation)
                || (FormInfo.IsTotemForm(species, form) && context is not EntityContext.Gen7))
                continue;

            var pk = TryGenerateOne(tr, species, form, options.SetShiny);
            if (pk is null)
            {
                failedNames.Add(LivingDexVerificationUseCase.GetDisplayName(species, form, strings));
                if (!options.IncludeForms)
                    break;
                continue;
            }

            candidates.Add(pk);
            if (!options.IncludeForms)
                break;
        }
    }

    private static PKM? TryGenerateOne(ITrainerInfo tr, ushort species, byte form, bool shiny)
    {
        PKM blank;
        try
        {
            blank = EntityBlank.GetBlank(tr);
            blank.Species = species;
            blank.Gender = blank.GetSaneGender();
            blank.Form = form;
        }
        catch
        {
            return null;
        }

        string setText;
        try
        {
            var rendered = new ShowdownSet(blank).Text.Replace("\r", "");
            var firstLine = rendered.Split('\n')[0];
            setText = firstLine;

            if (shiny && !SimpleEdits.IsShinyLockedSpeciesForm(species, blank.Form))
                setText += "\nShiny: Yes";
        }
        catch
        {
            return null;
        }

        ShowdownSet set;
        try
        {
            set = new ShowdownSet(setText);
        }
        catch
        {
            return null;
        }

        if (set.Species == 0)
            return null;

        APILegality.AsyncLegalizationResult result;
        try
        {
            result = tr.GetLegalFromSet(set);
        }
        catch
        {
            return null;
        }

        if (result.Status != LegalizationResult.Regenerated)
            return null;

        var pk = result.Created;

        // Lower the generated Pokémon to the minimum CURRENT level that
        // PKHeX itself still considers legal. This intentionally uses the
        // legality engine instead of a hard-coded evolution table.
        //
        // Examples for ordinary Gen 1 starters normally become:
        // Bulbasaur 1, Ivysaur 16, Venusaur 32
        // Charmander 1, Charmeleon 16, Charizard 36
        //
        // If a species has a legitimate special encounter below its normal
        // evolution level, PKHeX is allowed to keep that lower legal level.
        try
        {
            var legality = new LegalityAnalysis(pk);
            if (!legality.Valid)
                return null;

            EncounterSuggestion.IterateMinimumCurrentLevel(
                pk,
                isLegal: true,
                level: pk.CurrentLevel);

            if (!new LegalityAnalysis(pk).Valid)
                return null;
        }
        catch
        {
            return null;
        }

        pk.Heal();
        return pk;
    }
}
