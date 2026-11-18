# EML 1.0-RC2 发布与升级

`1.0-RC2` 是 Windows x64 候选测试版，产品版本为 `1.0.0-rc.2`。本次主要更新是接入 EML「接续」Logo，并整理便携包的文件布局；不是单文件 EXE，也没有自动更新或自动回滚功能。

原 `v1.0.0-rc.1` 的标签、下载包和 SHA-256 保持不变。`1.0-RC1-Logo1` 是保留旧布局、加入 Logo 的独立重打包版。不要把几个版本的 EXE、DLL 或校验文件混用。

## 下载包里有什么

```text
EML-1.0-RC2-win-x64/
├── EML.exe                 # 从这里启动
├── app/                    # 主程序、.NET 运行库、语言资源和公开登录配置
├── docs/                   # 许可证、源码定位、隐私、安全和使用说明
├── START-HERE.txt
├── UPGRADE.txt
└── release-manifest.json   # 版本、构建来源和逐文件校验值
```

`cs`、`ko` 等运行库语言目录位于 `app/`，它们不是玩家数据，也不表示 EML 已有对应语言的完整界面。`app/` 是程序的一部分，不能删除、改名或与其他版本混用。只复制 `EML.exe` 不能运行启动器。

包内附带所需 .NET 运行时，玩家不需要安装 .NET SDK、Visual Studio 或另装 .NET。Java、Minecraft 和整合包不随启动器附带，需要在使用游戏前下载。

发布包不包含维护者的实例、账号、下载设置、私人日志或存档。CurseForge 授权仍沿用程序集内嵌方案；它可被提取，目录整理和 Logo 不会改变这一风险。公开的 `microsoft-login.json` 只保存应用 Client ID，不保存玩家凭证。

## 第一次使用

1. 将 ZIP 完整解压到当前用户有写入权限的目录。不要放到 `Program Files`、Windows 系统目录或只读位置。
2. 运行根目录的 `EML.exe`。不要在压缩软件内运行，不要只取走 EXE。
3. 在“下载实例”或“GTNH 专区”下载安装整合包，也可以导入已有包或从其他启动器复制受支持的实例。
4. 在“Java 环境”准备适合实例的 Java；启动前缺少兼容版本时，也可按提示确认下载。
5. 核对实例、启动模式和玩家身份。正版模式需要有效的 Minecraft 账号；离线玩家名不能替代正版认证。

不要求管理员权限。首次下载需要网络和足够磁盘空间。启动器能打开，不代表游戏依赖已补全或所有整合包都已完成兼容性验收。

## 程序目录与玩家数据

RC2 的 `app/` 只放程序内容。正常使用官方 RC2 布局时，玩家数据仍保存在根目录，也就是 `EML.exe` 旁边，不会因为 DLL 移到 `app/` 就变成 `app/instances`。

| 位置 | 内容与升级要求 |
| --- | --- |
| `instances/` | 全部实例、世界、地图、截图和游戏设置；整个目录保留 |
| `instances/<实例>/.eml-runtime/` | 启动设置、文件来源、用户修改、模组回收区、迁移记录与备份；不是可以随意删除的缓存 |
| `settings/` | 并发、限速等启动器设置；保留 |
| `runtimes/` | EML 下载的 Java；保留，原安装路径不变有助于保留 Java 路径设置 |
| `cache/` | 下载缓存、断点文件及原始整合包；建议保留，部分原包可用于文件恢复 |
| 根目录 `.eml-runtime/`、`.eml-imports/` | 保留，不在升级中粗暴清空 |

微软账号集合仍在当前 Windows 用户的 `%LOCALAPPDATA%\EML\accounts\microsoft.dpapi`，不在发布包或 `app/` 内。同一 Windows 用户的 EML 副本共用该账号位置；换电脑或换 Windows 用户应重新登录，不要分享或搬运凭证文件。实例内保存的公开玩家名、UUID 和启动选择不是登录令牌。

## 从 RC1 或 RC1-Logo1 升级

建议保持原安装路径，只合并新版程序文件。这样不需要搬动实例、下载缓存或已安装 Java。

1. 正常退出 Minecraft，结束下载、导入和迁移，再退出所有 EML 窗口。
2. 将整个旧 EML 文件夹复制备份到其他位置，包含隐藏目录，并确认备份可读取。仅“导出实例 ZIP”不能代替这份升级备份。
3. 将 RC2 解压到一个新的临时目录，核对 ZIP 的 SHA-256 和包内发布信息。
4. 将 RC2 解压目录内的文件和文件夹复制到旧 EML 根目录，合并目录，只覆盖新版包内的同名程序文件。不要覆盖或删除 `instances`、`settings`、`runtimes`、`cache` 或 `.eml-*` 玩家数据目录。
5. 运行根目录新的 `EML.exe`，确认标题显示 `1.0-RC2`；更新桌面或开始菜单快捷方式，使其指向这个入口。
6. 检查实例列表、上次使用的模式和账号、Java 路径、内存、JVM 参数及下载设置。第一次游戏测试使用存档副本。

