using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ContainmentFatalityReport
{
    internal sealed class MarkMonitorOption
    {
        internal string Key;
        internal string Label;
        internal string SourceId;
        internal string SourceLabel;
        internal Texture2D Icon;
    }

    internal sealed class MarkedPawnDeathSnapshot
    {
        internal Pawn Pawn;
        internal string PawnLabel;
        internal Map Map;
        internal IntVec3 Position;
        internal NotificationColumn Column;
        internal string Marks;
        internal ContainmentDeathSnapshot Report;
    }

    internal static class MarkedPawnMonitoring
    {
        internal const string UsefulMarksPackage = "Andromeda.UsefulMarks";
        internal const string MarkThatPawnPackage = "Mlie.MarkThatPawn";
        internal const string UsefulPrefix = "mark:useful:";
        internal const string MarkThatPawnPrefix = "mark:mtp:";
        private static bool warnedUseful;
        private static bool warnedMtp;

        internal static List<MarkMonitorOption> GetOptions(ContainmentFatalityReportSettings settings)
        {
            var result = new List<MarkMonitorOption>();
            if (IsLoaded(UsefulMarksPackage)) TryAddUsefulOptions(settings, result);
            if (IsLoaded(MarkThatPawnPackage)) TryAddMarkThatPawnOptions(result);
            return result;
        }

        internal static MarkedPawnDeathSnapshot Capture(Pawn pawn, DamageInfo? dinfo)
        {
            ContainmentFatalityReportSettings settings = ContainmentFatalityReportMod.Settings;
            if (settings == null || !settings.HasMarkedDeathMonitoring || pawn == null || pawn.Dead ||
                pawn.ParentHolder is Building_HoldingPlatform || pawn.MapHeld == null) return null;

            try
            {
                var configured = new HashSet<string>(settings.ConfiguredMarkKeys, StringComparer.Ordinal);
                if (configured.Count == 0) return null;
                List<MarkMonitorOption> active = GetActiveMarks(pawn, settings, configured);
                if (active.Count == 0) return null;

                NotificationColumn best = null;
                int bestPriority = int.MinValue;
                int bestIndex = int.MaxValue;
                foreach (MarkMonitorOption mark in active)
                {
                    NotificationColumn column = settings.ColumnForMark(mark.Key);
                    if (column == null) continue;
                    int priority = MarkedPawnRules.NotificationPriority(column);
                    int index = settings.columns.IndexOf(column);
                    if (priority > bestPriority || priority == bestPriority && index < bestIndex)
                    {
                        best = column;
                        bestPriority = priority;
                        bestIndex = index;
                    }
                }
                if (best == null) return null;
                string labels = string.Join(", ", active.Where(mark => settings.ColumnForMark(mark.Key) != null)
                    .Select(mark => mark.Label).Distinct().ToArray());
                var snapshot = new MarkedPawnDeathSnapshot
                {
                    Pawn = pawn,
                    PawnLabel = pawn.LabelShortCap,
                    Map = pawn.MapHeld,
                    Position = pawn.PositionHeld,
                    Column = best,
                    Marks = labels
                };
                snapshot.Report = ContainmentDeathSnapshot.CapturePawn(pawn, snapshot.Position, snapshot.Map,
                    dinfo, best);
                return snapshot;
            }
            catch (Exception exception)
            {
                Log.ErrorOnce("[How Did This Entity Die?] Marked-pawn death capture failed: " + exception, 1197294402);
                return null;
            }
        }

        internal static void Report(MarkedPawnDeathSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Pawn == null || !snapshot.Pawn.Dead) return;
            LookTargets targets = snapshot.Map != null
                ? new LookTargets(snapshot.Position, snapshot.Map)
                : new LookTargets();
            ContainmentDeathSnapshot report = snapshot.Report;
            string cause = report != null ? report.CauseSummary : "CFR.Cause.OtherLogic".Translate().ToString();
            if (snapshot.Column.mode == DeliveryMode.Message)
            {
                Messages.Message("CFR.MarkedDeath.Body".Translate(snapshot.PawnLabel, cause),
                    targets, MessageTypeDefOf.NegativeEvent, true);
                return;
            }
            LetterDef letter = DefDatabase<LetterDef>.GetNamedSilentFail(snapshot.Column.letterDef);
            if (letter == null || letter.letterClass != typeof(StandardLetter)) letter = LetterDefOf.NegativeEvent;
            string body = report != null
                ? report.BuildBody("CFR.Report.Pawn", snapshot.Marks)
                : "CFR.MarkedDeath.Body".Translate(snapshot.PawnLabel, cause).ToString();
            Find.LetterStack.ReceiveLetter("CFR.MarkedDeath.Title".Translate(snapshot.PawnLabel), body,
                letter, targets, null, null, null, null, 0, true);
        }

        private static List<MarkMonitorOption> GetActiveMarks(Pawn pawn,
            ContainmentFatalityReportSettings settings, HashSet<string> configured)
        {
            var result = new List<MarkMonitorOption>();
            if (configured.Any(key => key.StartsWith(UsefulPrefix, StringComparison.Ordinal)) && IsLoaded(UsefulMarksPackage))
                TryAddActiveUsefulMarks(pawn, settings, configured, result);
            if (configured.Any(key => key.StartsWith(MarkThatPawnPrefix, StringComparison.Ordinal)) && IsLoaded(MarkThatPawnPackage))
                TryAddActiveMarkThatPawnMarks(pawn, configured, result);
            return result;
        }

        private static bool IsLoaded(string packageId)
        {
            return LoadedModManager.RunningModsListForReading.Any(mod =>
                string.Equals(mod.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
        }

        private static void TryAddUsefulOptions(ContainmentFatalityReportSettings settings, List<MarkMonitorOption> result)
        {
            try
            {
                object component = UsefulComponent();
                IEnumerable markers = GetMember(component, "UserMarkers") as IEnumerable;
                if (markers == null) return;
                int index = 0;
                foreach (object marker in markers)
                {
                    string key = UsefulKey(marker, index++, settings);
                    if (key == null) continue;
                    Invoke(marker, "LoadIcon");
                    result.Add(new MarkMonitorOption
                    {
                        Key = key,
                        Label = UsefulLabel(marker),
                        SourceId = UsefulMarksPackage,
                        SourceLabel = "Useful Marks",
                        Icon = GetMember(marker, "icon") as Texture2D
                    });
                }
            }
            catch (Exception exception) { WarnAdapter(ref warnedUseful, "Useful Marks", exception); }
        }

        private static void TryAddActiveUsefulMarks(Pawn pawn, ContainmentFatalityReportSettings settings,
            HashSet<string> configured, List<MarkMonitorOption> result)
        {
            try
            {
                object component = UsefulComponent();
                IEnumerable active = Invoke(component, "ProcessMarkersFor", pawn) as IEnumerable;
                if (active == null) return;
                int index = 0;
                foreach (object marker in active.Cast<object>().ToList())
                {
                    string key = UsefulKey(marker, index++, settings);
                    if (key != null && configured.Contains(key))
                        result.Add(new MarkMonitorOption { Key = key, Label = UsefulLabel(marker) });
                }
            }
            catch (Exception exception) { WarnAdapter(ref warnedUseful, "Useful Marks", exception); }
        }

        private static object UsefulComponent()
        {
            Type type = AccessTools.TypeByName("UsefulMarks.PawnLabelCustomColors_WorldComponent");
            if (type == null) return null;
            FieldInfo instance = AccessTools.Field(type, "instance");
            return instance != null ? instance.GetValue(null) : null;
        }

        private static string UsefulKey(object marker, int index, ContainmentFatalityReportSettings settings)
        {
            if (marker == null || settings == null) return null;
            const string dataKey = "Vorsel.HowDidThisEntityDie.MarkId";
            string id = Invoke(marker, "GetExtraData", dataKey) as string;
            if (string.IsNullOrEmpty(id))
            {
                string serialized = Convert.ToString(Invoke(marker, "Serialize"));
                string fingerprint = Hash((serialized ?? UsefulLabel(marker)) + "|" + index);
                id = settings.GetOrCreateUsefulMarkId(fingerprint);
                Invoke(marker, "SetExtraData", dataKey, id);
            }
            return string.IsNullOrEmpty(id) ? null : UsefulPrefix + id;
        }

        private static string UsefulLabel(object marker)
        {
            string label = Convert.ToString(GetMember(marker, "description"));
            if (string.IsNullOrWhiteSpace(label)) label = Convert.ToString(GetMember(marker, "iconName"));
            return string.IsNullOrWhiteSpace(label) ? "CFR.Settings.Mark.Unnamed".Translate().ToString() : label;
        }

        private static void TryAddMarkThatPawnOptions(List<MarkMonitorOption> result)
        {
            try
            {
                Type main = AccessTools.TypeByName("MarkThatPawn.MarkThatPawn");
                IEnumerable defs = GetStaticMember(main, "MarkerDefs") as IEnumerable;
                if (defs == null) return;
                foreach (object markerDef in defs)
                {
                    object enabled = GetMember(markerDef, "Enabled");
                    if (enabled is bool && !(bool)enabled) continue;
                    Def def = markerDef as Def;
                    string defName = def != null ? def.defName : Convert.ToString(GetMember(markerDef, "defName"));
                    string label = def != null ? def.LabelCap.ToString() : defName;
                    IEnumerable textures = GetMember(markerDef, "MarkerTextures") as IEnumerable;
                    if (string.IsNullOrEmpty(defName) || textures == null) continue;
                    int number = 1;
                    foreach (object texture in textures)
                    {
                        result.Add(new MarkMonitorOption
                        {
                            Key = MarkThatPawnPrefix + defName + ";" + number,
                            Label = label + " — " + number,
                            SourceId = MarkThatPawnPackage,
                            SourceLabel = "Mark That Pawn",
                            Icon = texture as Texture2D
                        });
                        number++;
                    }
                }
                AddMtpDynamicOption(result, "FactionIcon", "CFR.Settings.Mark.FactionIcon".Translate());
                AddMtpDynamicOption(result, "IdeologyIcon", "CFR.Settings.Mark.IdeologyIcon".Translate());
            }
            catch (Exception exception) { WarnAdapter(ref warnedMtp, "Mark That Pawn", exception); }
        }

        private static void AddMtpDynamicOption(List<MarkMonitorOption> result, string name, string label)
        {
            result.Add(new MarkMonitorOption
            {
                Key = MarkThatPawnPrefix + "__custom__;" + name,
                Label = label,
                SourceId = MarkThatPawnPackage,
                SourceLabel = "Mark That Pawn"
            });
        }

        private static void TryAddActiveMarkThatPawnMarks(Pawn pawn, HashSet<string> configured,
            List<MarkMonitorOption> result)
        {
            try
            {
                object tracker = MarkThatPawnTracker();
                if (tracker == null) return;
                var blobs = new List<string>();
                IDictionary marked = GetMember(tracker, "MarkedPawns") as IDictionary;
                if (marked != null && marked.Contains(pawn))
                {
                    int number = Convert.ToInt32(marked[pawn]);
                    if (number > 0)
                    {
                        Type main = AccessTools.TypeByName("MarkThatPawn.MarkThatPawn");
                        object markerDef = InvokeStatic(main, "GetMarkerDefForPawn", pawn);
                        Def def = markerDef as Def;
                        if (def != null) blobs.Add(def.defName + ";" + number);
                    }
                }
                AddDictionaryBlobs(tracker, "AutomaticPawns", pawn, blobs);
                AddDictionaryBlobs(tracker, "CustomPawns", pawn, blobs);
                AddDictionaryBlobs(tracker, "OverridePawns", pawn, blobs);
                foreach (string blob in blobs.SelectMany(SplitMarkerBlobs).Distinct())
                {
                    string normalized = blob.Split('§')[0];
                    string key = MarkThatPawnPrefix + normalized;
                    if (configured.Contains(key))
                        result.Add(new MarkMonitorOption { Key = key, Label = MtpBlobLabel(normalized) });
                }
            }
            catch (Exception exception) { WarnAdapter(ref warnedMtp, "Mark That Pawn", exception); }
        }

        private static object MarkThatPawnTracker()
        {
            if (Current.Game == null) return null;
            Type type = AccessTools.TypeByName("MarkThatPawn.GlobalMarkingTracker");
            MethodInfo get = typeof(Game).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "GetComponent" && method.IsGenericMethodDefinition &&
                    method.GetParameters().Length == 0);
            return type != null && get != null ? get.MakeGenericMethod(type).Invoke(Current.Game, null) : null;
        }

        private static void AddDictionaryBlobs(object tracker, string field, Pawn pawn, List<string> blobs)
        {
            IDictionary dictionary = GetMember(tracker, field) as IDictionary;
            if (dictionary != null && dictionary.Contains(pawn) && dictionary[pawn] != null)
                blobs.Add(Convert.ToString(dictionary[pawn]));
        }

        private static IEnumerable<string> SplitMarkerBlobs(string value)
        {
            return (value ?? string.Empty).Split(new[] { '£' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(blob => blob.Contains(";"));
        }

        private static string MtpBlobLabel(string blob)
        {
            string[] parts = blob.Split(';');
            if (parts.Length < 2) return blob;
            if (parts[0] == "__custom__")
                return parts[1] == "FactionIcon" ? "CFR.Settings.Mark.FactionIcon".Translate().ToString() :
                    parts[1] == "IdeologyIcon" ? "CFR.Settings.Mark.IdeologyIcon".Translate().ToString() : parts[1];
            Type main = AccessTools.TypeByName("MarkThatPawn.MarkThatPawn");
            IEnumerable defs = GetStaticMember(main, "MarkerDefs") as IEnumerable;
            Def def = defs == null ? null : defs.Cast<object>().OfType<Def>()
                .FirstOrDefault(item => item.defName == parts[0]);
            return (def != null ? def.LabelCap.ToString() : parts[0]) + " — " + parts[1];
        }

        private static string Hash(string value)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).Replace("-", "");
        }

        private static object GetMember(object instance, string name)
        {
            if (instance == null) return null;
            Type type = instance.GetType();
            FieldInfo field = AccessTools.Field(type, name);
            if (field != null) return field.GetValue(instance);
            PropertyInfo property = AccessTools.Property(type, name);
            return property != null ? property.GetValue(instance, null) : null;
        }

        private static object GetStaticMember(Type type, string name)
        {
            if (type == null) return null;
            FieldInfo field = AccessTools.Field(type, name);
            if (field != null) return field.GetValue(null);
            PropertyInfo property = AccessTools.Property(type, name);
            return property != null ? property.GetValue(null, null) : null;
        }

        private static object Invoke(object instance, string name, params object[] args)
        {
            if (instance == null) return null;
            MethodInfo method = AccessTools.Method(instance.GetType(), name,
                args.Select(arg => arg != null ? arg.GetType() : typeof(object)).ToArray()) ??
                instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length);
            return method != null ? method.Invoke(instance, args) : null;
        }

        private static object InvokeStatic(Type type, string name, params object[] args)
        {
            if (type == null) return null;
            MethodInfo method = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length);
            return method != null ? method.Invoke(null, args) : null;
        }

        private static void WarnAdapter(ref bool warned, string name, Exception exception)
        {
            if (warned) return;
            warned = true;
            Log.Warning("[How Did This Entity Die?] " + name + " integration was disabled after an error: " + exception);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill), new[] { typeof(DamageInfo?), typeof(Hediff) })]
    internal static class MarkedPawnDeathPatch
    {
        private static void Prefix(Pawn __instance, DamageInfo? dinfo, out MarkedPawnDeathSnapshot __state)
        {
            __state = MarkedPawnMonitoring.Capture(__instance, dinfo);
        }

        private static void Postfix(MarkedPawnDeathSnapshot __state)
        {
            if (__state == null || __state.Pawn == null || !__state.Pawn.Dead) return;
            try { MarkedPawnMonitoring.Report(__state); }
            catch (Exception exception)
            {
                Log.ErrorOnce("[How Did This Entity Die?] Marked-pawn death notification failed: " + exception,
                    1197294403);
            }
        }
    }
}
