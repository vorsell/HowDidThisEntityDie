using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace ContainmentFatalityReport
{
    public enum DeliveryMode { Message, Letter }

    public sealed class NotificationColumn : IExposable
    {
        public string id;
        public DeliveryMode mode = DeliveryMode.Message;
        public string letterDef = "NegativeEvent";

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref mode, "mode", DeliveryMode.Message);
            Scribe_Values.Look(ref letterDef, "letterDef", "NegativeEvent");
        }
    }

    public sealed class PartHealthWarningRule : IExposable
    {
        public string id;
        public string thingDef;
        public string bodyPartDef;
        public float threshold = 1f;
        public DeliveryMode mode = DeliveryMode.Message;
        public string letterDef = "NegativeEvent";

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref thingDef, "thingDef");
            Scribe_Values.Look(ref bodyPartDef, "bodyPartDef");
            Scribe_Values.Look(ref threshold, "threshold", 1f);
            Scribe_Values.Look(ref mode, "mode", DeliveryMode.Message);
            Scribe_Values.Look(ref letterDef, "letterDef", "NegativeEvent");
        }
    }

    public sealed class ContainmentFatalityReportSettings : ModSettings
    {
        public bool containmentHintsEnabled = true;
        public List<NotificationColumn> columns = new List<NotificationColumn>();
        public List<PartHealthWarningRule> partHealthWarnings = new List<PartHealthWarningRule>();
        public List<string> observedThingDefs = new List<string>();
        public List<string> observedMutantDefs = new List<string>();
        private Dictionary<string, string> entityColumns = new Dictionary<string, string>(StringComparer.Ordinal);
        private Dictionary<string, string> markColumns = new Dictionary<string, string>(StringComparer.Ordinal);
        private Dictionary<string, string> usefulMarkIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private Dictionary<string, NotificationColumn> columnsById;
        private List<string> assignmentKeys;
        private List<string> assignmentValues;
        private List<string> markAssignmentKeys;
        private List<string> markAssignmentValues;
        private List<string> usefulMarkFingerprintKeys;
        private List<string> usefulMarkFingerprintValues;
        private int schemaVersion;
        // Remember a type even when it has no column. Absence from entityColumns
        // alone cannot distinguish a new type from an intentionally unchecked one.
        private List<string> registeredTypeKeys = new List<string>();
        private HashSet<string> registeredTypeSet;
        private bool partHealthWarningsInitialized;
        private Dictionary<string, List<PartHealthWarningRule>> warningsByThingDef;

        // Read the old keys only for one-time migration. Never interpret an empty
        // new-format column list as fresh settings: zero columns is intentional.
        private bool legacyLoaded;
        private bool legacyDefaultsInitialized;
        private List<string> legacyGroupAKeys = new List<string>();
        private DeliveryMode legacyAMode = DeliveryMode.Letter;
        private DeliveryMode legacyBMode = DeliveryMode.Message;
        private string legacyALetter = "ThreatBig";
        private string legacyBLetter = "NegativeEvent";

        internal bool Initialized { get { return schemaVersion >= 2; } }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref schemaVersion, "columnSchemaVersion", 0);
            Scribe_Values.Look(ref containmentHintsEnabled, "containmentHintsEnabled", true);
            Scribe_Values.Look(ref partHealthWarningsInitialized, "partHealthWarningsInitialized", false);
            Scribe_Collections.Look(ref partHealthWarnings, "partHealthWarnings", LookMode.Deep);
            Scribe_Collections.Look(ref observedThingDefs, "observedThingDefs", LookMode.Value);
            Scribe_Collections.Look(ref observedMutantDefs, "observedMutantDefs", LookMode.Value);
            Scribe_Collections.Look(ref registeredTypeKeys, "registeredTypeKeys", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars && schemaVersion == 0)
            {
                legacyLoaded = true;
                Scribe_Values.Look(ref legacyDefaultsInitialized, "defaultsInitialized", false);
                Scribe_Collections.Look(ref legacyGroupAKeys, "groupAKeys", LookMode.Value);
                Scribe_Values.Look(ref legacyAMode, "groupAMode", DeliveryMode.Letter);
                Scribe_Values.Look(ref legacyBMode, "groupBMode", DeliveryMode.Message);
                Scribe_Values.Look(ref legacyALetter, "groupALetterDef", "ThreatBig");
                Scribe_Values.Look(ref legacyBLetter, "groupBLetterDef", "NegativeEvent");
            }
            if (schemaVersion >= 1)
            {
                Scribe_Collections.Look(ref columns, "notificationColumns", LookMode.Deep);
                Scribe_Collections.Look(ref entityColumns, "entityColumns", LookMode.Value, LookMode.Value,
                    ref assignmentKeys, ref assignmentValues);
                Scribe_Collections.Look(ref markColumns, "markColumns", LookMode.Value, LookMode.Value,
                    ref markAssignmentKeys, ref markAssignmentValues);
                Scribe_Collections.Look(ref usefulMarkIds, "usefulMarkIds", LookMode.Value, LookMode.Value,
                    ref usefulMarkFingerprintKeys, ref usefulMarkFingerprintValues);
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                NormalizeLoadedData();
        }

        internal void NormalizeLoadedData()
        {
            observedThingDefs = CleanNames(observedThingDefs);
            observedMutantDefs = CleanNames(observedMutantDefs);
            legacyGroupAKeys = CleanNames(legacyGroupAKeys);
            registeredTypeKeys = CleanNames(registeredTypeKeys);
            registeredTypeSet = null;
            columns = columns ?? new List<NotificationColumn>();
            partHealthWarnings = partHealthWarnings ?? new List<PartHealthWarningRule>();
            entityColumns = entityColumns ?? new Dictionary<string, string>();
            markColumns = markColumns ?? new Dictionary<string, string>();
            usefulMarkIds = usefulMarkIds ?? new Dictionary<string, string>();
            RememberExistingTypes(Enumerable.Empty<ContainmentCandidate>());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            columns.RemoveAll(column => column == null || string.IsNullOrEmpty(column.id) || !ids.Add(column.id));
            foreach (NotificationColumn column in columns)
            {
                if (!Enum.IsDefined(typeof(DeliveryMode), column.mode)) column.mode = DeliveryMode.Message;
                if (!ContainmentSettingsLayout.IsCommonLetterType(column.letterDef)) column.letterDef = "NegativeEvent";
            }
            var warningIds = new HashSet<string>(StringComparer.Ordinal);
            partHealthWarnings.RemoveAll(rule => rule == null || string.IsNullOrEmpty(rule.thingDef) ||
                string.IsNullOrEmpty(rule.bodyPartDef));
            foreach (PartHealthWarningRule rule in partHealthWarnings)
            {
                if (string.IsNullOrEmpty(rule.id) || !warningIds.Add(rule.id))
                {
                    rule.id = Guid.NewGuid().ToString("N");
                    warningIds.Add(rule.id);
                }
                if (float.IsNaN(rule.threshold) || float.IsInfinity(rule.threshold)) rule.threshold = 1f;
                rule.threshold = Math.Max(0f, rule.threshold);
                if (!Enum.IsDefined(typeof(DeliveryMode), rule.mode)) rule.mode = DeliveryMode.Message;
                if (!ContainmentSettingsLayout.IsCommonLetterType(rule.letterDef)) rule.letterDef = "NegativeEvent";
            }
            // Missing/deleted column IDs fall back to vanilla; never reassign them
            // by visual index. Keep names of temporarily unloaded entity mods.
            foreach (string key in entityColumns.Keys.ToList())
                if (string.IsNullOrEmpty(key) || entityColumns[key] == null || !ids.Contains(entityColumns[key]))
                    entityColumns.Remove(key);
            foreach (string key in markColumns.Keys.ToList())
                if (string.IsNullOrEmpty(key) || markColumns[key] == null || !ids.Contains(markColumns[key]))
                    markColumns.Remove(key);
            foreach (string key in usefulMarkIds.Keys.ToList())
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(usefulMarkIds[key])) usefulMarkIds.Remove(key);
            columnsById = null;
            warningsByThingDef = null;
        }

        private static List<string> CleanNames(List<string> values)
        {
            return (values ?? new List<string>()).Where(value => !string.IsNullOrEmpty(value)).Distinct().ToList();
        }

        internal bool EnsureInitialized(IEnumerable<ContainmentCandidate> candidates)
        {
            List<ContainmentCandidate> known = candidates.ToList();
            bool warningChanged = EnsurePartHealthWarningsInitialized();
            if (Initialized)
            {
                bool changed = warningChanged;
                NotificationColumn fallback = ColumnFor(ContainmentRegistry.OtherKey);
                foreach (ContainmentCandidate candidate in known)
                    changed |= InheritNewType(candidate.Key, fallback != null ? fallback.id : null);
                return changed;
            }
            if (schemaVersion == 1)
            {
                // Old settings did not record unchecked definition-based rows.
                // Preserve every existing choice instead of guessing which x was new.
                RememberExistingTypes(known);
                schemaVersion = 2;
                return true;
            }
            if (!legacyLoaded)
            {
                ResetDeathReportsToDefaults(known);
                return true;
            }

            columns.Clear();
            entityColumns.Clear();
            NotificationColumn first = AddColumn(legacyAMode, legacyALetter);
            NotificationColumn second = AddColumn(legacyBMode, legacyBLetter);
            var keys = new HashSet<string>(known.Select(candidate => candidate.Key), StringComparer.Ordinal);
            keys.UnionWith(legacyGroupAKeys);
            keys.UnionWith(observedThingDefs.Select(ContainmentRegistry.ThingKey));
            keys.UnionWith(observedMutantDefs.Select(ContainmentRegistry.MutantKey));
            foreach (string key in keys)
            {
                bool selected = legacyGroupAKeys.Contains(key) || (!legacyDefaultsInitialized &&
                    key.StartsWith("thing:", StringComparison.Ordinal) && ContainmentRegistry.IsDangerousAfterDeath(key.Substring(6)));
                entityColumns[key] = selected ? first.id : second.id;
            }
            RememberExistingTypes(known);
            schemaVersion = 2;
            legacyLoaded = false;
            NormalizeLoadedData();
            return true;
        }

        private bool EnsurePartHealthWarningsInitialized()
        {
            if (partHealthWarningsInitialized) return false;
            ResetPartHealthWarnings();
            return true;
        }

        internal void ResetPartHealthWarnings()
        {
            partHealthWarnings.Clear();
            partHealthWarnings.Add(new PartHealthWarningRule
            {
                id = Guid.NewGuid().ToString("N"),
                thingDef = "Bulbfreak",
                bodyPartDef = "Brain",
                threshold = 1f,
                mode = DeliveryMode.Message,
                letterDef = "NegativeEvent"
            });
            partHealthWarningsInitialized = true;
            warningsByThingDef = null;
        }

        internal void ResetContainmentAlertsToDefaults()
        {
            containmentHintsEnabled = true;
            ResetPartHealthWarnings();
        }

        internal bool HasPartHealthWarnings
        {
            get { return partHealthWarnings != null && partHealthWarnings.Count > 0; }
        }

        internal List<PartHealthWarningRule> WarningsFor(ThingDef thingDef)
        {
            if (thingDef == null || !HasPartHealthWarnings) return null;
            if (warningsByThingDef == null)
                warningsByThingDef = partHealthWarnings.GroupBy(rule => rule.thingDef, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            List<PartHealthWarningRule> result;
            return warningsByThingDef.TryGetValue(thingDef.defName, out result) ? result : null;
        }

        internal PartHealthWarningRule AddPartHealthWarning(string thingDef, string bodyPartDef)
        {
            var rule = new PartHealthWarningRule
            {
                id = Guid.NewGuid().ToString("N"),
                thingDef = thingDef,
                bodyPartDef = bodyPartDef
            };
            partHealthWarnings.Add(rule);
            warningsByThingDef = null;
            return rule;
        }

        internal void RemovePartHealthWarning(string id)
        {
            partHealthWarnings.RemoveAll(rule => rule.id == id);
            warningsByThingDef = null;
        }

        internal void InvalidatePartHealthWarnings()
        {
            warningsByThingDef = null;
        }

        private bool RememberType(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (registeredTypeSet == null)
                registeredTypeSet = new HashSet<string>(registeredTypeKeys, StringComparer.Ordinal);
            if (!registeredTypeSet.Add(key)) return false;
            registeredTypeKeys.Add(key);
            return true;
        }

        private void RememberExistingTypes(IEnumerable<ContainmentCandidate> candidates)
        {
            foreach (string key in candidates.Select(candidate => candidate.Key).Concat(entityColumns.Keys)
                .Concat(observedThingDefs.Select(ContainmentRegistry.ThingKey))
                .Concat(observedMutantDefs.Select(ContainmentRegistry.MutantKey)))
                RememberType(key);
        }

        internal NotificationColumn AddColumn(DeliveryMode mode = DeliveryMode.Message, string letterDef = "NegativeEvent")
        {
            var column = new NotificationColumn { id = Guid.NewGuid().ToString("N"), mode = mode, letterDef = letterDef };
            columns.Add(column);
            columnsById = null;
            return column;
        }

        internal NotificationColumn FindColumn(string id)
        {
            if (id == null) return null;
            if (columnsById == null)
                columnsById = columns.ToDictionary(item => item.id, StringComparer.Ordinal);
            NotificationColumn column;
            return columnsById.TryGetValue(id, out column) ? column : null;
        }

        internal NotificationColumn ColumnFor(string key)
        {
            string id;
            return key != null && entityColumns.TryGetValue(key, out id) ? FindColumn(id) : null;
        }

        internal NotificationColumn ColumnForMark(string key)
        {
            string id;
            return key != null && markColumns.TryGetValue(key, out id) ? FindColumn(id) : null;
        }

        internal bool HasMarkedDeathMonitoring
        {
            get { return markColumns.Count > 0; }
        }

        internal IEnumerable<string> ConfiguredMarkKeys
        {
            get { return markColumns.Keys; }
        }

        internal bool TrySetSelected(string key, string columnId, bool selected)
        {
            if (string.IsNullOrEmpty(key) || FindColumn(columnId) == null) return false;
            NotificationColumn current = ColumnFor(key);
            if (current != null && current.id != columnId) return false;
            RememberType(key);
            if (selected) entityColumns[key] = columnId;
            else entityColumns.Remove(key);
            return true;
        }

        internal bool TrySetMarkSelected(string key, string columnId, bool selected)
        {
            if (string.IsNullOrEmpty(key) || FindColumn(columnId) == null) return false;
            NotificationColumn current = ColumnForMark(key);
            if (current != null && current.id != columnId) return false;
            if (selected) markColumns[key] = columnId;
            else markColumns.Remove(key);
            return true;
        }

        internal string GetOrCreateUsefulMarkId(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint)) return null;
            string id;
            if (!usefulMarkIds.TryGetValue(fingerprint, out id))
            {
                id = Guid.NewGuid().ToString("N");
                usefulMarkIds[fingerprint] = id;
            }
            return id;
        }

        internal void InheritAssignment(string key, string columnId)
        {
            RememberType(key);
            if (FindColumn(columnId) != null) entityColumns[key] = columnId;
            else entityColumns.Remove(key);
        }

        internal bool InheritNewType(string key, string columnId)
        {
            if (!RememberType(key)) return false;
            InheritAssignment(key, columnId);
            return true;
        }

        internal void ClearColumn(string columnId)
        {
            foreach (string key in entityColumns.Where(pair => pair.Value == columnId).Select(pair => pair.Key).ToList())
                entityColumns.Remove(key);
            foreach (string key in markColumns.Where(pair => pair.Value == columnId).Select(pair => pair.Key).ToList())
                markColumns.Remove(key);
        }

        internal void ClearDiscovery(ISet<string> definitionKeys)
        {
            // A previously observed type can later gain explicit containment support.
            // Keep its ordinary definition-based selection in that case.
            foreach (string key in observedThingDefs.Select(ContainmentRegistry.ThingKey)
                .Concat(observedMutantDefs.Select(ContainmentRegistry.MutantKey)))
                if (!definitionKeys.Contains(key))
                {
                    entityColumns.Remove(key);
                    registeredTypeKeys.Remove(key);
                    if (registeredTypeSet != null) registeredTypeSet.Remove(key);
                }
            observedThingDefs.Clear();
            observedMutantDefs.Clear();
        }

        internal void RemoveColumn(string columnId)
        {
            ClearColumn(columnId);
            columns.RemoveAll(column => column.id == columnId);
            columnsById = null;
        }

        internal bool WouldClaimOtherColumns(string columnId)
        {
            return entityColumns.Values.Any(value => value != columnId) ||
                markColumns.Values.Any(value => value != columnId);
        }

        internal void SelectAll(string columnId, IEnumerable<ContainmentCandidate> candidates,
            IEnumerable<string> markKeys = null)
        {
            if (FindColumn(columnId) == null) return;
            entityColumns.Clear();
            markColumns.Clear();
            foreach (ContainmentCandidate candidate in candidates) InheritAssignment(candidate.Key, columnId);
            if (markKeys != null)
                foreach (string key in markKeys.Where(key => !string.IsNullOrEmpty(key))) markColumns[key] = columnId;
        }

        internal void ResetDeathReportsToDefaults(IEnumerable<ContainmentCandidate> candidates)
        {
            columns.Clear();
            entityColumns.Clear();
            markColumns.Clear();
            NotificationColumn first = AddColumn(DeliveryMode.Letter, "ThreatBig");
            NotificationColumn second = AddColumn();
            foreach (ContainmentCandidate candidate in candidates)
                InheritAssignment(candidate.Key, candidate.DangerousAfterDeath ? first.id : second.id);
            RememberExistingTypes(Enumerable.Empty<ContainmentCandidate>());
            schemaVersion = 2;
            legacyLoaded = false;
            // Discovery and source expansion are not notification preferences.
        }
    }
}
