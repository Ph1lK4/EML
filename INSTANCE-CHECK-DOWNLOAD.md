# 实例文件检查和通用下载

Prism、Modrinth 和 CurseForge 支持的运行依赖现已接入：导入后自动继续准备，也可点击“补全实例依赖”重试。详细支持范围及启动模块对接见 [PRISM-RUNTIME.md](PRISM-RUNTIME.md)、[MODRINTH.md](MODRINTH.md) 和 [CURSEFORGE.md](CURSEFORGE.md)。下文的检查及下载基础机制继续适用。

本地实例页下方提供“文件检查”和“补全实例依赖”标签页。功能只操作启动器根目录 `instances` 中选中的直接子实例。

## 使用

1. 选中导入过的实例，在“文件检查”执行快速或完整检查。
2. 快速检查确认文件是否存在、路径类型和已知大小；完整检查还会在一次读取中计算所有声明的 SHA-1 / SHA-256 / SHA-512。
3. 点击“补全实例依赖”。Prism 先解析并安装运行组件及资源，其他导入文件仍只补齐缺失、必需、有明确 HTTPS 地址及哈希的 DirectDownload 文件。
4. 每个文件校验后才发布；一个文件失败不会阻止后续文件。下载结束后自动完整复查。
5. 取消会保留已完成的文件，未完成文件不发布为目标。最终复查未结束时不展示旧检查结果，请重新检查。

检查不会在启动时自动计算哈希；启动时现有的 Java 和实例列表检测保持原有行为。检查报告是内存中的一次快照，切换实例、刷新或重命名后失效，不写回 `IntegrityStatus`。

## 检查范围

检查依据是导入时生成的 `eml-instance.json`，其中的文件路径相对**实例根目录**，可能是 `.minecraft/mods/a.jar`、`minecraft/mods/a.jar` 或 `mods/a.jar`。没有清单时显示“缺少检查依据”，不会根据当前文件反向生成“全部完整”的结论。

| 结果 | 含义 |
| --- | --- |
| 校验通过 | 所有声明哈希与当前内容一致 |
| 存在，未校验内容 | 快速检查或清单没有哈希，不能确认内容一致 |
| 缺失 | 声明路径不存在；可选文件单独标识 |
| 导入后已变化 | Embedded 文件与导入时快照不同，可能是正常配置、存档或实例名称变化 |
| 大小 / 哈希不符 | 下载来源文件与清单声明不一致；保留现有文件，不自动替换 |
| 待解析来源 | 尚未取得平台文件路径等信息，不能判定缺失 |
| 无法读取 / 检查时变化 | 权限、占用、链接、目录类型不符或检查期间变化，不能判定通过 |

未声明的额外文件不会被删除或判断为错误。Prism 和 Modrinth 会展开支持的运行组件及资源索引；其他格式的运行依赖及 Java 自动安装尚未接入。文件校验通过不代表已实际启动游戏。

## 不同来源的补全能力

- Modrinth：按清单下载并校验必需文件，准备原版/Fabric/Quilt 的本体、运行库、资源和日志配置。Forge/NeoForge 的安装处理器尚未接入。
- Prism：解析 Minecraft、加载器、LWJGL 等运行依赖与资源，并准备启动配置。包内 Embedded 模组缺失时仍需要原压缩包或明确来源，不能从文件名猜测。
- 通用 ZIP：包内文件可以对照导入快照检查，运行依赖自动解析尚未接入。
- CurseForge：通过维护者构建时注入的授权，直连官方 API/CDN，按固定项目/文件 ID 下载必需模组/资源包/光影并校验 SHA-1；明确声明 Paxi 和包内加载顺序配置的实例也支持数据包。玩家无需配置 Key，受限下载显示官方手动入口。原版/Fabric/Quilt 运行补全已接入，Forge/NeoForge 安装处理器尚未实现。
- 普通导入清单中没有哈希的文件、可选文件、已经存在但不匹配的文件不进入自动补全。Prism 明确声明下载地址但没有来源哈希的旧式类库例外：下载后检查 JAR 结构，并标为“来源无哈希”。

## 代码结构

- `EML.Core/Downloads/IFileDownloader.cs`：通用下载请求、结果、进度和接口，无 Minecraft 依赖。
- `EML.Infrastructure/Downloads/HttpFileDownloader.cs`：HTTPS 流式下载、镜像回退、有限重试、取消、超时、大小及哈希校验。
- `EML.Infrastructure/Downloads/FileDownloadOptions.cs`：每地址最多 3 次尝试、最多 5 次跳转、单次尝试 10 分钟、默认文件上限 8 GiB 等配置。
- `EML.Core/Minecraft/IMinecraftInstanceChecker.cs`：检查模式、文件状态、快照及进度。
- `EML.Infrastructure/Minecraft/MinecraftInstanceChecker.cs`：只读检查导入清单、Prism 运行依赖及生成文件。
- `EML.Core/Minecraft/IMinecraftInstanceDownloader.cs`：实例批量补全的结果与进度。
- `EML.Infrastructure/Minecraft/MinecraftInstanceDownloader.cs`：Prism 运行依赖准备 → 重新检查 → 补全其他必需缺失文件 → 完整复查。
- `EML.App/ViewModels/InstancesViewModel.Checking.cs`：新功能的界面命令、进度、报告及取消；共用原有忙碌状态。
- `EML.App/MainWindow.xaml(.cs)`、`InstancesViewModel.cs`：界面入口、服务组装和状态失效。

下载目标旁使用随机 `.part` 文件，通过本次请求可用的大小及哈希检查后，禁止覆盖地移动为正式文件。失败及正常取消会清理本次临时文件，关闭窗口时会等待正在运行的文件操作结束。进程被强制终止或操作系统拒绝删除时可能留下 `.part`，不会被当作完整目标；清理错误保留在原异常的诊断数据中。Modrinth 清单文件默认四路并发；运行类库顺序下载、资源对象最多四路并发。不支持跨进程续传单个未完成文件，但完成文件可复用。下载器拒绝覆盖已存在的文件，并拒绝链接路径、非 HTTPS 地址和降级重定向。

Prism 运行解析与安装已放在独立服务中，复用通用下载器。后续平台解析器也可以把引用转换为具体文件需求；原压缩包恢复及用户明确选择的文件替换应保持独立。

## 验证

在解决方案目录运行 `dotnet build EML.slnx -c Release` 和 `dotnet test tests/EML.Tests/EML.Tests.csproj -c Release`。新增测试使用 GUID 临时目录、假 HTTP 传输和假下载器，覆盖检查、下载及批量补全流程，不联网下载，不操作已有测试整合包。
