# Prism 运行依赖补全

Prism 导入后现在会自动继续准备运行依赖。已经导入的实例不必重新导入：在“本地实例”选中实例，打开“补全实例依赖”，点击同名按钮即可。失败和取消保留已经完成的下载，再次执行会校验并复用现有文件。

## 这次修正了什么

上一版只检查 `eml-instance.json` 中已经带 URL 的文件，并没有把 Prism 的组件配方转为下载任务。GTNH 2.9.0-beta-1 的模组 JAR 已经随压缩包附带，但 Minecraft、Forge 类库、LWJGL 和游戏资源仍要由启动器安装。

现在的流程为：读取 `mmc-pack.json` → 按固定组件读取本地 `patches` 或 Prism 元数据 → 合并启动信息并筛选当前平台依赖 → 下载本体、类库和资源索引 → 展开并下载资源对象 → 解压旧式 native / 整理本地库 → 生成版本配置 → 完整检查。

不会把 `mmc-pack.json` 当成普通模组下载清单。包内模组没有 URL 时仍不能从文件名推测下载来源；缺少 `MMC-hint: local` 的自定义库时明确要求从原压缩包恢复。

## GTNH 的关键行为

- 优先使用包内自定义补丁，保留 `mmc-pack.json` 中组件的实际顺序，不用普通原版配方覆盖 LWJGL3ify。
- 合并 `mainClass`、`+jvmArgs`、`+tweakers`、`+traits` 和兼容 Java 主版本。保留最终的 `MainStartOnFirstThread` 主类。
- 同一 Maven 库保留适用的较高版本，例如此包的 Guava 17.0；本地 `libraries/lwjgl3ify-…-forgePatches.jar` 会正确转为启动类路径中的库。
- LWJGL3 的 native JAR 按普通 classpath 库处理；带 `natives` 映射的旧库按平台选择并安全解压。
- 上游声明 `sha1: ""`、`size: 0` 的旧库视为未提供这些校验依据，不当成零字节文件。按明确 HTTPS 地址下载后会检查 JAR 结构，界面仍显示“来源无哈希”，不伪称通过来源哈希验证。
- 游戏资源按资源索引中的 SHA-1 下载，重复内容去重，最多四路并发；支持旧索引的 virtual 和 map_to_resources 映射。

## 输出与启动模块对接

生成的文件均在实例内：

- 游戏目录的 `libraries/`、`assets/`：固定版本运行库和资源。
- 游戏目录的 `natives/<指纹>/`：旧式 native 文件。
- 游戏目录的 `versions/EML-Prism-<指纹>/`：版本 JSON、客户端 JAR 及同名 `.eml-launch.json`。
- 实例根目录的 `.eml-runtime/metadata/`：缺少本地补丁时取得的固定版本组件元数据。
- 实例根目录的 `.eml-runtime/state.json`：本次生成文件的基线和配置指纹。原 `eml-instance.json` 不改写。

**“运行文件已准备”与“已实际进入游戏”分开。** 这个分支没有启动实现；安装器不启动 Java、执行包内脚本、保存账号令牌或自动安装 Java。

启动模块建议读取 `.eml-launch.json`：路径均相对实例根目录，包含有序 `classPath`、`mainClass`、`jvmArguments`、`gameArguments`、`assetsRoot`、`assetIndexId`、`nativesDirectory`、`compatibleJavaMajors`、`runtimeOs` 和 `runtimeArchitecture`。将这些路径解析为绝对路径，再使用参数列表启动 Java；不要把参数串交给 PowerShell。

必须将 `${classpath}`、`${natives_directory}`、`${game_directory}`、账号和资源占位符替换为实际值。classpath 使用系统路径分隔符连接。Java 架构须与计划一致，Java 主版本须符合组件要求；当前安装器默认按 EML 进程的平台/架构准备。标准版本 JSON 附带 EML 的 native 路径和平台扩展，不能假设任意第三方启动器都会理解这些扩展。

生成文件只在不存在或内容完全一致时写入/复用，不覆盖不同内容的用户文件。主版本 JSON 最后发布；组件配置改变后旧准备状态失效。检查按钮只读取本地补丁、缓存和资源索引，不联网。

## 文件职责

- `EML.Core/Minecraft/PrismRuntimePlan.cs`：解析计划、运行依赖、元数据请求和安装结果。
- `Prism/PrismRuntimeResolver.cs`、`PrismRuntimeJson.cs`：固定组件、依赖关系、平台规则及 Maven 信息解析。
- `Prism/PrismInstanceInstaller.cs`：Prism 专用入口；共用的下载、文件复用、资源展开及配置生成协调移入 `Runtime/InstanceRuntimeInstaller.cs`。
- `Prism/PrismRuntimeMaterializer.cs`：资源展开、native 解压、文件整理及启动描述生成。
- `Prism/PrismRuntimeStateStore.cs`：独立运行准备基线。
- `MinecraftInstanceChecker.cs`：把运行依赖和生成文件纳入检查结果。
- `MinecraftInstanceDownloader.cs`：Prism 先准备运行依赖，再检查并处理其他导入文件。
- `InstancesViewModel.cs` / `.Checking.cs`：导入后衔接、手动补全、取消与结果展示。
- 通用下载请求的 `MaximumBytes`：元数据下载单独限制为 8 MiB，不改变普通大文件的全局上限。

## 仍有明确边界

当前覆盖这份 GTNH 使用的 Prism 组件模式，以及普通本地/固定版本组件、类库、资源和旧式 native。未实现的安装处理器、jarMods、特殊合并字段及 Prism 专属日志声明会作为阻断原因显示，不会悄悄丢弃后报告已准备。Modrinth 和 CurseForge 的原版/Fabric/Quilt 补全已另行接入，见 [MODRINTH.md](MODRINTH.md) 和 [CURSEFORGE.md](CURSEFORGE.md)。从原 ZIP 恢复缺失的 Embedded 文件、Java 自动安装仍是独立功能。

新增自动化测试使用内存 HTTP 响应及临时目录，涵盖来源不带哈希、平台规则、依赖冲突、路径安全、资源索引、生成文件冲突、取消与失败保留记录，以及完整的解析→下载→生成→复查流程。

2026-10-28 验证：Release 构建无警告、无错误，全部 255 项测试通过。另用真实 GTNH 2.9.0-beta-1 的组件声明和本地 bootstrap 库，在独立测试目录进行了真实联网安装：760 项下载通过来源哈希检查，1 项旧式 lzma 类库来源无哈希，成功生成启动配置。随后完整检查的 784 条记录中 783 条哈希通过、1 条来源无哈希，无缺失或不匹配项。此测试未复制整个模组目录、未改动原实例、未启动游戏。
