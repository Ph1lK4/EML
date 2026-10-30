using System.Collections.Immutable;
using EML.Build.Secrets;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EML.Tests;

/// <summary>完全使用假环境和内存编译验证发布生成器，不接触真实凭证、磁盘生成文件或网络。</summary>
public sealed class CurseForgeSecretGeneratorTests
{
    private const string EnableVariable = "EML_EMBED_CURSEFORGE_KEY";
    private const string KeyVariable = "EML_CURSEFORGE_API_KEY";
    private const string TestSecret = "unit-test-only-not-a-real-key";

    /// <summary>其他程序集不会生成凭证类，也不会读取任何环境变量。</summary>
    [Theory]
    [InlineData("EML.App")]
    [InlineData("EML.Tests")]
    [InlineData("EML.Infrastructure.Other")]
    public void OtherAssembliesDoNotReadEnvironment(string assemblyName)
    {
        var (run, _) = Run(_ => throw new InvalidOperationException(TestSecret), assemblyName);

        Assert.Empty(run.GeneratedTrees);
        Assert.Empty(run.Diagnostics);
    }

    /// <summary>无明确发布开关时输出空凭证，即使假环境中已有密钥也不能读取。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData(" 1")]
    public void DisabledBuildGeneratesEmptyWithoutReadingKey(string? flag)
    {
        var names = new List<string>();
        var (run, compilation) = Run(name =>
        {
            names.Add(name);
            return name == EnableVariable ? flag : throw new InvalidOperationException(TestSecret);
        });

        Assert.Equal(new[] { EnableVariable }, names);
        Assert.Empty(run.Diagnostics);
        Assert.Equal(string.Empty, ConstantValue(compilation));
        Assert.DoesNotContain(TestSecret, Assert.Single(run.GeneratedTrees).ToString());
    }

    /// <summary>有效假密钥只生成内存常量；引号、斜杠和美元符号正确转义为原值。</summary>
    [Theory]
    [InlineData(TestSecret)]
    [InlineData("test-\\-\"-$-only")]
    public void EnabledBuildProducesCorrectInMemoryConstant(string secret)
    {
        var names = new List<string>();
        var (run, compilation) = Run(name =>
        {
            names.Add(name);
            return name == EnableVariable ? "1" : secret;
        });

        Assert.Equal(new[] { EnableVariable, KeyVariable }, names);
        Assert.Empty(run.Diagnostics);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(secret, ConstantValue(compilation));
        var source = Assert.Single(Assert.Single(run.Results).GeneratedSources);
        Assert.Equal("GeneratedCurseForgeSecret.g.cs", source.HintName);
    }

    /// <summary>无效输入仅产生固定诊断，不能把假密钥写进生成源码或错误信息。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(TestSecret + "\r\nInjected:value")]
    [InlineData(TestSecret + "\t")]
    [InlineData(TestSecret + "密钥")]
    public void InvalidKeyProducesFixedDiagnosticAndEmptyConstant(string? secret)
    {
        var (run, compilation) = Run(name => name == EnableVariable ? "1" : secret);

        AssertFailure(run, compilation, "EMLCF001");
    }

    /// <summary>超出请求头长度上限的密钥会在生成阶段被拒绝。</summary>
    [Fact]
    public void OversizedKeyIsRejected()
    {
        var secret = TestSecret + new string('x', 1280);
        var (run, compilation) = Run(name => name == EnableVariable ? "1" : secret);

        AssertFailure(run, compilation, "EMLCF001");
    }

    /// <summary>不安全的源码导出选项在读取密钥前被拒绝，即使大小写不同也不能绕过。</summary>
    [Theory]
    [InlineData("EmitCompilerGeneratedFiles", "true")]
    [InlineData("EmitCompilerGeneratedFiles", "True")]
    [InlineData("EmbedAllSources", "true")]
    [InlineData("EmbedUntrackedSources", "true")]
    public void UnsafeOutputIsRejectedBeforeReadingKey(string option, string value)
    {
        var names = new List<string>();
        var (run, compilation) = Run(name =>
        {
            names.Add(name);
            return name == EnableVariable ? "1" : throw new InvalidOperationException(TestSecret);
        }, options: new Dictionary<string, string> { ["build_property." + option] = value });

        Assert.Equal(new[] { EnableVariable }, names);
        AssertFailure(run, compilation, "EMLCF002");
    }

