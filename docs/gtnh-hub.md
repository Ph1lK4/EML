# GTNH 专区

左侧选择“GTNH 专区”，再在右上角选择目标实例。专区分为推荐模组和数据迁移，选择只作用于启动器已经发现的本地实例，不扫描整台电脑。GTNH 身份沿用现有内容标识识别，实例名称里写着 GTNH 并不足以通过识别。

## 推荐模组

页面保留用户提供的 [GTNH 中文 Wiki 可添加 MOD 指南](https://gtnh.huijiwiki.com/wiki/可添加MOD) 入口。首批目录按原作者仓库和发行说明核实。此次自动访问 Wiki 遇到访问限制，未逐项核对完整 Wiki 正文，目录不代表该页面的全部推荐项目。

| 项目 | 原作者发行仓库 | 用途与注意事项 |
| --- | --- | --- |
| WorldEdit GTNH | [GTNewHorizons/worldedit-gtnh](https://github.com/GTNewHorizons/worldedit-gtnh) | 1.7.10 GTNH 分支；编辑建筑，多人游戏需服务端支持与权限 |
| 拼音搜索 Rework | [asdflj/NeverEnoughCharacters-Rework](https://github.com/asdflj/NeverEnoughCharacters-Rework) | GTNH 的 NEI 拼音搜索；注意旧版或重复搜索模组 |
| 可编程仓室 | [reobf/Programmable-Hatches-Mod](https://github.com/reobf/Programmable-Hatches-Mod) | GregTech/AE2 自动化附属；按作者版本表选择，不同 GTNH Beta/RC 也可能需要不同版本 |
| 鼠标侧键修复 | [asdflj/MouseSideButtonFix](https://github.com/asdflj/MouseSideButtonFix) | 终端侧键问题修复；没有官方摘要的旧发行仅展示说明与作者链接 |

选择项目后，页面读取该仓库的发行列表。用户必须选择具体发行文件，并确认已核对 GTNH、Java、前置依赖及服务端要求；页面不会自动选中最新版本，也不把同为 Minecraft 1.7.10 当成兼容证明。第一版不自动安装这些项目的前置依赖，不保证所有发行文件都适用于当前 GTNH。

下载直接来自原作者 GitHub Release。列表保留正常 JAR，排除 sources、javadoc、dev、api、deobf 等开发文件。只有具备官方 SHA-256 和大小的发行资产才允许自动安装。没有摘要、限流或网络超时会明确提示，不降级校验。读取每个项目最多 120 个发行版，缓存两分钟；更早版本可从作者页查看。选择在 30 分钟后或执行一次后失效，重试需刷新发行版本。

安装先写入启动器的临时目录，校验后再通过现有模组管理事务登记到所选实例的 mods。原包文件、相同 modid 或已管理路径发生冲突时会拒绝提交。成功代表所选文件已校验并安装，不代表已完成依赖检查或实际游戏兼容测试。

切换页面不会停止已确认的安装；点击取消或关闭启动器会取消并等待清理。安装期间目标选择、启动、迁移等实例写操作共用忙碌状态。切换实例后的旧网络响应不能更新新目标。

## 数据迁移

上方选择接收数据的新版 GTNH，点击“从旧 GTNH 迁入数据”，在原有对话框中选择旧实例、迁移范围与冲突策略。请保留旧实例，先补全并试运行新版，再迁入存档、地图、服务器记录等数据。只迁移语言偏好，不复制旧版完整汉化包。额外模组仍需单独勾选并接受兼容风险。

“恢复上次迁移”沿用原有事务记录和备份。如果实例的 GTNH 标识已经无法识别，但仍有迁移恢复记录，列表会标为“待恢复”，保留恢复操作而禁止推荐模组下载。

## 代码与来源记录

- `EML.Core/Minecraft/Gtnh/GtnhAddonModels.cs`：推荐项目、固定发行资产和服务接口。
- `EML.Infrastructure/Minecraft/Gtnh/GtnhAddonCatalog.cs`：人工核对的仓库及中文适配提示。
- `EML.Infrastructure/Minecraft/Gtnh/GtnhAddonService.cs`：GitHub 发行读取、固定实例快照、下载和提交。
- `EML.App/ViewModels/GtnhHubViewModel.cs` 与 `Views/GtnhHubView.xaml`：目标选择、两个子页和操作状态。

新增 `GithubReleaseDownload` 内容来源，保留仓库、标签、release ID、asset ID、文件名，以及官方大小和 SHA-256。模组管理、检查补全、GTNH 额外模组迁移和备份往返使用同一份来源要求，不伪装成本地导入、不混入其他平台哈希。枚举追加且新字段为空时不序列化，旧清单与指纹继续兼容。

新增项目时先核实仓库实际支持 GTNH，再添加唯一 ID、中文说明与适配限制；不要直接收录一个已经转向现代 Minecraft 的同名仓库。仓库和发行信息可能变化，维护目录时应重新核实作者说明。

测试使用临时 GTNH 实例与受控 HTTP，覆盖固定目标、损坏下载、原包保护、来源保存、补全、迁移、备份往返，以及页面切换和取消。WPF 测试加载生产 XAML 并离屏渲染两个页面，不启动真实游戏。
