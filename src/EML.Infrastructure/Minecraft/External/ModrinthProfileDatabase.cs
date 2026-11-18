using System.Runtime.InteropServices;
using EML.Infrastructure.Minecraft.Importing;

namespace EML.Infrastructure.Minecraft.External;

/// <summary>只携带公开的游戏版本字段，不读取或传递启动器账户记录。</summary>
internal sealed record ModrinthInstalledProfile(string Path, string Name, string MinecraftVersion, string Loader, string? LoaderVersion);

/// <summary>通过 Windows 系统 SQLite 只读查询 Modrinth 的实例元数据，无需额外原生发行依赖。</summary>
internal static class ModrinthProfileDatabase
{
    /// <summary>只查询固定表和列；未结束的 WAL 会要求退出原启动器，绝不写回或 checkpoint 用户数据库。</summary>
    internal static IReadOnlyList<ModrinthInstalledProfile> Read(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Modrinth 数据库实例发现目前支持 Windows。");
        InstanceImportPaths.EnsureNoLinks(path);
        foreach (var suffix in new[] { "-wal", "-journal" })
        {
            InstanceImportPaths.EnsureNoLinks(path + suffix);
            if (File.Exists(path + suffix) && new FileInfo(path + suffix).Length > 0)
                throw new IOException("Modrinth 数据库仍有未合并的写入，请正常退出 Modrinth App 后重试：" + path);
        }
        using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (guard.Length > 256L * 1024 * 1024) throw new InvalidDataException("Modrinth 实例数据库超过 256 MiB，请使用导出包导入。");
        var handle = IntPtr.Zero;
        try
        {
            // 持有禁止写入的文件句柄且确认没有 WAL 后使用 immutable，禁止 SQLite 创建 -shm 或恢复日志。
            var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro&immutable=1";
            if (Native.sqlite3_open_v2(uri, out handle, 1 | 0x40 /* READONLY | URI */, IntPtr.Zero) != 0)
                throw new IOException("无法只读打开 Modrinth 实例数据库：" + path);
            Native.sqlite3_busy_timeout(handle, 1000);
            var tables = Query(handle, "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('profiles','instances','instance_content_sets')", 1, token)
                .Select(row => row[0]).ToHashSet(StringComparer.Ordinal);
            string sql;
            if (tables.Contains("instances") && tables.Contains("instance_content_sets"))
                sql = "SELECT i.path,i.name,c.game_version,c.loader,c.loader_version FROM instances i JOIN instance_content_sets c ON c.id=i.applied_content_set_id AND c.instance_id=i.id LIMIT 2049";
            else if (tables.Contains("profiles"))
            {
                var columns = Query(handle, "PRAGMA table_info(profiles)", 6, token).Select(row => row[1]).ToHashSet(StringComparer.Ordinal);
                if (!new[] { "path", "name", "game_version" }.All(columns.Contains))
                    throw new InvalidDataException("Modrinth profiles 表缺少公开版本字段，请使用导出包导入。");
                if (columns.Contains("modloader") && columns.Contains("modloader_version"))
                    sql = "SELECT path,name,game_version,modloader,modloader_version FROM profiles LIMIT 2049";
                else if (columns.Contains("loader") && columns.Contains("loader_version"))
                    sql = "SELECT path,name,game_version,loader,loader_version FROM profiles LIMIT 2049";
                else throw new InvalidDataException("Modrinth 数据库加载器字段不受支持，请使用导出包导入。");
            }
            else throw new InvalidDataException("Modrinth 数据库结构不受支持，请使用导出包导入。");
            return Query(handle, sql, 5, token).Select(row => new ModrinthInstalledProfile(
                row[0] ?? throw new InvalidDataException("实例路径为空。"), row[1] ?? "Modrinth 实例",
                row[2] ?? throw new InvalidDataException("Minecraft 版本为空。"),
                row[3] ?? throw new InvalidDataException("加载器类型为空。"), row[4])).ToArray();
        }
        finally { if (handle != IntPtr.Zero) Native.sqlite3_close(handle); }
    }

    /// <summary>执行代码内固定的只读查询，限制返回行、字段长度与 SQLite 指令数量。</summary>
    private static IReadOnlyList<string?[]> Query(IntPtr database, string sql, int columns, CancellationToken token)
    {
        var statement = IntPtr.Zero;
        var instructions = 0;
        Native.Progress callback = _ => token.IsCancellationRequested || ++instructions > 100_000 ? 1 : 0;
        Native.sqlite3_progress_handler(database, 1000, callback, IntPtr.Zero);
        try
        {
            if (Native.sqlite3_prepare_v2(database, sql, -1, out statement, IntPtr.Zero) != 0)
                throw new InvalidDataException("无法读取 Modrinth 的公开实例字段，可能是数据库版本不受支持。");
            var rows = new List<string?[]>();
            int result;
            while ((result = Native.sqlite3_step(statement)) == 100)
            {
                token.ThrowIfCancellationRequested();
                if (rows.Count >= 2048) throw new InvalidDataException("Modrinth 实例过多，请使用导出包导入。");
                var row = new string?[columns];
                for (var i = 0; i < columns; i++)
                {
                    if (Native.sqlite3_column_bytes(statement, i) > 4096) throw new InvalidDataException("Modrinth 实例字段过长。");
                    row[i] = Marshal.PtrToStringUTF8(Native.sqlite3_column_text(statement, i));
                }
                rows.Add(row);
            }
            token.ThrowIfCancellationRequested();
            if (result != 101) throw new InvalidDataException("Modrinth 实例数据库读取失败，请关闭 Modrinth App 后重试。");
            return rows;
        }
        finally
        {
            if (statement != IntPtr.Zero) Native.sqlite3_finalize(statement);
            Native.sqlite3_progress_handler(database, 0, null, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
    }

    /// <summary>仅加载 System32 的系统 DLL，防止从实例目录加载同名原生库。</summary>
    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Progress(IntPtr state);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr database, int flags, IntPtr vfs);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close(IntPtr database);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void sqlite3_progress_handler(IntPtr database, int instructions, Progress? callback, IntPtr state);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_prepare_v2(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(IntPtr statement);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(IntPtr statement);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_bytes(IntPtr statement, int column);
    }
}
