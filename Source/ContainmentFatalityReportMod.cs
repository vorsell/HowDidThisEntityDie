using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ContainmentFatalityReport
{
    public sealed class ContainmentFatalityReportMod : Mod
    {
        private enum SettingsPage { ContainmentAlerts, DeathReports }

        internal static ContainmentFatalityReportSettings Settings;
        private Vector2 scrollPosition;
        private float horizontalScroll;
        private float viewportWidth = 1f;
        private string dragColumnId;
        private bool dragValue;
        private bool deferPlatformScan;
        private SettingsPage currentPage = SettingsPage.ContainmentAlerts;
        private Vector2 partWarningScroll;
        private readonly Dictionary<string, string> thresholdBuffers = new Dictionary<string, string>();
        private List<ContainmentCandidate> layoutCandidates;
        private List<ContainmentSettingsRow> visibleRows;
        private List<MarkMonitorOption> layoutMarks;
        private readonly Dictionary<string, bool> expansionOverrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private const float ColumnWidth = 304f;
        private const float Gutter = 18f;
        private const float HeaderHeight = 140f;
        private const float RowHeight = 30f;
        private const float SidebarWidth = 170f;

        public ContainmentFatalityReportMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<ContainmentFatalityReportSettings>();
            new Harmony("Vorsel.HowDidThisEntityDie").PatchAll();
            ContainmentHintPatchController.SetEnabled(Settings.containmentHintsEnabled);
            // Definitions are ready here, including patches from optional mods.
            // Defaults/migration must not depend on the player opening settings.
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                ContainmentRegistry.GetCandidates(Settings);
                // Startup happens before a save/map is loaded. The first settings
                // visit must still scan the platforms in that later-loaded game.
                ContainmentRegistry.Invalidate();
            });
        }

        public override string SettingsCategory()
        {
            return "CFR.Settings.Title".Translate();
        }

        public override void WriteSettings()
        {
            // Vanilla Dialog_ModSettings.PreClose calls this. In-window changes
            // use SaveSettings so clearing discovery lasts until the next visit.
            deferPlatformScan = false;
            layoutMarks = null;
            SaveSettings();
        }

        private void SaveSettings()
        {
            base.WriteSettings();
            ContainmentHintPatchController.SetEnabled(Settings.containmentHintsEnabled);
            ContainmentRegistry.Invalidate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            const float gap = 8f;
            Rect sidebar = new Rect(inRect.x, inRect.y, SidebarWidth, inRect.height);
            Rect content = new Rect(sidebar.xMax + gap, inRect.y,
                Math.Max(1f, inRect.width - SidebarWidth - gap), inRect.height);
            Widgets.DrawMenuSection(sidebar);
            DrawPageButton(new Rect(sidebar.x + 6f, sidebar.y + 8f, sidebar.width - 12f, 42f),
                SettingsPage.ContainmentAlerts, "CFR.Settings.Group.PartWarnings".Translate());
            DrawPageButton(new Rect(sidebar.x + 6f, sidebar.y + 56f, sidebar.width - 12f, 42f),
                SettingsPage.DeathReports, "CFR.Settings.Group.DeathReports".Translate());

            Widgets.DrawMenuSection(content);
            if (currentPage == SettingsPage.ContainmentAlerts)
                DrawContainmentAlerts(content.ContractedBy(10f));
            else
                DrawDeathSettings(content.ContractedBy(8f));
        }

        private void DrawPageButton(Rect rect, SettingsPage page, string label)
        {
            if (currentPage == page) Widgets.DrawBoxSolid(rect, new Color(1f, 1f, 1f, 0.12f));
            if (Widgets.ButtonText(rect, label)) currentPage = page;
        }

        private void DrawContainmentAlerts(Rect inRect)
        {
            bool previous = Settings.containmentHintsEnabled;
            Widgets.CheckboxLabeled(new Rect(inRect.x, inRect.y, inRect.width, 28f),
                "CFR.Settings.ContainmentHints.Enabled".Translate(), ref Settings.containmentHintsEnabled);
            if (previous != Settings.containmentHintsEnabled)
            {
                ContainmentHintPatchController.SetEnabled(Settings.containmentHintsEnabled);
                SaveSettings();
            }
            Widgets.Label(new Rect(inRect.x + 4f, inRect.y + 32f, inRect.width - 8f, 48f),
                "CFR.Settings.ContainmentHints.Description".Translate());
            Rect warningsRect = new Rect(inRect.x, inRect.y + 84f, inRect.width,
                Math.Max(1f, inRect.height - 84f));
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && Settings.containmentHintsEnabled;
            DrawPartHealthWarnings(warningsRect);
            GUI.enabled = oldEnabled;
            if (Widgets.ButtonText(new Rect(warningsRect.x, warningsRect.yMax - 30f, 130f, 30f),
                "CFR.Settings.Reset".Translate()))
            {
                ThingDef defaultWarningDef = DefDatabase<ThingDef>.GetNamedSilentFail("Bulbfreak");
                string defaultWarningLabel = defaultWarningDef != null ? defaultWarningDef.LabelCap.ToString() : "Bulbfreak";
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "CFR.Settings.ResetPartWarnings".Translate(defaultWarningLabel), delegate
                    {
                        Settings.ResetContainmentAlertsToDefaults();
                        thresholdBuffers.Clear();
                        partWarningScroll = Vector2.zero;
                        SaveSettings();
                    }));
            }
        }

        private void DrawDeathSettings(Rect inRect)
        {
            List<ContainmentCandidate> candidates = ContainmentRegistry.GetCandidates(Settings, !deferPlatformScan);
            List<MarkMonitorOption> markOptions = layoutMarks ?? (layoutMarks = MarkedPawnMonitoring.GetOptions(Settings));
            if (!ReferenceEquals(layoutCandidates, candidates) || visibleRows == null)
            {
                layoutCandidates = candidates;
                visibleRows = ContainmentSettingsLayout.BuildRows(candidates, expansionOverrides);
            }
            List<DeathSettingsRow> rows = BuildDeathRows(visibleRows, markOptions);
            viewportWidth = Math.Max(1f, inRect.width - 16f);
            float contentWidth = ContentWidth(Settings.columns.Count);
            horizontalScroll = Mathf.Clamp(horizontalScroll, 0f, MaxHorizontalScroll(Settings.columns.Count, viewportWidth));

            // Allocate/handle this control BEFORE any culled columns or conditional
            // letter controls. Otherwise their changing control-ID count can steal
            // the scrollbar's hotControl partway through a horizontal drag.
            if (contentWidth > viewportWidth)
                horizontalScroll = GUI.HorizontalScrollbar(
                    new Rect(inRect.x, inRect.yMax - 54f, viewportWidth, 16f),
                    horizontalScroll, viewportWidth, 0f, contentWidth);

            int firstColumn = Math.Max(0, (int)(horizontalScroll / (ColumnWidth + Gutter)));
            int lastColumn = Math.Min(Settings.columns.Count,
                (int)Math.Ceiling((horizontalScroll + viewportWidth) / (ColumnWidth + Gutter)));

            if (Event.current.rawType == EventType.MouseDown)
                dragColumnId = null;

            // Clip headers separately so they stay fixed during vertical scrolling.
            // Both header and body columns use the SAME horizontal offset.
            const float noteHeight = 0f;
            GUI.BeginGroup(new Rect(inRect.x, inRect.y + noteHeight, viewportWidth, HeaderHeight));
            for (int columnIndex = firstColumn; columnIndex < lastColumn; columnIndex++)
            {
                float x = ColumnX(columnIndex);
                DrawColumnControls(new Rect(x, 0f, ColumnWidth, HeaderHeight), Settings.columns[columnIndex], columnIndex);
                DrawSeparator(x, columnIndex, HeaderHeight);
            }
            GUI.EndGroup();

            Rect outRect = new Rect(inRect.x, inRect.y + noteHeight + HeaderHeight + 4f, inRect.width,
                Math.Max(1f, inRect.height - noteHeight - HeaderHeight - 64f));
            Rect viewRect = new Rect(0f, 0f, viewportWidth, Math.Max(outRect.height - 1f, rows.Count * RowHeight));
            scrollPosition.x = 0f;
            scrollPosition.y = Mathf.Clamp(scrollPosition.y, 0f, Math.Max(0f, viewRect.height - outRect.height));
            bool pointerOverBody = new Rect(outRect.x, outRect.y, viewportWidth, outRect.height).Contains(Event.current.mousePosition);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            for (int columnIndex = firstColumn; columnIndex < lastColumn; columnIndex++)
            {
                NotificationColumn column = Settings.columns[columnIndex];
                float x = ColumnX(columnIndex);
                DrawSeparator(x, columnIndex, viewRect.height);
                for (int i = 0; i < rows.Count; i++)
                {
                    float y = i * RowHeight;
                    if (y + RowHeight < scrollPosition.y || y > scrollPosition.y + outRect.height) continue;
                    Rect rowRect = new Rect(x, y, ColumnWidth, RowHeight - 2f);
                    if (rows[i].IsHeader) DrawSourceHeader(rowRect, rows[i]);
                    else
                    {
                        if (rows[i].SourceId != null) { rowRect.x += 14f; rowRect.width -= 14f; }
                        if (rows[i].Candidate != null)
                            DrawCandidateRow(rowRect, rows[i].Candidate, column.id, pointerOverBody);
                        else
                            DrawMarkRow(rowRect, rows[i].Mark, column.id, pointerOverBody);
                    }
                }
            }
            Widgets.EndScrollView();
            if (Event.current.rawType == EventType.MouseUp || Event.current.type == EventType.Ignore)
                dragColumnId = null;

            if (Settings.columns.Count == 0)
                Widgets.Label(new Rect(inRect.x + 8f, inRect.y + 12f, viewportWidth - 16f, 80f),
                    "CFR.Settings.NoColumns".Translate());

            if (Widgets.ButtonText(new Rect(inRect.x, inRect.yMax - 32f, 130f, 30f), "CFR.Settings.Reset".Translate()))
            {
                dragColumnId = null;
                string confirmation = "CFR.Settings.ResetConfirm".Translate();
                if (Settings.columns.Count > 2)
                    confirmation += "\n\n" + "CFR.Settings.ResetExtraColumns".Translate(Settings.columns.Count - 2);
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(confirmation, delegate
                {
                    Settings.ResetDeathReportsToDefaults(ContainmentRegistry.GetCandidates(Settings, !deferPlatformScan));
                    OnColumnsChanged();
                    SaveSettings();
                }));
            }
            if (Widgets.ButtonText(new Rect(inRect.x + 140f, inRect.yMax - 32f, 180f, 30f),
                "CFR.Settings.ClearDiscovery".Translate()))
            {
                dragColumnId = null;
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    ContainmentSettingsLayout.ClearDiscoveryConfirmation(ContainmentRegistry.IsHeldHumanActive), delegate
                    {
                        ContainmentRegistry.ClearDiscovery(Settings);
                        deferPlatformScan = true;
                        layoutCandidates = null;
                        visibleRows = null;
                        SaveSettings();
                    }));
            }
            if (Widgets.ButtonText(new Rect(inRect.x + 330f, inRect.yMax - 32f, 180f, 30f),
                "CFR.Settings.AddColumn".Translate()))
            {
                dragColumnId = null;
                Settings.AddColumn();
                OnColumnsChanged(true);
                SaveSettings();
            }
        }

        private void DrawPartHealthWarnings(Rect inRect)
        {
            const float headerHeight = 26f;
            const float rowHeight = 34f;
            const float footerHeight = 34f;
            List<ContainmentCandidate> candidates = ContainmentRegistry.GetCandidates(Settings, !deferPlatformScan)
                .Where(candidate => candidate.Key.StartsWith("thing:", StringComparison.Ordinal))
                .GroupBy(candidate => candidate.Key, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(candidate => candidate.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            float deleteWidth = 28f;
            float available = inRect.width - deleteWidth - 20f;
            float entityWidth = available * 0.31f;
            float partWidth = available * 0.24f;
            float thresholdWidth = available * 0.17f;
            float deliveryWidth = available - entityWidth - partWidth - thresholdWidth;
            float x = inRect.x;
            Widgets.Label(new Rect(x + 4f, inRect.y, entityWidth - 8f, headerHeight), "CFR.Settings.PartWarnings.Entity".Translate());
            x += entityWidth;
            Widgets.Label(new Rect(x + 4f, inRect.y, partWidth - 8f, headerHeight), "CFR.Settings.PartWarnings.BodyPart".Translate());
            x += partWidth;
            Widgets.Label(new Rect(x + 4f, inRect.y, thresholdWidth - 8f, headerHeight), "CFR.Settings.PartWarnings.Threshold".Translate());
            x += thresholdWidth;
            Widgets.Label(new Rect(x + 4f, inRect.y, deliveryWidth - 8f, headerHeight), "CFR.Settings.PartWarnings.Notification".Translate());

            Rect outRect = new Rect(inRect.x, inRect.y + headerHeight, inRect.width,
                Math.Max(1f, inRect.height - headerHeight - footerHeight - 4f));
            float viewHeight = Math.Max(outRect.height - 1f, Settings.partHealthWarnings.Count * rowHeight);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, viewHeight);
            partWarningScroll.x = 0f;
            Widgets.BeginScrollView(outRect, ref partWarningScroll, viewRect);
            for (int i = 0; i < Settings.partHealthWarnings.Count; i++)
            {
                PartHealthWarningRule rule = Settings.partHealthWarnings[i];
                float rowY = i * rowHeight;
                if (rowY + rowHeight < partWarningScroll.y || rowY > partWarningScroll.y + outRect.height) continue;
                DrawPartHealthWarningRow(new Rect(0f, rowY, viewRect.width, rowHeight - 4f), rule,
                    candidates, entityWidth, partWidth, thresholdWidth, deliveryWidth, deleteWidth);
            }
            Widgets.EndScrollView();

            if (Settings.partHealthWarnings.Count == 0)
                Widgets.Label(new Rect(outRect.x + 6f, outRect.y + 5f, outRect.width - 12f, 24f),
                    "CFR.Settings.PartWarnings.Empty".Translate());

            if (Widgets.ButtonText(new Rect(inRect.x + 140f, inRect.yMax - 30f, 180f, 30f),
                "CFR.Settings.PartWarnings.Add".Translate()))
                AddPartHealthWarning(candidates);
        }

        private void DrawPartHealthWarningRow(Rect rect, PartHealthWarningRule rule,
            List<ContainmentCandidate> candidates, float entityWidth, float partWidth,
            float thresholdWidth, float deliveryWidth, float deleteWidth)
        {
            float x = rect.x;
            ContainmentCandidate currentCandidate = candidates.FirstOrDefault(candidate =>
                candidate.Key == ContainmentRegistry.ThingKey(rule.thingDef));
            string entityLabel = currentCandidate != null ? currentCandidate.Label : rule.thingDef;
            if (Widgets.ButtonText(new Rect(x, rect.y, entityWidth - 4f, rect.height), entityLabel.Truncate(entityWidth - 16f)))
            {
                var options = candidates.Select(candidate => new FloatMenuOption(candidate.Label, delegate
                {
                    rule.thingDef = candidate.Key.Substring(6);
                    rule.bodyPartDef = PreferredBodyPart(rule.thingDef);
                    Settings.InvalidatePartHealthWarnings();
                })).ToList();
                Find.WindowStack.Add(new FloatMenu(options));
            }
            x += entityWidth;

            List<BodyPartDef> parts = BodyPartsFor(rule.thingDef);
            BodyPartDef currentPart = parts.FirstOrDefault(part => part.defName == rule.bodyPartDef);
            string partLabel = currentPart != null ? currentPart.LabelCap.ToString() : rule.bodyPartDef;
            if (Widgets.ButtonText(new Rect(x, rect.y, partWidth - 4f, rect.height), partLabel.Truncate(partWidth - 16f)))
            {
                Find.WindowStack.Add(new FloatMenu(parts.Select(part => new FloatMenuOption(part.LabelCap, delegate
                {
                    rule.bodyPartDef = part.defName;
                    Settings.InvalidatePartHealthWarnings();
                })).ToList()));
            }
            x += partWidth;

            string buffer;
            if (!thresholdBuffers.TryGetValue(rule.id, out buffer))
                buffer = rule.threshold.ToString("0.##");
            Widgets.TextFieldNumeric(new Rect(x, rect.y, thresholdWidth - 4f, rect.height),
                ref rule.threshold, ref buffer, 0f, 100000f);
            thresholdBuffers[rule.id] = buffer;
            x += thresholdWidth;

            if (Widgets.ButtonText(new Rect(x, rect.y, deliveryWidth - 4f, rect.height),
                PartWarningDeliveryLabel(rule)))
            {
                var options = new List<FloatMenuOption>
                {
                    new FloatMenuOption(ModeLabel(DeliveryMode.Message), delegate { rule.mode = DeliveryMode.Message; })
                };
                options.AddRange(ContainmentSettingsLayout.CommonLetterTypes
                    .Select(name => DefDatabase<LetterDef>.GetNamedSilentFail(name))
                    .Where(def => def != null && def.letterClass == typeof(StandardLetter))
                    .Select(def => new FloatMenuOption(LetterTypeLabel(def), delegate
                    {
                        rule.mode = DeliveryMode.Letter;
                        rule.letterDef = def.defName;
                    })));
                Find.WindowStack.Add(new FloatMenu(options));
            }
            x += deliveryWidth;

            if (Widgets.ButtonText(new Rect(x + 2f, rect.y, deleteWidth - 2f, rect.height), "×"))
            {
                string id = rule.id;
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "CFR.Settings.PartWarnings.DeleteConfirm".Translate(entityLabel, partLabel), delegate
                    {
                        Settings.RemovePartHealthWarning(id);
                        thresholdBuffers.Remove(id);
                        SaveSettings();
                    }));
            }
        }

        private void AddPartHealthWarning(List<ContainmentCandidate> candidates)
        {
            foreach (ContainmentCandidate candidate in candidates.OrderByDescending(item => item.Key == "thing:Bulbfreak"))
            {
                string defName = candidate.Key.Substring(6);
                foreach (BodyPartDef part in BodyPartsFor(defName).OrderByDescending(item => item.defName == "Brain"))
                {
                    if (Settings.partHealthWarnings.Any(rule => rule.thingDef == defName && rule.bodyPartDef == part.defName))
                        continue;
                    Settings.AddPartHealthWarning(defName, part.defName);
                    SaveSettings();
                    return;
                }
            }
            Messages.Message("CFR.Settings.PartWarnings.NoAvailableRule".Translate(), MessageTypeDefOf.RejectInput, false);
        }

        private static List<BodyPartDef> BodyPartsFor(string thingDefName)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(thingDefName);
            if (def == null || def.race == null || def.race.body == null) return new List<BodyPartDef>();
            return def.race.body.AllParts.Select(part => part.def).Where(part => part != null)
                .GroupBy(part => part.defName, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(part => part.LabelCap.ToString(), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static string PreferredBodyPart(string thingDefName)
        {
            List<BodyPartDef> parts = BodyPartsFor(thingDefName);
            BodyPartDef brain = parts.FirstOrDefault(part => part.defName == "Brain");
            return brain != null ? brain.defName : (parts.Count > 0 ? parts[0].defName : "Brain");
        }

        private static string PartWarningDeliveryLabel(PartHealthWarningRule rule)
        {
            if (rule.mode == DeliveryMode.Message) return ModeLabel(DeliveryMode.Message);
            LetterDef def = DefDatabase<LetterDef>.GetNamedSilentFail(rule.letterDef) ?? LetterDefOf.NegativeEvent;
            return LetterTypeLabel(def);
        }

        internal static float ContentWidth(int columnCount)
        {
            return Math.Max(0f, columnCount * (ColumnWidth + Gutter) - Gutter);
        }

        internal static float MaxHorizontalScroll(int columnCount, float visibleWidth)
        {
            return Math.Max(0f, ContentWidth(columnCount) - Math.Max(1f, visibleWidth));
        }

        private void OnColumnsChanged(bool focusLastColumn = false)
        {
            dragColumnId = null;
            float maximum = MaxHorizontalScroll(Settings.columns.Count, viewportWidth);
            horizontalScroll = focusLastColumn ? maximum : Mathf.Clamp(horizontalScroll, 0f, maximum);
        }

        private float ColumnX(int index)
        {
            return index * (ColumnWidth + Gutter) - horizontalScroll;
        }

        private static void DrawSeparator(float x, int index, float height)
        {
            if (index > 0) Widgets.DrawBoxSolid(new Rect(x - Gutter / 2f, 0f, 1f, height), Color.white);
        }

        private void DrawSourceHeader(Rect rect, DeathSettingsRow row)
        {
            Widgets.DrawHighlight(rect);
            Widgets.Label(new Rect(rect.x + 6f, rect.y + 3f, rect.width - 36f, 24f), row.SourceLabel.Truncate(rect.width - 36f));
            Widgets.Label(new Rect(rect.xMax - 24f, rect.y + 3f, 24f, 24f), row.Expanded ? "▼" : "▶");
            TooltipHandler.TipRegion(rect, "CFR.Settings.SourceGroupTip".Translate(row.SourceLabel));
            if (Widgets.ButtonInvisible(rect))
            {
                expansionOverrides[row.SourceId] = !row.Expanded;
                visibleRows = null;
                dragColumnId = null;
            }
        }

        private void DrawColumnControls(Rect rect, NotificationColumn column, int index)
        {
            Widgets.Label(new Rect(rect.x, rect.y, rect.width - 30f, 24f), "CFR.Settings.Column".Translate(index + 1));
            if (Widgets.ButtonText(new Rect(rect.xMax - 24f, rect.y, 24f, 24f), "×"))
            {
                dragColumnId = null;
                string columnId = column.id;
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "CFR.Settings.DeleteColumnConfirm".Translate(index + 1), delegate
                    {
                        Settings.RemoveColumn(columnId);
                        OnColumnsChanged();
                        SaveSettings();
                    }));
            }
            if (Widgets.ButtonText(new Rect(rect.x, rect.y + 28f, rect.width, 30f), ModeLabel(column.mode)))
            {
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption(ModeLabel(DeliveryMode.Message), delegate { column.mode = DeliveryMode.Message; }),
                    new FloatMenuOption(ModeLabel(DeliveryMode.Letter), delegate { column.mode = DeliveryMode.Letter; })
                }));
            }
            if (column.mode == DeliveryMode.Letter)
            {
                LetterDef current = DefDatabase<LetterDef>.GetNamedSilentFail(column.letterDef) ?? LetterDefOf.NegativeEvent;
                if (Widgets.ButtonText(new Rect(rect.x, rect.y + 64f, rect.width, 30f),
                    "CFR.Settings.LetterType".Translate(LetterTypeLabel(current))))
                {
                    var options = ContainmentSettingsLayout.CommonLetterTypes
                        .Select(name => DefDatabase<LetterDef>.GetNamedSilentFail(name))
                        .Where(def => def != null && def.letterClass == typeof(StandardLetter))
                        .Select(def => new FloatMenuOption(LetterTypeLabel(def), delegate { column.letterDef = def.defName; }))
                        .ToList();
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }

            // Fixed button row, even when Message mode hides the letter-style row.
            float buttonWidth = (rect.width - 8f) / 2f;
            if (Widgets.ButtonText(new Rect(rect.x, rect.y + 102f, buttonWidth, 30f), "CFR.Settings.SelectAll".Translate()))
            {
                dragColumnId = null;
                string columnId = column.id;
                Action select = delegate
                {
                    Settings.SelectAll(columnId, ContainmentRegistry.GetCandidates(Settings, !deferPlatformScan),
                        MarkedPawnMonitoring.GetOptions(Settings).Select(mark => mark.Key));
                    SaveSettings();
                };
                if (Settings.WouldClaimOtherColumns(columnId))
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "CFR.Settings.SelectAllConfirm".Translate(index + 1), select));
                else select();
            }
            if (Widgets.ButtonText(new Rect(rect.x + buttonWidth + 8f, rect.y + 102f, buttonWidth, 30f),
                "CFR.Settings.ClearAll".Translate()))
            {
                dragColumnId = null;
                Settings.ClearColumn(column.id);
                SaveSettings();
            }
        }

        internal static string LetterTypeLabel(LetterDef def)
        {
            if (def == null) return "CFR.LetterType.NegativeEvent".Translate();
            string key = "CFR.LetterType." + def.defName;
            if (key.CanTranslate()) return key.Translate();
            return string.IsNullOrWhiteSpace(def.label) ? def.defName : def.LabelCap.ToString();
        }

        private void DrawCandidateRow(Rect rect, ContainmentCandidate candidate, string columnId, bool pointerOverBody)
        {
            DrawAssignmentRow(rect, candidate.Key, candidate.Label,
                candidate.DangerousAfterDeath ? " ⚠" : string.Empty, null, columnId, pointerOverBody, false);
        }

        private void DrawMarkRow(Rect rect, MarkMonitorOption mark, string columnId, bool pointerOverBody)
        {
            DrawAssignmentRow(rect, mark.Key, mark.Label, string.Empty, mark.Icon, columnId, pointerOverBody, true);
        }

        private void DrawAssignmentRow(Rect rect, string key, string label, string suffix, Texture2D icon,
            string columnId, bool pointerOverBody, bool mark)
        {
            NotificationColumn owner = mark ? Settings.ColumnForMark(key) : Settings.ColumnFor(key);
            bool selected = owner != null && owner.id == columnId;
            bool locked = owner != null && !selected;
            if (locked) Widgets.DrawBoxSolid(rect, new Color(1f, 1f, 1f, 0.08f));
            else Widgets.DrawHighlightIfMouseover(rect);
            Color previousColor = GUI.color;
            if (locked) GUI.color = new Color(previousColor.r * 0.72f, previousColor.g * 0.72f, previousColor.b * 0.72f, previousColor.a);
            Widgets.CheckboxDraw(rect.x + 4f, rect.y + 3f, selected, locked, 22f);
            float labelX = rect.x + 32f;
            if (icon != null)
            {
                GUI.DrawTexture(new Rect(labelX, rect.y + 3f, 22f, 22f), icon, ScaleMode.ScaleToFit);
                labelX += 26f;
            }
            Widgets.Label(new Rect(labelX, rect.y + 3f, rect.xMax - labelX - 2f, 24f),
                (label + suffix).Truncate(rect.xMax - labelX - 2f));
            GUI.color = previousColor;
            if (locked || !pointerOverBody) return;

            Event evt = Event.current;
            if (evt.type == EventType.MouseDown && evt.button == 0 && rect.Contains(evt.mousePosition))
            {
                dragColumnId = columnId;
                dragValue = !selected;
                if (mark) Settings.TrySetMarkSelected(key, columnId, dragValue);
                else Settings.TrySetSelected(key, columnId, dragValue);
                evt.Use();
            }
            else if ((evt.type == EventType.MouseDrag || evt.rawType == EventType.ScrollWheel) &&
                dragColumnId == columnId && rect.Contains(evt.mousePosition))
            {
                if (mark) Settings.TrySetMarkSelected(key, columnId, dragValue);
                else Settings.TrySetSelected(key, columnId, dragValue);
                if (evt.type == EventType.MouseDrag) evt.Use();
            }
        }

        private List<DeathSettingsRow> BuildDeathRows(List<ContainmentSettingsRow> entityRows,
            List<MarkMonitorOption> marks)
        {
            var result = new List<DeathSettingsRow>();
            // BuildRows places the two ungrouped fallback rows first. Concrete
            // source rows always carry a source ID, so this remains independent
            // of localized labels and optional Held Human availability.
            int fallbackCount = entityRows.TakeWhile(row => row.Candidate != null && row.SourceId == null).Count();
            result.AddRange(entityRows.Take(fallbackCount).Select(ToDeathSettingsRow));
            foreach (var group in marks.GroupBy(mark => mark.SourceId, StringComparer.OrdinalIgnoreCase))
            {
                string sourceId = "marks:" + group.Key;
                bool expanded;
                if (!expansionOverrides.TryGetValue(sourceId, out expanded)) expanded = false;
                result.Add(new DeathSettingsRow
                {
                    SourceId = sourceId,
                    SourceLabel = group.First().SourceLabel,
                    Expanded = expanded
                });
                if (expanded)
                    result.AddRange(group.OrderBy(mark => mark.Label, StringComparer.CurrentCultureIgnoreCase)
                        .Select(mark => new DeathSettingsRow { Mark = mark, SourceId = sourceId }));
            }
            result.AddRange(entityRows.Skip(fallbackCount).Select(ToDeathSettingsRow));
            return result;
        }

        private static DeathSettingsRow ToDeathSettingsRow(ContainmentSettingsRow row)
        {
            return new DeathSettingsRow
            {
                Candidate = row.Candidate,
                SourceId = row.SourceId,
                SourceLabel = row.SourceLabel,
                Expanded = row.Expanded
            };
        }

        private sealed class DeathSettingsRow
        {
            internal ContainmentCandidate Candidate;
            internal MarkMonitorOption Mark;
            internal string SourceId;
            internal string SourceLabel;
            internal bool Expanded;
            internal bool IsHeader { get { return Candidate == null && Mark == null; } }
        }

        private static string ModeLabel(DeliveryMode mode)
        {
            return mode == DeliveryMode.Letter ? "CFR.Settings.Mode.Letter".Translate() : "CFR.Settings.Mode.Message".Translate();
        }
    }
}
