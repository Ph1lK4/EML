# Code signing policy / 代码签名政策

更新日期：2026-11-16。

## 当前状态 / Current status

EML 目前的 `1.0-RC1` 包**未进行代码签名**。维护者计划申请 SignPath Foundation 支持，但目前尚未启用签名流程，本文不是已获批准、受信任或已经完成安全审核的证明。

根据维护者提供的信息，GitHub / SignPath 多因素认证目前尚未配置。它是正式签名前必须完成的准备项，不能用本文档代替账号设置。当前也尚未建立供 SignPath 验证的 CI 构建和签名审批链。

The current `1.0-RC1` package is **unsigned**. SignPath support is planned, not active. This document does not claim approval, certification or a working signing integration. Multi-factor authentication and the trusted CI/approval workflow remain prerequisites to be completed.

## 项目责任人 / Team roles

| 角色 / Role | 负责人 / Member |
| --- | --- |
| 公开维护者 / Maintainer | K4ssia |
| 代码审核者 / Reviewer | K4ssia |
| 签名批准者 / Signing approver | K4ssia |

公开联系及安全报告邮箱：**[477619464@qq.com](mailto:477619464@qq.com)**。未确认的其他贡献者不会被自动列为签名批准者；上述角色不等于已经在 GitHub 或 SignPath 配置了对应权限。

## 拟采用的流程 / Planned controls

以下是接入签名时需要落实的流程要求，**不是对当前 CI 或账号配置的完成声明**：

1. 相关成员启用 GitHub 和 SignPath 多因素认证。外部提交由项目审核者检查，尤其是构建脚本和 CI 配置。
2. 从可追溯的源码提交自动构建，确认依赖、版本、发布包范围和测试结果。签名批准者核对来源后，逐次人工批准签名；不允许未受信任的 PR 自动获得签名权限。
3. 签名配置仅覆盖 EML 自身的目标程序和程序集。随包附带的 .NET、WPF、CommunityToolkit.Mvvm 等第三方文件保留原有许可及签名状态，不使用项目授权重新签署。
4. 验证签名和时间戳，再生成包含最终字节的文件完整性清单、ZIP 和 SHA-256 校验值；发布对应源码版本与说明。SHA-256 不替代发布者数字签名。
5. 在正式获批并接入后更新本页及 Release 文案，添加 SignPath 要求的服务与证书署名。未签名或签名失败的产物不得标成已签名；未来签名不会追溯改变当前 RC1 ZIP。

签名状态以具体发行文件的验证结果为准，不以网页上的徽章或本政策标题为准。代码签名不保证程序不存在漏洞，也不担保下载的整合包、模组或服务器安全。

## 隐私与报告 / Privacy and reporting

- [隐私说明 / Privacy](../PRIVACY.md)：本地数据、登录与下载请求、第三方服务及用户选择。
- [安全报告 / Security](../SECURITY.md)：私密漏洞报告渠道。
- [卸载说明 / Uninstallation](uninstall.md)：保留玩家数据和清除本机账号的边界。

目前没有自建数据接收服务，也没有启用捐赠或独立反馈平台。邮箱是用户主动联系渠道，不是程序自动上传目的地。不得把第三方登录、搜索和下载请求描述为完全不联网。

## 申请依据

准备工作参考 [SignPath Foundation Terms](https://signpath.org/terms)（核对日期：2026-11-16，页面标注 Draft）。是否接受项目及具体配置，以基金会审核和正式接入结果为准。本文不承诺审核通过，也不代替隐私或许可证合规审查。
