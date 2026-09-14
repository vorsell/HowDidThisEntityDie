# How Did This Entity Die?

[简体中文](#简体中文) | [English](#english)

## 简体中文

为死亡时仍在收容平台上的实体提供可配置的死亡通知和更详细的报告。

### 主要功能

- 在能够判定时说明主要死因；详细信件还会记录致死伤害、死亡前状态、全部伤势与缺失部位，并可定位事发地点。
- 按实体类型配置死亡消息或信件，并从五种通用信件样式中选择通知级别。
- 为已收容实体配置身体部位 HP 阈值提示。提示只在实体受到伤害时检查，不进行逐 Tick 扫描。
- 可选支持 **Useful Marks** 和 **Mark That Pawn**：监控指定标记，并按已配置通知中的最高优先级报告被标记 Pawn 的死亡。
- 不改变原版伤害、死亡机制或死亡后果；未配置的实体继续使用原版通知。

### 需求

- RimWorld 1.6
- RimWorld - Anomaly
- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)

Useful Marks 与 Mark That Pawn 均为可选集成，不是必需依赖。

### 安装与配置

[Steam Workshop 项目 ID：3794931477](https://steamcommunity.com/sharedfiles/filedetails/?id=3794931477)。手动安装时，将整个 Mod 文件夹放入 RimWorld 的 `Mods` 目录。启用 Harmony、Anomaly 和本 Mod 后，可在 Mod 设置中分别配置“收容实体提示”和“死亡报告配置”。

## English

Configurable death notifications and detailed reports for entities that die while still contained on holding platforms.

### Features

- Shows a primary cause of death when it can be identified. Detailed letters can also include fatal damage, pre-death condition, all injuries, missing body parts, and a jump-to-location action.
- Configures messages or letters per entity type, with five general-purpose letter styles available.
- Supports body-part HP threshold alerts for contained entities. Alerts are evaluated only when damage is taken; no per-tick scan is added.
- Optionally integrates with **Useful Marks** and **Mark That Pawn** to monitor selected marks. If several configured marks match, the highest-priority notification is used.
- Preserves vanilla damage, death mechanics, and consequences. Unconfigured entities keep their vanilla notification behavior.

### Requirements

- RimWorld 1.6
- RimWorld - Anomaly
- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)

Useful Marks and Mark That Pawn are optional integrations, not required dependencies.

### Installation and configuration

[Steam Workshop item ID: 3794931477](https://steamcommunity.com/sharedfiles/filedetails/?id=3794931477). For manual installation, place the complete mod folder in RimWorld's `Mods` directory. Enable Harmony, Anomaly, and this mod, then use Mod Settings to configure contained-entity alerts and death-report rules.

## Source

The C# source is in `Source`; the RimWorld 1.6 release assembly is in `1.6/Assemblies`.

Created by **Vorsel**, with code assistance from Codex.
