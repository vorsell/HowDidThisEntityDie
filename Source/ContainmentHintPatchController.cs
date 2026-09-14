using System.Reflection;
using HarmonyLib;
using Verse;

namespace ContainmentFatalityReport
{
    internal static class ContainmentHintPatchController
    {
        internal const string OwnerId = "Vorsel.HowDidThisEntityDie.ContainmentHints";
        private static readonly Harmony Harmony = new Harmony(OwnerId);
        private static readonly MethodInfo Target = AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage),
            new[] { typeof(DamageInfo) });
        private static bool installed;

        internal static bool Installed
        {
            get { return installed; }
        }

        internal static void SetEnabled(bool enabled)
        {
            if (Target == null)
            {
                Log.ErrorOnce("[How Did This Entity Die?] Could not find Thing.TakeDamage(DamageInfo); contained entity alerts are unavailable.",
                    1197294401);
                return;
            }
            if (enabled == installed) return;

            if (enabled)
            {
                Harmony.Patch(Target,
                    prefix: new HarmonyMethod(typeof(PartHealthWarningPatch), nameof(PartHealthWarningPatch.Prefix)),
                    postfix: new HarmonyMethod(typeof(PartHealthWarningPatch), nameof(PartHealthWarningPatch.Postfix)));
                installed = true;
                return;
            }

            Harmony.Unpatch(Target, HarmonyPatchType.All, OwnerId);
            installed = false;
        }
    }
}
