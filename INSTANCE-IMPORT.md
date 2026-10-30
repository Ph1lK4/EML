# 实例导入与后续安装流程

在“本地实例”点击“导入实例”，选择 `.zip` / `.mrpack`，检查预览并输入新名称。导入完成后列表自动刷新并选中新实例。导入支持取消，不会覆盖已有实例。

导入器本身只负责离线解压；界面在 Prism、Modrinth 或 CurseForge 导入成功后自动接着调用对应的独立安装服务。其他格式仍由用户在补全页处理已有下载来源。导入成功不等于已启动游戏，程序不会执行包中的脚本或程序。后续流程详见 [PRISM-RUNTIME.md](PRISM-RUNTIME.md)、[MODRINTH.md](MODRINTH.md) 和 [CURSEFORGE.md](CURSEFORGE.md)。

## 格式与布局

| 格式 | 识别依据 | 本次处理 | 留给下载/检查阶段 |
| --- | --- | --- | --- |
| Prism | `mmc-pack.json` | 保留完整实例内容与 patches、libraries、instance.cfg，支持外层目录 | Minecraft、加载器及共享依赖需要继续解析；禁用组件不加入安装需求 |
| Modrinth | `modrinth.index.json` | 通用 overrides 后叠加 client-overrides，放入 `.minecraft` | 保存 HTTPS 地址、SHA-1/SHA-512、大小和可选标志；忽略客户端 unsupported 文件 |
| CurseForge | `manifest.json` | 按 overrides 字段指定的目录导入配置等文件 | 保存 projectID/fileID/required；目标路径和下载地址须后续解析，不能从 ID 猜测 |
| 通用 ZIP | `.minecraft`、`minecraft` 或可识别的游戏目录内容 | 支持常见单层/多层包装，保留存档、模组及空目录 | 没有完整依赖清单时不能保证知道所有缺失文件 |

一个 ZIP 中出现多个可识别实例时拒绝自动挑选。损坏的平台清单不会悄悄降级为通用 ZIP。EML 自己导出的备份再次导入时优先使用统一清单，保留远端需求（包括已经存在的下载文件的原始 URL/哈希），普通包内配置则按备份中的当前内容重新计算哈希。

## 文件职责

- `EML.Core/Minecraft/IMinecraftInstanceImporter.cs`：只读预览、导入接口和进度/结果类型。
- `EML.Core/Minecraft/InstanceImportManifest.cs`：平台无关的组件和文件需求。
- `EML.Infrastructure/Minecraft/MinecraftInstanceImporter.cs`：组织分析、提取、哈希计算和提交。
- `EML.Infrastructure/Minecraft/Importing/InstanceArchiveReader.cs`：各格式到统一计划的转换。
- `Importing/SafeInstanceArchive.cs`、`InstanceImportPaths.cs`：压缩条目与目标路径验证。
- `Importing/InstanceManifestStore.cs`：统一清单的读取、校验和保存。
- 原扫描器读取统一清单，因此尚未下载 versions 的平台包也能显示声明的游戏版本。
- App 的对话框和 ViewModel 只负责交互；原管理服务仍负责删除、重命名与导出。

## 统一清单

每个导入实例根目录生成 `eml-instance.json`（schemaVersion=1）。所有文件目标相对实例根目录，移动或重命名实例后仍能定位。原平台清单同时保留，方便后续解析未映射的扩展字段。

- `components` 保存游戏本体/加载器等 ID 与版本，CurseForge 的 primary 标记也会保留。这些是安装声明，并不是已经安装的状态。
- `files` 的来源为 `Embedded`、`DirectDownload`、`CurseForge`。包内文件包含实际 SHA-256 和大小；下载文件保留平台哈希；尚未解析的 CurseForge 文件允许 path/size 为空。
- `required=false` 保留可选需求，未来下载 UI 应提供选择，不能静默当成必需项。
- Modrinth overrides 覆盖的远端需求从最终文件列表移除，以免后续下载再次覆盖配置。
- `integrityStatus=NotChecked` 明确区分“导入”和“当前磁盘完整性检查”。原包文件名仅用于溯源，不保存用户电脑绝对路径。
- Prism 的 `instance.cfg` 会同步导入名称，所以此文件的 `archiveEntry` 留空；它已经过转换，不能用原 ZIP 条目直接恢复新哈希。

建议后续流程：读取统一清单 → 解析组件/CurseForge 文件信息 → 检查当前磁盘 → 生成下载计划 → 下载临时文件并校验 → 原子替换。检查结果应区分缺失、已修改、哈希不符、可选、待解析与无法自动下载；不要仅凭此清单的哈希把玩家主动修改的配置和存档覆盖回去。CurseForge API 的凭据和分发限制应在后续解析器处理。

HTTP 下载、文件检查、Prism 运行依赖展开、Modrinth 及 CurseForge 原版/Fabric/Quilt 补全已由独立服务实现。CurseForge 使用维护者在构建时注入的授权直连官方，玩家无需配置 Key；受限文件提供手动下载提示。Forge/NeoForge 安装处理器、Java 自动安装和游戏启动尚未接入此分支。

## 写入行为

先验证全部 ZIP 条目，再写入启动器根目录 `.eml-imports/<随机ID>`；写入清单成功后才整体移动到 `instances/<名称>`。取消/失败会尝试清理本次临时目录；若被外部进程占用而清理失败，临时文件不进入实例列表，诊断输出包含其位置。

拒绝越界路径、Windows 设备名、ADS、大小写重复、文件/目录冲突及链接；默认最多 100000 个 ZIP 条目、50 GiB 解压数据、8 MiB 清单。当前不支持 RAR/7z、任意文档 ZIP 或多实例批量导入。

## 格式参考

- Modrinth 官方格式：https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack
- CurseForge 官方导出说明：https://support.curseforge.com/support/solutions/articles/9000197908
- CurseForge API：https://docs.curseforge.com/rest-api/
- Prism 实例导入源码：https://github.com/PrismLauncher/PrismLauncher/blob/develop/launcher/InstanceImportTask.cpp
- Prism 的 CurseForge 清单解析：https://github.com/PrismLauncher/PrismLauncher/blob/develop/launcher/modplatform/flame/PackManifest.cpp
