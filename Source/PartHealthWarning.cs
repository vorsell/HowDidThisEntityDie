using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ContainmentFatalityReport
{
    internal sealed class PartHealthWarningState
    {
        internal Pawn Pawn;
        internal Building_HoldingPlatform Platform;
        internal List<PartHealthWarningEntry> Entries;
    }

    internal sealed class PartHealthWarningEntry
    {
        internal PartHealthWarningRule Rule;
        internal BodyPartRecord Part;
        internal int BeforeDisplayed;
    }

    internal static class PartHealthWarningPatch
    {
        private static bool warnedFailure;

        internal static void Prefix(Thing __instance, out PartHealthWarningState __state)
        {
            __state = null;
            try
            {
                ContainmentFatalityReportSettings settings = ContainmentFatalityReportMod.Settings;
                if (settings == null || !settings.containmentHintsEnabled || !settings.HasPartHealthWarnings) return;
                Pawn pawn = __instance as Pawn;
                if (pawn == null || pawn.Dead || pawn.health == null) return;
                Building_HoldingPlatform platform = pawn.ParentHolder as Building_HoldingPlatform;
                if (platform == null || platform.HeldPawn != pawn) return;
                List<PartHealthWarningRule> rules = settings.WarningsFor(pawn.def);
                if (rules == null || rules.Count == 0) return;

                var entries = new List<PartHealthWarningEntry>();
                foreach (PartHealthWarningRule rule in rules)
                {
                    foreach (BodyPartRecord part in pawn.RaceProps.body.AllParts)
                    {
                        if (part.def.defName != rule.bodyPartDef) continue;
                        int beforeDisplayed = DisplayedHealth(pawn.health.hediffSet.GetPartHealth(part));
                        if (beforeDisplayed > rule.threshold)
                            entries.Add(new PartHealthWarningEntry { Rule = rule, Part = part, BeforeDisplayed = beforeDisplayed });
                    }
                }
                if (entries.Count > 0)
                    __state = new PartHealthWarningState { Pawn = pawn, Platform = platform, Entries = entries };
            }
            catch (Exception exception)
            {
                WarnOnce("capture", exception);
            }
        }

        internal static void Postfix(PartHealthWarningState __state)
        {
            if (__state == null) return;
            try
            {
                Pawn pawn = __state.Pawn;
                if (pawn == null || pawn.Dead || pawn.health == null || __state.Platform == null ||
                    pawn.ParentHolder != __state.Platform || __state.Platform.HeldPawn != pawn) return;

                foreach (PartHealthWarningEntry entry in __state.Entries)
                {
                    int afterDisplayed = DisplayedHealth(pawn.health.hediffSet.GetPartHealth(entry.Part));
                    if (afterDisplayed <= entry.Rule.threshold)
                        Notify(pawn, __state.Platform, entry.Rule, entry.Part, afterDisplayed);
                }
            }
            catch (Exception exception)
            {
                WarnOnce("publish", exception);
            }
        }

        private static void Notify(Pawn pawn, Building_HoldingPlatform platform, PartHealthWarningRule rule,
            BodyPartRecord part, int displayedHealth)
        {
            string body = "CFR.PartWarning.Body".Translate(pawn.LabelShortCap, part.LabelCap,
                displayedHealth.ToString(), rule.threshold.ToString("0.##"));
            LookTargets targets = platform.Map != null
                ? new LookTargets(platform.Position, platform.Map)
                : new LookTargets();
            if (rule.mode == DeliveryMode.Message)
            {
                Messages.Message(body, targets, MessageTypeDefOf.NegativeEvent, true);
                return;
            }

            LetterDef letterDef = DefDatabase<LetterDef>.GetNamedSilentFail(rule.letterDef);
            if (letterDef == null || letterDef.letterClass != typeof(StandardLetter))
                letterDef = LetterDefOf.NegativeEvent;
            Find.LetterStack.ReceiveLetter("CFR.PartWarning.Title".Translate(pawn.LabelShortCap), body,
                letterDef, targets, null, null, null, null, 0, true);
        }

        internal static int DisplayedHealth(float health)
        {
            // HealthCardUtility displays HediffSet.GetPartHealth. Vanilla already
            // rounds that value, but applying the same rule explicitly keeps the
            // warning aligned with the visible integer if another mod returns a
            // fractional value from the health query.
            return Mathf.RoundToInt(health);
        }

        private static void WarnOnce(string stage, Exception exception)
        {
            if (warnedFailure) return;
            warnedFailure = true;
            Log.Error("[How Did This Entity Die?] Part-health warning failed during " + stage + ": " + exception);
        }
    }
}
