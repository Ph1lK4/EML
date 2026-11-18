# 卸载 EML 与保留玩家数据

更新日期：2026-11-18。以下面向便携 ZIP 发布版；源码调试版本的实例可能位于解决方案根目录，操作前先核对实际路径。

**EML 文件夹不只是程序，也可能包含你的存档、地图和备份。删除整个文件夹会一并删除其中的数据。** 升级不需要先卸载，升级请使用[发布与升级说明](release-rc1.md)。

## 1. 先确认要保留的数据

正常退出 Minecraft，结束 EML 的下载、导入、迁移等任务，再退出 EML。把需要保留的内容复制到 EML 目录之外，核对副本可以读取后再继续。最稳妥的备份是整个 EML 文件夹，包含隐藏目录；不要只保留一个导出的实例 ZIP。

| 位置（相对于 EML.App.exe） | 内容与注意事项 |
| --- | --- |
| `instances/` | 实例、存档、地图、服务器列表、截图、模组和游戏设置 |
| `instances/<实例>/.eml-runtime/` | 启动设置、文件来源与用户替换记录、模组管理记录、迁移备份；不是可随意清除的普通缓存 |
| `settings/` | 下载并发、限速等启动器设置 |
| `runtimes/java/` | EML 下载的 Java；实例启动设置可能引用它的绝对路径 |
| `cache/` | 下载缓存、断点文件和整合包原包；部分原包用于恢复 Embedded 文件，建议保留 |
| 根目录 `.eml-runtime/`、`.eml-imports/` | 运行状态、缓存与导入工作数据；备份时保留，不在仍有任务运行时手动清理 |

如果存档、Java、原包或备份位于其他位置，需要分别处理；不要因为卸载 EML 就删除它们。移动备份后，部分保存的绝对路径可能需要重新选择。

## 2. 按需清除本机登录凭证

微软账号不在便携目录内。当前 EML 使用的凭证文件是：

```text
%LOCALAPPDATA%\EML\accounts\microsoft.dpapi
```

- 如需移除一个账号，在删除程序前打开 EML 的“微软账号”页面，选中该账号并确认“移除所选账号”。这只清除该账号，不清除其他已保存账号。需要全部清除时逐个移除，确认列表为空后关闭 EML。
- 如果 EML 已无法运行，先关闭游戏及所有 EML 进程，再通过资源管理器核对并仅删除上述 `microsoft.dpapi` 文件。它保存整个账号集合，删除会清除所有已保存账号；不要删除整个 `%LOCALAPPDATA%` 或其他启动器的数据目录。
- 同一 Windows 用户下的其他 EML 副本也使用这个账号位置；删除整份文件后，它们都需要重新登录。实例内保存的公开玩家名、UUID 和上次启动记录仍随实例保留，不包含登录令牌。
- 移除 EML 账号只清除 EML 的本机凭证，不会退出浏览器中的微软账号、撤销微软侧的应用授权，或让已经运行的游戏会话立即失效。需要撤销授权时，请在微软账号的官方管理页面处理。

不要把凭证文件作为普通跨电脑迁移资料发送给他人。它使用当前 Windows 用户的 DPAPI 保护，不能保证在另一台电脑或另一个用户下可用。

## 3. 删除程序

在程序已关闭、数据备份已确认后，才删除你确认不再需要的 EML 解压目录及自己创建的快捷方式。

- 想保留玩家数据：先确保上述数据已在目录外完整备份；也可以暂不删除旧目录，待新安装核对成功后再处理。
- 想一并删除玩家数据：明确确认不再需要其中的存档、迁移备份和截图，再删除对应目录。删除结果取决于所用工具与回收站设置，不能保证恢复。
- 此步骤不会自动清除单独存放的账号文件、外部原包、其他位置的备份或浏览器授权。
- 不要卸载机器上由其他软件使用的 Java 或 .NET。EML 的自包含 .NET 在发布目录内；EML 下载的 Java 在上述 `runtimes/java/` 内，其他安装的 Java 不属于这份目录。

需要帮助可联系维护者 **K4ssia**：**[477619464@qq.com](mailto:477619464@qq.com)**，请勿附带账号文件或完整私人日志。更多数据处理说明见[隐私说明](../PRIVACY.md)。

## English summary

The portable EML folder may contain worlds, maps and migration backups, not just application files. Exit Minecraft and EML, then make and verify a full backup outside that folder, including hidden `.eml-runtime` directories, before deleting anything. An exported instance ZIP is not a substitute for the full backup.

To remove one account, select it on EML's Microsoft account page and confirm removal; other accounts remain. Repeat for every account if you want to clear the list. If EML cannot run, close all EML/game processes and remove only `%LOCALAPPDATA%\EML\accounts\microsoft.dpapi` after verifying the path. Deleting this file clears the entire account collection shared by all EML copies under that Windows user. Public names, UUIDs and launch history remain in instance settings, without tokens. Removing credentials does not revoke browser sessions or Microsoft-side authorization.

Remove the portable folder only after deciding what data to keep. Do not uninstall unrelated Java/.NET installations. See the [upgrade guide](release-rc1.md) if you are updating rather than uninstalling.
