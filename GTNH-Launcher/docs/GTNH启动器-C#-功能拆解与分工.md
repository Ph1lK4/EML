# GTNH 专用启动器（C#）· 功能拆解与分工

> 给四人小队的分工方案 + 给你本人的方向建议。
> 配套可编译骨架：`E:\Deepseek工作区\GTNH-Launcher\`（启动核心：预检 / 进程 / 诊断 / 下载 / Forge 安装 / Prism 直连 / WPF 外壳已通，24 个测试全绿）。
> 写于 2026-10-24。文末「已核实 / 未核实」分开标注，别把推测当事实用。
>
> **进度（2026-10-24）**：启动核心已落地并验证——元数据/合并/参数、实例布局、启动预检、
> 进程层（异步读流/取消/退出码）、日志落盘、崩溃诊断、下载引擎（镜像 + sha1 + 并发，1.7.10 实测 35 个文件 13s 下完）、
> Forge 安装（解包真安装包，不需要 JVM；版本 JSON + universal jar 落位）、WPF 外壳（实例/版本、Java 检测、
> 安装 Forge、下载 / 修复、启动 / 停止、实时日志）、Prism / MultiMC 实例直连（只读合并 `patches/*.json`，
> 不下载不写盘；本机真实 GTNH 2.9.0-beta-1 实例已用拼出来的命令行真启动进主菜单）。
> `GTNH-Launcher` 现为 8 工程、24 个测试全绿。还缺：GTNH 分发包安装器、正版登录。

---

## 0. 先对齐三个事实（这决定分工怎么切）

1. **GTNH 官方分发的是 Prism/MultiMC 实例 ZIP，不是 CurseForge 包。**
   下载页上直接给 `GT_New_Horizons_2.8.4_Java_8.zip` 和 `..._Java_17-25.zip`，解压后是
   `instance.cfg` + `mmc-pack.json` + `.minecraft/`。也就是说：**不需要接 CurseForge API，也不需要做整合包解析器**，
   下载 ZIP → 解压 → 读 `mmc-pack.json` 拿 Forge 版本 → 装 Forge → 启动。
   这是「适配 GTNH」能把工作量砍掉一半的根本原因。

2. **你们已经有一个跑通的 TS 核心（EML）。** 29 个测试 + 1.20.1 真实下载 e2e。
   C# 版本不是从零发明，而是「照着规格书重写」——这是好事，风险最低；代价是要接受两套代码短期并存。

3. **朋友的 Java 检测已经把接口定死了**：`JavaProvider.resolve(JavaRequirement) → JavaRuntime`。
   全组唯一已经锁死的边界就是它，其他人都应该围着这个接口对齐，而不是另起一套。

---

## 1. 范围收缩：做什么 / 不做什么

| 做 | 不做（至少 1.0 前不做） |
|---|---|
| 只支持 **1.7.10 + Forge**（GTNH 的固定底座） | 通用版本支持、1.13+ 的 `arguments` 数组 |
| 只支持 **GTNH 官方 Prism ZIP 两个变体**（Java 8 / Java 17-25） | Fabric / NeoForge / LiteLoader |
| 实例隔离（每个实例独立 `.minecraft`） | CurseForge / Modrinth 整合包浏览与一键安装 |
| 下载、校验、断点续传、镜像回退 | 盗版绕过、离线皮肤、账号系统全家桶 |
| 进程管理、日志、崩溃提示 | 自动更新 mod、mod 冲突分析（先做提示，不做自动修） |
| 离线登录（先）+ 微软登录（后置） | 服务器列表、好友系统 |

**合规红线（写进 README 和关于页）**：不分发 Minecraft 游戏文件；不提供绕过正版验证的功能；
必须展示 `NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.`

**版本选择**：官网当前稳定版是 2.8.4；GitHub 上 2.9.0 还是 RC / nightly。**先锁 2.8.4**，
版本号写进配置，等 2.9.0 正式发布再升，别追 nightly。

---

## 2. 功能拆解（8 个模块）

| 编号 | 模块 | 职责 | 输入 → 输出 | 难度 | 能独立开发 | 验收标准 |
|---|---|---|---|---|---|---|
| M1 | 契约与骨架 | 共享 DTO、接口、sln、CI | 需求 → `Launcher.Contracts` | ★ | 是（第一天） | 四个人能各自编译、不互相等 |
| M2 | Java 运行时 | 探测/校验/（可选）下载 JRE | `JavaRequirement` → `JavaRuntime` | ★★ | 是（朋友已在做） | 本机能找到 Java 8 和 Java 17，位数判断正确 |
| M3 | 版本元数据 + 启动核心 | 版本 JSON、继承合并、rules、classpath、natives、参数模板 | 版本 JSON + 请求 → `LaunchPlan` | ★★★ | 是（有真实 JSON 就能离线开发） | 打印出的命令行与原版/Prism 一致 |
| M4 | 下载引擎 | 并发、重试、续传、SHA1、镜像回退 | `DownloadTask[]` → 进度/完成 | ★★★ | 是（可先只下 1.7.10 的库） | 断网重连后能续传，坏文件能重下 |
| M5 | GTNH 安装器 | ZIP 下载解压、读 `mmc-pack.json`、装 Forge、实例管理、更新 | 用户点「安装」→ 可启动实例 | ★★★★ | 依赖 M4 | 全新机器一键装到能进游戏 |
| M6 | 进程与崩溃诊断 | 启动、日志流、退出码、规则库 | `LaunchPlan` → 进程/日志/诊断 | ★★★ | 依赖 M3 | GTNH 能起来；崩溃时给得出人话提示 |
| M7 | 界面（WPF） | 实例列表、安装向导、设置、日志面板 | 用户操作 → 调用各模块 | ★★★ | 可以先用假数据 | 全流程能点完，不卡 UI |
| M8 | 登录 | 离线 UUID（先）、微软 OAuth（后） | 账号 → `AuthInfo` | ★★ / ★★★★ | 是 | 离线进服务器 UUID 一致；微软登录能拿到 token |

> 难度按「一个人、没做过 MC 启动器」估；M5 的难度主要在 Forge 安装的边界情况。

---

## 3. 四人分工（建议）

| 人 | 负责 | 理由 |
|---|---|---|
| **你** | **M1 + M3 + M6** | 你写过 EML 的下载引擎和参数拼接，M3 是你最熟的部分；M6 和 M3 贴得最近，合并成「启动核心」一个人做最顺 |
| 朋友 | M2 | 已经在做，保持不动 |
| C | M4 + M5 | 下载和安装器是同一条数据流（ZIP/库/资源），一个人做省掉接口摩擦 |
| D | M7 | 界面工作量独立，可以拿假数据先画 |

```
        M1 契约（你，第 1 天）
              │
   ┌──────────┼──────────────┬─────────────┐
   ▼          ▼              ▼             ▼
 M2 Java    M3 启动核心     M4 下载       M7 界面
（朋友）     （你）          （C）         （D，先用假数据）
   │          │              │
   └────┬─────┘              ▼
        ▼                  M5 GTNH 安装器（C）
      M6 进程/崩溃（你）
```

**关键约定**：M3 只依赖 M2 的输出类型和 M4 的「文件已就位」；M3 不直接发网络请求，也不直接碰 UI。
这三条守住了，四个人就能真的并行。

---

## 4. 给你的方向：**启动核心（M3 + M6）**

### 为什么是它

1. **它是唯一「不写完谁都别想跑」的模块。** 下载器写完但启动核心没写完，进度条走到 100% 也进不了游戏；
   启动核心写完，哪怕下载器还是手动的，也能立刻演示「点一下出命令行」。你掌握主线程，合并时不会被别人卡住。
2. **有现成规格书。** EML 的 `plan.ts` / `arguments.ts` 已经把逻辑跑通了，29 个测试就是验收用例，移植风险最低。
3. **纯函数，能离线开发。** 一份真实 `1.7.10.json` 就能写完全部逻辑和测试——不用等网络、不用等别人的模块。
   这是四个人里最适合「先做完、后合并」的一块。
4. **边界最清晰。** 入口是朋友的 `JavaRuntime`，出口是 `LaunchPlan`（一条命令行）；中间不碰 UI、不碰网络。
5. **正反馈最快。** 做完当天就能打印出一条与原版启动器一致的命令，团队士气直接拉起来。

### 备选（如果你更想要「产品感」）

| 方向 | 适合谁 | 优点 | 代价 |
|---|---|---|---|
| M3 启动核心 | 你（推荐） | 主线程、可离线、有规格书 | 界面看不见，需要自己造演示 |
| M5 GTNH 安装器 | 喜欢啃硬骨头的人 | 产品最显眼，「一键装 GTNH」就是卖点 | 依赖 M4，Forge 安装坑多 |
| M7 界面 | 想快速看到东西的人 | 每天都有画面 | 耦合最高，合并时最容易打架 |
| M4 下载引擎 | 喜欢网络/性能的人 | 可独立测试 | 你已经写过一遍，重复劳动 |

### 具体第一步（今天就能做）

1. 跑通 `GTNH-Launcher`：`dotnet run --project src\Launcher.Cli`，确认打印出 30 项 classpath、4 个 natives。
2. 把 `ArgumentBuilder` / `RuleEvaluator` / `VersionMerger` 的测试补到 15 个以上（用真实 JSON 当夹具）。
3. 写 `IProcessRunner`：用 `ProcessStartInfo.ArgumentList` 启动，先跑 `java -version` 验证进程层，再接 `LaunchPlan`。
4. 定义崩溃规则库的第一版（见 §7.9），先只做「识别 + 给建议」，不做自动修复。

---

## 5. 组合契约（已经是可编译代码）

```csharp
// Launcher.Contracts —— 谁都不许加依赖
public sealed record JavaRequirement(string Component, int MajorVersion);
public sealed record JavaRuntime(string Path, string Version, int Major, string? Vendor = null, bool? Is64Bit = null, string Source = "system");
public interface IJavaProvider { Task<JavaRuntime?> ResolveAsync(JavaRequirement requirement, CancellationToken ct = default); }

public sealed record DownloadTask(string Url, string Destination, string? Sha1, long? Size, string Label, string Kind = "library");
public interface IDownloadService { Task DownloadAsync(IReadOnlyList<DownloadTask> tasks, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default); }

public sealed record LaunchRequest { /* Java / GameDirectory / VersionName / Auth / 内存 / 额外参数 */ }
public sealed record LaunchPlan { /* JavaPath / MainClass / JvmArgs / GameArgs / Classpath / WorkingDirectory / Natives */ }
```

合并节奏建议：

- **契约先行**：改 `Launcher.Contracts` 必须先发 PR、全员过一眼，别在 feature 分支里偷偷加字段。
- **每周一次集成日**：周五下午把四个分支合到 `main`，只跑「能不能打印命令行 + 能不能启动到主菜单」两条冒烟。
- **别维护两份真值**：EML 的 TS 代码当「规格书 + 对照测试」，不要两边同时加功能。C# 版过了 EML 的用例，TS 版就冻结。
- 如果朋友的 Java 检测是别的语言（TS/Java），**把边界降到进程级**：约定一个 JSON 输入输出，别强行绑 C# 接口。

---

## 6. 工程结构（骨架已落地）

```
GTNH-Launcher/
  Directory.Build.props          net8.0 / nullable / implicit usings
  data/1.7.10.json               官方 1.7.10 版本 JSON（已抓，离线可用）
  data/forge-1.7.10.sample.json  手写样例，只用于测试继承合并（不是官方原文）
  src/Launcher.Contracts/        契约（零依赖）
  src/Launcher.Metadata/         JSON 模型、rules、合并、参数、natives、离线 UUID
  src/Launcher.Runtime/          实例布局、启动预检、进程管理、日志、崩溃诊断
  src/Launcher.Download/         下载清单、资产索引、BMCLAPI 镜像、并发下载 + sha1
  src/Launcher.Forge/            Forge 安装器解包（install_profile → 版本 JSON + universal jar）
  src/Launcher.Cli/              演示：plan / run / download / forge / exec / diagnose
  src/Launcher.Gui/              WPF 外壳：实例 / Java / 下载 / Forge / 启动 / 日志
  tests/Launcher.Metadata.Tests/ 无第三方依赖的测试台
