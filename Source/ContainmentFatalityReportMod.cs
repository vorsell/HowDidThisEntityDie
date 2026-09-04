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
        internal static ContainmentFatalityReportSettings Settings;
        private Vector2 scrollPosition;
        private float horizontalScroll;
        private float viewportWidth = 1f;
        private string dragColumnId;
        private bool dragValue;
        private bool deferPlatformScan;
        private List<ContainmentCandidate> layoutCandidates;
        private List<ContainmentSettingsRow> visibleRows;
        private readonly Dictionary<string, bool> expansionOverrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private const float ColumnWidth = 320f;
        private const float Gutter = 18f;
        private const float HeaderHeight = 140f;
        private const float RowHeight = 30f;

        public ContainmentFatalityReportMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<ContainmentFatalityReportSettings>();
            new Harmony("Vorsel.HowDidThisEntityDie").PatchAll();
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
            SaveSettings();
        }

        private void SaveSettings()
        {
            base.WriteSettings();
            ContainmentRegistry.Invalidate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            List<ContainmentCandidate> candidates = ContainmentRegistry.GetCandidates(Settings, !deferPlatformScan);
            if (!ReferenceEquals(layoutCandidates, candidates) || visibleRows == null)
            {
                layoutCandidates = candidates;
                visibleRows = ContainmentSettingsLayout.BuildRows(candidates, expansionOverrides);
            }
            List<ContainmentSettingsRow> rows = visibleRows;
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
            GUI.BeginGroup(new Rect(inRect.x, inRect.y, viewportWidth, HeaderHeight));
            for (int columnIndex = firstColumn; columnIndex < lastColumn; columnIndex++)
            {
                float x = ColumnX(columnIndex);
                DrawColumnControls(new Rect(x, 0f, ColumnWidth, HeaderHeight), Settings.columns[columnIndex], columnIndex);
                DrawSeparator(x, columnIndex, HeaderHeight);
            }
            GUI.EndGroup();

            Rect outRect = new Rect(inRect.x, inRect.y + HeaderHeight + 4f, inRect.width,
                Math.Max(1f, inRect.height - HeaderHeight - 64f));
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
                        DrawCandidateRow(rowRect, rows[i].Candidate, column.id, pointerOverBody);
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
                    Settings.ResetToDefaults(ContainmentRegistry.GetCandidates(Settings, !deferPlatformScan));
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

        private void DrawSourceHeader(Rect rect, ContainmentSettingsRow row)
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
                    Settings.SelectAll(columnId, ContainmentRegistry.GetCandidates(Settings, !deferPlatformScan));
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
            NotificationColumn owner = Settings.ColumnFor(candidate.Key);
            bool selected = owner != null && owner.id == columnId;
            bool locked = owner != null && !selected;
            if (locked) Widgets.DrawBoxSolid(rect, new Color(1f, 1f, 1f, 0.08f));
            else Widgets.DrawHighlightIfMouseover(rect);
            Color previousColor = GUI.color;
            if (locked) GUI.color = new Color(previousColor.r * 0.72f, previousColor.g * 0.72f, previousColor.b * 0.72f, previousColor.a);
            Widgets.CheckboxDraw(rect.x + 4f, rect.y + 3f, selected, locked, 22f);
            string warning = candidate.DangerousAfterDeath ? " ⚠" : string.Empty;
            Widgets.Label(new Rect(rect.x + 32f, rect.y + 3f, rect.width - 34f, 24f), (candidate.Label + warning).Truncate(rect.width - 34f));
            GUI.color = previousColor;
            if (locked || !pointerOverBody) return;

            Event evt = Event.current;
            if (evt.type == EventType.MouseDown && evt.button == 0 && rect.Contains(evt.mousePosition))
            {
                dragColumnId = columnId;
                dragValue = !selected;
                Settings.TrySetSelected(candidate.Key, columnId, dragValue);
                evt.Use();
            }
            else if (evt.type == EventType.MouseDrag && dragColumnId == columnId && rect.Contains(evt.mousePosition))
            {
                Settings.TrySetSelected(candidate.Key, columnId, dragValue);
                evt.Use();
            }
        }

        private static string ModeLabel(DeliveryMode mode)
        {
            return mode == DeliveryMode.Letter ? "CFR.Settings.Mode.Letter".Translate() : "CFR.Settings.Mode.Message".Translate();
        }
    }
}
