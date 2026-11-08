# EML Launcher

EML 是一款面向中国 Minecraft 玩家开发的 Windows 桌面启动器，专注于整合包的导入、依赖补全、启动和本地实例管理。

我们希望降低游玩整合包的门槛，让不熟悉电脑操作的玩家也能更方便地体验来自不同平台的整合包，并逐步提供直观的模组管理、参数调整和玩家数据迁移功能。

项目目前处于开发阶段，界面与兼容性仍在完善。

## 当前功能

- **Java 检测**：启动时自动检测本机 Java，支持手动选择，以及根据实例要求自动匹配已安装的 Java。
- **本地实例管理**：识别启动器根目录下 `instances/` 中的实例，展示名称、Minecraft 版本、加载器、实例路径和游戏目录，支持重命名、删除及 ZIP 导出备份。
- **多格式导入**：支持 Prism ZIP、Modrinth `.mrpack`、CurseForge ZIP 和通用游戏目录 ZIP，导入后自动开始依赖补全。
- **下载与补全**：根据整合包清单下载所需模组和受支持的运行依赖；部分 CurseForge 文件无法直接下载时，尝试从 Modrinth 获取哈希与大小一致的文件，无法匹配则提示处理。
- **文件完整性检查**：提供快速检查和完整哈希校验，区分不同文件来源、用户明确接受的替换及允许正常变化的玩家数据。
- **游戏启动**：按实例配置玩家名称、Java、内存和附加 JVM 参数，查看运行日志；日常启动采用必要的运行准备检查，不重复执行整包哈希检查。
- **GTNH 玩家数据迁移**：在旧版与新版 GTNH 客户端实例之间迁移存档、地图记录、个人设置和语言偏好，支持预览、备份、恢复，以及可选迁移玩家额外添加的模组。

微软正版登录流程已经接入，当前仍在完成 Minecraft API 准入和实际登录验证，暂不作为已可用功能。配置与进展见 [微软账号登录说明](docs/microsoft-login.md)。

## 使用方式

1. 启动 EML，等待 Java 和本地实例检测完成。
2. 点击“导入实例”，选择整合包文件，等待导入及自动补全结束。
3. 查看补全结果；若有需要手动处理的文件，按提示处理后再次检查。
4. 在“启动设置”中选择 Java、设置内存与 JVM 参数，然后点击“启动游戏”。

开发调试时，实例默认保存在解决方案根目录的 `instances/`；发布版本使用启动器 EXE 旁的 `instances/`。

目前的兼容范围取决于整合包提供的元数据和已实现的加载器支持，并非所有 ZIP 或整合包都能直接补全运行。实例导出不包含实例目录之外的共享依赖。GTNH 迁移仅支持客户端，不负责服务端迁移或新版汉化包安装；迁移前请保留旧实例，额外模组需要自行确认与新版的兼容性。

## 技术栈与项目结构

- C# / .NET 10
- WPF + CommunityToolkit.Mvvm
- xUnit 自动化测试

```text
EML/
├── src/
│   ├── EML.App/              # 桌面界面、ViewModel 和交互
│   ├── EML.Core/             # 领域模型、接口和共享规则
│   ├── EML.Infrastructure/   # Java 检测、实例管理、下载、启动与账号实现
│   └── EML.Build.Secrets/    # 构建时的授权配置生成器
├── tests/EML.Tests/          # 自动化测试
├── docs/                    # 功能说明与开发文档
├── tools/                   # 开发与构建辅助脚本
├── instances/               # 本地实例，不提交到仓库
└── EML.slnx                 # 解决方案
```

## 从源码运行

在 Windows 上安装 .NET 10 SDK，在项目根目录执行：

```powershell
dotnet restore EML.slnx
dotnet build EML.slnx
dotnet run --project src/EML.App/EML.App.csproj
```

也可以使用支持 .NET 10 的 Visual Studio 打开 `EML.slnx`，将 `EML.App` 设为启动项目运行。

执行测试：

```powershell
dotnet test EML.slnx
```

游戏运行还需要适配整合包的 Java，当前的 Java 检测不会自动安装 Java。从源码构建时，CurseForge 下载需要开发者配置相应授权，可参考 `tools/Configure-CurseForge.ps1` 和 `tools/Build-CurseForge.ps1`；微软登录配置见下方文档。

## 后续方向

- 完成微软正版登录的准入与实际验证。
- 提供整合包和模组的在线搜索、筛选及下载入口。
- 完善玩家自行添加、移除模组的管理体验。
- 持续优化界面、下载体验和不同整合包的兼容性。

## 相关文档

- [实例启动与参数设置](docs/launching.md)
- [GTNH 玩家数据迁移](docs/gtnh-player-data-migration.md)
- [文件完整性与游玩后的变化](docs/gameplay-file-policy.md)
- [微软账号登录与开发者配置](docs/microsoft-login.md)

欢迎反馈使用中遇到的问题或参与开发。反馈时请尽量提供整合包名称与版本、Java 版本、复现步骤和相关错误信息。
