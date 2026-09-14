using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace ContainmentFatalityReport
{
    internal sealed class ContainmentDeathSnapshot
    {
        public int PawnId;
        public string PawnLabel;
        public string ThingDefName;
        public string MutantDefName;
        public IntVec3 Position;
        public Map Map;
        public string DamageDef;
        public string DamageLabel;
        public float DamageAmount;
        public string HitPart;
        public string Instigator;
        public string Weapon;
        public float HealthPercent;
        public float Consciousness;
        public float Pain;
        public HealthDeathCause HealthCause;
        public DeathDecisionKind DecisionKind;
        public float DecisionChance;
        public string AssociatedHediff;
        public List<string> Injuries = new List<string>();
        public List<string> MissingParts = new List<string>();
        public DeliveryMode Delivery;
        public string LetterDefName;
        internal Action OriginalNotification;

        public static ContainmentDeathSnapshot Capture(Building_HoldingPlatform platform, Pawn pawn, DamageInfo? dinfo,
            NotificationColumn column)
        {
            return CapturePawn(pawn, platform.Position, platform.Map, dinfo, column);
        }

        internal static ContainmentDeathSnapshot CapturePawn(Pawn pawn, IntVec3 position, Map map, DamageInfo? dinfo,
            NotificationColumn column)
        {
            var snapshot = new ContainmentDeathSnapshot
            {
                PawnId = pawn.thingIDNumber,
                PawnLabel = pawn.LabelShortCap,
                ThingDefName = pawn.def != null ? pawn.def.defName : "?",
                MutantDefName = pawn.IsMutant ? pawn.mutant.Def.defName : null,
                Position = position,
                Map = map,
                Delivery = column.mode,
                LetterDefName = column.letterDef
            };

            DeathDecisionTrace.Read(pawn, out snapshot.DecisionKind, out snapshot.DecisionChance, out snapshot.AssociatedHediff);
            snapshot.HealthCause = DeathDecisionTrace.ReadHealthCause(pawn);
            if (snapshot.DecisionKind == DeathDecisionKind.Health && snapshot.HealthCause == null)
                snapshot.HealthCause = HealthDeathCause.CaptureSafely(pawn, dinfo);

            if (dinfo.HasValue)
            {
                DamageInfo info = dinfo.Value;
                snapshot.DamageDef = info.Def != null ? info.Def.defName : "?";
                snapshot.DamageLabel = info.Def != null ? info.Def.LabelCap.ToString() : "?";
                snapshot.DamageAmount = info.Amount;
                snapshot.HitPart = info.HitPart != null ? info.HitPart.LabelCap.ToString() : "-";
                snapshot.Instigator = info.Instigator != null ? info.Instigator.LabelCap.ToString() : null;
                snapshot.Weapon = info.Weapon != null ? info.Weapon.LabelCap.ToString() : null;
                if (snapshot.DecisionKind == DeathDecisionKind.Unrecorded && info.Def != null &&
                    HealthDeathCause.IsRatkinWatchDamage(info.Def.defName, info.Def.modContentPack != null ? info.Def.modContentPack.PackageId : null))
                    snapshot.HealthCause = new HealthDeathCause { Kind = HealthCauseKind.RatkinWatch };
            }

            // Freeze the selected delivery before death/observation completes.
            // Short messages need the cause, not a full injury report.
            if (snapshot.Delivery != DeliveryMode.Letter) return snapshot;
            snapshot.HealthPercent = pawn.health.summaryHealth.SummaryHealthPercent;
            snapshot.Consciousness = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness);
            snapshot.Pain = pawn.health.hediffSet.PainTotal;
            BodyPartRecord brain = pawn.health.hediffSet.GetBrain();
            BodyPartRecord finalPart = dinfo.HasValue ? dinfo.Value.HitPart : null;
            var partsByLabel = pawn.RaceProps.body.AllParts.GroupBy(part => part.Label)
                .ToDictionary(group => group.Key, group => group.ToList());
            var groups = pawn.health.hediffSet.hediffs.OfType<Hediff_Injury>()
                .GroupBy(injury => injury.Part)
                .OrderByDescending(group => group.Key != null && group.Key == brain)
                .ThenByDescending(group => group.Key != null && group.Key == finalPart)
                .ThenByDescending(group => group.Sum(injury => injury.Severity))
                .ToList();
            foreach (var group in groups)
            {
                BodyPartRecord part = group.Key;
                string label = part != null ? part.LabelCap.ToString() : "-";
                if (part != null)
                {
                    List<BodyPartRecord> sameLabels = partsByLabel[part.Label];
                    if (sameLabels.Count > 1)
                        label += " " + (sameLabels.IndexOf(part) + 1);
                }
                // Verse.Formatted does not support .NET numeric format specifiers.
                snapshot.Injuries.Add("CFR.Report.InjuryPart".Translate(label,
                    group.Sum(injury => injury.Severity).ToString("0.##"),
                    part != null ? pawn.health.hediffSet.GetPartHealth(part).ToString("0.##") : "-",
                    part != null ? part.def.GetMaxHealth(pawn).ToString("0.##") : "-"));
            }

            snapshot.MissingParts.AddRange(pawn.health.hediffSet.GetMissingPartsCommonAncestors()
                .Where(part => part.Part != null)
                .Select(part => part.Part.LabelCap.ToString()));
            return snapshot;
        }

        public string CauseSummary
        {
            get
            {
                if (DecisionKind == DeathDecisionKind.ForcedDowned)
                    return "CFR.Cause.ForcedDowned".Translate();
                if (DecisionKind == DeathDecisionKind.ChanceOnDowned)
                    return "CFR.Cause.ChanceOnDowned".Translate(DecisionChance.ToString("P0"));
                if (DecisionKind == DeathDecisionKind.HediffMtb)
                    return "CFR.Cause.HediffMtb".Translate(AssociatedHediff ?? "?");

                // Never infer an unknown/direct Kill from incidental old injuries.
                return HealthCause != null ? HealthCause.Describe() : "CFR.Cause.OtherLogic".Translate().ToString();
            }
        }

        public string BuildBody(string subjectKey = "CFR.Report.Entity", string marks = null)
        {
            var text = new StringBuilder();
            text.AppendLine(subjectKey.Translate(PawnLabel));
            if (!string.IsNullOrEmpty(marks))
                text.AppendLine("CFR.Report.Marks".Translate(marks));
            text.AppendLine("CFR.Report.Cause".Translate(CauseSummary));
            text.AppendLine("CFR.Report.Vitals".Translate(Consciousness.ToString("P0"), Pain.ToString("P0"),
                HealthPercent.ToString("P0")));
            if (DamageLabel != null)
                text.AppendLine("CFR.Report.Damage".Translate(DamageLabel, DamageAmount.ToString("0.##"), HitPart ?? "-"));
            else
                text.AppendLine("CFR.Report.NoDamageInfo".Translate());
            if (Instigator != null || Weapon != null)
                text.AppendLine("CFR.Report.Source".Translate(Instigator ?? "-", Weapon ?? "-"));

            if (Injuries.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("CFR.Report.Injuries".Translate());
                foreach (string injury in Injuries) text.AppendLine("  • " + injury);
            }

            if (MissingParts.Count > 0)
            {
                text.AppendLine();
                text.Append("CFR.Report.MissingParts".Translate());
                text.AppendLine(string.Join(", ", MissingParts));
            }
            return text.ToString().TrimEnd();
        }
    }

    internal static class ContainmentDeathReporter
    {
        internal static NotificationColumn ResolveColumn(ThingDef def, string mutantDefName)
        {
            ContainmentFatalityReportSettings settings = ContainmentFatalityReportMod.Settings;
            if (settings == null) return null;
            if (!settings.Initialized) ContainmentRegistry.GetCandidates(settings);
            return settings.ColumnFor(ContainmentRegistry.ResolveRoutingKey(def, mutantDefName, settings));
        }

        internal static void Report(ContainmentDeathSnapshot snapshot)
        {
            LookTargets targets = snapshot.Map != null
                ? new LookTargets(snapshot.Position, snapshot.Map)
                : new LookTargets();

            if (snapshot.Delivery == DeliveryMode.Message)
            {
                Messages.Message("CFR.Message".Translate(snapshot.PawnLabel, snapshot.CauseSummary),
                    targets, MessageTypeDefOf.NegativeEvent, true);
                return;
            }

            LetterDef letterDef = DefDatabase<LetterDef>.GetNamedSilentFail(snapshot.LetterDefName);
            if (letterDef == null || letterDef.letterClass != typeof(StandardLetter))
                letterDef = LetterDefOf.NegativeEvent;
            Find.LetterStack.ReceiveLetter("CFR.LetterTitle".Translate(snapshot.PawnLabel), snapshot.BuildBody(),
                letterDef, targets, null, null, null, null, 0, true);
        }
    }
}
