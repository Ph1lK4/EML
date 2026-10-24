using Launcher.Contracts;

namespace Launcher.Runtime;

/// <summary>
/// 退出码 + 日志尾部 → 人话诊断。
/// 规则是字面量匹配（不用正则，避免日志里的怪字符触发回溯），按优先级从高到低排；
/// 先看退出码里最确定的几种，再按规则顺序扫日志最后 800 行。
/// </summary>
public sealed class CrashDiagnoser : ICrashDiagnoser
{
    private const int ScanWindow = 800;

    private static readonly CrashRule[] LogRules =
    {
        new("OOM", "内存不足（OutOfMemoryError）",
            "Java 堆被打满了。GTNH 有 300+ mod，6 GB 是底线，给 4 GB 以下基本必崩。",
            new[]
            {
                "把最大内存调到 6–8 GB（物理内存 16 GB 时）",
                "确认用的是 64 位 Java（32 位最多只能用 1.5 GB 左右）",
                "如果只在进世界时崩，先把渲染距离降到 8、关掉光影再试",
            },
            "java.lang.OutOfMemoryError", "OutOfMemoryError: Java heap space", "GC overhead limit exceeded"),

        new("JVM_CRASH", "JVM 自己崩了（hs_err）",
            "日志里有 A fatal error has been detected by the Java Runtime，属于 JVM 层崩溃，不是普通异常。",
            new[]
            {
                "打开 .minecraft 下最新的 hs_err_pid*.log，看第一段的 Problematic frame",
                "更新显卡驱动；关掉内存超频（XMP）再试",
                "换一个 Java 发行版（Temurin / Zulu）排除 JVM 自身问题",
            },
            "A fatal error has been detected by the Java Runtime"),

        new("JAVA_VERSION", "Java 版本不匹配",
            "日志里有 UnsupportedClassVersionError：class 文件版本比当前 Java 新。",
            new[]
            {
                "GTNH 的 Java 17-25 变体必须配 Java 17+；Java 8 变体才配 Java 8",
                "检查实例设置里选的 Java 与下载的包是否对应",
            },
            "UnsupportedClassVersionError", "class file version"),

        new("GL_DRIVER", "显卡 / 驱动问题",
            "窗口或 OpenGL 初始化失败，通常是驱动太旧，或笔记本双显卡没切到独显。",
            new[]
            {
                "更新显卡驱动（去 NVIDIA / AMD 官网下，不要用系统自带）",
                "笔记本双显卡：在驱动面板里强制用独显运行 java.exe",
                "关掉多显示器 / 独占全屏再试",
            },
            "Pixel format not accelerated", "Could not create window", "GLFW error"),

        new("NATIVES_MISSING", "natives 缺失",
            "日志里有 Failed to locate library：lwjgl / jinput 的 dll 没解压到 natives 目录。",
            new[]
            {
                "点启动器的「修复」，重新解压 natives",
                "确认 Java 位数与 natives 一致（都是 64 位）",
            },
            "Failed to locate library", "no lwjgl in java.library.path"),

        new("MOD_CONFLICT", "mod 冲突 / 版本不匹配",
            "日志里有 NoSuchMethodError / NoClassDefFoundError / VerifyError，典型的 mod 之间版本打架。",
            new[]
            {
                "先把最近新加的 mod 挪出 mods 目录再试（二分法）",
                "确认 Forge 版本与 mod 要求一致（GTNH 用固定版本，不要自己升）",
                "删掉 config 里对应 mod 的旧配置再启动一次",
            },
            "NoSuchMethodError", "NoClassDefFoundError", "java.lang.VerifyError", "java.lang.ClassNotFoundException"),

        new("CRASH_INIT", "游戏初始化阶段崩溃",
            "日志末尾是 The game crashed whilst initializing game：mod 或资源包在初始化时炸了。",
            new[]
            {
                "看 crash-reports 里最新那份，找 \"Caused by\" 的第一行",
                "先停用最近装的 mod / 资源包",
                "把 crash-report 发群里，别只发截图",
            },
            "The game crashed whilst initializing game"),
    };

    public CrashDiagnosis? Diagnose(IReadOnlyList<string> logLines, int? exitCode)
    {
        if (exitCode is 0) return null;

        if (exitCode is { } code && FromExitCode(code) is { } fromCode)
            return fromCode;

        var lines = logLines ?? Array.Empty<string>();
        var start = Math.Max(0, lines.Count - ScanWindow);

        foreach (var rule in LogRules)
        {
            for (var i = lines.Count - 1; i >= start; i--)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line)) continue;
                if (!rule.Patterns.Any(p => line.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;

                return new CrashDiagnosis(rule.Code, rule.Title, rule.Detail, rule.Suggestions, line.Trim());
            }
        }

        if (exitCode is null) return null;

        return new CrashDiagnosis(
            "UNKNOWN",
            "游戏异常退出",
            $"退出码 {exitCode}（0x{exitCode:X8}），日志里没有命中已知模式。",
            new[]
            {
                "把 logs/latest.log 和 crash-reports 里最新那份发出来",
                "先撤掉最近新装的 mod 再试一次",
            });
    }

    private static CrashDiagnosis? FromExitCode(int code) => code switch
    {
        unchecked((int)0xC0000135) => new CrashDiagnosis(
            "DLL_MISSING",
            "缺少 DLL（natives 没解压或位数不对）",
            "进程以 0xC0000135 退出：Windows 找不到依赖的 DLL。",
            new[]
            {
                "点「修复」重新解压 natives",
                "确认 Java 与系统都是 64 位（别混用 32 位 Java）",
                "装一下 VC++ 2015-2022 运行库",
            }),

        unchecked((int)0xC0000005) => new CrashDiagnosis(
            "ACCESS_VIOLATION",
            "内存访问冲突（0xC0000005）",
            "进程以 0xC0000005 退出：常见于 32 位 Java 撞到 1.5 GB 上限、显卡驱动异常或 natives 不匹配。",
            new[]
            {
                "换 64 位 Java",
                "更新显卡驱动",
                "确认没有混用 32/64 位 natives",
            }),

        unchecked((int)0xC0000409) => new CrashDiagnosis(
            "STACK_BUFFER",
            "栈溢出 / 安全检查失败（0xC0000409）",
            "进程以 0xC0000409 退出，多半是某个 mod 递归过深或内存越界。",
            new[]
            {
                "先把最近加的 mod 撤掉",
                "把 crash-report 发出来",
                "换 64 位 Java 并给足内存",
            }),

        _ => null,
    };

    private sealed class CrashRule
    {
        public CrashRule(string code, string title, string detail, string[] suggestions, params string[] patterns)
        {
            Code = code;
            Title = title;
            Detail = detail;
            Suggestions = suggestions;
            Patterns = patterns;
        }

        public string Code { get; }
        public string Title { get; }
        public string Detail { get; }
        public string[] Suggestions { get; }
        public string[] Patterns { get; }
    }
}
