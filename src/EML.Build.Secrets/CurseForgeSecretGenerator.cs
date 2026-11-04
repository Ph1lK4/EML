using System;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace EML.Build.Secrets;

/// <summary>从显式发布环境或本机加密构建输入生成内存源码，不输出明文凭证文件。</summary>
[Generator(LanguageNames.CSharp)]
public sealed class CurseForgeSecretGenerator : IIncrementalGenerator
{
    private const string TargetAssembly = "EML.Infrastructure";
    private const string EnableVariable = "EML_EMBED_CURSEFORGE_KEY";
    private const string KeyVariable = "EML_CURSEFORGE_API_KEY";
    private const string CredentialFileProperty = "build_property.EMLCurseForgeCredentialFile";
    private const int MaximumCredentialTextLength = 16 * 1024;
    private static readonly DiagnosticDescriptor InvalidCredential = new(
        "EMLCF001", "CurseForge 发布凭证无效", "发布凭证缺失或格式无效，无法完成此构建。",
        "Build", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor UnsafeSourceOutput = new(
        "EMLCF002", "禁止导出包含凭证的源码", "嵌入发布凭证时必须关闭生成源码导出与源码嵌入。",
        "Build", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor UnreadableEnvironment = new(
        "EMLCF003", "无法读取发布环境", "无法读取发布环境，请在独立构建进程中重试。",
        "Build", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor UnreadableCredentialFile = new(
        "EMLCF004", "无法读取本机加密构建凭证", "本机加密构建凭证缺失或无法解密，请重新配置开发构建授权。",
        "Build", DiagnosticSeverity.Error, true);
    private readonly Func<string, string?> _readEnvironment;
    private readonly Func<string, string?> _unprotectCredential;

    /// <summary>生产编译使用当前进程环境和 Windows 当前用户的加密构建输入。</summary>
    public CurseForgeSecretGenerator() : this(ReadProcessEnvironment) { }

    /// <summary>允许离线测试注入假环境，不读取或修改测试机器的实际环境变量。</summary>
    public CurseForgeSecretGenerator(Func<string, string?> readEnvironment)
        : this(readEnvironment, WindowsBuildCredential.Unprotect) { }

    /// <summary>允许测试以假解密函数核实读取顺序及错误处理，不需要真实凭证。</summary>
    public CurseForgeSecretGenerator(Func<string, string?> readEnvironment, Func<string, string?> unprotectCredential)
    {
        _readEnvironment = readEnvironment ?? throw new ArgumentNullException(nameof(readEnvironment));
        _unprotectCredential = unprotectCredential ?? throw new ArgumentNullException(nameof(unprotectCredential));
    }

    /// <summary>将附加文件纳入增量依赖，凭证替换后重新生成；读取内容前仍执行目标与输出检查。</summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var input = context.CompilationProvider.Select((compilation, _) => compilation.AssemblyName)
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Combine(context.AdditionalTextsProvider.Collect());
        context.RegisterSourceOutput(input, (production, values) =>
            Execute(production, values.Left.Left, values.Left.Right.GlobalOptions, values.Right));
    }

    /// <summary>环境显式授权优先，否则使用配置的加密文件；所有失败只产生不含原始异常的固定诊断。</summary>
    private void Execute(SourceProductionContext context, string? assemblyName, AnalyzerConfigOptions options,
        ImmutableArray<AdditionalText> additionalFiles)
    {
        if (!string.Equals(assemblyName, TargetAssembly, StringComparison.Ordinal)) return;
        string? enabled;
        try { enabled = _readEnvironment(EnableVariable); }
        catch
        {
            ReportFailure(context, UnreadableEnvironment);
            return;
        }
        var useEnvironment = string.Equals(enabled, "1", StringComparison.Ordinal);
        options.TryGetValue(CredentialFileProperty, out var credentialPath);
        if (!useEnvironment && string.IsNullOrEmpty(credentialPath))
        {
            AddValue(context, string.Empty);
            return;
        }
        if (IsEnabled(options, "EmitCompilerGeneratedFiles") || IsEnabled(options, "EmbedAllSources") ||
            IsEnabled(options, "EmbedUntrackedSources"))
        {
            ReportFailure(context, UnsafeSourceOutput);
            return;
        }
        string? key;
        if (!useEnvironment)
        {
            try { key = ReadCredential(context, credentialPath!, additionalFiles); }
            catch
            {
                ReportFailure(context, UnreadableCredentialFile);
                return;
            }
            if (!IsValidKey(key))
            {
                ReportFailure(context, UnreadableCredentialFile);
                return;
            }
            AddValue(context, key!);
            return;
        }
        try { key = _readEnvironment(KeyVariable); }
        catch
        {
            ReportFailure(context, UnreadableEnvironment);
            return;
        }
        if (!IsValidKey(key))
        {
            ReportFailure(context, InvalidCredential);
            return;
        }
        AddValue(context, key!);
    }

    /// <summary>只读取编译器提供的唯一、完整路径匹配文件；限制输入长度后交由当前用户解密。</summary>
    private string? ReadCredential(SourceProductionContext context, string path,
        ImmutableArray<AdditionalText> additionalFiles)
    {
        // 拒绝相对路径和盘符相对路径，避免凭证来源取决于编译进程的工作目录。
        if (!Path.IsPathRooted(path)) return null;
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
            return null;
        AdditionalText? selected = null;
        foreach (var file in additionalFiles)
        {
            if (!string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)) continue;
            if (selected != null) return null;
            selected = file;
        }
        if (selected == null) return null;
        var content = selected.GetText(context.CancellationToken);
        if (content == null || content.Length == 0 || content.Length > MaximumCredentialTextLength) return null;
        return _unprotectCredential(content.ToString());
    }

    /// <summary>识别布尔型编译输出选项，仅查询非秘密的 analyzer 配置项。</summary>
    private static bool IsEnabled(AnalyzerConfigOptions options, string name) =>
        options.TryGetValue("build_property." + name, out var value) &&
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>只接受有界的可见 ASCII 密钥，防止换行、空白或控制字符进入请求头。</summary>
    private static bool IsValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key!.Length > 1280) return false;
        foreach (var value in key) if (value < 33 || value > 126) return false;
        return true;
    }

    /// <summary>生成正确转义的内存字符串字面量；发布构建通过项目防护关闭 PDB 及源码导出。</summary>
    private static void AddValue(SourceProductionContext context, string value)
    {
        var source = "// <auto-generated/>\n" +
            "namespace EML.Infrastructure.Minecraft.CurseForge.Api\n{\n" +
            "    /// <summary>由构建输入生成的凭证；未配置授权的构建为空。</summary>\n" +
            "    internal static class GeneratedCurseForgeSecret\n    {\n" +
            "        internal const string Value = " + SymbolDisplay.FormatLiteral(value, true) + ";\n" +
            "    }\n}\n";
        // 使用 Roslyn 默认源码表示以兼容 .NET 10 编译器；维护者脚本同时关闭 PDB，避免凭证进入调试文件。
        context.AddSource("GeneratedCurseForgeSecret.g.cs", SourceText.From(source, Encoding.UTF8));
    }

    /// <summary>报告固定错误并生成空值，避免进一步诊断引用未生成类型而干扰定位。</summary>
    private static void ReportFailure(SourceProductionContext context, DiagnosticDescriptor descriptor)
    {
        context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None));
        AddValue(context, string.Empty);
    }

    /// <summary>构建工具的唯一实际环境入口；独立编译进程保证显式发布使用当前值。</summary>
#pragma warning disable RS1035 // 此生成器的明确用途是在发布编译时读取进程级凭证。
    private static string? ReadProcessEnvironment(string name) => Environment.GetEnvironmentVariable(name);
#pragma warning restore RS1035
}
