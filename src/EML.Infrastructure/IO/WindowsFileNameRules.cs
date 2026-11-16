namespace EML.Infrastructure.IO;

/// <summary>导入与下载共用的 Windows 文件名约束，合法 Unicode 名称原样保留。</summary>
internal static class WindowsFileNameRules
{
    /// <summary>返回单段文件名被拒绝的具体原因；不裁剪空白、不转换全角标点或修改名称。</summary>
    internal static string? InvalidReason(string name)
    {
        if (name.Length == 0) return "文件名为空";
        if (name.Length > 255) return "单段文件名超过 255 个字符";
        if (name is "." or "..") return "不能使用当前目录或上级目录标记";
        foreach (var character in name)
        {
            if (character < 32) return $"含控制字符 U+{(int)character:X4}";
            if ("<>:\"/\\|?*".Contains(character))
                return $"含 Windows 不允许的字符“{character}”（U+{(int)character:X4}）";
        }
        // 仅禁止 Win32 不可靠的尾随 ASCII 空格/点；全角空格、NBSP 和中文句号均保留。
        if (name.EndsWith(' ')) return "以半角空格结尾（U+0020）";
        if (name.EndsWith('.')) return "以半角句点结尾（U+002E）";
        // 扩展名前的 ASCII 空格不能用于绕过设备名检查；Unicode 空格不等同于 ASCII 空格。
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
            return "属于 Windows 保留设备名";
        return null;
    }
}
