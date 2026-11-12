# GTNH 推荐内容下载核查（2026-11-12）

本次检查区分“项目已收录”“发行文件可下载并验证”和“与某个 GTNH 版本实测兼容”。前两项通过目录及平台元数据检查；资源包装入实例并不代表其视觉效果或模组组合已经在游戏内测试。

核查重点是资源包仓库和正式资产，另核对了 43 个模组及加载器仓库，其中 30 个只检查最近五次发行；另有 29 个仓库尚未逐项读取发行列表。GitHub 公共 API 额度不足以继续完成剩余请求，因此这里不是整个目录全部历史版本的可安装性认证。仅下文列出的三个 Modernity 文件执行了真实下载和安装；其他结论基于平台元数据。CurseForge、Modrinth 项目继续使用现有平台验证流程，本次没有逐个重新下载。

## Modernity

| 页面入口 | 固定仓库 | 本次处理 |
| --- | --- | --- |
| Modernity GTNH · 主资源包 | `ModernityGTNH/Modernity-GTNH` | 取代组织主页，只列出主包 ZIP，排除附加包、UI 和 JAR |
| Modernity GTNH · 浅色界面 | `ModernityGTNH/Modernity-GTNH-UI` | 新增独立下载入口 |
| Modernity GTNH · 深色界面 | `ModernityGTNH/Modernity-GTNH-Dark-UI` | 保留原入口，限定对应界面资产 |

使用 2026-11-10 发行中的三个官方资产进行真实下载验证：`Modernity-29x-1650c31.zip`、`Modernity-GTNH-UI-2026-11-10.zip`、`Modernity-GTNH-Dark-UI-2026-11-10.zip`。共约 71.7 MiB，均通过 GitHub 公布的大小及 SHA-256 校验、根目录 `pack.mcmeta` 检查，并通过生产注册器安装到隔离测试实例。未启动游戏，未修改玩家现有实例。

历史界面资产 `default.zip` 使用固定本地别名避免重名。原始文件字节、官方文件名、下载地址、release ID、asset ID 及摘要保持不变；本地实际目标随安装结果返回，通知可复制正确路径。

## 其他已确认的问题

| 项目 | 发现 | 处理 |
| --- | --- | --- |
| MyCTMLib | `ABKQPO/MyCTMLib` 官方 API 重定向至 `GTNotLeisure/MyCTMLib`，repository ID 为 `1001802914` | 更新到已核实的新仓库，并排除 `-predowngrade.jar` 构建中间文件 |
| Transparent UI | `S4mpsa/TransparentUI` 官方 API 重定向至 `S4mpsa/Glass-UI`，repository ID 为 `649640902` | 保留原目录 ID，显示 Glass UI 并使用新仓库 |
| Outlined Ores Modern | `Ranzuu/OutlinedOres-Modern` 已迁入 `Ranzuu/GTNH-OutlinedOres`，repository ID 为 `1013085320` | 保留两个原目录 ID，分别筛选 Modernity 纹理与普通纹理，避免两项下载同一组文件 |
| Shadow UI | 同仓库发行页还包含隐藏治愈之斧、血条及特定效果小包 | 主入口只接受 `Shadow.UI.` 前缀，隐藏治愈之斧仍限定到原指定标签和文件 |
| Mineshot | `ABKQPO/mineshot` 迁至 `GTNotLeisure/mineshot`，repository ID 为 `1079303147` | 使用新仓库，保留原目录 ID |
| GT Not Leisure | `ABKQPO/GT-Not-Leisure` 迁至 `GTNotLeisure/GT-Not-Leisure`，repository ID 为 `880690432` | 使用新仓库，保留原目录 ID |
| Cheaper Void Miners | `Jonodonozym/CheaperVoidMiners` 迁至 `Tungsten-W-74/CheaperVoidMiners`，repository ID 为 `873275821` | 使用新仓库，保留原目录 ID |
| BetterLineBreak | 最新发行文件标注 Minecraft 1.12.2 Forge，不能用于确认 GTNH 1.7.10 适配 | 入口只接收文件名明确标注 1.7.10 的 JAR，不自动采用其他 Minecraft 版本 |

运行时仍拒绝未经核实的仓库跳转，不会根据名称自动选择其他作者的同名资源。旧实例已经登记的文件来源不会被目录更新改写。

## 暂不能承诺全部直接安装

| 项目 | 本次核对到的限制 |
| --- | --- |
| Usernm 机器纹理（`machines` 标签） | 指定 ZIP 未提供 GitHub SHA-256 |
| Usernm GTNH Textures | 本次列表中的 16 个 ZIP 均未提供 GitHub SHA-256 |
| JSTR Alk Modded 1.7.10 | 两个发行 ZIP 未提供 GitHub SHA-256 |
| Wily-bit's GTNH Textures | 较老的两个发行 ZIP 未提供 GitHub SHA-256；较新的 0.0.3/0.0.4 为 RAR，也没有摘要，不能直接放入 resourcepacks |
| UU-Tex | GitHub 返回空发行列表；不能将源码归档直接当作资源包 |
| MouseSideButtonFix、Applied Cooking | 各自唯一发行 JAR 没有 GitHub SHA-256 |
| OmniOcular、LagGogglesLegacy | 本次检查的最近五个 JAR 均没有 GitHub SHA-256 |
| BetterLineBreak | 本次检查的最近五个 JAR 均没有 GitHub SHA-256；仅首个文件的 1.12.2 版本已核实，不推断其余文件适配版本 |
| Discord 社区资源包 | 按用户原先要求保留原帖入口，由用户自行到频道下载 |
| Stellar Fusion、Better Rain、OptiFine | 分别保留历史页面、旧论坛、官方下载页入口；尚无已接入且通过现有校验规则的固定发行资产 |
| Ivelitex | GitLab 的两个发行没有正式资产链接，正文上传 ZIP 无官方大小及摘要；当前保留作者入口 |

没有官方摘要的文件不会在本次修改中被降级为“本地文件”来绕过校验。后续如作者补发带摘要的发行资产，会在重新读取版本后参与现有校验；源码仓库、外部附件和需要登录的下载需要分别接入并核对，不能仅把链接改成下载按钮。
