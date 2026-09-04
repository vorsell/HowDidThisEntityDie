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
    [HarmonyPatch(typeof(Building_HoldingPlatform), nameof(Building_HoldingPlatform.Notify_PawnDied))]
    internal static class HoldingPlatformDeathPatch
    {
        private static readonly Dictionary<int, ContainmentDeathSnapshot> Pending =
            new Dictionary<int, ContainmentDeathSnapshot>();
        private static bool warnedTranspilerMismatch;
        private static bool customReportingEnabled = true;

        private static void Prefix(Building_HoldingPlatform __instance, Pawn pawn, DamageInfo? dinfo)
        {
            if (!customReportingEnabled || pawn == null || __instance.HeldPawn != pawn)
                return;
            if (dinfo.HasValue && dinfo.Value.Def != null && dinfo.Value.Def.execution)
                return;

            try
            {
                string mutantDefName = pawn.IsMutant ? pawn.mutant.Def.defName : null;
                NotificationColumn column = ContainmentDeathReporter.ResolveColumn(pawn.def, mutantDefName);
                // Discovery must survive even when this type uses vanilla messages.
                if (ContainmentFatalityReportMod.Settings != null)
                    ContainmentRegistry.RecordObserved(pawn.def, mutantDefName, ContainmentFatalityReportMod.Settings, true);
                if (column != null)
                    Pending[pawn.thingIDNumber] = ContainmentDeathSnapshot.Capture(__instance, pawn, dinfo, column);
            }
            catch (Exception exception)
            {
                Log.Error("[How Did This Entity Die?] Failed to capture a platform death: " + exception);
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = instructions.ToList();
            List<CodeInstruction> result;
            if (TryRewrite(code, __originalMethod, out result)) return result;
            if (!warnedTranspilerMismatch)
            {
                warnedTranspilerMismatch = true;
                Log.Warning("[How Did This Entity Die?] Could not identify the vanilla holding-platform message call. Original IL was left untouched; custom capture was disabled to avoid duplicate reports.");
            }
            customReportingEnabled = false;
            Pending.Clear();
            return code;
        }

        internal static bool TryRewrite(List<CodeInstruction> code, MethodBase originalMethod, out List<CodeInstruction> result)
        {
            result = null;
            if (originalMethod == null || originalMethod.IsStatic || originalMethod.DeclaringType != typeof(Building_HoldingPlatform) ||
                originalMethod.Name != nameof(Building_HoldingPlatform.Notify_PawnDied)) return false;
            ParameterInfo[] parameters = originalMethod.GetParameters();
            if (parameters.Length != 2 || parameters[0].ParameterType != typeof(Pawn) ||
                parameters[1].ParameterType != typeof(DamageInfo?)) return false;
            MethodInfo original = AccessTools.Method(typeof(Messages), nameof(Messages.Message),
                new[] { typeof(string), typeof(LookTargets), typeof(MessageTypeDef), typeof(bool) });
            MethodInfo replacement = AccessTools.Method(typeof(HoldingPlatformDeathPatch), nameof(RouteOriginalMessage));
            List<int> matches = Enumerable.Range(0, code.Count)
                .Where(position => code[position].opcode == OpCodes.Call && Equals(code[position].operand, original))
                .ToList();

            if (matches.Count != 1) return false;
            result = code.Select(instruction => new CodeInstruction(instruction)).ToList();
            int index = matches[0];
            CodeInstruction call = result[index];
            var loadPawn = new CodeInstruction(OpCodes.Ldarg_1);
            loadPawn.labels.AddRange(call.labels);
            loadPawn.blocks.AddRange(call.blocks);
            call.labels.Clear();
            call.blocks.Clear();
            call.operand = replacement;
            result.Insert(index, loadPawn);
            return true;
        }

        private static void RouteOriginalMessage(string text, LookTargets lookTargets, MessageTypeDef messageType, bool historical, Pawn pawn)
        {
            ContainmentDeathSnapshot snapshot;
            if (pawn != null && Pending.TryGetValue(pawn.thingIDNumber, out snapshot))
            {
                // Preserve the actual original call (including its target/type) for
                // fallback if publishing the replacement later fails.
                snapshot.OriginalNotification = delegate { Messages.Message(text, lookTargets, messageType, historical); };
                return;
            }
            Messages.Message(text, lookTargets, messageType, historical);
        }

        internal static bool TryTake(Pawn pawn, out ContainmentDeathSnapshot snapshot)
        {
            if (pawn == null)
            {
                snapshot = null;
                return false;
            }

            if (!Pending.TryGetValue(pawn.thingIDNumber, out snapshot))
                return false;
            Pending.Remove(pawn.thingIDNumber);
            return true;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill), new[] { typeof(DamageInfo?), typeof(Hediff) })]
    internal static class PawnKillCompletionPatch
    {
        private static void Postfix(Pawn __instance)
        {
            ContainmentDeathSnapshot snapshot;
            if (!HoldingPlatformDeathPatch.TryTake(__instance, out snapshot) || !__instance.Dead)
                return;

            try
            {
                ContainmentDeathReporter.Report(snapshot);
            }
            catch (Exception exception)
            {
                Log.Error("[How Did This Entity Die?] Failed to publish a platform death report: " + exception);
                RestoreOriginalNotification(snapshot);
            }
        }

        private static Exception Finalizer(Pawn __instance, Exception __exception)
        {
            if (__exception != null)
            {
                ContainmentDeathSnapshot snapshot;
                if (HoldingPlatformDeathPatch.TryTake(__instance, out snapshot))
                    RestoreOriginalNotification(snapshot);
            }
            return __exception;
        }

        private static void RestoreOriginalNotification(ContainmentDeathSnapshot snapshot)
        {
            try { snapshot.OriginalNotification?.Invoke(); }
            catch (Exception exception)
            {
                // A reporting failure must not replace an exception from Kill.
                Log.Error("[How Did This Entity Die?] Could not restore the vanilla death notification: " + exception);
            }
        }
    }
}
