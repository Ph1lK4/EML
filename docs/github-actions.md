# GitHub Actions 自动构建

EML Windows CI 是接入 SignPath 前的第一步：从干净的源码检出执行构建、测试和发布冒烟检查。它不签名、不创建 GitHub Release，也不要求配置任何 Secrets。

## 如何触发和查看

- 推送到 `master`、`feature/**` 或 `codex/**` 分支时自动执行；拉取请求也会执行。
- 打开仓库的 **Actions → EML Windows CI**，选择对应提交的运行记录。
- 工作流进入默认分支后，可通过 **Run workflow** 手动选择分支执行。首次在功能分支接入时，使用 push 触发即可。
- 只有相应提交的整个运行显示成功，才能说它已通过云端 CI；本地验证不替代这一结果。
- 外部贡献者的首次 PR 可能需要维护者批准运行。批准运行不等于授予密钥或签名权限。

工作流使用 GitHub 托管的 Windows 2025 机器，按照 `global.json` 安装 SDK，运行 `tools/Test-CI.ps1`：

1. 还原 NuGet 依赖并以 Release 配置编译整个 `EML.slnx`。
2. 运行 `EML.Tests` 核心测试和 `EML.App.Tests` WPF 测试，不启动真实游戏或登录账号。
3. 单独 publish Windows x64 自包含输出，检查 .NET/WPF 运行时、RID 和文件路径白名单。
4. 保存 TRX 测试报告和成功时生成的 `publish-check.json`，保留 7 天。

测试失败时仍会尝试上传已经生成的报告，但不会把工作流改为成功。若在测试前失败，可能没有报告；查看失败步骤日志。相同分支和事件的旧运行可能被新运行取消，应核对提交 SHA。

## 安全与发布边界

- 工作流只有 `contents: read` 权限；checkout 不持久化 Git 凭证。
- Actions 固定到经过核对的完整提交 SHA；升级时需重新核对官方版本。
- 不使用 `pull_request_target`，不接入 SignPath Token、CurseForge Key 或本机 DPAPI 文件。
- CI 强制关闭 CurseForge 密钥嵌入，并显式清空 MSBuild 凭证路径；本地验证入口拒绝包含开发者凭证的检出。
- 不上传 publish 目录、程序集或 NuGet 缓存，只上传指定报告。报告可能包含测试名称及临时机器路径，不应加入真实账号或游戏数据。
- publish 输出只用于冒烟检查，不包含正式发布流程全部许可说明、最终完整性清单及签名验证，不能作为官方 RC1 下载包分发。
- 成功不代表在所有玩家电脑上可运行，也不替代干净电脑验收或真实整合包端到端测试。

## 本地复现

在 **Windows + PowerShell 7 + `global.json` 对应 SDK** 的干净源码检出中执行：

```powershell
./tools/Test-CI.ps1
```

不要在含有 `.eml-build/curseforge.key.dpapi` 的开发目录中执行。本脚本创建 `artifacts/ci/`，已有该目录时会拒绝覆盖，以免把旧报告或残留文件误当作本次结果。重跑使用新的干净检出；脚本不删除已有文件。

## 后续 SignPath 接入

先确认一次真实 GitHub 运行成功，再将 GitHub Actions 填入申请表的 Build System。正式签名将使用独立、受控的发布工作流：审核过的源码 → 测试 → 上传未签名构建产物 → SignPath 人工批准 → 验签 → 最终文件清单、ZIP 与 SHA-256。当前 CI 不表示已获 SignPath 批准或已有受信任证书。
