using System.Runtime.InteropServices;
using System.Security.Cryptography;
using EML.Infrastructure.Minecraft.External;

namespace EML.Tests;

/// <summary>用隔离的 SQLite 数据库验证 Modrinth 实例字段读取；不访问真实账号或安装目录。</summary>
public sealed class ModrinthProfileDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EML-Modrinth-数据库 # " + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "app.db");

    /// <summary>每项测试创建独立临时目录，中文、空格和井号也用于验证数据库路径编码。</summary>
    public ModrinthProfileDatabaseTests() => Directory.CreateDirectory(_root);

    /// <summary>旧版两种加载器列名均保留固定版本，并正确处理原版的空加载器版本。</summary>
    [Theory]
    [InlineData("modloader", "modloader_version")]
    [InlineData("loader", "loader_version")]
    public void ReadsLegacyProfiles(string loaderColumn, string versionColumn)
    {
        if (!OperatingSystem.IsWindows()) return;
        Create($"""
            CREATE TABLE profiles(path TEXT,name TEXT,game_version TEXT,{loaderColumn} TEXT,{versionColumn} TEXT);
            INSERT INTO profiles VALUES('测试实例','我的模组实例','1.20.1','fabric','0.16.10');
            INSERT INTO profiles VALUES('vanilla','原版','1.21.1','vanilla',NULL);
            """);

        var rows = ModrinthProfileDatabase.Read(Database, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Contains(new ModrinthInstalledProfile("测试实例", "我的模组实例", "1.20.1", "fabric", "0.16.10"), rows);
        Assert.Contains(new ModrinthInstalledProfile("vanilla", "原版", "1.21.1", "vanilla", null), rows);
    }

    /// <summary>新版只使用同一实例当前已应用的内容集，不误用待更新版本或其他实例的内容集。</summary>
    [Fact]
    public void ReadsOnlyOwnedAppliedContentSet()
    {
        if (!OperatingSystem.IsWindows()) return;
        Create("""
            CREATE TABLE instances(id TEXT,path TEXT,name TEXT,applied_content_set_id TEXT);
            CREATE TABLE instance_content_sets(id TEXT,instance_id TEXT,game_version TEXT,loader TEXT,loader_version TEXT);
            INSERT INTO instances VALUES('one','第一实例','已安装实例','applied');
            INSERT INTO instance_content_sets VALUES('applied','one','1.20.1','forge','47.4.0');
            INSERT INTO instance_content_sets VALUES('pending','one','1.21.1','neoforge','21.1.200');
            INSERT INTO instances VALUES('two','第二实例','其他实例','applied');
            """);

        var row = Assert.Single(ModrinthProfileDatabase.Read(Database, CancellationToken.None));

        Assert.Equal(new ModrinthInstalledProfile("第一实例", "已安装实例", "1.20.1", "forge", "47.4.0"), row);
    }

    /// <summary>公开字段读取不会改动数据库和账号哨兵，也不会创建 WAL、共享内存或日志文件。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadingDoesNotModifyDatabaseOrCreateSidecars(bool walMode)
    {
        if (!OperatingSystem.IsWindows()) return;
        Create("""
            CREATE TABLE profiles(path TEXT,name TEXT,game_version TEXT,loader TEXT,loader_version TEXT);
            INSERT INTO profiles VALUES('instance','公开实例','1.20.1','fabric','0.16.10');
            CREATE TABLE accounts(id TEXT,access_token TEXT,refresh_token BLOB);
            INSERT INTO accounts VALUES('synthetic-account','NOT-A-REAL-CREDENTIAL',zeroblob(65536));
            """ + (walMode ? "PRAGMA journal_mode=WAL; PRAGMA wal_checkpoint(TRUNCATE);" : ""));
        var before = Snapshot();
        var databaseTime = File.GetLastWriteTimeUtc(Database);

        var row = Assert.Single(ModrinthProfileDatabase.Read(Database, CancellationToken.None));

        Assert.Equal("公开实例", row.Name);
        Assert.DoesNotContain("NOT-A-REAL-CREDENTIAL", row.ToString());
        Assert.Equal(databaseTime, File.GetLastWriteTimeUtc(Database));
        var after = Snapshot();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, hash) in before) Assert.Equal(hash, after[path]);
    }

    /// <summary>未合并写入必须要求用户退出旧启动器，不 checkpoint 或丢弃待写入记录。</summary>
    [Theory]
    [InlineData("-wal")]
    [InlineData("-journal")]
    public void RefusesNonemptyJournalWithoutChangingIt(string suffix)
    {
        if (!OperatingSystem.IsWindows()) return;
        Create(LegacySchema);
        var journal = Database + suffix;
        File.WriteAllText(journal, "synthetic pending update");
        var before = Snapshot();

        var error = Assert.Throws<IOException>(() => ModrinthProfileDatabase.Read(Database, CancellationToken.None));

        Assert.Contains("退出", error.Message);
        Assert.Contains(Database, error.Message);
        var after = Snapshot();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, hash) in before) Assert.Equal(hash, after[path]);
    }

    /// <summary>未知结构或缺少关键版本字段不能降级为猜测原版。</summary>
    [Theory]
    [InlineData("CREATE TABLE unrelated(id TEXT);")]
    [InlineData("CREATE TABLE profiles(path TEXT,name TEXT);")]
    [InlineData("CREATE TABLE profiles(path TEXT,name TEXT,game_version TEXT);")]
    [InlineData("CREATE TABLE instances(id TEXT,path TEXT,name TEXT); CREATE TABLE instance_content_sets(id TEXT);")]
    public void RefusesUnsupportedSchema(string schema)
    {
        if (!OperatingSystem.IsWindows()) return;
        Create(schema);
        Assert.Throws<InvalidDataException>(() => ModrinthProfileDatabase.Read(Database, CancellationToken.None));
    }

    /// <summary>关键游戏版本为空时拒绝返回不完整候选。</summary>
    [Fact]
    public void RefusesMissingMinecraftVersion()
    {
        if (!OperatingSystem.IsWindows()) return;
        Create(LegacySchema + "INSERT INTO profiles VALUES('instance','test',NULL,'vanilla',NULL);");
        Assert.Throws<InvalidDataException>(() => ModrinthProfileDatabase.Read(Database, CancellationToken.None));
    }

    /// <summary>有界读取不会接受超过字段长度限制的实例显示名。</summary>
    [Fact]
    public void RefusesOversizedPublicField()
    {
        if (!OperatingSystem.IsWindows()) return;
        Create(LegacySchema + "INSERT INTO profiles VALUES('instance',replace(hex(zeroblob(2049)),'0','a'),'1.20.1','vanilla',NULL);");
        Assert.Throws<InvalidDataException>(() => ModrinthProfileDatabase.Read(Database, CancellationToken.None));
    }

    /// <summary>实例数量超过上限时拒绝静默截断，提示用户改用导出包。</summary>
    [Fact]
    public void RefusesTooManyRows()
    {
        if (!OperatingSystem.IsWindows()) return;
        Create(LegacySchema + """
            WITH RECURSIVE sequence(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM sequence WHERE n<2049)
            INSERT INTO profiles SELECT 'instance-'||n,'test','1.20.1','vanilla',NULL FROM sequence;
            """);
        Assert.Throws<InvalidDataException>(() => ModrinthProfileDatabase.Read(Database, CancellationToken.None));
    }

    /// <summary>预先取消时不打开数据库，也不创建缺失文件。</summary>
    [Fact]
    public void CancellationDoesNotOpenMissingDatabase()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ModrinthProfileDatabase.Read(Database, cancellation.Token));
        Assert.False(File.Exists(Database));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    /// <summary>缺失数据库不应被 SQLite 自动创建为空数据库。</summary>
    [Fact]
    public void MissingDatabaseIsNotCreated()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<FileNotFoundException>(() => ModrinthProfileDatabase.Read(Database, CancellationToken.None));
        Assert.False(File.Exists(Database));
    }

    private const string LegacySchema = "CREATE TABLE profiles(path TEXT,name TEXT,game_version TEXT,loader TEXT,loader_version TEXT);";

    /// <summary>仅在本测试随机临时目录创建数据；SQL 均来自测试内固定字符串。</summary>
    private void Create(string sql)
    {
        var opened = Native.sqlite3_open_v2(Database, out var handle, 2 | 4, IntPtr.Zero);
        try
        {
            Assert.Equal(0, opened);
            var result = Native.sqlite3_exec(handle, sql, IntPtr.Zero, IntPtr.Zero, out var error);
            try { Assert.True(result == 0, Marshal.PtrToStringUTF8(error)); }
            finally { if (error != IntPtr.Zero) Native.sqlite3_free(error); }
        }
        finally { if (handle != IntPtr.Zero) Native.sqlite3_close(handle); }
    }

    /// <summary>记录测试目录每个文件的摘要，以检查只读路径不会产生隐藏副作用。</summary>
    private Dictionary<string, string> Snapshot() => Directory.EnumerateFiles(_root)
        .ToDictionary(file => Path.GetFileName(file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), StringComparer.Ordinal);

    /// <summary>只清理当前测试拥有的随机目录，不涉及真实启动器文件。</summary>
    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>使用 Windows 系统 SQLite 创建测试夹具；生产读取仍由被测代码执行。</summary>
    private static class Native
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags, IntPtr vfs);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr state, out IntPtr error);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close(IntPtr database);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void sqlite3_free(IntPtr memory);
    }
}
