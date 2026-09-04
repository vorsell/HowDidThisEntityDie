using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace ContainmentFatalityReport
{
    internal enum HealthCauseKind
    {
        Unknown, DestroyedPart, LostPartFunction, RequiredCapacity,
        CapacityBelowMinimum, LethalHediff, LethalInjuries, RatkinWatch
    }

    internal sealed class HealthDeathCause
    {
        internal HealthCauseKind Kind;
        internal string Subject;

        internal string Describe()
        {
            switch (Kind)
            {
                case HealthCauseKind.DestroyedPart: return "CFR.Cause.PartZero".Translate(Subject);
                case HealthCauseKind.LostPartFunction: return "CFR.Cause.PartFunction".Translate(Subject);
                case HealthCauseKind.RequiredCapacity: return "CFR.Cause.RequiredCapacity".Translate(Subject);
                case HealthCauseKind.CapacityBelowMinimum: return "CFR.Cause.CapacityBelowMinimum".Translate(Subject);
                case HealthCauseKind.LethalHediff: return "CFR.Cause.HediffMtb".Translate(Subject);
                case HealthCauseKind.LethalInjuries: return "CFR.Cause.LethalThreshold".Translate();
                case HealthCauseKind.RatkinWatch: return "CFR.Cause.RatkinWatch".Translate();
                default: return "CFR.Cause.OtherLogic".Translate();
            }
        }

        private static readonly Dictionary<Type, bool> UsesBaseDeathCheck = new Dictionary<Type, bool>();
        private static bool warnedCapture;

        // Called only at an already selected HEALTH Kill call, before Kill mutates
        // the pawn. Reconstructs the installed vanilla deterministic check order;
        // does not call CauseDeathNow again, roll RNG, or sample living pawns.
        internal static HealthDeathCause CaptureSafely(Pawn pawn, DamageInfo? damage)
        {
            try { return Capture(pawn, damage.HasValue ? damage.Value.HitPart : null); }
            catch (Exception exception)
            {
                if (!warnedCapture)
                {
                    warnedCapture = true;
                    Log.Warning("[How Did This Entity Die?] Health details unavailable; death behavior was not changed. " + exception);
                }
                return new HealthDeathCause();
            }
        }

        private static HealthDeathCause Capture(Pawn pawn, BodyPartRecord hitPart)
        {
            if (pawn == null || pawn.health == null || pawn.Dead) return new HealthDeathCause();
            HediffSet set = pawn.health.hediffSet;

            // Current Core/Anomaly and Ratkin Anomaly+ use the base CauseDeathNow.
            // Unknown overridden predicates may have side effects; never re-run them.
            foreach (Hediff hediff in set.hediffs)
            {
                bool baseCheck;
                Type type = hediff.GetType();
                if (!UsesBaseDeathCheck.TryGetValue(type, out baseCheck))
                {
                    MethodInfo method = type.GetMethod(nameof(Hediff.CauseDeathNow));
                    UsesBaseDeathCheck[type] = baseCheck = method != null && method.DeclaringType == typeof(Hediff);
                }
                if (!baseCheck) return new HealthDeathCause();
                if (hediff.IsLethal && hediff.Severity >= hediff.def.lethalSeverity)
                    return new HealthDeathCause { Kind = HealthCauseKind.LethalHediff, Subject = hediff.LabelCap };
            }

            PawnCapacityDef capacity = pawn.health.ShouldBeDeadFromRequiredCapacity();
            if (capacity != null)
            {
                string destroyed = FindDestroyedSource(set, capacity, hitPart);
                // Prefer a demonstrably destroyed vital organ over a secondary
                // capacity symptom when more than one required capacity has failed.
                if (destroyed == null)
                    foreach (PawnCapacityDef other in DefDatabase<PawnCapacityDef>.AllDefsListForReading)
                    {
                        bool required = pawn.RaceProps.IsFlesh ? other.lethalFlesh : other.lethalMechanoids;
                        if (other == capacity || !required || pawn.health.capacities.CapableOf(other)) continue;
                        destroyed = FindDestroyedSource(set, other, hitPart);
                        if (destroyed != null) break;
                    }
                if (destroyed != null)
                    return new HealthDeathCause { Kind = HealthCauseKind.DestroyedPart, Subject = destroyed };
                return new HealthDeathCause
                {
                    Kind = pawn.health.capacities.GetLevel(capacity) <= 0f
                        ? HealthCauseKind.RequiredCapacity : HealthCauseKind.CapacityBelowMinimum,
                    Subject = capacity.GetLabelFor(pawn)
                };
            }

            BodyPartRecord core = pawn.RaceProps.body.corePart;
            if (PawnCapacityUtility.CalculatePartEfficiency(set, core) <= 0.0001f)
                return new HealthDeathCause
                {
                    Kind = set.GetPartHealth(core) <= 0f ? HealthCauseKind.DestroyedPart : HealthCauseKind.LostPartFunction,
                    Subject = core.LabelCap
                };

            if (pawn.health.ShouldBeDeadFromLethalDamageThreshold())
                return new HealthDeathCause { Kind = HealthCauseKind.LethalInjuries };
            return new HealthDeathCause();
        }

        private static string FindDestroyedSource(HediffSet set, PawnCapacityDef capacity, BodyPartRecord hitPart)
        {
            // Tags follow the verified vanilla capacity workers, not English organ
            // names: the same logic supports the bodies of the scoped optional mods.
            foreach (BodyPartTagDef tag in SourceTags(set.pawn.RaceProps.body, capacity))
            {
                var members = set.pawn.RaceProps.body.AllParts.Where(part => part.def.tags.Contains(tag)).ToList();
                if (members.Count == 0) continue;
                if (PawnCapacityUtility.CalculateTagEfficiency(set, tag, float.MaxValue,
                    default(FloatRange), null, -1f) > 0.0001f) continue;

                // A single old missing lung/kidney is not enough. Every source in
                // this failed functional group must actually have been destroyed.
                var destroyed = members.Select(part => DestroyedPartOrAncestor(set, part)).ToList();
                if (destroyed.Any(part => part == null)) continue;
                List<BodyPartRecord> distinct = destroyed.Distinct().ToList();
                if (hitPart != null)
                {
                    BodyPartRecord hitSource = DestroyedPartOrAncestor(set, hitPart);
                    if (hitSource != null && distinct.Contains(hitSource)) return hitSource.LabelCap;
                }
                return string.Join(" / ", distinct.Select(part => part.LabelCap.ToString()));
            }
            return null;
        }

        private static BodyPartRecord DestroyedPartOrAncestor(HediffSet set, BodyPartRecord part)
        {
            for (BodyPartRecord current = part; current != null; current = current.parent)
                if (set.GetPartHealth(current) <= 0f) return current;
            return null;
        }

        private static IEnumerable<BodyPartTagDef> SourceTags(BodyDef body, PawnCapacityDef capacity)
        {
            if (capacity == PawnCapacityDefOf.Consciousness)
                yield return BodyPartTagDefOf.ConsciousnessSource;
            else if (capacity == PawnCapacityDefOf.BloodPumping)
                yield return BodyPartTagDefOf.BloodPumpingSource;
            else if (capacity == PawnCapacityDefOf.BloodFiltration)
            {
                if (body.HasPartWithTag(BodyPartTagDefOf.BloodFiltrationKidney))
                {
                    yield return BodyPartTagDefOf.BloodFiltrationKidney;
                    yield return BodyPartTagDefOf.BloodFiltrationLiver;
                }
                else yield return BodyPartTagDefOf.BloodFiltrationSource;
            }
            else if (capacity == PawnCapacityDefOf.Breathing)
            {
                yield return BodyPartTagDefOf.BreathingSource;
                yield return BodyPartTagDefOf.BreathingPathway;
                yield return BodyPartTagDefOf.BreathingSourceCage;
            }
            else if (capacity.defName == "Metabolism") // Core defines it without a PawnCapacityDefOf field.
                yield return BodyPartTagDefOf.MetabolismSource;
        }

        internal static bool IsRatkinWatchDamage(string defName, string packageId)
        {
            return defName == "RA_WatchDamage" &&
                string.Equals(packageId, "fxz.ratkinanomaly.update", StringComparison.OrdinalIgnoreCase);
        }
    }
}
