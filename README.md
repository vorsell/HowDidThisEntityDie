# How Did This Entity Die?

[简体中文](#简体中文) | [English](#english)

## 简体中文

为死亡时仍在收容平台上的实体提供可配置的死亡通知和更详细的报告。

### 主要功能

- 在能够判定时说明主要死因；详细信件还会记录致死伤害、死亡前状态、全部伤势与缺失部位，并可定位事发地点。
- 按实体类型配置死亡消息或信件，并从五种通用信件样式中选择通知级别。
- 为已收容实体配置身体部位 HP 阈值提示。提示只在实体受到伤害时检查，不进行逐 Tick 扫描。
- 可选支持 **Useful Marks** 和 **Mark That Pawn**：监控指定标记，并按已配置通知中的最高优先级报告被标记 Pawn 的死亡。
- 死亡报告设置列表支持分类吸顶与整组批量选择，方便浏览和配置较多选项。
- 不改变原版伤害、死亡机制或死亡后果；未配置的实体继续使用原版通知。

### 需求

- RimWorld 1.6 / 1.7
- RimWorld - Anomaly
- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)

Useful Marks 与 Mark That Pawn 均为可选集成，不是必需依赖。

第三方 Mod 也需要能够在对应游戏版本正常运行。本 Mod 包含 Held Human、Ratkin Anomaly+、Anomaly Portal、Useful Marks 和 Mark That Pawn 的针对性适配；Ratkin Anomaly+ 的专项死因适配已在 1.6 验证，1.7 尚未单独复验。

Anomalies Expected、New Anomaly Threats 和 Alien | Rimworld 已在 1.6 进行实体选项识别测试；上述三项及 Anomalies Expected Addon 的 1.7 兼容测试暂缓，不属于本轮已验证的 1.7 兼容范围。

### 安装与配置

[Steam Workshop 项目 ID：3794931477](https://steamcommunity.com/sharedfiles/filedetails/?id=3794931477)。手动安装时，将整个 Mod 文件夹放入 RimWorld 的 `Mods` 目录。启用 Harmony、Anomaly 和本 Mod 后，可在 Mod 设置中分别配置“收容实体提示”和“死亡报告配置”。

### 更新日志 — 2026年10月14日

- 新增 RimWorld 1.7 支持。
- 死亡报告配置新增分类标题吸顶，浏览较长的实体或标记列表时，可持续看到当前分类。
- 分类标题新增三态指示，以原版绿色勾选、红色叉号和黄色部分选择标记，显示本列的全选、未选或部分选中状态。
- 支持单击全选或取消整个分类，包含折叠项。需要从其他配置列转移选项时，会先弹出确认，不影响其他分类或通知样式。
- 分类标题与列表拖选操作分离：单击名称可展开或收起分类，列表项仍可拖拽批量勾选；拖过分类或吸顶标题不会误改分类状态，也不会选中被吸顶标题遮挡的列表项。

## English

Configurable death notifications and detailed reports for entities that die while still contained on holding platforms.

### Features

- Shows a primary cause of death when it can be identified. Detailed letters can also include fatal damage, pre-death condition, all injuries, missing body parts, and a jump-to-location action.
- Configures messages or letters per entity type, with five general-purpose letter styles available.
- Supports body-part HP threshold alerts for contained entities. Alerts are evaluated only when damage is taken; no per-tick scan is added.
- Optionally integrates with **Useful Marks** and **Mark That Pawn** to monitor selected marks. If several configured marks match, the highest-priority notification is used.
- Sticky category headers and whole-category selection make long death report settings lists easier to browse and configure.
- Preserves vanilla damage, death mechanics, and consequences. Unconfigured entities keep their vanilla notification behavior.

### Requirements

- RimWorld 1.6 / 1.7
- RimWorld - Anomaly
- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)

Useful Marks and Mark That Pawn are optional integrations, not required dependencies.

Third-party mods must also work with the corresponding game version. Dedicated integrations are included for Held Human, Ratkin Anomaly+, Anomaly Portal, Useful Marks, and Mark That Pawn. Ratkin Anomaly+'s special death-cause integration has been verified in 1.6 but has not been separately retested in 1.7.

Entity-option recognition has been tested in 1.6 with Anomalies Expected, New Anomaly Threats, and Alien | Rimworld. RimWorld 1.7 compatibility testing for these three mods and Anomalies Expected Addon is currently deferred; they are not included in the compatibility verified for this 1.7 release.

### Installation and configuration

[Steam Workshop item ID: 3794931477](https://steamcommunity.com/sharedfiles/filedetails/?id=3794931477). For manual installation, place the complete mod folder in RimWorld's `Mods` directory. Enable Harmony, Anomaly, and this mod, then use Mod Settings to configure contained-entity alerts and death-report rules.

### Changelog — October 14, 2026

- Added support for RimWorld 1.7.
- Added sticky category headers to the death report settings, keeping the current category visible while browsing long lists of entities or marks.
- Added three-state selection indicators to category headers, using the game's green checkmark, red cross, and yellow partial-selection icon to show whether all, none, or some entries are selected in the current column.
- Clicking a category's selection indicator now selects or deselects the entire category, including collapsed entries. Moving entries from another configuration column requires confirmation and does not affect other categories or notification styles.
- Separated category clicks from list drag-selection. Clicking a category name expands or collapses it, while individual entries still support drag-selection. Dragging across regular or sticky headers will not change category selection or select entries hidden beneath a sticky header.

## Source

The shared C# source is in `Source`; release assemblies are in `1.6/Assemblies` and `1.7/Assemblies`. `LoadFolders.xml` selects only the matching game-version branch.

RimWorld 1.6 uses `Source/ContainmentFatalityReport.csproj` (.NET Framework 4.8.1); RimWorld 1.7 uses `Source/ContainmentFatalityReport.1.7.csproj` (.NET 10). Both projects use C# 7.3 and local game/Harmony reference paths, which must be adjusted for your build environment. Game and Harmony dependencies are not bundled in this repository.

Created by **Vorsel**, with code assistance from Codex.
