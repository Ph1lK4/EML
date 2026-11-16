namespace EML.Core.Minecraft.Mods.Online;

/// <summary>启动器整理的中文展示信息；不替代平台原文、项目身份或文件完整性要求。</summary>
public sealed record OnlineModTranslation(string ChineseName, string Summary);

/// <summary>按固定平台编号提供中文名称和简介，并将已收录的中文搜索词映射到原名。</summary>
public interface IOnlineModLocalization
{
    /// <summary>仅对收录的准确项目身份返回中文说明，未收录项目保留原文。</summary>
    OnlineModTranslation? Find(OnlineProjectId project);

    /// <summary>仅转换明确收录的中文名或别名；未知及有歧义的输入返回空结果。</summary>
    string? ResolveSearchTerm(string text);
}
