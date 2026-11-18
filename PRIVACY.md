# 隐私说明 / Privacy notice

更新日期：2026-11-18。适用于当前 EML 源码和 `1.0-RC1` 候选发布版。

EML 是面向 Minecraft、尤其是 GTNH 玩家的 Windows 启动器。公开维护者为 **K4ssia**，联系及安全漏洞私报邮箱为 **[477619464@qq.com](mailto:477619464@qq.com)**。

目前 EML 没有维护者自建的数据接收服务，也没有启用捐赠或独立反馈平台。这不等于启动器不联网：正版登录、在线搜索、图标、文件补全和下载会访问相应的第三方服务。

## 本机保存和处理的数据

便携发布版通常将实例与设置保存在程序目录下；源码调试版或用户另外选择的文件可能位于其他位置。删除前请核对实际路径。

| 数据 | 用途与位置 |
| --- | --- |
| 实例与玩家数据 | `instances/` 内的世界、地图、服务器列表、截图、模组、资源包、游戏设置及日志，用于游戏和实例管理 |
| 启动与文件状态 | 实例内 `.eml-runtime/` 保存启动模式、公开玩家名和 UUID、上次游戏进程启动时间、内存/JVM 参数、Java 路径、文件来源和完整性记录、用户修改及迁移记录；其中可能有备份，不是普通缓存，不保存登录令牌 |
| 启动器与下载数据 | `settings/`、`cache/`、根目录 `.eml-runtime/` 和 `.eml-imports/` 中的设置、图标缓存、下载缓存、断点文件、原包及工作数据；不是所有任务列表状态都会持久化 |
| Java 环境 | 检测本机 Java 的路径、版本与架构，供启动时选择；EML 下载的 Java 位于 `runtimes/java/` |
| 微软登录记录 | `%LOCALAPPDATA%\EML\accounts\microsoft.dpapi`，整体加密保存多个 Minecraft 玩家档案、各自的应用 Client ID 与刷新令牌，以及默认账号选择 |

Java 检测和实例检查会读取本机相关文件或环境信息，完整性检查也会计算文件摘要。平台文件识别功能可能向平台查询摘要；这与上传整个本机文件不同。

EML 的账号存储使用当前 Windows 用户范围的 DPAPI 加密，不将微软密码保存到该文件。当前持久化账号模型不保存短期 Minecraft access token。DPAPI 不是对同一 Windows 用户下所有程序的隔离保证；不要分享凭证文件，也不要运行来源不明的模组或程序。

从其他启动器复制实例时，会读取所选实例的名称、目录、版本及游戏文件；对支持的 Modrinth App 数据库只查询实例与组件记录，不读取账号表。原启动器的账号凭证、启动命令和应用数据库不作为游戏内容导入。游戏或模组自建配置可能仍包含私人信息，只导入你信任的实例。

## 正版登录

微软登录在微软授权页面完成，EML 不要求在自己的界面输入微软密码。登录及会话刷新会连接 Microsoft、Xbox Live/XSTS 和 Minecraft 服务，交换该流程必需的授权码或令牌，并获取玩家档案及游戏访问相关信息。

启动器打开时读取本机账号记录，不据此主动刷新会话；用户进行正版登录或正版启动时会按流程联网。正版启动还需要把玩家身份及游戏会话信息传给本机 Minecraft 进程。因此本机进程、游戏或模组对这些信息的访问不能仅靠凭证文件加密来阻止。

离线模式使用用户填写的玩家名，不等于获得正版授权，也不能保证进入要求正版认证的服务器。

## 搜索、下载及第三方服务

下表概括当前主要联网用途，不是穷尽的域名白名单。实际文件、图标和运行库可能由接口或整合包清单指定的 CDN、项目发布页及其他主机提供。

| 服务 | 用途及相关请求信息 |
| --- | --- |
| Microsoft、Xbox、Minecraft | 账号授权、会话刷新、玩家档案及游戏访问相关请求 |
| Modrinth、CurseForge | 搜索整合包/模组、版本与依赖查询、文件识别和下载；涉及搜索词、筛选条件、项目/版本/文件 ID，以及部分用于匹配的文件摘要 |
| Minecraft/Mojang、Prism 元数据、Fabric、Quilt 及清单中的下载源 | 获取 Minecraft 版本、加载器、运行库、资源和完整性元数据，下载所选实例的运行依赖 |
| Adoptium / Eclipse Temurin 及其文件下载源 | 查询匹配的 Java 版本、操作系统和架构，下载 Java |
| GitHub、GTNH 国内镜像及项目下载源 | GTNH 整合包、可添加模组、资源包等的发布信息查询和下载；当前镜像下载使用 `downloads.gaytnh.com` |
| 接口提供的图片地址 | 显示项目图标等内容，并在本机缓存 |