```

当前状态：`dotnet build` 0 警告 0 错误；24 个测试全绿；1.7.10 下载清单 36 个任务（client 1 + 库 29 + natives 4 + 索引 1 + 日志 1），
真机实测 35 个文件 13s 下完并全部通过 sha1；Forge 真安装包实测：id `1.7.10-Forge10.13.4.1614`、classpath 45 项、预检就绪、主类 launchwrapper。
Prism 真实实例实测：classpath 84 项、体检全齐、拼出的命令行真启动进主菜单（Java 25 + lwjgl3ify + 300+ mod）。
WPF 外壳已随解决方案一起编译通过（`src\Launcher.Gui`），点完整个流程还需要在真实实例上跑一遍。

---

## 7. GTNH 适配清单（每条都是坑，按顺序踩）

### 7.1 版本 JSON 是 legacy 格式

1.7.10 用的是**单行 `minecraftArguments` 模板**，不是 1.13+ 的 `arguments` 数组。别照抄新版启动器代码。
模板（已核实）：

```
--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory}
--assetsDir ${assets_root} --assetIndex ${assets_index_name} --uuid ${auth_uuid}
--accessToken ${auth_access_token} --userProperties ${user_properties} --userType ${user_type}
```

### 7.2 `javaVersion` 是存在的（纠正 EML 交接文档）

`1.7.10.json` 里有 `"javaVersion": { "component": "jre-legacy", "majorVersion": 8 }`。
EML 的 `docs/HANDOFF-下载引擎.md` 写「1.7.10 没有 javaVersion」是错的，**请改掉**，
否则 Java 模块永远拿不到 1.7.10 的需求。

### 7.3 两种 Java 变体，需求不是从 JSON 来的

GTNH 给 Java 8 和 Java 17-25 两个 ZIP。**Java 需求由「用户选哪个包」决定**，不是由版本 JSON 决定：

- Java 8 包：经典 lwjgl2 + 老 Forge 兼容层
- Java 17-25 包：带 lwjgl3ify，JVM 参数与 Java 8 不同

实现上：`LaunchRequest` 里加 `JavaMajorOverride`，安装时按 ZIP 变体写入实例配置；
`JavaProvider` 只负责「按 major 找 Java」，不负责猜。找不到就明确报错，别回退到别的版本硬跑。

### 7.4 assets 是 objects 布局（无 virtual）

1.7.10 的 assets index **没有** `virtual` / `map_to_resources`（我们全量检索过）。
所以资源直接放 `assets/objects/<hash 前两位>/<hash>`，`--assetsDir` 指 `assets` 根，
`--assetIndex` 传 `1.7.10`。别按 1.6 以前的老版本做虚拟目录。

### 7.5 log4j 配置参数必须有

`logging.client.argument = -Dlog4j.configurationFile=${path}`，文件是 `client-1.7.xml`
（sha1 `50c9cc4a…`）。不下载/不替换 `${path}`，日志和字体相关的问题会很难查。

### 7.6 natives 一共 4 个（Windows x64，已核实）

| 库 | 分类器 |
|---|---|
| `org.lwjgl.lwjgl:lwjgl-platform:2.9.1` | `natives-windows` |
| `net.java.jinput:jinput-platform:2.0.5` | `natives-windows` |
| `tv.twitch:twitch-platform:5.16` | `natives-windows-64` |
| `tv.twitch:twitch-external-platform:4.5` | `natives-windows-64` |

解压时跳过 `META-INF/`；`-Djava.library.path` 指向实例的 `natives/`。
**注意 `twitch-platform` 的 rules 是 `allow` + `disallow linux`**，在 Windows 上要正确判成允许——
这是规则引擎最容易被写错的一条。

### 7.7 Forge 只有差异，必须合并

Forge 的版本 JSON 有 `inheritsFrom: "1.7.10"`，主类变成 `net.minecraft.launchwrapper.Launch`，
`--tweakClass cpw.mods.fml.common.launcher.FMLTweaker`。客户端 jar 仍取父版本的 `1.7.10.jar`。
合并时**按 `group:artifact` 去重、子版本优先**，否则会出现两个 asm / 两个 guava。

### 7.8 Forge 安装的两条路

- **A. 手动铺库（推荐）**：从 installer jar 里取 `install_profile.json`，拿 `versionInfo`
  （含 libraries、inheritsFrom）和 universal jar，自己写 `versions/<id>/<id>.json` + 库文件。
  过程确定、可校验、不需要安装阶段就有 Java。
- **B. 跑官方 installer**：工作目录设为实例的 `.minecraft`，`java -jar forge-...-installer.jar --installClient`。
  省事，但依赖 installer 版本的行为，出错时不好定位。

建议 A 做主线、B 做兜底。Forge 版本**从 ZIP 的 `mmc-pack.json` 读**，不要硬编码；
社区普遍认为 2.8.x 是 Forge 10.13.4.1614，但我们没解包验证，别把它写死。

### 7.9 进程与崩溃（M6 的核心价值）

- 用 `ProcessStartInfo.ArgumentList`，**永远不要拼字符串**——GTNH 玩家路径里带空格、中文、括号是常态。
- 工作目录设为实例 `.minecraft`，`RedirectStandardOutput/Error = true`，异步读、UTF-8。
- GTNH 启动 2–5 分钟正常（300+ mod），**不要做超时杀进程**；UI 要显示「还在加载」而不是「卡死」。
- 退出码速查：
  - `0` 正常退出
  - `1` 一般崩溃（先看日志最后 100 行）
  - `-1073741819` (0xC0000005) 访问冲突：32 位 Java / 显卡驱动 / natives 不对
  - `-1073740791` (0xC0000409) 栈溢出：多半是 mod 或内存
  - `-1073741515` (0xC0000135) 缺 DLL：natives 没解压或 Java 位数不匹配
- 规则库第一版（日志关键字 → 人话建议）：
  `OutOfMemoryError` → 加内存/换 64 位 Java；`Pixel format not accelerated` → 显卡驱动；
  `Failed to locate library` → natives；`NoSuchMethodError`/`NoClassDefFoundError` → mod 版本不匹配；
  `The game crashed whilst initializing game` → 先二分 mods。

### 7.10 内存与 JVM 参数

- GTNH 官方 wiki 建议：单人 5–7 GB；客户端 + 专用服务器 4 GB+。默认给 6 GB 是合理起点。
- 上限要按物理内存算：`min(用户设置, 物理内存 - 2GB)`；32 位 Java 硬上限约 1.5 GB，必须拦。
- G1GC 参数：社区常见模板是 `-XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200`
  这一套。**把它做成「可编辑的默认值」而不是写死**，不同机器差异很大，出了问题玩家自己能改。

### 7.11 下载源

- 官方：`piston-meta` / `launchermeta` / `libraries.minecraft.net` / `resources.download.minecraft.net`。
- 国内镜像：BMCLAPI（`bmclapi2.bangbang93.com`），把上面几个域名按前缀替换即可。
- 我们实测：BMCLAPI 的 `/version/1.7.10/json` 会 302 到随机镜像主机，**多跳跨域**，偶尔很慢。
  .NET 的 `HttpClient` 默认跟随重定向，没问题；但必须设超时（30s）+ 失败回退官方，
  否则一次镜像抽风就把用户卡死。
- 下载量参考：1.7.10 资源索引 `totalSize` 约 112 MB，客户端 jar 约 5.3 MB，库 29 个。
  GTNH 的 mods 另算（ZIP 本身不小，下载前先检查磁盘余量）。

### 7.12 离线登录

`--uuid` = `MD5("OfflinePlayer:" + 名字)` 按 UUID v3 改位，32 位不带连字符；`--userType legacy`；
`--userProperties {}` 原样传。骨架里的 `OfflineUuid` 已经和 Java 版对齐（Steve / Notch 的值都写进测试了）。

### 7.13 账号方式

先只做离线。微软登录需要注册 Azure 应用（public client + device code flow），
**团队要先申请 app id**，别等 UI 做完了才发现没有凭据。放到 1.0 之后。

### 7.14 实例隔离与更新

每个 GTNH 实例一个目录（自己的 `.minecraft`、`mods`、`config`、`saves`）。
更新 2.8.4 → 2.9.0 时：新 ZIP 覆盖 `mods/config`，**保留 `saves`、`options.txt`、自定义配置**；
覆盖前先备份。ZIP 里没有 hash 清单，完整性校验只能自己算（下载后记 SHA256，下次对比）。

### 7.15 路径与编码

- 中文用户名/路径是常态：全程 `Path.Combine` + `ArgumentList`，别用 `cmd /c` 拼串。
- 读 Java 输出时注意编码（`java -version` 走 stderr，中文 Windows 下可能是 GBK）。

---

## 8. 里程碑（4 周 MVP，8 周 1.0）

| 周 | 目标 | 演示 | 风险 |
|---|---|---|---|
| W1 | M1 + M3 能打印命令行；M2 输出真 JavaRuntime；M4 能下 1.7.10 的库/资源 | `dotnet run` 打印完整命令 | 契约反复改 |
| W2 | 端到端启动**原版 1.7.10** 进主菜单 | 点「启动」进标题界面 | natives / log4j 配置漏了 |
| W3 | Forge 安装 + 手动放 GTNH mods 能进游戏 | 进 GTNH 主菜单 | Forge 安装边界情况 |
| W4 | GTNH ZIP 一键安装 + Java 变体选择 + 内存设置 | 全新目录一键装完能玩 | ZIP 解压/覆盖策略 |
| W5–6 | 崩溃诊断、日志面板、更新器、完整性修复 | 故意 OOM 能看到人话提示 | 规则库误报 |
| W7–8 | 打包（单文件发布/安装包）、合规文案、发布 | 发给朋友试用 | 杀软误报、签名 |

**MVP 的判定标准**：一台没装过 Java、没装过 Forge 的 Windows 机器，点「安装 GTNH」→ 选 Java 变体 →
等下载 → 点「启动」→ 进主菜单。做不到这条就不算 MVP。

---

## 9. 风险表

| 风险 | 概率 | 影响 | 对策 |
|---|---|---|---|
| Forge 安装的版本差异/边界情况 | 中 | 高 | 锁 Forge 版本；手动铺库为主、installer 兜底 |
| Java 17-25 变体的 lwjgl3ify 参数写错 | 中 | 高 | 从 ZIP 的 `instance.cfg` 读 `JvmArgs`，别猜 |
| 镜像/官方源抽风 | 中 | 中 | 双源回退、重试、缓存已下文件 |
| 四个人合并冲突 | **高** | 高 | 契约先行 + 每周集成日 + 冒烟两条 |
| C# 重写拖慢进度 | 中 | 中 | EML 当规格书，先过用例再扩功能 |
| 微软登录凭据没申请 | 低 | 低（后置） | 1.0 前只做离线 |
| GTNH 包体积大 | 中 | 中 | 断点续传 + 磁盘检查 + 进度节流 |

---

## 10. 下一步 5 件事

1. 跑通骨架，确认 CLI 输出（`dotnet run --project src\Launcher.Cli`）。
2. 拿 EML 的 29 个测试对照，把 C# 侧测试补到同等覆盖。
3. 写 `ProcessRunner`，用 `java -version` 打通进程层（不依赖游戏文件）。
4. 让朋友按 `JavaRuntime` 契约交第一版（本机 Java 8 + 17 都能识别）。
5. 定下「每周集成日」和冒烟脚本，再开始并行写 M4/M5/M7。

---

## 附录 A：已核实的数据

**1.7.10 版本 JSON**（`https://piston-meta.mojang.com/v1/packages/ed5d8789ed29872ea2ef1c348302b0c55e3f3468/1.7.10.json`）

