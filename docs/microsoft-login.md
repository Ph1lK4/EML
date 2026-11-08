# Minecraft Java 微软账号登录

## 当前功能

在“微软账号”页面点击“登录 / 更换微软账号”，EML 显示验证码并打开微软网页。
玩家在网页输入验证码、登录并确认授权。EML 不收集微软密码。
成功后到实例“启动设置”勾选“使用已登录的正版账号”，保存设置或直接启动。
正版名称、UUID 来自 Minecraft 档案；离线名字字段仅保留原离线偏好。
切换身份会改变 UUID，已有单人存档中的背包、任务等可能被识别为不同玩家。

当前支持一个本机账号，重新登录可更换账号。不同实例可以分别选择正版或离线身份。
未勾选时保持原离线行为；勾选后登录无效、网络刷新失败、没有 Java 许可都会明确失败，不自动回退离线。

## 开发者配置（首次启用所需）

1. 在 Microsoft Entra 管理中心创建属于 EML 的应用注册。
2. 账号类型选择支持个人微软账号（Personal accounts only，或同时支持组织和个人账号）。Minecraft 玩家通常使用个人账号；此实现使用 consumers 租户。
3. 在应用身份验证设置中启用公共客户端流程（Allow public client flows）。本实现使用设备授权码，不需要客户端密码，也不需要桌面回调端口。
4. 复制 Application (client) ID。不要复制 Object ID、Directory ID，不要创建或填写 client secret。
5. 编辑 `src/EML.App/microsoft-login.json`，将 `clientId` 设置为自己的公开应用 GUID。该文件自动复制到 Debug、Release 和发布输出。
6. 重新编译运行。也可在启动 EML/Visual Studio 的环境中设置 `EML_MICROSOFT_CLIENT_ID` 覆盖配置。普通玩家使用发布包内的 ID，不需要自行注册应用。

Client ID 是公开标识，不是密钥。不要使用其他启动器的 Client ID 或伪装官方启动器。
应用注册成功不代表已获得所有 Minecraft Services 接口权限。若 Minecraft 返回 403，请按 Microsoft/Minecraft 当前官方要求确认应用访问权限；不要绕过访问限制。
Client ID 是否已配置只代表本地配置有效。自动化测试不使用真实用户授权，不能代替实际注册应用后的端到端登录验证。

## 网页授权成功，但 EML 登录失败

网页显示“大功告成”只表示完成了微软设备授权；EML 仍需完成 Xbox、XSTS、Minecraft 登录、游戏许可和玩家档案查询。
账号页面现在提供可复制的安全诊断，包含失败阶段、固定官方接口和 HTTP 状态，例如：

```text
Minecraft 登录失败（HTTP 403）。
接口：api.minecraftservices.com/launcher/login
```

先停止旧的调试进程，重新编译运行，再重试登录。复制账号页面的诊断文字即可，不需要设置断点或提供令牌。
微软接口失败时检查应用登录配置；Xbox/XSTS 失败时结合已知 Xbox 错误码检查账号及应用访问权限；Minecraft 接口失败时再核对应用的 Minecraft API 访问权限和账号状态。
HTTP 403 本身不能证明账号未购买游戏，也不能证明应用一定未获准入。不要根据这一状态反复更换 Client ID 或创建 client secret。
诊断不会回显响应正文、请求头、查询字符串或内部异常。空白或 HTML 错误响应同样保留 HTTP 状态；失败、取消不清除已有本机账号。

### 已定位到 Minecraft 登录接口的 403

2026-11-08 用户重新授权后的实际诊断为 `api.minecraftservices.com/launcher/login` 返回 HTTP 403。
依照当前串行调用链，这说明微软令牌、Xbox 用户令牌和 XSTS 交换已经完成，尚未执行游戏许可和玩家档案查询。
请求端点、XSTS RelyingParty 及登录字段与核对过的 Prism 上游实现一致。
目前没有服务端拒绝理由，不能保证仅凭改客户端代码即可解决；首先确认这个 Client ID 是否取得 Minecraft 的 AppID 审核批准。

官方入口：<https://aka.ms/mce-reviewappid>。它是独立的 AppID Review 申请，不是在 Entra 的 API permissions 页面添加 Microsoft Graph 权限。
2026-11-08 读取的表单状态为 Active，最后修改于 2026-10-20。新应用申请应选择 `New AppID for Approval`，准备：

- 应用显示名称，例如 `EML Launcher`。
- 当前 EML 配置使用的 Application (client) ID。
- 同一个应用所属的 Directory (tenant) ID，新应用申请的说明要求填写。
- 可以与 Azure Portal 信息核对的联系邮箱，以及介绍应用的网站或仓库网址。
- 应用用途与所需 API 的说明，并由申请人本人阅读、确认 EULA 和使用准则。

用途说明草稿（提交前按实际行为核对）：

