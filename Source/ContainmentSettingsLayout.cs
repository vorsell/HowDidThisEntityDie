using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace ContainmentFatalityReport
{
    internal sealed class ContainmentSettingsRow
    {
        internal ContainmentCandidate Candidate;
        internal string SourceId;
        internal string SourceLabel;
        internal bool Expanded;
        internal bool IsHeader { get { return Candidate == null; } }
    }

    internal static class ContainmentSettingsLayout
    {
        internal const string AnomalySourceId = "ludeon.rimworld.anomaly";
        internal const string HumanlikeGroupId = "group:humanlike";

        // These are notification styles, not event-specific LetterDefs that happen
        // to use StandardLetter (pregnancy, ritual results, boss groups, etc.).
        internal static readonly string[] CommonLetterTypes =
        {
            "ThreatBig", "ThreatSmall", "NegativeEvent", "NeutralEvent", "PositiveEvent"
        };

        internal static bool IsCommonLetterType(string defName)
        {
            return CommonLetterTypes.Contains(defName);
        }

        internal static string ClearDiscoveryConfirmation(bool heldHumanActive)
        {
            string text = "CFR.Settings.ClearDiscoveryConfirm".Translate();
            text += "\n    " + "CFR.Settings.ClearDiscoveryOther".Translate();
            if (heldHumanActive)
                text += "\n    " + "CFR.Settings.ClearDiscoveryHeldHuman".Translate();
            return text + "\n\n" + "CFR.Settings.ClearDiscoveryUnassigned".Translate();
        }

        internal static List<ContainmentSettingsRow> BuildRows(List<ContainmentCandidate> candidates,
            IDictionary<string, bool> expansionOverrides)
        {
            var rows = new List<ContainmentSettingsRow>();
            foreach (string key in new[] { ContainmentRegistry.OtherKey, ContainmentRegistry.HeldHumanKey })
                foreach (ContainmentCandidate candidate in candidates.Where(item => item.Key == key))
                    rows.Add(new ContainmentSettingsRow { Candidate = candidate });

            var groups = candidates.Where(item => item.Key != ContainmentRegistry.OtherKey && item.Key != ContainmentRegistry.HeldHumanKey)
                .GroupBy(item => item.HumanlikeGroup ? HumanlikeGroupId : item.SourceId ?? "source:unknown", StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => string.Equals(group.Key, AnomalySourceId, StringComparison.OrdinalIgnoreCase) ? 2 : group.Key == HumanlikeGroupId ? 0 : 1)
                .ThenBy(group => group.First().SourceLabel, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                bool expanded;
                if (!expansionOverrides.TryGetValue(group.Key, out expanded))
                    expanded = string.Equals(group.Key, AnomalySourceId, StringComparison.OrdinalIgnoreCase);
                rows.Add(new ContainmentSettingsRow
                {
                    SourceId = group.Key,
                    SourceLabel = group.Key == HumanlikeGroupId ? "CFR.Settings.HumanlikeGroup".Translate().ToString() : group.First().SourceLabel ?? group.Key,
                    Expanded = expanded
                });
                if (!expanded) continue;
                foreach (ContainmentCandidate candidate in group.OrderByDescending(item => item.DangerousAfterDeath)
                    .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase))
                    rows.Add(new ContainmentSettingsRow { Candidate = candidate, SourceId = group.Key });
            }
            return rows;
        }
    }
}
