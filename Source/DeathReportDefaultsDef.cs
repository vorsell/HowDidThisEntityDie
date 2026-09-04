using System;
using System.Collections.Generic;
using Verse;

namespace ContainmentFatalityReport
{
    public sealed class DeathReportDefaultsDef : Def
    {
        // Strings deliberately avoid unresolved cross-references when an optional
        // entity mod is absent. Other mods can extend this list through XML patches.
        public List<string> dangerousThingDefs = new List<string>();
        private static HashSet<string> dangerousNames;

        internal static bool IsDangerous(string defName)
        {
            // First queried by the candidate registry after Def loading completes.
            // Defaults affect initialization/reset and warnings, never death mechanics.
            if (dangerousNames == null)
            {
                dangerousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DeathReportDefaultsDef defaults in DefDatabase<DeathReportDefaultsDef>.AllDefs)
                {
                    if (defaults.dangerousThingDefs == null) continue;
                    foreach (string name in defaults.dangerousThingDefs)
                        if (!string.IsNullOrWhiteSpace(name)) dangerousNames.Add(name.Trim());
                }
            }
            return defName != null && dangerousNames.Contains(defName);
        }
    }
}
