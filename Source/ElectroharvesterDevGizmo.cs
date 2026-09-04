using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ContainmentFatalityReport
{
    [HarmonyPatch(typeof(Building), nameof(Building.GetGizmos))]
    internal static class ElectroharvesterDevGizmoPatch
    {
        private static readonly FieldInfo VanillaDamageRange =
            AccessTools.Field(typeof(Building_HoldingPlatform), "Damage");

        private static void Postfix(Building __instance, ref IEnumerable<Gizmo> __result)
        {
            Building_Electroharvester harvester = __instance as Building_Electroharvester;
            if (harvester != null)
                __result = AppendGizmo(__result, harvester);
        }

        private static IEnumerable<Gizmo> AppendGizmo(
            IEnumerable<Gizmo> original,
            Building_Electroharvester harvester)
        {
            if (original != null)
            {
                foreach (Gizmo gizmo in original)
                    yield return gizmo;
            }

            if (!DebugSettings.ShowDevGizmos)
                yield break;

            var command = new Command_Action
            {
                defaultLabel = "CFR.Dev.TriggerDamage.Label".Translate(),
                defaultDesc = "CFR.Dev.TriggerDamage.Desc".Translate(),
                action = delegate { ChooseAndDamage(harvester); }
            };

            if (VanillaDamageRange == null)
                command.Disable("CFR.Dev.TriggerDamage.Unavailable".Translate());
            else if (GetEligiblePlatforms(harvester).Count == 0)
                command.Disable("CFR.Dev.TriggerDamage.NoTarget".Translate());

            yield return command;
        }

        private static List<Building_HoldingPlatform> GetEligiblePlatforms(Building_Electroharvester harvester)
        {
            if (harvester == null || harvester.Electroharvester == null || harvester.Electroharvester.Platforms == null)
                return new List<Building_HoldingPlatform>();

            return harvester.Electroharvester.Platforms
                .OfType<Building_HoldingPlatform>()
                .Where(platform => platform.Spawned && platform.HeldPawn != null && platform.HasAttachedElectroharvester)
                .Distinct()
                .ToList();
        }

        private static void ChooseAndDamage(Building_Electroharvester harvester)
        {
            List<Building_HoldingPlatform> platforms = GetEligiblePlatforms(harvester);
            if (platforms.Count == 0)
            {
                Messages.Message("CFR.Dev.TriggerDamage.NoTarget".Translate(), harvester,
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (platforms.Count == 1)
            {
                ApplyVanillaDamage(platforms[0]);
                return;
            }

            List<FloatMenuOption> options = platforms.Select(platform =>
            {
                Pawn pawn = platform.HeldPawn;
                string label = "CFR.Dev.TriggerDamage.Target".Translate(
                    pawn != null ? pawn.LabelShortCap : "?", platform.Position.ToString());
                return new FloatMenuOption(label, delegate { ApplyVanillaDamage(platform); });
            }).ToList();
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void ApplyVanillaDamage(Building_HoldingPlatform platform)
        {
            Pawn pawn = platform != null ? platform.HeldPawn : null;
            if (pawn == null || !platform.Spawned || !platform.HasAttachedElectroharvester || VanillaDamageRange == null)
            {
                Messages.Message("CFR.Dev.TriggerDamage.NoTarget".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            FloatRange range;
            try
            {
                range = (FloatRange)VanillaDamageRange.GetValue(null);
            }
            catch (Exception exception)
            {
                Log.Error("[How Did This Entity Die?] Could not read the vanilla electroharvester damage range: " + exception);
                Messages.Message("CFR.Dev.TriggerDamage.Unavailable".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            float amount = range.RandomInRange;
            pawn.TakeDamage(new DamageInfo(DamageDefOf.ElectricalBurn, amount));
            Messages.Message("CFR.Dev.TriggerDamage.Applied".Translate(amount.ToString("0.##"), pawn.LabelShortCap),
                pawn, MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
