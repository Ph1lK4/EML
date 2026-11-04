using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using EML.Build.Secrets;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace EML.Tests;

/// <summary>使用假环境、假凭证与内存编译验证授权构建，不接触真实凭证或网络。</summary>
public sealed class CurseForgeSecretGeneratorTests
{
    private const string EnableVariable = "EML_EMBED_CURSEFORGE_KEY";
    private const string KeyVariable = "EML_CURSEFORGE_API_KEY";
    private const string TestSecret = "unit-test-only-not-a-real-key";
    private const string CredentialProperty = "build_property.EMLCurseForgeCredentialFile";
    private static readonly string CredentialPath = Path.Combine(Path.GetTempPath(), "eml-generator-tests", "curseforge.key.dpapi");

    /// <summary>其他程序集不会生成凭证类，也不会读取任何环境变量。</summary>
    [Theory]
    [InlineData("EML.App")]
    [InlineData("EML.Tests")]
    [InlineData("EML.Infrastructure.Other")]
    public void OtherAssembliesDoNotReadEnvironment(string assemblyName)
    {
        var file = new MemoryAdditionalText(CredentialPath, () => throw new InvalidOperationException(TestSecret));
        var (run, _) = Run(_ => throw new InvalidOperationException(TestSecret), assemblyName,
            CredentialOptions(), new[] { file }, _ => throw new InvalidOperationException(TestSecret));

        Assert.Empty(run.GeneratedTrees);
        Assert.Empty(run.Diagnostics);
        Assert.Equal(0, file.ReadCount);
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

    /// <summary>普通构建读取指定加密文件，其他附加文件和密钥环境变量都不会被读取。</summary>
    [Fact]
    public void ConfiguredCredentialProducesConstantFromOnlyMatchingAdditionalFile()
    {
        var selected = new MemoryAdditionalText(CredentialPath, "encrypted-test-input");
        var unrelated = new MemoryAdditionalText(CredentialPath + ".other", () => throw new InvalidOperationException(TestSecret));
        var decrypted = new List<string>();
        var (run, compilation) = Run(name => name == EnableVariable ? null : throw new InvalidOperationException(TestSecret),
            options: CredentialOptions(), files: new[] { unrelated, selected }, unprotect: encoded =>
            {
                decrypted.Add(encoded);
                return TestSecret;
            });

        Assert.Empty(run.Diagnostics);
        Assert.Equal(TestSecret, ConstantValue(compilation));
        Assert.Equal(new[] { "encrypted-test-input" }, decrypted);
        Assert.Equal(1, selected.ReadCount);
        Assert.Equal(0, unrelated.ReadCount);
    }

    /// <summary>显式发布环境优先于本机文件，即使文件缺失、损坏或路径无效也不会读取它。</summary>
    [Fact]
    public void EnvironmentAuthorizationTakesPriorityOverCredentialFile()
    {
        var file = new MemoryAdditionalText(CredentialPath, () => throw new InvalidOperationException(TestSecret));
        var (run, compilation) = Run(name => name == EnableVariable ? "1" : TestSecret,
            options: CredentialOptions("relative.key"), files: new[] { file },
            unprotect: _ => throw new InvalidOperationException(TestSecret));

        Assert.Empty(run.Diagnostics);
        Assert.Equal(TestSecret, ConstantValue(compilation));
        Assert.Equal(0, file.ReadCount);
    }

    /// <summary>显式环境授权无效时直接报错，不回退到可能过期的本机凭证。</summary>
    [Fact]
    public void InvalidExplicitEnvironmentDoesNotFallbackToCredentialFile()
    {
        var file = new MemoryAdditionalText(CredentialPath, "encrypted-test-input");
        var (run, compilation) = Run(name => name == EnableVariable ? "1" : null,
            options: CredentialOptions(), files: new[] { file }, unprotect: _ => TestSecret);

        AssertFailure(run, compilation, "EMLCF001");
        Assert.Equal(0, file.ReadCount);
    }

    /// <summary>配置了本机授权却没有匹配附加文件时必须失败，不能悄悄生成无授权版本。</summary>
    [Fact]
    public void ConfiguredMissingCredentialFailsWithoutReadingUnrelatedFile()
    {
        var file = new MemoryAdditionalText(CredentialPath + ".other", () => throw new InvalidOperationException(TestSecret));
        var (run, compilation) = Run(_ => null, options: CredentialOptions(), files: new[] { file },
            unprotect: _ => throw new InvalidOperationException(TestSecret));

        AssertFailure(run, compilation, "EMLCF004");
        Assert.Equal(0, file.ReadCount);
    }

    /// <summary>相对路径不能随编译工作目录变化而指向其他文件。</summary>
    [Theory]
    [InlineData("relative.key")]
    [InlineData(".eml-build/curseforge.key.dpapi")]
    [InlineData("C:relative.key")]
    [InlineData("\\relative.key")]
    public void RelativeCredentialPathsAreRejectedBeforeReading(string path)
    {
        var file = new MemoryAdditionalText(path, () => throw new InvalidOperationException(TestSecret));
        var (run, compilation) = Run(_ => null, options: CredentialOptions(path), files: new[] { file },
            unprotect: _ => throw new InvalidOperationException(TestSecret));

        AssertFailure(run, compilation, "EMLCF004");
        Assert.Equal(0, file.ReadCount);
    }

    /// <summary>重复的凭证输入造成歧义时拒绝读取任何一个副本。</summary>
    [Fact]
    public void DuplicateCredentialInputsAreRejectedBeforeReading()
    {
        var first = new MemoryAdditionalText(CredentialPath, "encrypted-first");
        var second = new MemoryAdditionalText(CredentialPath, "encrypted-second");
        var (run, compilation) = Run(_ => null, options: CredentialOptions(), files: new[] { first, second },
            unprotect: _ => throw new InvalidOperationException(TestSecret));

        AssertFailure(run, compilation, "EMLCF004");
        Assert.Equal(0, first.ReadCount);
        Assert.Equal(0, second.ReadCount);
    }

    /// <summary>导出或嵌入生成源码的设置必须在读取、解密文件之前失败。</summary>
    [Theory]
    [InlineData("EmitCompilerGeneratedFiles")]
    [InlineData("EmbedAllSources")]
    [InlineData("EmbedUntrackedSources")]
    public void UnsafeOutputRejectsCredentialBeforeReading(string option)
    {
        var options = CredentialOptions();
        options["build_property." + option] = "true";
        var file = new MemoryAdditionalText(CredentialPath, () => throw new InvalidOperationException(TestSecret));
        var (run, compilation) = Run(_ => null, options: options, files: new[] { file },
            unprotect: _ => throw new InvalidOperationException(TestSecret));

        AssertFailure(run, compilation, "EMLCF002");
        Assert.Equal(0, file.ReadCount);
    }

    /// <summary>缺失内容、空文件和超限文件都在调用解密函数前被拒绝。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(16385)]
    public void MissingOrOversizedCredentialContentIsRejected(int length)
    {
        var file = new MemoryAdditionalText(CredentialPath, () => length < 0 ? null : SourceText.From(new string('A', length)));
        var (run, compilation) = Run(_ => null, options: CredentialOptions(), files: new[] { file },
            unprotect: _ => throw new InvalidOperationException(TestSecret));

        AssertFailure(run, compilation, "EMLCF004");
    }

    /// <summary>读取和解密异常只留下固定错误，异常携带的秘密不会进入诊断。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CredentialReadAndUnprotectFailuresAreSanitized(bool failOnRead)
    {
        var file = new MemoryAdditionalText(CredentialPath, () => failOnRead
            ? throw new InvalidOperationException(TestSecret) : SourceText.From("encrypted-test-input"));
        var (run, compilation) = Run(_ => null, options: CredentialOptions(), files: new[] { file },
            unprotect: _ => throw new InvalidOperationException(TestSecret));

        AssertFailure(run, compilation, "EMLCF004");
    }

    /// <summary>解密成功也要验证请求头格式，错误用户、空结果或非法明文都不能生成授权。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(TestSecret + "\r\n")]
    [InlineData(TestSecret + "密钥")]
    public void InvalidDecryptedCredentialIsRejected(string? plaintext)
    {
        var file = new MemoryAdditionalText(CredentialPath, "encrypted-test-input");
        var (run, compilation) = Run(_ => null, options: CredentialOptions(), files: new[] { file }, unprotect: _ => plaintext);

        AssertFailure(run, compilation, "EMLCF004");
    }

    /// <summary>真实解密入口遇到损坏的 Base64 或 DPAPI 密文时返回固定诊断。</summary>
    [Theory]
    [InlineData("not base64!")]
    [InlineData("AAECAwQ=")]
    public void CorruptEncryptedCredentialIsSanitized(string encrypted)
    {
        var file = new MemoryAdditionalText(CredentialPath, encrypted);
        var (run, compilation) = Run(_ => null, options: CredentialOptions(), files: new[] { file });

        AssertFailure(run, compilation, "EMLCF004");
    }

    /// <summary>同一增量驱动器替换或移除凭证文件后必须刷新常量，不能继续使用旧授权。</summary>
    [Fact]
    public void IncrementalFileReplacementAndRemovalRefreshAuthorization()
    {
        var original = new MemoryAdditionalText(CredentialPath, "cipher-one");
        var replacement = new MemoryAdditionalText(CredentialPath, "cipher-two");
        var compilation = CreateCompilation("EML.Infrastructure");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { new CurseForgeSecretGenerator(_ => null,
                encrypted => encrypted == "cipher-one" ? TestSecret : "replacement-test-secret").AsSourceGenerator() },
            additionalTexts: new[] { original }, optionsProvider: new MemoryOptionsProvider(CredentialOptions()));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var first, out _);
        Assert.Equal(TestSecret, ConstantValue(first));
        driver = driver.ReplaceAdditionalText(original, replacement);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var second, out _);
        Assert.Empty(driver.GetRunResult().Diagnostics);
        Assert.Equal("replacement-test-secret", ConstantValue(second));
        driver = driver.RemoveAdditionalTexts(ImmutableArray.Create<AdditionalText>(replacement));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var third, out _);
        AssertFailure(driver.GetRunResult(), third, "EMLCF004");
    }

    /// <summary>Windows 下用临时假密钥做真正的当前用户 DPAPI 往返，验证无额外依赖的解密入口。</summary>
    [Fact]
    public void WindowsCurrentUserDpapiRoundtripUsesOnlyFakeCredential()
    {
        if (!OperatingSystem.IsWindows()) return;
        var encrypted = ProtectFakeCredential(TestSecret);
        var file = new MemoryAdditionalText(CredentialPath, encrypted);
        var (run, compilation) = Run(_ => null, options: CredentialOptions(), files: new[] { file });

        Assert.Empty(run.Diagnostics);
        Assert.Equal(TestSecret, ConstantValue(compilation));
    }

    /// <summary>提供只包含非秘密路径的测试配置，不创建磁盘文件。</summary>
    private static Dictionary<string, string> CredentialOptions(string? path = null) =>
        new() { [CredentialProperty] = path ?? CredentialPath };

    /// <summary>以隔离的假环境运行内存编译，不调用实际环境或磁盘写入 API。</summary>
    private static (GeneratorDriverRunResult Run, Compilation Compilation) Run(Func<string, string?> environment,
        string assemblyName = "EML.Infrastructure", IReadOnlyDictionary<string, string>? options = null,
        IEnumerable<AdditionalText>? files = null, Func<string, string?>? unprotect = null)
    {
        var compilation = CreateCompilation(assemblyName);
        var generator = unprotect == null ? new CurseForgeSecretGenerator(environment)
            : new CurseForgeSecretGenerator(environment, unprotect);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new[] { generator.AsSourceGenerator() }, additionalTexts: files,
            optionsProvider: new MemoryOptionsProvider(options ?? ImmutableDictionary<string, string>.Empty));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var result, out _);
        return (driver.GetRunResult(), result);
    }

    /// <summary>创建最小内存编译，使测试可以检查生成常量及增量更新。</summary>
    private static CSharpCompilation CreateCompilation(string assemblyName) => CSharpCompilation.Create(assemblyName,
        references: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    /// <summary>通过 Windows 加密假密钥，所有缓冲区立即清理，测试不保存本机凭证文件。</summary>
    private static string ProtectFakeCredential(string value)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        var input = new TestDataBlob { Length = plaintext.Length, Data = Marshal.AllocHGlobal(plaintext.Length) };
        var output = new TestDataBlob();
        byte[]? encrypted = null;
        try
        {
            Marshal.Copy(plaintext, 0, input.Data, plaintext.Length);
            Assert.True(CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output));
            encrypted = new byte[output.Length];
            Marshal.Copy(output.Data, encrypted, 0, encrypted.Length);
            return Convert.ToBase64String(encrypted);
        }
        finally
        {
            Array.Clear(plaintext);
            if (encrypted != null) Array.Clear(encrypted);
            for (var index = 0; index < input.Length; index++) Marshal.WriteByte(input.Data, index, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var index = 0; index < output.Length; index++) Marshal.WriteByte(output.Data, index, 0);
                LocalFree(output.Data);
            }
        }
    }

    /// <summary>在测试中使用 Windows DATA_BLOB 的原生布局。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TestDataBlob
    {
        internal int Length;
        internal IntPtr Data;
    }

    /// <summary>仅加密固定测试字符串，使用当前用户和禁止交互标志。</summary>
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref TestDataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out TestDataBlob output);

    /// <summary>释放原生测试加密函数分配的密文缓冲区。</summary>
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

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

    /// <summary>通过内存附加文件记录读取时机，让测试检查未授权路径和失败前置条件。</summary>
    private sealed class MemoryAdditionalText : AdditionalText
    {
        private readonly Func<SourceText?> _read;
        public override string Path { get; }
        public int ReadCount { get; private set; }

        /// <summary>把固定假密文包装为内存文本，不读取磁盘。</summary>
        public MemoryAdditionalText(string path, string text) : this(path, () => SourceText.From(text)) { }

        /// <summary>允许模拟缺失内容和带秘密的读取异常。</summary>
        public MemoryAdditionalText(string path, Func<SourceText?> read)
        {
            Path = path;
            _read = read;
        }

        /// <summary>记录生成器请求内容的次数，随后返回当前测试提供的结果。</summary>
        public override SourceText? GetText(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return _read();
        }
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