> EML Launcher is a Windows desktop launcher that helps players install, manage and launch Minecraft modpacks. We request access to Minecraft Services for Microsoft-account sign-in, Java Edition ownership verification, player profile retrieval and authenticated game sessions. Credentials are stored locally using Windows account protection, and tokens are excluded from application logs.

表单要求在开放游戏或服务器访问前验证所有权并进行认证，绕过这些检查的应用不会获批。当前 EML 仍有直接输入离线名字启动的功能，不能在申请中声称所有启动均经过所有权验证。
若为审核调整该行为，需要明确产品方案并实现；本文档未改变离线启动逻辑，也没有替用户提交或作出合规声明。
表单说明每周审核，重复申请不会加快处理；这不代表一周内必定批准。已经获批但仍出现该错误时，继续核对实际 Client ID、批准记录和官方服务状态。

## 实现和数据边界

- Core/Minecraft/Accounts：公开账号档案、仅内存会话及认证接口。
- Infrastructure/Minecraft/Accounts：设备码、令牌交换、账号管理、Windows DPAPI 存储和公开配置。
- App/ViewModels/MinecraftAccountViewModel：验证码、账号显示、登录、取消和退出。
- MinecraftLaunchPlanner：在正版模式下按需刷新会话，替换玩家名、UUID、access token、session、xuid、client ID 和 user type。
- MinecraftGameLauncher：Java 9+ 参数文件只写无令牌的前缀；敏感参数直接传入进程。两条输出管道在分块、写日志和显示前进行跨缓冲区脱敏。

刷新凭证加密保存在 `%LOCALAPPDATA%/EML/accounts/microsoft.dpapi`，只能由对应 Windows 用户解密。
Minecraft access token 不写入账号文件；微软和 Xbox 短期令牌只用于内存交换。
账号文件不在实例、`.eml-runtime` 或导出 ZIP 内。退出账号删除 EML 本机凭证，不撤销已运行游戏的会话或浏览器中的微软登录。
游戏进程必须接收会话参数，本机同权限进程可能查看命令行；EML 也不能保证第三方模组自行生成的日志不会记录会话。
EML 自己的日志不输出认证响应、refresh token 或已知游戏 access token。

## 协议核对

2026-11-08 查阅微软设备授权文档，并核对 Prism 上游当前认证实现的端点与字段；实现代码独立编写。

```text
https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code
https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app
https://github.com/PrismLauncher/PrismLauncher/tree/develop/launcher/minecraft/auth/steps
```

设备码与刷新使用 `consumers/oauth2/v2.0`，scope 为 `XboxLive.SignIn XboxLive.offline_access`。
交换链为 Xbox user/authenticate → XSTS authorize → Minecraft launcher/login → entitlements/license → minecraft/profile。
设备码流程没有浏览器回调，不使用 OAuth authorization-code 的 state 参数；device_code 保持在认证层，仅公开 user_code。
网络请求不自动跟随重定向，错误不回显响应正文。

## 配置后人工验收

1. 登录持有 Java 版的账号，确认玩家名和 UUID。
2. 选择已补全的原版、Fabric/Modrinth、CurseForge、GTNH 实例，以正版身份启动并进入正版验证服务器。
3. 重启 EML，确认账号能恢复并在启动时刷新；取消登录和刷新故障不删除原凭证。
4. 检查 EML 运行日志及实例参数文件没有 access token。
5. 退出账号后，正版启动必须要求登录；显式取消勾选正版模式后原离线功能仍可使用。

## 本次验证结果

- Debug 和 Release 解决方案构建均通过：0 警告、0 错误。
- 全量测试 1092 项通过，0 失败、0 跳过；相比之前新增 49 项账号、认证与启动保护测试。
- 本机真实 Java 8、17、25 均通过合成参数程序：令牌传递和参数顺序正确，stdout/stderr 被脱敏，参数文件被清理。
- 生产 WPF 账号页完成不显示窗口的布局和绑定检查：账号数据源正确，未配置 Client ID 时禁用登录并展示配置提示。
- 测试结果保存于 `artifacts/test-results/microsoft-login/microsoft-login-tests.trx`。
- 没有进行真实账号授权、启动 Minecraft 或连接正版服务器；这些需要注册应用后按上述人工步骤验收。

### 2026-11-08 登录错误诊断修复

- 全量测试 1112 项通过，0 失败、0 跳过；新增 20 项涵盖不同阶段的 403、非 JSON 错误响应、轮询终止、诊断脱敏和账号保留。
- WPF 使用独立输出目录构建成功：0 警告、0 错误。常规 Debug 输出当时被正在运行的 EML/Visual Studio 锁定，需要停止调试后重新生成。
- 修复了诊断与错误处理；用户真实重试已定位到 Minecraft `/launcher/login` 的 403，外部接口拒绝尚未解决，需确认 AppID 准入状态。
