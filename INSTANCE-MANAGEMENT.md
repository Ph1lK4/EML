# 实例管理

在“本地实例”选择实例后，可修改名称、删除或导出 ZIP。操作前请关闭游戏；当前没有检测游戏进程，也不能保证运行中的存档一致性。

- 重命名：修改实例文件夹名称；Prism 实例同时更新 instance.cfg 的 General/name。不会修改版本 ID 或版本 JSON。旧路径引用需要重新选择。
- 删除：显示名称与完整路径确认，默认取消。请求 Windows 回收站；系统可能因磁盘或回收站限制提示永久删除，恢复能力以系统实际处理为准。
- 导出：完整备份实例内文件，包括存档、配置、隐藏文件和空目录；ZIP 根层直接放置实例内容。进度按原始文件字节计算，可取消，不覆盖已有 ZIP。失败/取消会尝试清理临时文件。
- Prism 导出保留 mmc-pack.json 和 instance.cfg，但不包含实例外部的共享 Java、libraries 或 assets；不是保证离线可启动的分发包，也不是 CurseForge/Modrinth 格式转换。

新文件分工：

- EML.Core/Minecraft/IMinecraftInstanceManager.cs：管理接口和导出进度类型。
- EML.Infrastructure/Minecraft/MinecraftInstanceManager.cs：目录校验、重命名、Windows 回收与 ZIP 导出。检测仍由原 LauncherRootMinecraftInstanceLocator 负责。
- EML.App/Services/IInstanceDialogs.cs 与 InstanceDialogs.cs：确认和保存位置窗口。
- EML.Infrastructure/Properties/AssemblyInfo.cs：允许测试注入回收替身。
- EML.Tests/MinecraftInstanceManagerTests.cs：使用临时实例进行测试，不操作真实测试包。

修改 InstancesViewModel、MainWindow.xaml 与 MainWindow.xaml.cs，接入命令、按钮、进度和服务。操作期间禁用其他实例操作。支持范围为启动器 instances 下的直接子实例，拒绝符号链接/目录联接。Debug 仍扫描项目根目录，Release 扫描 EXE 旁的 instances。
