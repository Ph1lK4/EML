# GTNH-Launcher（C#）

适配 **GTNH（GregTech: New Horizons，MC 1.7.10 + Forge）** 的 Minecraft 启动器。
本仓库是**启动核心**的可编译实现：版本 JSON → 启动计划（`LaunchPlan`）→ 预检 → 起进程 → 收日志 → 崩溃诊断。

> 设计与分工见：`docs\GTNH启动器-C#-功能拆解与分工.md`

## 现状

| 项 | 状态 |
|---|---|
| 契约层 `Launcher.Contracts` | ✅ 零依赖，四人共用 |
| 版本元数据（JSON 模型 / rules / `inheritsFrom` 合并） | ✅ 含 1.7.10 真实 JSON |
| 启动计划（classpath / natives / JVM 参数 / 游戏参数） | ✅ 1.7.10 与 Forge 样例都能出命令 |
| 离线 UUID | ✅ 与 Java 版一致 |
| natives 解压 | ✅ 跳过 `extract.exclude`、防 zip slip |
| 实例布局 / 启动预检 | ✅ 缺哪些文件一次列全 |
| 进程管理（异步读流 / 取消杀进程树 / 退出码） | ✅ 没有 `InvalidOperationException` 那个经典坑 |
| 日志落盘 | ✅ `logs/launch-*.log` |
| 崩溃诊断（OOM / Java 版本 / 显卡 / natives / mod 冲突…） | ✅ 退出码 + 日志规则 |
| 下载引擎（BMCLAPI 镜像 / sha1 校验 / 并发 / 已存在跳过） | ✅ 1.7.10 实测 35 个文件 13s 下完 |
| Forge 安装（解包 installer，不跑 JVM） | ✅ 真实安装包实测：版本 id / JSON / universal jar 全对 |
| WPF 外壳（M6） | ✅ 实例 / 版本、Java 检测、安装 Forge、下载 / 修复、启动 / 停止、日志面板 |
| Prism / MultiMC 实例直连 | ✅ 只读复用现成实例：读 `patches/*.json` 合并出命令行，不下载不写盘；用户真实 GTNH 实例已真启动进主菜单；**WPF 也能直接选 Prism 实例目录启动** |
| 登录（正版 / 微软） | ⬜ 未实现（当前用离线 UUID） |

测试：自带的 mini 测试台（无 xunit / 无外部 NuGet 包），当前 **24 个用例全绿**。

## 快速开始

需要 .NET 8 SDK。

```bat
dotnet build GTNH-Launcher.sln
dotnet run --project src\Launcher.Cli
```

图形界面（WPF，Windows）：

```bat
dotnet run --project src\Launcher.Gui
:: 或直接跑
src\Launcher.Gui\bin\Debug\net8.0-windows\GTNH-Launcher.exe
```

界面里的顺序：填实例目录 → 刷新版本 → 点「安装 Forge…」选 installer jar →
「下载 / 修复文件」补齐 → 选 Java（自动检测，也可手填）→ 「启动游戏」。
运行中日志实时进右侧面板，崩了会给结论和建议。

输出示例（1.7.10，截断）：

```
版本       : 1.7.10（客户端 jar 取 1.7.10）
Java 需求  : jre-legacy / Java 8
主类       : net.minecraft.client.main.Main
classpath  : 30 项
natives    : 4 个 jar
--- 命令行（启动核心的最终产物）---
java -Xms1024M -Xmx6144M -Dlog4j.configurationFile=C:\...\assets\log_configs\client-1.7.xml
  -Djava.library.path=C:\...\.minecraft\natives -cp "C:\...\lwjgl-2.9.1.jar;..."
  net.minecraft.client.main.Main --username Steve --version 1.7.10 ... --userType legacy
```

Forge 样例（`inheritsFrom` 合并 → 主类变 `launchwrapper`，末尾追加 `--tweakClass`）：

```bat
dotnet run --project src\Launcher.Cli -- plan --forge data\forge-1.7.10.sample.json
```

