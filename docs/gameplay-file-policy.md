# 游戏正常变化与文件完整性

本次策略继续区分获取方式 `Source`、内容来源 `Origin` 和用途 `Role`。不会重写导入清单、重算原始可信哈希或把来源要求合并。旧清单的可空字段继续省略；用途分类只在有效需求快照中推导。

## 正常修改

只有 `Source=Embedded`、`Origin=null` 且用途没有显式设为 `Content` 的文件可采用普通包内变化策略。

- 原有设置文件及 `config/`、`defaultconfigs/` 中的配置允许已存在、可读取文件的内容变化。
- ServerUtilities 仅增加 `serverutilities/aurora.cfg`、`serverutilities/serverutilities.cfg`、`serverutilities/client/serverutilities.cfg` 三个游戏目录内的精确路径。
- `UserData` 只涵盖 `logs/` 下 `.log`、`.log.gz` 与 `saves/` 下已识别的世界状态格式（level、session、region、entities、poi、playerdata、data、stats、advancements）。这些文件缺失仍要求处理。
- 可执行文件、脚本、归档和 `datapacks` 中的内容不会因为位于配置或存档目录而取得可变许可。旧备份可能已经声明它们为 `UserConfiguration`，该声明可继续读取，但不产生放行许可。
- `EmbeddedOverride`、`UserLocalImport`、平台来源以及显式 `Role.Content` 始终按各自原始完整性要求校验。

检查器仍报告 `ChangedSinceImport`。共享状态策略把经过允许的正常变化与真正的损坏区分；不会把现存文件覆盖回原包版本。

## 一次性更新标记

游戏目录内 `config/modpack-update` 采用 `ConsumableMarker` 用途。只有同时满足普通包内来源、原条目记录、`Size=0`、唯一 SHA-256 为标准空内容摘要、无下载 URL，才允许消耗后缺失。兼容旧备份已记录的 `UserConfiguration`；显式 `Content` 继续收紧。用途不授予缺失许可，因此用户明确替代该文件后仍可保留用途描述，但须严格匹配用户选择的 Size 与 SHA-256。

真实不存在时报告 `Consumed`，表示其已允许被游戏消耗，不宣称 EML 观察到了具体删除进程。检查、补全及安装后验证采用同一规则，不恢复该标记。若标记仍存在，则继续校验它必须为空；目录占位、父路径为文件、权限错误、链接或验证中消失都不能通过缺失许可。

备份保留原 Size、SHA-256、条目和用途语义，缺失标记不会被虚构写进 ZIP。恢复备份后仍按同一策略判断，不把其他缺失配置一并放行。

## 本地证据（2026-11-05）

通过只读查看用户 GTNH 2.9.0-beta-1 原 ZIP 与实际安装 JAR，并用本机 JDK 8 的 `javap -c -p` / `javap -v -p` 检查字节码；未执行 JAR、未改写实例。

1. 原 ZIP 条目 `GT New Horizons 2.9.0-beta-1/.minecraft/config/modpack-update` 长度为 0，SHA-256 为 `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`，与导入记录一致。
2. `defaultkeys-1.3.1.jar` 的 `net.blay09.mods.defaultkeys.EventHandler.preStartGame()` 读取 `config/modpack-update`，`restoreLocalConfig()` 成功后调用 `java.nio.file.Files.delete`。其错误文本说明无法删除时配置会持续恢复。因此自动补回该标记会重新触发恢复配置。
3. `GTNewHorizonsCoreMod-2.8.279.jar` 的 `com.dreammaster.coremod.DreamCoreMod` 读取同一标记并设置 `modpackHasUpdated`；该状态还影响默认任务数据库更新。
4. `ServerUtilities-2.3.0.jar` 中 `ServerUtilitiesConfig`、`AuroraConfig`、`ServerUtilitiesClientConfig` 的配置注解分别指向 `../serverutilities/` 和 `../serverutilities/client/`，Aurora 显式文件名为 `aurora`。
5. `gtnhlib-0.11.9.jar` 的 `com.gtnewhorizon.gtnhlib.config.ConfigurationManager.registerConfig()` 在处理配置后调用 Forge `Configuration.save()`。三个 CFG 是程序可重写的设置，不是固定模组内容。

这些是已安装版本的直接证据；规则不把整个 ServerUtilities 目录、任意零字节文件或所有 EmbeddedOverride 视为可变内容。

## 服务合同

`InstanceFileChangePolicy` 负责用途和允许的变化，`InstanceFileStatePolicy` 负责是否阻断。`InstanceContentVerifier.CheckExistingAsync` 供 CurseForge、Modrinth 内容准备及所有平台的最终内容复核共用；检查器共享路径类型验证。运行依赖依旧严格，不套用游戏内容的可变策略。

`ContentReady`、`RuntimeReady`、`LaunchReady` 保持独立。内容规则变化不意味着跳过 Minecraft 或加载器校验，快速检查也仍不能发布最终启动许可。
