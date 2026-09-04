using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ContainmentFatalityReport
{
    internal sealed class ContainmentCandidate
    {
        public string Key;
        public string Label;
        public string Tooltip;
        public string SourceId;
        public string SourceLabel;
        public bool DangerousAfterDeath;
        public bool HumanlikeGroup;
    }

    internal static class ContainmentRegistry
    {
        internal const string OtherKey = "category:other";
        internal const string HeldHumanKey = "category:heldHuman";

        private static List<ContainmentCandidate> cachedCandidates;
        private static HashSet<string> structuralThingDefs;
        private static bool? heldHumanActive;
        private static readonly FieldInfo RequiresHoldingPlatform =
            AccessTools.Field(typeof(CompProperties_Studiable), "requiresHoldingPlatform");

        internal static string ThingKey(string defName)
        {
            return "thing:" + defName;
        }

        internal static string MutantKey(string defName)
        {
            return "mutant:" + defName;
        }

        internal static void Invalidate()
        {
            cachedCandidates = null;
            structuralThingDefs = null;
        }

        internal static void ClearDiscovery(ContainmentFatalityReportSettings settings)
        {
            var definitionKeys = new HashSet<string>(DefDatabase<ThingDef>.AllDefs
                .Where(IsStructuralCandidate).Select(def => ThingKey(def.defName)), StringComparer.Ordinal);
            definitionKeys.UnionWith(DefDatabase<MutantDef>.AllDefs
                .Where(def => def.canBeCapturedToHoldingPlatform).Select(def => MutantKey(def.defName)));
            settings.ClearDiscovery(definitionKeys);
            Invalidate();
        }

        internal static List<ContainmentCandidate> GetCandidates(ContainmentFatalityReportSettings settings,
            bool scanHeldPlatforms = true)
        {
            if (cachedCandidates != null)
                return cachedCandidates;

            structuralThingDefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<ContainmentCandidate>();

            candidates.Add(new ContainmentCandidate
            {
                Key = OtherKey,
                Label = "CFR.Settings.Other".Translate(),
                Tooltip = "CFR.Settings.OtherTip".Translate(),
                DangerousAfterDeath = false
            });

            if (IsHeldHumanActive)
            {
                ModContentPack heldHuman = LoadedModManager.RunningModsListForReading.First(mod =>
                    string.Equals(mod.PackageId, "Woolyung.HeldHuman", StringComparison.OrdinalIgnoreCase));
                candidates.Add(new ContainmentCandidate
                {
                    Key = HeldHumanKey,
                    Label = "CFR.Settings.HeldHuman".Translate(),
                    Tooltip = "CFR.Settings.HeldHumanTip".Translate(),
                    SourceId = heldHuman.PackageId,
                    SourceLabel = heldHuman.Name,
                    DangerousAfterDeath = false
                });
            }

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                if (!IsStructuralCandidate(def))
                    continue;

                structuralThingDefs.Add(def.defName);
                candidates.Add(MakeThingCandidate(def, false));
            }

            foreach (string defName in settings.observedThingDefs)
            {
                if (structuralThingDefs.Contains(defName))
                    continue;

                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def != null && def.race != null)
                {
                    structuralThingDefs.Add(defName);
                    candidates.Add(MakeThingCandidate(def, true));
                }
            }

            foreach (MutantDef def in DefDatabase<MutantDef>.AllDefs)
            {
                if (!def.canBeCapturedToHoldingPlatform && !settings.observedMutantDefs.Contains(def.defName))
                    continue;
                candidates.Add(new ContainmentCandidate
                {
                    Key = MutantKey(def.defName),
                    Label = def.LabelCap,
                    Tooltip = "CFR.Settings.MutantTip".Translate(def.defName),
                    SourceId = SourceId(def.modContentPack),
                    SourceLabel = SourceLabel(def.modContentPack),
                    DangerousAfterDeath = false
                });
            }

            cachedCandidates = candidates
                .Take(IsHeldHumanActive ? 2 : 1)
                .Concat(candidates.Skip(IsHeldHumanActive ? 2 : 1)
                    .OrderByDescending(candidate => candidate.DangerousAfterDeath)
                    .ThenBy(candidate => candidate.Label))
                .ToList();
            bool changed = settings.EnsureInitialized(cachedCandidates);
            int observedCount = settings.observedThingDefs.Count + settings.observedMutantDefs.Count;
            if (scanHeldPlatforms) ScanHeldPlatforms(settings);
            if (changed || observedCount != settings.observedThingDefs.Count + settings.observedMutantDefs.Count)
                settings.Write();
            // A scan can discover new rows. Rebuild once with the recorded types;
            // known types do not invalidate the cache a second time.
            return cachedCandidates ?? GetCandidates(settings, scanHeldPlatforms);
        }

        internal static string ResolveRoutingKey(ThingDef def, string mutantDefName, ContainmentFatalityReportSettings settings)
        {
            // A shambler's original animal/human race does not identify its containment type.
            if (!string.IsNullOrEmpty(mutantDefName))
            {
                MutantDef mutant = DefDatabase<MutantDef>.GetNamedSilentFail(mutantDefName);
                return (mutant != null && mutant.canBeCapturedToHoldingPlatform) || settings.observedMutantDefs.Contains(mutantDefName)
                    ? MutantKey(mutantDefName) : OtherKey;
            }

            EnsureStructuralSet();
            if (def != null && (structuralThingDefs.Contains(def.defName) || settings.observedThingDefs.Contains(def.defName)))
                return ThingKey(def.defName);
            return IsHeldHumanActive && def != null && def.race != null && def.race.Humanlike
                ? HeldHumanKey : OtherKey;
        }

        internal static void RecordObserved(ThingDef def, string mutantDefName,
            ContainmentFatalityReportSettings settings, bool save)
        {
            if (def == null || def.race == null)
                return;
            string inheritedKey = ResolveRoutingKey(def, mutantDefName, settings);
            if (inheritedKey != OtherKey && inheritedKey != HeldHumanKey)
                return;

            NotificationColumn inheritedColumn = settings.ColumnFor(inheritedKey);
            string newKey;
            if (!string.IsNullOrEmpty(mutantDefName))
            {
                settings.observedMutantDefs.Add(mutantDefName);
                newKey = MutantKey(mutantDefName);
            }
            else
            {
                settings.observedThingDefs.Add(def.defName);
                newKey = ThingKey(def.defName);
            }
            settings.InheritNewType(newKey, inheritedColumn != null ? inheritedColumn.id : null);
            if (save)
                settings.Write();
            // Rebuild the settings list on its next use, not once per UI frame.
            cachedCandidates = null;
        }

        internal static bool IsDangerousAfterDeath(string defName)
        {
            return DeathReportDefaultsDef.IsDangerous(defName);
        }

        internal static bool IsHeldHumanActive
        {
            get
            {
                if (!heldHumanActive.HasValue)
                {
                    heldHumanActive = LoadedModManager.RunningModsListForReading.Any(mod =>
                        string.Equals(mod.PackageId, "Woolyung.HeldHuman", StringComparison.OrdinalIgnoreCase));
                }
                return heldHumanActive.Value;
            }
        }

        private static void EnsureStructuralSet()
        {
            if (structuralThingDefs != null)
                return;

            structuralThingDefs = new HashSet<string>(
                DefDatabase<ThingDef>.AllDefs.Where(IsStructuralCandidate).Select(def => def.defName),
                StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsStructuralCandidate(ThingDef def)
        {
            if (def == null || def.race == null || def.GetCompProperties<CompProperties_HoldingPlatformTarget>() == null)
                return false;

            if (def.race.IsAnomalyEntity)
                return true;

            // Core installs both comps on normal animals for their possible shambler state.
            // Held Human also changes study properties on ALL humanlike races.
            if (def.race.Humanlike)
                return false;
            CompProperties_Studiable study = def.GetCompProperties<CompProperties_Studiable>();
            return study != null && RequiresHoldingPlatform != null &&
                (bool)RequiresHoldingPlatform.GetValue(study);
        }

        private static ContainmentCandidate MakeThingCandidate(ThingDef def, bool observedOnly)
        {
            string source = SourceLabel(def.modContentPack);
            string status = observedOnly ? "CFR.Settings.ObservedTip".Translate() : "CFR.Settings.StructuralTip".Translate();
            // Presentation only: keep race/mutant routing keys and real provenance.
            // Dedicated anomaly entities remain in their original source group.
            bool humanlikeGroup = def.race != null && def.race.Humanlike && !def.race.IsAnomalyEntity;
            return new ContainmentCandidate
            {
                Key = ThingKey(def.defName),
                Label = humanlikeGroup && def.defName == "Human" ? "CFR.Settings.HumanRace".Translate().ToString() : def.LabelCap.ToString(),
                Tooltip = "CFR.Settings.EntityTip".Translate(def.defName, source, status),
                SourceId = SourceId(def.modContentPack),
                SourceLabel = source,
                DangerousAfterDeath = IsDangerousAfterDeath(def.defName),
                HumanlikeGroup = humanlikeGroup
            };
        }

        private static string SourceId(ModContentPack source)
        {
            return source != null ? source.PackageId : "source:unknown";
        }

        private static string SourceLabel(ModContentPack source)
        {
            if (source == null) return "CFR.Settings.UnknownSource".Translate().ToString();
            // ModContentPack.Name was captured during loading. Expansion labels
            // here have received the current language's DefInjected translations.
            ExpansionDef expansion = DefDatabase<ExpansionDef>.AllDefs.FirstOrDefault(def =>
                string.Equals(def.linkedMod, source.PackageId, StringComparison.OrdinalIgnoreCase));
            return expansion != null && !string.IsNullOrWhiteSpace(expansion.label) ? expansion.label : source.Name;
        }

        private static void ScanHeldPlatforms(ContainmentFatalityReportSettings settings)
        {
            if (Current.Game == null)
                return;

            foreach (Map map in Find.Maps)
            {
                foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.EntityHolder))
                {
                    Building_HoldingPlatform platform = thing as Building_HoldingPlatform;
                    Pawn pawn = platform != null ? platform.HeldPawn : null;
                    if (pawn == null || pawn.def == null)
                        continue;

                    RecordObserved(pawn.def, pawn.IsMutant ? pawn.mutant.Def.defName : null, settings, false);
                }
            }
        }
    }
}