> 注：`data\forge-1.7.10.sample.json` 是**手写样例**，只用来验证继承合并与库去重，
> 不是 Forge 官方原文。真实 Forge 版本 JSON 在安装后生成。

## 子命令

```bat
:: 拼命令行（不读实例，只看启动核心的产物）
dotnet run --project src\Launcher.Cli -- plan [版本 JSON] [--forge JSON] [--java path] [--player 名字] [--memory MB] [--instance 目录]

:: 预检 + 真启动（实例目录下要有 .minecraft）
dotnet run --project src\Launcher.Cli -- run --instance D:\gtnh --version 1.7.10-Forge10.13.4.1614 --dry-run

:: 用同一个进程层跑任意命令（调试流式输出 / 取消 / 退出码）
dotnet run --project src\Launcher.Cli -- exec --exe cmd.exe -- /d /c "echo hi"

:: 对一份日志跑崩溃诊断
dotnet run --project src\Launcher.Cli -- diagnose logs\latest.log 1

:: 按版本 JSON 把实例缺的文件补齐（--dry-run 只列清单）
dotnet run --project src\Launcher.Cli -- download --instance D:\gtnh --version 1.7.10 --threads 8

:: 装 Forge（解包 installer，不需要 JAVA；--base 缺省用内置的 1.7.10.json）
dotnet run --project src\Launcher.Cli -- forge install --instance D:\gtnh --installer forge-installer.jar

:: 只看看这个安装包是什么版本
dotnet run --project src\Launcher.Cli -- forge info --installer forge-installer.jar

:: Prism/MultiMC 实例：只读体检（不下载、不写盘），文件齐全返回 0，缺文件返回 2
dotnet run --project src\Launcher.Cli -- prism verify --instance "C:\Users\你\AppData\Roaming\PrismLauncher\instances\GT_New_Horizons_xxx"

:: 体检之后把完整命令行打出来（离线 UUID，可直接拿去启动）
dotnet run --project src\Launcher.Cli -- prism plan --instance "上边的目录" [--memory 6144] [--player 名字]
```

`run` 的返回码：`0` 正常退出，`2` 预检没通过（文件没下全），其他值 = 游戏/JVM 的退出码。

诊断覆盖的典型情况：`OutOfMemoryError`、`UnsupportedClassVersionError`（Java 版本不对）、
`EXCEPTION_ACCESS_VIOLATION`（显卡/驱动）、`no lwjgl in java.library.path`（natives 没解压）、
`NoSuchMethodError`（mod 冲突）、`Failed to create display`、以及 Windows 的 `0xC0000135`（缺 VC++ 运行库）。

## 结构

```
src/Launcher.Contracts    契约（DTO + 接口），不得引用其他工程
src/Launcher.Metadata     版本 JSON、rules、合并、参数、natives、离线 UUID
src/Launcher.Runtime      实例布局、启动预检、进程管理、日志、崩溃诊断
src/Launcher.Download     下载计划、资产索引、镜像、并发下载 + sha1 校验
src/Launcher.Forge        Forge 安装器解包（版本 JSON + universal jar）
src/Launcher.Prism        Prism/MultiMC 实例：读 patches、合并、拼出可启动命令行
src/Launcher.Cli          演示程序：plan / run / exec / download / forge / prism / diagnose
src/Launcher.Gui          WPF 外壳（M6）：实例 / Java / 下载 / Forge / 启动 / 日志
tests/Launcher.Metadata.Tests  自带测试台（退出码非 0 = 有失败）
data/                     1.7.10 真实 JSON + Forge 样例
```

## 在受限环境里构建

本机沙箱会挡默认的 `%TEMP%` 与 `%APPDATA%` 下的 NuGet 配置，构建前设这几个变量：

```bat
set TEMP=%~dp0.tmp
set TMP=%TEMP%
set DOTNET_CLI_HOME=%~dp0.dotnet-home
set APPDATA=%~dp0.appdata
set NUGET_PACKAGES=%~dp0.nuget\packages
```

本仓库不依赖任何外部 NuGet 包，正常机器上直接 `dotnet build` 即可。

