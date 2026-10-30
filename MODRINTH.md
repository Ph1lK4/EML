# Modrinth 整合包补全与 API

导入 `.mrpack` 后自动补全必需的模组、资源包和运行依赖。已导入的实例可在“本地实例 → 补全实例依赖”继续；取消后已完成文件保留，下次校验并复用。无需 Modrinth API token。

## 安装流程与范围

1. 导入器读取 `modrinth.index.json`，保留固定 Minecraft / 加载器版本、每个文件的地址、大小及 SHA1/SHA512。展开 `overrides` 和 `client-overrides`。
2. `ModrinthInstanceInstaller` 默认四路下载必需文件，下载后校验清单哈希。客户端不支持的文件不导入；可选文件暂不自动下载。
3. `ModrinthRuntimeResolver` 从本地缓存解析官方 Minecraft 元数据与固定 Fabric/Quilt 配置。缺少缓存时返回下载需求，解析器本身不联网。
4. 共用运行安装管线下载客户端、运行库、日志配置、资源索引与资源对象，整理 native 文件和生成启动描述。
5. 完整复查导入文件、运行依赖、资源和生成文件。只有所需文件及配置已准备好才显示准备完成，实际进入游戏仍由启动模块验证。

支持原版、Fabric、Quilt。Forge/NeoForge 的安装处理器尚未实现，会明确显示阻断原因，不能声称所有 Modrinth 整合包都已支持。当前桌面测试包为 **Fresh & Smooth 26.3 (test) 2.9.7**，声明 Minecraft 26.3、Fabric Loader 0.19.5，需 Java 25；运行版本始终按包内固定声明选择。

已有下载文件哈希不匹配时保留原文件并报告，不自动覆盖。包内文件缺失时需要原包恢复。已存在的 `config/`、`defaultconfigs/` 与已知用户选项文件允许用户修改，安装器保留并提示，完整检查仍显示“导入后已变化”。其它包内文件继续严格校验。所有路径限制在指定实例内，不删除未在清单中声明的额外模组。

## 文件结构

```text
src/EML.Core/
  Modrinth/                         API 接口、筛选参数及返回模型
  Minecraft/IModrinthInstanceInstaller.cs
src/EML.Infrastructure/Minecraft/
  Modrinth/
    ModrinthInstanceInstaller.cs    必需模组/资源文件与运行阶段协调
    ModrinthInstallOptions.cs       文件并发设置，默认 4，范围 1–16
    ModrinthRuntimeResolver.cs      固定游戏和加载器版本解析
    Api/
      ModrinthApiClient.cs          搜索、项目详情及版本查询
      ModrinthApiOptions.cs         API 地址、User-Agent、超时和 JSON 上限
      ModrinthApiException.cs       HTTP 状态及限流等待信息
      ModrinthVersionDownloadService.cs  版本文件选择及通用下载请求
  Runtime/InstanceRuntimeInstaller.cs   共用运行文件安装步骤
  Prism/                           保留 Prism 入口及已有整理/状态工具
```

为兼容已有 Prism 调用，公共运行计划及结果暂沿用 `PrismRuntimePlan` / `PrismInstallResult` 名称，由 `SourceFormat` 区分来源。Modrinth 解析和 API 不依赖 Prism 的组件元数据服务。

实例内的独立资源：

```text
实例根目录/
  eml-instance.json                 原导入清单，不因补全重写
  .eml-runtime/modrinth/
    metadata/                      Minecraft 和加载器缓存
    state.json                     生成文件及运行配置指纹
  <游戏目录>/
    mods/、resourcepacks/          按包清单下载
    libraries/、assets/、natives/  运行资源
    versions/EML-Modrinth-<指纹>/  版本 JSON、客户端 JAR、.eml-launch.json
```

## 启动模块对接

读取生成的 `.eml-launch.json`，把其中相对实例根目录的路径转成绝对路径。保留 `jvmArguments` 和 `gameArguments` 的每个参数边界，通过 `ProcessStartInfo.ArgumentList` 启动 Java。

除了原有账号、classpath、native、游戏目录与资源占位符，若有 `loggingPath`，还须把 `${logging_path}` 替换为该文件的绝对路径。Minecraft 26.3 的 `-Djava.library.path=${natives_directory}/java` 应保留 `/java` 后缀；Fabric 的 `-DFabricMcEmu= net.minecraft.client.main.Main ` 是包含空格的单个参数，不可拆开或 Trim。

标准版本 JSON 的 `logging.client` 保存官方日志声明；其中的 `emlLoggingPath` 等扩展供 EML 使用。旧 Prism 无日志计划的生成结构保持兼容。内存设置交由启动模块，官方 `default-user-jvm` 的可选调优不会覆盖用户配置。

## 为后续搜索和下载准备的接口

- `IModrinthApiClient.SearchAsync`：关键词、项目类型（mod/modpack/resourcepack/shader）、游戏版本、加载器、分类、环境、额外 facet 分组、排序及分页。
- `GetProjectAsync` / `GetVersionsAsync` / `GetVersionAsync`：保留固定 ID、文件大小、全部哈希、下载 URL、依赖类型及版本引用。
- `ModrinthVersionDownloadService`：把选定版本文件转换为通用下载任务。唯一 primary 文件可直接选择；多文件且默认项不明确时要求明确文件名。此服务不自动安装依赖或导入包。
- API 使用可识别应用的 User-Agent、独立请求超时、JSON 大小限制、取消令牌与限流响应信息。公开读取不依赖私人 token。

本次提供服务层，尚未加入搜索/筛选界面或可选模组选择界面。Java 自动安装、模组首次启动自行获取的额外资源、从原包恢复缺失文件仍是后续功能。

## 验证方式

在解决方案目录执行 `dotnet build EML.slnx -c Release` 和 `dotnet test tests/EML.Tests/EML.Tests.csproj -c Release`。自动化测试使用临时目录和假传输，覆盖 API、加载器规则、安装取消/失败、文件校验、日志/native 参数及 Prism 回归；不会启动游戏。

2026-10-29 实测：Release 构建 0 警告、0 错误，353 项测试全部通过。桌面真实包经生产导入器及下载器在隔离目录完成：179 个清单下载文件全部通过校验，补齐 84 项运行文件和 5,147 个资源对象，生成 Minecraft 26.3 / Fabric 0.19.5 配置。独立完整检查共 6,997 条：6,996 条哈希通过，1 条 Fabric Loader 类库因官方配置没有声明哈希标为“存在，未校验内容”；无缺失、不匹配或运行阻断项。间歇性 TLS 连接失败后重新补全，已完成下载得到复用。此验证没有启动游戏。

同日通过生产 API 客户端实际验证了整合包搜索、Minecraft 版本和 Fabric 筛选、项目详情、版本列表及固定版本详情，不需账户凭据。
