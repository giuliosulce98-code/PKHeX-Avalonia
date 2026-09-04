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

    public LivingDexGenerationResult Generate(
        SaveFile sav,
        LivingDexOptions options,
        IProgress<LivingDexGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int? maxSpeciesId = null)
    {
        ArgumentNullException.ThrowIfNull(sav);

        if (cancellationToken.IsCancellationRequested)
            return LivingDexGenerationResult.Cancel();

        var tr = (ITrainerInfo)sav;
        var personal = sav.Personal;
        var context = sav.Context;
        var generation = sav.Generation;
        var strings = GameInfo.Strings;

        var maxSpecies = maxSpeciesId is { } cap
            ? Math.Min(cap, personal.MaxSpeciesID)
            : personal.MaxSpeciesID;

        var candidates = new List<PKM>();
        var failedNames = new List<string>();
        var completed = 0;

        try
        {
            for (ushort species = 1; species <= maxSpecies; species++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (personal.IsSpeciesInGame(species))
                {
                    ProcessSpecies(
                        tr,
                        personal,
                        strings,
                        context,
                        generation,
                        species,
                        options,
                        candidates,
                        failedNames,
                        cancellationToken);
                }

                completed++;

                progress?.Report(
                    new LivingDexGenerationProgress(
                        completed,
                        maxSpecies));
            }
        }
        catch (OperationCanceledException)
        {
            return LivingDexGenerationResult.Cancel();
        }

        // Final independent legality check on every generated Pokémon.
        var verified = _verification.Verify(candidates);

        var allSkipped = new List<string>(
            failedNames.Count + verified.SkippedSpeciesNames.Count);

        allSkipped.AddRange(failedNames);
        allSkipped.AddRange(verified.SkippedSpeciesNames);

        return LivingDexGenerationResult.Ok(
            verified.Accepted,
            allSkipped);
    }

    private static void ProcessSpecies(
        ITrainerInfo tr,
        IPersonalTable personal,
        GameStrings strings,
        EntityContext context,
        byte generation,
        ushort species,
        LivingDexOptions options,
        List<PKM> candidates,
        List<string> failedNames,
        CancellationToken cancellationToken)
    {
        var numForms = personal[species].FormCount;

        if (numForms == 1 && options.IncludeForms)
        {
            numForms = (byte)FormConverter.GetFormList(
                species,
                strings.types,
                strings.forms,
                GameInfo.GenderSymbolUnicode,
                context).Length;
        }

        for (byte f = 0; f < numForms; f++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var form = options.IncludeForms
                ? f
                : ModLogic.GetBaseForm((Species)species, f, tr);

            if (!personal.IsPresentInGame(species, form)
                || FormInfo.IsLordForm(species, form, context)
                || FormInfo.IsBattleOnlyForm(species, form, generation)
                || FormInfo.IsFusedForm(species, form, generation)
                || (FormInfo.IsTotemForm(species, form)
                    && context is not EntityContext.Gen7))
            {
                continue;
            }

            var pk = TryGenerateAtMinimumLegalLevel(
                tr,
                species,
                form,
                options.SetShiny,
                cancellationToken);

            if (pk is null)
            {
                failedNames.Add(
                    LivingDexVerificationUseCase.GetDisplayName(
                        species,
                        form,
                        strings));

                if (!options.IncludeForms)
                    break;

                continue;
            }

            candidates.Add(pk);

            if (!options.IncludeForms)
                break;
        }
    }

    /// <summary>
    /// Searches for the absolute minimum current level at which
    /// Auto-Legality can actually generate this species/form as legal.
    ///
    /// Level 1 is tried first, then 2, 3, etc. up to 100.
    /// Special/event/underleveled encounters are therefore allowed
    /// whenever PKHeX itself considers them legal.
    /// </summary>
    private static PKM? TryGenerateAtMinimumLegalLevel(
        ITrainerInfo tr,
        ushort species,
        byte form,
        bool shiny,
        CancellationToken cancellationToken)
    {
        string firstLine;

        try
        {
            var blank = EntityBlank.GetBlank(tr);

            blank.Species = species;
            blank.Form = form;
            blank.Gender = blank.GetSaneGender();

            var rendered = new ShowdownSet(blank)
                .Text
                .Replace("\r", "");

            firstLine = rendered.Split('\n')[0];
        }
        catch
        {
            return null;
        }

        bool requestShiny =
            shiny &&
            !SimpleEdits.IsShinyLockedSpeciesForm(species, form);

        // Try every possible current level, lowest first.
        for (int level = 1; level <= 100; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string setText =
                firstLine +
                $"\nLevel: {level}";

            if (requestShiny)
                setText += "\nShiny: Yes";

            ShowdownSet set;

            try
            {
                set = new ShowdownSet(setText);
            }
            catch
            {
                continue;
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
                continue;
            }

            if (result.Status != LegalizationResult.Regenerated)
                continue;

            var pk = result.Created;

            // Auto-Legality must have respected the requested species.
            if (pk.Species != species)
                continue;

            // It must have respected the requested form.
            if (pk.Form != form)
                continue;

            // CRITICAL:
            // Never accept Auto-Legality falling back to another level.
            if (pk.CurrentLevel != level)
                continue;

            // Verify legality independently.
            bool legal;

            try
            {
                legal = new LegalityAnalysis(pk).Valid;
            }
            catch
            {
                legal = false;
            }

            if (!legal)
                continue;

            pk.Heal();

            // Final verification after healing.
            try
            {
                if (!new LegalityAnalysis(pk).Valid)
                    continue;
            }
            catch
            {
                continue;
            }

            // Because levels are tested in ascending order,
            // the first valid result is the absolute minimum
            // that Auto-Legality + PKHeX can generate.
            return pk;
        }

        // No legal specimen found from level 1 through 100.
        return null;
    }
}