- `mainClass`: `net.minecraft.client.main.Main`
- `javaVersion`: `jre-legacy` / `8`
- `assetIndex`: id `1.7.10`，sha1 `1863782e33ce7b584fc45b037325a1964e095d3e`，size 72996，totalSize 112396854
- `client.jar`: sha1 `e80d9b3bf5085002218d4be59e668bac718abbc6`，size 5256245
- `logging.client`: `-Dlog4j.configurationFile=${path}`，`client-1.7.xml`，sha1 `50c9cc4a…`
- 库 33 个（29 个有 artifact，4 个 natives），`minimumLauncherVersion` 13，`complianceLevel` 0

**GTNH**

- 下载页：`https://www.gtnewhorizons.com/downloads/`，最新稳定 2.8.4
- Prism ZIP：`https://downloads.gtnewhorizons.com/Multi_mc_downloads/GT_New_Horizons_2.8.4_Java_8.zip`
  与 `..._Java_17-25.zip`；另有 Server ZIP、Vanilla Launcher ClientPack（仅 Java 8）
- 官方推荐 Prism；Technic / CurseForge 标注 not recommended
- GitHub：`GTNewHorizons/GT-New-Horizons-Modpack`，README 标 2.9.0-RC-2（2026-10-04）

## 附录 B：未核实 / 需实测

- GTNH 2.8.4 ZIP 里 `mmc-pack.json` 的具体组件清单（Forge 版本号、lwjgl3ify 组件名）——**没解包**，
  实现时以文件为准。
- `instance.cfg` 里 `JvmArgs` 的确切内容（Java 17-25 变体的启动参数）。
- ZIP 的实际体积与文件数。
- Forge 1.7.10 installer 的 `--installClient` 是否接受路径参数（不同小版本行为可能不同）。

## 附录 C：参考

- EML 工程：`E:\Deepseek工作区\EML`（规格书 + 29 个测试）
- 旧计划：`E:\Deepseek工作区\MC-Launcher-计划.md`
- Mojang 版本清单：`https://piston-meta.mojang.com/mc/game/version_manifest_v2.json`
- BMCLAPI：`https://bmclapi2.bangbang93.com`
- Prism / MultiMC 实例格式：`instance.cfg` + `mmc-pack.json` + `.minecraft/`
