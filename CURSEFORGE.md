# CurseForge 整合包下载与补全

采用与 PCL-CE 类似的构建方式：维护者构建时注入自己的 CurseForge API Key，启动器直接请求官方 API 和文件 CDN。玩家无需申请、输入或保存 Key，不需要额外部署转发服务器。本实现独立编写，没有复制 PCL-CE 的源码。

Key 会存在于发布的程序中；构建时注入可以避免公开仓库或明文配置暴露，但不能保证专业用户无法从程序中提取。没有另行承诺加密、混淆或不可逆保护。

## 维护者如何构建

在仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-CurseForge.ps1
```

脚本会要求输入开发者 Key，输入不回显。Key 只传给本次构建进程，不放入命令行参数或明文配置；退出时恢复环境。默认构建 Release，程序位于 `src/EML.App/bin/Release/net10.0-windows/EML.App.exe`。

需要同时输出发布目录时：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-CurseForge.ps1 -Publish
```

结果在 `artifacts/EML-CurseForge/`。当前为需要 .NET 10 Windows Desktop Runtime 的普通发布目录，不是单文件、自带运行时的发行包。

自动化构建可从 CI 的秘密变量向进程注入 `EML_CURSEFORGE_API_KEY`，再使用 `-UseEnvironmentKey`。不要把真实值写进工作流、脚本、命令行、诊断日志或 binlog。脚本开启 `EML_EMBED_CURSEFORGE_KEY=1`、强制重新编译、关闭共享编译器、生成源码落盘及调试符号；源生成器只读取这一个指定 Key，不收集其它环境变量。

普通 `dotnet build` 不需要 Key，也不会在运行时读取本机凭据或 Key 环境变量。无授权构建仍可使用 Java 检测、实例管理、Prism/Modrinth 和离线文件检查；请求 CurseForge 时会明确提示此构建未包含授权。切换有授权/无授权构建或更换 Key 时应重新生成解决方案，不应复用旧的增量产物。使用带 Key 的发布程序后再次普通重新生成，会得到不含 Key 的程序，这是预期行为。

## 请求与下载

1. 读取导入清单中的固定 `projectID` / `fileID`，最多 50 个一批获取文件和项目信息。核实 Minecraft `gameId=432`、文件归属和返回 ID，不使用最新版替换清单版本。
2. 根据项目类别放置文件：6 → `mods/`，12 → `resourcepacks/`，6552 → `shaderpacks/`。6945 数据包只有在清单同时声明必需的 Paxi 模组及包内 `config/paxi/datapack_load_order.json` 时，才放入 `config/paxi/datapacks/`。其它数据包布局和未知类别明确提示，避免凭扩展名猜位置。
3. 采用官方返回的地址、大小和 SHA-1，默认四路并发下载。临时文件校验通过后才发布；已有文件不匹配时保留原文件并提示，不自动覆盖。
4. 必需内容齐全后，继续准备支持的 Minecraft、加载器、类库、资源与 native 文件，生成启动模块使用的 `.eml-launch.json`，最后再次完整校验。

`CurseForgeRequestHeaders` 统一负责附加 `x-api-key`。只对精确匹配的 HTTPS 默认端口官方地址签名：元数据 API `api.curseforge.com`，文件 CDN `edge.forgecdn.net` 和 `mediafilez.forgecdn.net`。Key 不放入 URL、实例清单、缓存、下载任务或公共 `DefaultRequestHeaders`。普通 Mojang、Prism、Modrinth 及其它域名请求不携带它；每次重定向都重新判断目标，避免把原请求鉴权转给其它站点。

本次没有接入第三方 CurseForge 镜像，也没有从 ID 拼接官方下载地址。官方 API 不提供地址、文件不可用或作者禁止第三方分发时，显示官方文件页面、文件名与安装位置；用户按提示手动取得文件后，再次补全会校验并继续。仅有 MD5、没有 SHA-1 的文件当前明确标为需处理。

可选模组暂时跳过并保留清单标记；不会擅自追加 API 依赖列表中的最新模组。失败或取消保留已完成文件，再次补全可继续。包内配置白名单允许玩家修改；其它包内文件仍按导入快照检查。

Paxi 目录识别依据作者的固定项目 ID：Fabric 为 418881，Forge/NeoForge 为 515708。若压缩包已经包含同路径的数据包，补全要求同时满足官方 SHA-1 和导入时包内哈希；验证一致后复用，发生冲突时保留原文件并提示。平台数据包不会因为位于 `config/` 下而被当作可随意修改的配置放行。

## 支持范围

完整运行文件准备覆盖原版、Fabric 和 Quilt。多个加载器声明中选择唯一主加载器；无法确定时明确阻断。

Forge/NeoForge 整合包的模组列表可以解析和下载，但安装处理器尚未接入，不会显示运行环境完整或退回原版冒充成功。世界包类别、恢复压缩包中缺失的内嵌文件、Java 自动安装和实际启动游戏不在本次实现中。

## 文件结构

```text
src/EML.Build.Secrets/                 构建时生成内存中的授权代码
src/EML.Core/CurseForge/               API 接口、搜索条件、项目与文件模型
src/EML.Core/Minecraft/               安装接口、配置变化白名单
src/EML.Infrastructure/Minecraft/CurseForge/
  Api/                               官方请求、内置授权、请求头处理
  CurseForgeFileResolver.cs           固定 ID → 可校验文件需求
  CurseForgeFileStateStore.cs         清单指纹与文件解析缓存
  CurseForgeInstanceInstaller.cs      文件下载、人工项与运行阶段协调
  CurseForgeRuntimeResolver.cs        独立的运行依赖解析入口
tools/Build-CurseForge.ps1            维护者交互/CI 构建入口
```

客户端不包含网关项目、Key 设置界面或 Windows 凭据存储。

实例中的 `.eml-runtime/curseforge/files.json` 保存文件解析结果，`metadata/` 保存运行元数据，`state.json` 保存生成配置基线。原 `eml-instance.json` 不改写。生成版本目录为 `versions/EML-CurseForge-<指纹>/`。缓存不包含 Key；旧服务端地址仅在文件缺失时重新解析，已存在文件仍可离线校验。

检查按钮只读文件和缓存，不联网、也不需要 Key。没有解析依据的文件保持“待解析”，不会声称完整。启动配置约定沿用 [MODRINTH.md](MODRINTH.md)；文件准备完成不等于实际启动通过。

## 后续搜索与下载

`ICurseForgeApiClient` 已包含批量项目/文件、固定文件和搜索入口。搜索条件保留项目类别、分类、Minecraft 版本、加载器、关键词、排序、偏移和页大小。模型包含依赖、哈希和分页，方便后续模组及整合包搜索页面复用；本次没有新增这些页面。

## 验证命令

```powershell
dotnet build EML.slnx -c Release
dotnet test tests/EML.Tests/EML.Tests.csproj -c Release --no-build
```

自动化测试使用假 HTTP、假 Key 和独立临时实例，不消耗真实 API 配额、不运行游戏。
