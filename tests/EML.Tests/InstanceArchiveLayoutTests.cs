using System.IO.Compression;
using System.Text;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Tests;

/// <summary>在内存中构造压缩包，验证包装目录与游戏载荷的边界，不操作用户实例。</summary>
public sealed class InstanceArchiveLayoutTests
{
    private const string PrismPack = """
        {"formatVersion":1,"components":[{"uid":"net.minecraft","version":"1.7.10"}]}
        """;

    /// <summary>平台包旁边还有通用实例时应拒绝，避免导入器只留下其中一份数据。</summary>
    [Theory]
    [InlineData("Other/.minecraft/custom.txt")]
    [InlineData("Other/minecraft/empty/")]
    [InlineData("Other/mods/test.jar")]
    public void RejectsGenericGameBesideWrappedPlatformPackage(string sibling)
    {
        WithArchive([
            ("Prism/mmc-pack.json", PrismPack),
            ("Prism/.minecraft/config/test.cfg", "setting=true"),
            (sibling, sibling.EndsWith('/') ? "" : "another instance")
        ], archive => Assert.Throws<InvalidDataException>(() =>
            InstanceArchiveReader.Read(archive, "mixed.zip", CancellationToken.None)));
    }

    /// <summary>常见的外层说明文件可以保留在包装目录外，不应被误判为另一个实例。</summary>
    [Fact]
    public void AcceptsReadmeOutsideWrappedPlatformPackage()
    {
        WithArchive([
            ("README.txt", "Open the enclosed instance."),
            ("Outer/Pack/mmc-pack.json", PrismPack),
            ("Outer/Pack/.minecraft/config/test.cfg", "setting=true")
        ], archive =>
        {
            var plan = InstanceArchiveReader.Read(archive, "wrapped.zip", CancellationToken.None);
            Assert.Equal("Prism", plan.Manifest.SourceFormat);
            Assert.Contains(plan.Copies, copy => copy.TargetPath == ".minecraft/config/test.cfg");
            Assert.DoesNotContain(plan.Copies, copy => copy.TargetPath == "README.txt");
        });
    }

    /// <summary>无点的 minecraft 游戏目录内也可能有同名配置文件；不能把它解析为 CurseForge 包入口。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsManifestInsideMinecraftPayload(bool prism)
    {
        var entries = new List<(string Path, string Content)>
        {
            ("Wrapped/minecraft/manifest.json", "this is game payload, not platform JSON")
        };
        if (prism)
            entries.Add(("Wrapped/mmc-pack.json", PrismPack));
        WithArchive(entries, archive =>
        {
            var plan = InstanceArchiveReader.Read(archive, "payload.zip", CancellationToken.None);
            Assert.Equal(prism ? "Prism" : "Zip", plan.Manifest.SourceFormat);
            Assert.Equal("minecraft", plan.Manifest.GameDirectory);
            Assert.Contains(plan.Copies, copy => copy.TargetPath == "minecraft/manifest.json");
        });
    }

    /// <summary>共享库与 .minecraft 并排时保留实例根目录布局，避免把游戏目录再次套进 .minecraft。</summary>
    [Fact]
    public void PreservesGenericSharedLibrariesBesideGameDirectory()
    {
        WithArchive([
            ("Wrapped/libraries/shared/example.jar", "shared library"),
            ("Wrapped/.minecraft/versions/1.7.10/1.7.10.json", "{\"id\":\"1.7.10\"}"),
            ("Wrapped/.minecraft/mods/example.jar", "mod")
        ], archive =>
        {
            var plan = InstanceArchiveReader.Read(archive, "generic.zip", CancellationToken.None);
            Assert.Equal(".minecraft", plan.Manifest.GameDirectory);
            Assert.Contains(plan.Copies, copy => copy.TargetPath == "libraries/shared/example.jar");
            Assert.Contains(plan.Copies, copy => copy.TargetPath == ".minecraft/mods/example.jar");
            Assert.DoesNotContain(plan.Copies, copy => copy.TargetPath.StartsWith(".minecraft/.minecraft/", StringComparison.Ordinal));
        });
    }

    /// <summary>让断言在 ZIP 仍打开时执行；只创建内存数据，不需要创建或删除磁盘目录。</summary>
    private static void WithArchive(IEnumerable<(string Path, string Content)> entries, Action<ZipArchive> assertion)
    {
        using var memory = new MemoryStream();
        using (var writerArchive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = writerArchive.CreateEntry(path);
                if (path.EndsWith('/'))
                    continue;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        memory.Position = 0;
        using var readerArchive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: true);
        assertion(readerArchive);
    }
}