**旧 `EML.App.exe` 以及原本平铺的 DLL、语言目录可能仍留在根目录。** 本次升级不自动删除它们，因此旧安装升级后不一定立即变得和全新解压目录一样整齐。不要继续从 `EML.App.exe` 或旧快捷方式启动，否则可能运行的仍是旧版。

不要为了整理文件而批量删除 `*.dll`、所有文件夹或隐藏目录。旧程序文件的清理应另行依据旧版发布清单逐项确认；本次升级流程不执行这种清理。不得使用“清空后解压”、镜像同步或 `robocopy /MIR`，也不要让两个 EML 版本同时操作同一个实例。

如果新版出现问题，先正常退出并保留错误信息。回退时使用升级前的完整备份，避免把新旧程序文件混合。若升级后已经继续游玩或更改设置，先单独备份这些新增数据，再考虑回退；不能用旧备份直接覆盖最新进度。没有事务式自动回滚。

移动安装目录或换电脑不是同路径升级。已保存的 Java 绝对路径、原整合包位置可能需要重新选择，微软账号可能需要重新登录。保留缓存不等于所有下载任务会自动恢复。

## 来源、校验与签名

每个包使用独立版本、构建提交和校验值。`docs/SOURCE.txt` 给出对应的源码提交，`release-manifest.json` 记录实际构建来源和文件哈希；Release 标签应指向同一提交。不要用原 RC1 的 SHA-256 检查 RC1-Logo1 或 RC2。

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath '实际下载的 EML-1.0-RC2-win-x64.zip 路径'
```

将输出与同版本的 `.zip.sha256` 比较。SHA-256 可检查内容变化或下载损坏，不能代替发布者数字签名，也不能保证第三方模组安全。

**RC2 仍未进行代码签名。** 签名服务尚未启用，不应把 GitHub 发布页或 CI 通过描述成 EXE 已签名。若 Windows 或安全软件阻止运行，请核对来源、保留提示并反馈，不要关闭防护强行运行。包内的代码签名政策、隐私说明和安全报告说明提供更多信息。

## 测试边界与待验收项目

自动化结果以本次构建的测试报告和发布说明为准。版本、文件白名单、哈希、Logo 资源、数据根目录和合成升级保留测试不能代替真实账号、服务器及完整整合包的端到端验收。

仍需在没有 Visual Studio、共享 .NET 运行时及 Java 的 Windows x64 普通用户环境完成验收。不要通过卸载开发机依赖来模拟干净机器。

| 项目 | 需要实际确认 |
| --- | --- |
| 干净电脑运行 | 完整解压后从 `EML.exe` 打开，无需额外 .NET 或管理员权限 |
| 中文与空格路径 | 程序能打开，实例、设置、Java 和缓存仍在根目录，不进入 `app/` |
| 不同启动位置 | 从快捷方式或其他工作目录启动，仍读取同一份实例与设置 |
| 离线与故障 | 断网能打开；网络失败原因明确，不把未完成任务报为成功 |
| Java 流程 | 缺失时提示；取消不启动；确认下载后校验、安装并重新尝试 |
| 游戏流程 | 测试原版与 GTNH 副本，核对启动、退出、日志及服务器连接 |
| 多账号 | 由测试者本人添加两个正版账号，核对实例独立记忆、刷新登录及正版服务器连接 |
| 升级保留 | 使用合成或已备份数据，核对存档、启动参数、账号选择、Java 和下载设置 |
| 视觉与隐私 | 新 Logo 在 EXE、窗口和任务栏正常；新 Windows 用户不出现维护者的私人记录 |

报告请注明 EML 版本、Windows 版本、ZIP 校验值、操作步骤和已脱敏错误文字；不要上传账号文件、密码、令牌或未经检查的私人日志。

维护者：K4ssia。联系和安全漏洞私报：477619464@qq.com。

## English summary

EML 1.0-RC2 is an unsigned Windows x64 release candidate. It adds the Relay logo and moves application files, bundled .NET and runtime language folders into `app/`. Run `EML.exe` from the package root; keep the entire package together. This is not a single-file build.

Player data stays beside `EML.exe`, not inside `app/`. Close Minecraft and EML, back up the complete old folder, then merge the RC2 package into the same installation path. Keep instances, settings, runtimes, caches and hidden EML data directories. Old flat-layout files are not automatically removed: use the new `EML.exe` and update shortcuts instead of running the old `EML.App.exe`.

Microsoft account credentials remain in the current Windows user's Local AppData. They are not part of the package or a portable account backup. Clean-machine testing, real two-account sign-in and full pack/server testing remain separate acceptance work. The original RC1 download and tag are unchanged; RC1-Logo1 and RC2 have their own source revisions and checksums.
