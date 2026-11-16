# 安全问题报告 / Security policy

更新日期：2026-11-16。

## 私密联系

EML 维护者：**K4ssia**。

请将疑似安全漏洞发送至 **[477619464@qq.com](mailto:477619464@qq.com)**。一般联系、隐私问题与数据删除请求也可使用此邮箱。目前没有另行启用的反馈平台，本文不作漏洞奖励承诺。

不要在公开 Issue、评论或 PR 中发布未修复漏洞的敏感细节、可利用攻击载荷或用户凭证。对不会暴露敏感信息的普通使用问题，可提供简短、脱敏的描述。

## 报告中可以提供什么

- EML 版本、发布包来源、Windows 版本，以及问题发生在哪项功能。
- 预期结果、实际结果与尽量简短的复现步骤。
- 影响范围、是否已造成文件或账号影响。
- 必要的脱敏截图、错误文字；优先使用合成测试文件重现。

不要发送密码、验证码、access token、refresh token、API Key、签名凭证、`.dpapi` 文件或整个实例/存档。日志也可能包含电脑用户名、服务器地址、聊天内容或第三方模组输出，发送前请检查并删除无关信息。

报告邮件由维护者的邮箱服务接收。请只发送排查所需的最少信息；需要讨论保密细节、较大附件或后续公开时间时，先通过邮件沟通。EML 不会自动向该邮箱提交崩溃报告。

## 处理范围

项目仍处于开发与 RC 测试阶段，维护者会以当前开发版本及候选发布版的问题为主要排查对象。没有承诺旧版长期支持、固定响应时限或所有报告均能修复。报告人可与维护者协商修复验证及披露安排。

安全功能不等于绝对安全：本地日志、游戏进程、第三方模组、下载服务及系统环境都有各自边界。请勿以唯一一份重要存档测试可疑文件或复现数据损坏。

有关数据位置与联网行为，参见[隐私说明](PRIVACY.md)；关于移除程序和本机凭证，参见[卸载说明](docs/uninstall.md)。

## English summary

Maintainer: **K4ssia**. Report security issues privately to **477619464@qq.com**. This address also handles privacy questions.

Include the EML version, affected feature, minimal reproduction steps and redacted evidence. Do not post sensitive vulnerability details publicly, or send passwords, authentication codes, tokens, API keys, credential files or full private instances. There is no automatic crash submission to this mailbox, promised bounty or guaranteed response time. Please contact the maintainer before sending sensitive attachments or arranging disclosure.
