namespace EML.App.Services;

/// <summary>返回已验证的本机图标路径；失败返回空值，列表继续使用占位图。</summary>
public interface IModIconCache
{
    Task<string?> GetAsync(string? address, CancellationToken token = default);
}