这些服务在收到请求时可获得 IP 地址、请求路径和请求头等网络信息。第三方如何处理和保留请求信息，取决于其自身政策，不由 EML 保证。请在使用相关服务前查阅其官方网站的隐私说明。

部分功能会在浏览器打开 Wiki、GitHub、GitLab、Discord、OptiFine 或其他项目页面。浏览器已有的账号、Cookie、扩展和授权适用浏览器及网站自己的设置；关闭 EML 不会清除它们。EML 启动的 Minecraft、模组及服务器也可能自行联网，其行为不属于这份启动器说明所能完整覆盖的范围。

## 使用统计、日志与主动反馈

本轮对当前源码及项目引用的检查未发现主动使用统计或自动上传崩溃报告的实现；这不是完整的网络流量审计，也不代表第三方登录、搜索、下载或游戏组件不会传输信息。需要正式发布时的验证仍应通过实际联网验收完成。

错误日志可能包含本机用户名、路径、服务器地址、游戏聊天或第三方模组输出。EML 不会自动把这些日志、存档或截图发送至维护者邮箱。请在主动联系时只发送排查必需的信息，并先去除令牌、凭证、私人路径及其他敏感内容。

用户主动发送到上述邮箱的内容会由邮件服务处理，并供维护者沟通和排查使用。如需讨论邮件或附件的删除，请通过同一邮箱联系。这里不承诺未经确认的固定保留期限，也不承诺能够删除第三方服务独立持有的记录。

## 用户选择与删除

- 可以不登录微软账号；离线模式的使用范围受游戏及服务器要求限制。
- 可以不使用在线搜索、镜像或 Java 下载；但缺少依赖的实例仍需要通过适当方式补齐文件。
- 本地设置、实例、缓存和备份通常保留在磁盘上，关闭程序不会自动清除。删除前应保留所需存档和来源记录；部分缓存原包可用于恢复包内文件。
- 在“微软账号”页面选择账号并确认“移除所选账号”，只删除该账号的本机登录记录，保留其他账号。实例记住的公开玩家名、UUID 和启动历史不会因此删除，也不会自动改成另一个账号。
- 同一 Windows 用户下的 EML 副本共用上述凭证文件。完整清除需逐个移除账号，或关闭全部 EML 后按卸载说明删除整份凭证文件；后者会清除该 Windows 用户所有 EML 副本共用的账号记录。移除不会撤销浏览器登录、微软侧的应用授权，或保证已启动的游戏会话立即失效。
- 需要完整移除程序及凭证时，参见[卸载与保留玩家数据](docs/uninstall.md)。不要把删除程序和删除存档视为同一选择。

隐私问题可联系 **K4ssia / 477619464@qq.com**；疑似漏洞请按[安全报告说明](SECURITY.md)私密报告。本文说明当前实现，不是合规认证或绝对安全承诺。数据处理方式改变时应同步更新本文。

## English summary

EML is maintained by **K4ssia**. Contact **477619464@qq.com** for privacy questions or private security reports. There is currently no maintainer-operated data collection server, donation service or separate feedback platform.

Instances, worlds, settings, file provenance, migration backups, caches and downloaded Java runtimes are stored locally. Per-instance launch settings remember the public player name, UUID, mode and last game-process start time, without login tokens. Multiple Microsoft profiles, their refresh tokens and the default account choice are stored together at `%LOCALAPPDATA%\EML\accounts\microsoft.dpapi`, protected with Windows current-user DPAPI. EML does not save your Microsoft password in that file. Encryption does not isolate account data from every program running as the same Windows user.

Folder imports read selected game files and supported launcher instance metadata, not launcher account records. Launcher credentials, application databases and launch commands are excluded from the copied game content. Private information stored by individual mods may still be present; only import instances you trust.

Sign-in uses Microsoft, Xbox and Minecraft services. Search and download features contact Modrinth, CurseForge, Minecraft runtime and loader services, Adoptium, GitHub, GTNH mirrors and project-specified hosts. Requests can include search filters, project/file IDs and file hashes used for matching. Recipients also receive ordinary network request information. Their retention practices, browser sessions and network activity of Minecraft/mods are outside EML's control.

The current source review found no active usage-analytics or automatic crash-upload implementation; this is not a complete traffic audit. Feedback is sent only when you choose to contact the maintainer. Redact logs and never send credentials or full private instances. Contact the same mailbox to discuss removal of information you have emailed.

Removing a selected account removes only that account's local login record. Other accounts and the public identity remembered by instances remain. All EML copies under the same Windows user share the credential file; deleting the whole file after closing EML clears all their saved accounts. Neither action revokes browser sessions or Microsoft-side consent. Back up player data before removing the portable directory. See the [uninstallation guide](docs/uninstall.md) and [security policy](SECURITY.md).
