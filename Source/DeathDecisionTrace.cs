using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ContainmentFatalityReport
{
    internal enum DeathDecisionKind { Unrecorded, Health, ForcedDowned, ChanceOnDowned, HediffMtb }

    internal static class DeathDecisionTrace
    {
        // Only alive for the synchronous lethal call. No per-pawn component, save data,
        // polling, random re-roll, or state carried over from hypothetical damage tests.
        [ThreadStatic] private static Pawn currentPawn;
        [ThreadStatic] private static DeathDecisionKind currentKind;
        [ThreadStatic] private static float currentChance;
        [ThreadStatic] private static Hediff currentHediff;
        [ThreadStatic] private static HealthDeathCause currentHealthCause;
        private static readonly FieldInfo HealthPawn = AccessTools.Field(typeof(Pawn_HealthTracker), "pawn");

        internal static void Read(Pawn pawn, out DeathDecisionKind kind, out float chance, out string associatedHediff)
        {
            kind = currentPawn == pawn ? currentKind : DeathDecisionKind.Unrecorded;
            chance = currentPawn == pawn ? currentChance : 0f;
            associatedHediff = currentPawn == pawn && currentHediff != null ? currentHediff.LabelCap.ToString() : null;
        }

        internal static HealthDeathCause ReadHealthCause(Pawn pawn)
        {
            return currentPawn == pawn ? currentHealthCause : null;
        }

        private struct Scope : IDisposable
        {
            private readonly Pawn previousPawn;
            private readonly DeathDecisionKind previousKind;
            private readonly float previousChance;
            private readonly Hediff previousHediff;
            private readonly HealthDeathCause previousHealthCause;

            internal Scope(Thing thing, DeathDecisionKind kind, float chance = 0f, Hediff culprit = null, DamageInfo? damage = null)
            {
                previousPawn = currentPawn;
                previousKind = currentKind;
                previousChance = currentChance;
                previousHediff = currentHediff;
                previousHealthCause = currentHealthCause;
                Pawn pawn = thing as Pawn;
                Building_HoldingPlatform platform = pawn != null ? pawn.ParentHolder as Building_HoldingPlatform : null;
                // Preserve an outer decision if a deathless pawn's forced brain removal
                // recursively enters the health-death path for the same pawn.
                if (pawn != null && platform != null && platform.HeldPawn == pawn && currentPawn != pawn)
                {
                    currentPawn = pawn;
                    currentKind = kind;
                    currentChance = chance;
                    currentHediff = culprit;
                    currentHealthCause = kind == DeathDecisionKind.Health ? HealthDeathCause.CaptureSafely(pawn, damage) : null;
                }
            }

            public void Dispose()
            {
                currentPawn = previousPawn;
                currentKind = previousKind;
                currentChance = previousChance;
                currentHediff = previousHediff;
                currentHealthCause = previousHealthCause;
            }
        }

        internal static void KillFromHealth(Thing target, DamageInfo? damage, Hediff culprit)
        {
            using (new Scope(target, DeathDecisionKind.Health, culprit: culprit, damage: damage))
                target.Kill(damage, culprit);
        }

        internal static void KillFromForcedDowned(Thing target, DamageInfo? damage, Hediff culprit)
        {
            using (new Scope(target, DeathDecisionKind.ForcedDowned))
                target.Kill(damage, culprit);
        }

        internal static void KillFromChanceOnDowned(Thing target, DamageInfo? damage, Hediff culprit, float chance)
        {
            using (new Scope(target, DeathDecisionKind.ChanceOnDowned, chance))
                target.Kill(damage, culprit);
        }

        internal static Hediff AddHediffFromChanceOnDowned(Pawn_HealthTracker health, HediffDef def,
            BodyPartRecord part, DamageInfo? damage, DamageWorker.DamageResult result, float chance)
        {
            Pawn pawn = HealthPawn != null ? HealthPawn.GetValue(health) as Pawn : null;
            using (new Scope(pawn, DeathDecisionKind.ChanceOnDowned, chance))
                return health.AddHediff(def, part, damage, result);
        }

        internal static void KillFromHediffMtb(Thing target, DamageInfo? damage, Hediff culprit)
        {
            using (new Scope(target, DeathDecisionKind.HediffMtb, culprit: culprit))
                target.Kill(damage, culprit);
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.CheckForStateChange),
        new[] { typeof(DamageInfo?), typeof(Hediff) })]
    internal static class HealthDeathDecisionPatch
    {
        private static bool warnedMismatch;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            List<CodeInstruction> source = instructions.ToList();
            List<CodeInstruction> result;
            if (TryRewrite(source, __originalMethod, out result))
                return result;

            if (!warnedMismatch)
            {
                warnedMismatch = true;
                Log.Warning("[How Did This Entity Die?] Unrecognized CheckForStateChange death branches. Death tracing was not applied; unsupported causes will be reported as unknown. Vanilla instructions were left unchanged.");
            }
            return source;
        }

        internal static bool TryRewrite(List<CodeInstruction> source, MethodBase original, out List<CodeInstruction> result)
        {
            result = null;
            MethodInfo kill = AccessTools.Method(typeof(Thing), nameof(Thing.Kill), new[] { typeof(DamageInfo?), typeof(Hediff) });
            MethodInfo roll = AccessTools.Method(typeof(Rand), nameof(Rand.Chance), new[] { typeof(float) });
            MethodInfo shouldDie = AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.ShouldBeDead));
            MethodInfo add = AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff),
                new[] { typeof(HediffDef), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult) });
            FieldInfo forced = AccessTools.Field(typeof(PawnKindDef), nameof(PawnKindDef.forceDeathOnDowned));
            FieldInfo missingPart = AccessTools.Field(typeof(HediffDefOf), nameof(HediffDefOf.MissingBodyPart));

            List<int> kills = Indices(source, item => Calls(item, kill));
            List<int> rolls = Indices(source, item => Calls(item, roll));
            List<int> healthChecks = Indices(source, item => Calls(item, shouldDie));
            List<int> forces = Indices(source, item => item.opcode == OpCodes.Ldfld && Equals(item.operand, forced));
            List<int> adds = Indices(source, item => Calls(item, add));
            if (kills.Count != 3 || rolls.Count != 1 || healthChecks.Count != 1 || forces.Count != 1 || adds.Count != 1)
                return false;

            int rng = rolls[0];
            int health = healthChecks[0];
            int force = forces[0];
            int addIndex = adds[0];
            if (!(health < kills[0] && kills[0] < force && force < kills[1] && kills[1] < rng && rng < addIndex && addIndex < kills[2]))
                return false;
            // Validate the successful branches, not just the order of three Kill calls.
            int afterHealth = FalseBranchTarget(source, health + 1);
            int afterForce = FalseBranchTarget(source, force + 1);
            int afterRoll = FalseBranchTarget(source, rng + 1);
            if (!(afterHealth > kills[0] && afterHealth < force && afterForce > kills[1] && afterForce < rng && afterRoll > kills[2]))
                return false;
            if (!source.Skip(rng).Take(addIndex - rng).Any(item => item.opcode == OpCodes.Ldsfld && Equals(item.operand, missingPart)))
                return false;
            if (rng == 0 || !IsFloatLocalLoad(source[rng - 1], original))
                return false;

            var replacements = new Dictionary<int, string>
            {
                { kills[0], nameof(DeathDecisionTrace.KillFromHealth) },
                { kills[1], nameof(DeathDecisionTrace.KillFromForcedDowned) },
                { kills[2], nameof(DeathDecisionTrace.KillFromChanceOnDowned) },
                { addIndex, nameof(DeathDecisionTrace.AddHediffFromChanceOnDowned) }
            };
            result = new List<CodeInstruction>();
            for (int i = 0; i < source.Count; i++)
            {
                CodeInstruction instruction = new CodeInstruction(source[i]);
                string methodName;
                if (replacements.TryGetValue(i, out methodName))
                {
                    if (i == kills[2] || i == addIndex)
                    {
                        // Reuse the exact probability local consumed by the ORIGINAL RNG.
                        var loadChance = new CodeInstruction(source[rng - 1].opcode, source[rng - 1].operand);
                        loadChance.labels.AddRange(instruction.labels);
                        loadChance.blocks.AddRange(instruction.blocks);
                        instruction.labels.Clear();
                        instruction.blocks.Clear();
                        result.Add(loadChance);
                    }
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(DeathDecisionTrace), methodName);
                }
                result.Add(instruction);
            }
            return true;
        }

        private static List<int> Indices(List<CodeInstruction> code, Func<CodeInstruction, bool> predicate)
        {
            return Enumerable.Range(0, code.Count).Where(index => predicate(code[index])).ToList();
        }

        private static bool Calls(CodeInstruction instruction, MethodInfo method)
        {
            return (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, method);
        }

        private static int FalseBranchTarget(List<CodeInstruction> code, int index)
        {
            if (index >= code.Count || (code[index].opcode != OpCodes.Brfalse && code[index].opcode != OpCodes.Brfalse_S) || !(code[index].operand is Label))
                return -1;
            Label label = (Label)code[index].operand;
            return code.FindIndex(item => item.labels.Contains(label));
        }

        private static bool IsFloatLocalLoad(CodeInstruction instruction, MethodBase original)
        {
            int index;
            if (instruction.opcode == OpCodes.Ldloc_0) index = 0;
            else if (instruction.opcode == OpCodes.Ldloc_1) index = 1;
            else if (instruction.opcode == OpCodes.Ldloc_2) index = 2;
            else if (instruction.opcode == OpCodes.Ldloc_3) index = 3;
            else if (instruction.opcode == OpCodes.Ldloc || instruction.opcode == OpCodes.Ldloc_S)
            {
                LocalBuilder local = instruction.operand as LocalBuilder;
                if (local != null) return local.LocalType == typeof(float);
                if (!(instruction.operand is int) && !(instruction.operand is byte)) return false;
                index = Convert.ToInt32(instruction.operand);
            }
            else return false;
            MethodBody body = original != null ? original.GetMethodBody() : null;
            return body != null && index < body.LocalVariables.Count && body.LocalVariables[index].LocalType == typeof(float);
        }
    }

    [HarmonyPatch]
    internal static class AdditionalDeathDecisionPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo afterDamage = AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.PostApplyDamage),
                new[] { typeof(DamageInfo), typeof(float) });
            MethodInfo mtbDeath = AccessTools.Method(typeof(Hediff), "DoMTBDeath", Type.EmptyTypes);
            if (afterDamage != null) yield return afterDamage;
            if (mtbDeath != null) yield return mtbDeath;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            List<CodeInstruction> source = instructions.ToList();
            List<CodeInstruction> result;
            if (TryRewrite(source, __originalMethod, out result))
                return result;
            Log.Warning("[How Did This Entity Die?] Unrecognized lethal call in " + __originalMethod.Name +
                ". Original instructions were left unchanged; this route will be reported as unknown.");
            return source;
        }

        internal static bool TryRewrite(List<CodeInstruction> source, MethodBase original, out List<CodeInstruction> result)
        {
            result = null;
            MethodInfo kill = AccessTools.Method(typeof(Thing), nameof(Thing.Kill), new[] { typeof(DamageInfo?), typeof(Hediff) });
            var calls = source.Select((item, position) => new { item, index = position })
                .Where(entry => entry.item.opcode == OpCodes.Callvirt && Equals(entry.item.operand, kill)).ToList();
            if (calls.Count != 1 || original == null) return false;
            int index = calls[0].index;
            string replacement;
            if (original.DeclaringType == typeof(Pawn_HealthTracker) && original.Name == nameof(Pawn_HealthTracker.PostApplyDamage))
            {
                MethodInfo shouldDie = AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.ShouldBeDead));
                if (source.Count < 3 || !Equals(source[1].operand, shouldDie) ||
                    (source[2].opcode != OpCodes.Brfalse && source[2].opcode != OpCodes.Brfalse_S) || !(source[2].operand is Label))
                    return false;
                Label skip = (Label)source[2].operand;
                if (source.FindIndex(item => item.labels.Contains(skip)) <= index) return false;
                replacement = nameof(DeathDecisionTrace.KillFromHealth);
            }
            else if (original.DeclaringType == typeof(Hediff) && original.Name == "DoMTBDeath")
            {
                FieldInfo destroysBrain = AccessTools.Field(typeof(HediffStage), nameof(HediffStage.mtbDeathDestroysBrain));
                if (index == 0 || source[index - 1].opcode != OpCodes.Ldarg_0 ||
                    !source.Take(index).Any(item => item.opcode == OpCodes.Ldfld && Equals(item.operand, destroysBrain)))
                    return false;
                replacement = nameof(DeathDecisionTrace.KillFromHediffMtb);
            }
            else return false;

            result = source.Select(item => new CodeInstruction(item)).ToList();
            result[index].opcode = OpCodes.Call;
            result[index].operand = AccessTools.Method(typeof(DeathDecisionTrace), replacement);
            return true;
        }
    }
}
