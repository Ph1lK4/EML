using System.Runtime.InteropServices;
using System.Security.Cryptography;
using EML.Infrastructure.Minecraft.External;

namespace EML.Tests;

/// <summary>验证新版 Modrinth 无 profile.json 时，数据库发现能真正接入目录复制。</summary>
public sealed class ModrinthFolderIntegrationTests
{
    /// <summary>数据库只是公开元数据来源，不复制账户、覆盖已安装模组或跟随待更新的组件版本。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseCandidateCanBeCopiedWithoutProfileJson(bool modern)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "EML-modrinth-copy-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "ModrinthApp");
        var source = Path.Combine(app, "profiles", "中文 实例");
        var eml = Path.Combine(root, "EML");
        Directory.CreateDirectory(Path.Combine(source, "mods"));
        Directory.CreateDirectory(eml);
        var db = Path.Combine(app, "app.db");
        try
        {
            File.WriteAllText(Path.Combine(source, "mods", "installed.jar"), "already downloaded");
            var sql = modern ? """
                CREATE TABLE instances(id TEXT,path TEXT,name TEXT,applied_content_set_id TEXT);
                CREATE TABLE instance_content_sets(id TEXT,instance_id TEXT,game_version TEXT,loader TEXT,loader_version TEXT);
                INSERT INTO instances VALUES('one','中文 实例','我的 Modrinth 实例','installed');
                INSERT INTO instance_content_sets VALUES('installed','one','1.20.1','fabric','0.16.10');
                INSERT INTO instance_content_sets VALUES('pending','one','1.21.1','neoforge','21.1.200');
                """ : """
                CREATE TABLE profiles(path TEXT,name TEXT,game_version TEXT,modloader TEXT,modloader_version TEXT);
                INSERT INTO profiles VALUES('中文 实例','我的 Modrinth 实例','1.20.1','fabric','0.16.10');
                """;
            Create(db, sql + "CREATE TABLE accounts(token TEXT); INSERT INTO accounts VALUES('PRIVATE-SENTINEL');");
            var hash = SHA256.HashData(File.ReadAllBytes(db));
            var discovery = new ExternalInstanceDiscovery([app]);
            var found = await discovery.DiscoverAsync();
            Assert.Empty(found.Warnings);
            var candidate = Assert.Single(found.Instances);
            Assert.Equal("我的 Modrinth 实例", candidate.Name);
            Assert.Equal("1.20.1", candidate.MinecraftVersion);
            Assert.Equal(source, candidate.GameDirectory);
            var imported = await new MinecraftFolderImporter(eml).ImportAsync(candidate, "copied");
            Assert.Equal("already downloaded", File.ReadAllText(Path.Combine(imported.GameDirectory, "mods", "installed.jar")));
            Assert.Contains(imported.Manifest.Components, component => component.Id == "net.fabricmc.fabric-loader" && component.Version == "0.16.10");
            Assert.DoesNotContain(imported.Manifest.Files, file => file.Path!.Contains("app.db"));
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(db)));
            Assert.False(File.Exists(db + "-shm"));
            Assert.False(File.Exists(db + "-wal"));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>只在随机测试目录创建固定架构的 SQLite 夹具。</summary>
    private static void Create(string path, string sql)
    {
        Assert.Equal(0, Native.sqlite3_open_v2(path, out var database, 6, IntPtr.Zero));
        try
        {
            var result = Native.sqlite3_exec(database, sql, IntPtr.Zero, IntPtr.Zero, out var error);
            try { Assert.True(result == 0, Marshal.PtrToStringUTF8(error)); }
            finally { if (error != IntPtr.Zero) Native.sqlite3_free(error); }
        }
        finally { Native.sqlite3_close(database); }
    }

    /// <summary>测试夹具仅调用系统 SQLite，不加载目录内的原生文件。</summary>
    private static class Native
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr state, out IntPtr error);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close(IntPtr db);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void sqlite3_free(IntPtr value);
    }
}