    /// <summary>无嵌入开关的日常构建允许调试生成源码，因其凭证始终为空。</summary>
    [Fact]
    public void DisabledBuildAllowsGeneratedSourceInspection()
    {
        var (run, compilation) = Run(_ => null,
            options: new Dictionary<string, string> { ["build_property.EmitCompilerGeneratedFiles"] = "true" });

        Assert.Empty(run.Diagnostics);
        Assert.Equal(string.Empty, ConstantValue(compilation));
    }

    /// <summary>环境读取异常中的秘密文本不会进入生成器诊断或编译器的异常诊断。</summary>
    [Theory]
    [InlineData(EnableVariable)]
    [InlineData(KeyVariable)]
    public void EnvironmentFailuresAreSanitized(string failedName)
    {
        var (run, compilation) = Run(name => name == failedName
            ? throw new InvalidOperationException(TestSecret)
            : "1");

        AssertFailure(run, compilation, "EMLCF003");
    }

    /// <summary>以隔离的假环境运行内存编译，不调用实际环境或磁盘写入 API。</summary>
    private static (GeneratorDriverRunResult Run, Compilation Compilation) Run(Func<string, string?> environment,
        string assemblyName = "EML.Infrastructure", IReadOnlyDictionary<string, string>? options = null)
    {
        var compilation = CSharpCompilation.Create(assemblyName,
            references: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new ISourceGenerator[] { new CurseForgeSecretGenerator(environment).AsSourceGenerator() },
            optionsProvider: new MemoryOptionsProvider(options ?? ImmutableDictionary<string, string>.Empty));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var result, out _);
        return (driver.GetRunResult(), result);
    }

    /// <summary>读取生成类的常量语义值，核实字面量转义后与输入一致。</summary>
    private static string ConstantValue(Compilation compilation)
    {
        var type = compilation.GetTypeByMetadataName("EML.Infrastructure.Minecraft.CurseForge.Api.GeneratedCurseForgeSecret");
        Assert.NotNull(type);
        var field = Assert.IsAssignableFrom<IFieldSymbol>(Assert.Single(type.GetMembers("Value")));
        return Assert.IsType<string>(field.ConstantValue);
    }

    /// <summary>检查失败只包含固定错误和空常量，不残留秘密或原始异常链。</summary>
    private static void AssertFailure(GeneratorDriverRunResult run, Compilation compilation, string id)
    {
        var diagnostic = Assert.Single(run.Diagnostics);
        Assert.Equal(id, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.DoesNotContain(TestSecret, diagnostic.ToString());
        Assert.DoesNotContain(TestSecret, Assert.Single(run.GeneratedTrees).ToString());
        Assert.Equal(string.Empty, ConstantValue(compilation));
        Assert.Null(Assert.Single(run.Results).Exception);
    }

    /// <summary>在内存中提供非秘密编译选项，避免测试创建 editorconfig 文件。</summary>
    private sealed class MemoryOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions Empty = new MemoryOptions(ImmutableDictionary<string, string>.Empty);
        public override AnalyzerConfigOptions GlobalOptions { get; }

        /// <summary>保存当前测试指定的全局编译选项。</summary>
        public MemoryOptionsProvider(IReadOnlyDictionary<string, string> values) => GlobalOptions = new MemoryOptions(values);

        /// <summary>语法树没有单独配置，始终返回空集合。</summary>
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;

        /// <summary>不使用附加文件配置，始终返回空集合。</summary>
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty;
    }

    /// <summary>只读的测试选项键值表。</summary>
    private sealed class MemoryOptions : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> _values;

        /// <summary>绑定内存字典，不接触进程环境或真实配置。</summary>
        public MemoryOptions(IReadOnlyDictionary<string, string> values) => _values = values;

        /// <summary>查询当前测试显式提供的非秘密选项。</summary>
        public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
    }
}