## 下载引擎（M4）怎么工作

- **清单**：`DownloadPlanner` 从版本 JSON 直接推导（client / 29 个库 / 4 个 natives / 资源索引 / 日志配置），路径规则与启动计划完全一致——下好的文件就是 `Prepare` 预检要的文件。
- **镜像**：只映射实测通的地址（`libraries.minecraft.net`、`resources.download.minecraft.net` → BMCLAPI），镜像失败自动回退官方；没验证过的地址不硬映射。
- **安全**：先写 `.part-*`，大小 + sha1 校验通过才改名；失败不留半成品；已存在且大小对的文件直接跳过。
- **资源**：先下 `assets/indexes/<id>.json`，再按 `objects/<hash前2>/<hash>` 展开（1.7.10 没有 virtual / map_to_resources）。

## 直接跑 Prism 里的实例（不下载）

如果你（或你的朋友）本来就在 Prism / MultiMC 里玩 GTNH，启动器不需要下载任何东西：
库、资源、Java、解压好的 natives 全都在 Prism 目录里，只要把实例的启动参数拼对即可。

`Launcher.Prism` 做的事：

- 读实例的 `instance.cfg` 与 Prism 根的 `prismlauncher.cfg`（Java 路径、内存覆盖开关）；
- 读 `patches/*.json`，按 `order` 合并：主类取最高优先级、库倒序合并去重（`group:artifact[:classifier]`）、
  `+tweakers` 展开成 `--tweakClass`、`+jvmArgs` 原序保留；
- 库路径兼容 Prism 的三种摆放：根 `libraries/`、实例 `libraries/`、实例里平铺的 jar
  （lwjgl3ify 的 `forgePatches` 就属于最后一种）；
- 只声明 natives 没有 artifact 的库（twitch / jinput 这类）不进 classpath——native 早已被 Prism 解压进 `natives/`。

**WPF 里怎么用**：实例目录直接选 `...\PrismLauncher\instances\<实例名>`（注意是实例目录，不是
`.minecraft`，也不是 Prism 根目录）。界面会显示 `[Prism] 实例名`，Java 与内存从 `instance.cfg` /
`prismlauncher.cfg` 带出来，点「启动」即可；这个模式下「安装 Forge / 下载」会自动跳过——
Prism 的库和资源是全局共享的，启动器只读不写。

实测（本机真实 GTNH 2.9.0-beta-1 + lwjgl3ify + Java 25 实例）：

```
实例       : GT_New_Horizons_2.9.0-beta-1_Java_17-25 (2)
主类       : com.gtnewhorizons.retrofuturabootstrap.MainStartOnFirstThread
classpath  : 84 项
体检       : 文件齐全，可以直接启动（本次没有联网、没有下载、没有写盘）
```

用这条命令行真的把游戏起起来了：日志里出现 `RFB-Main`、`LWJGL Version: 3.4.2`、
300+ mod 的资源重载与 `Sound engine started`，说明启动链路是通的（测试完已结束进程）。

注意：`prism plan` 用的是**离线 UUID**（`--username 名字` 的 MD5 版本），单机没问题；
正版服需要换成微软登录拿到的 token。



1. **Java 自动选择**：把 `JavaLocator` 接到朋友的 `IJavaProvider`（`JavaRequirement` 已带 `component` / `majorVersion`；GTNH 的 Java 8 / 17-25 两个变体靠这个挑），并按 `javaVersion` 自动选默认 Java。
2. **GTNH 分发包安装器**：下载官方 Prism ZIP → 解压 → 读 `mmc-pack.json` → 装 Forge → 铺 mods/config。
3. **assets 完整性校验**：现在按 size 判断跳过，可加一次全量 sha1 扫描（`verify` 子命令）。
4. **微软登录**：离线 UUID 已够单机，正版登录（device code flow）后续接。
5. **打包发布**：`dotnet publish -c Release -r win-x64 --self-contained` + 单文件。

## 合规

本启动器**不包含也不分发** Minecraft 游戏文件，不提供绕过正版验证的功能。

NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.
