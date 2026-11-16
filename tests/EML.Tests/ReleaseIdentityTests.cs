using System.Reflection;
using EML.Core;

namespace EML.Tests;

/// <summary>核对候选发布身份由编译元数据统一提供，避免测试版被误显示成正式版。</summary>
public sealed class ReleaseIdentityTests
{
    /// <summary>产品版本包含 RC 标记，窗口标题使用同一发布身份。</summary>
    [Fact]
    public void ReleaseCandidateIdentityIsConsistent()
    {
        Assert.Equal("1.0.0-rc.1", ReleaseIdentity.Version);
        Assert.Equal("1.0-RC1", ReleaseIdentity.DisplayVersion);
        Assert.Contains(ReleaseIdentity.DisplayVersion, ReleaseIdentity.WindowTitle);
    }

    /// <summary>程序集及 Windows 文件版本必须为有效数字格式。</summary>
    [Fact]
    public void AssemblyAndFileVersionsAreNumeric()
    {
        var assembly = typeof(ReleaseIdentity).Assembly;
        Assert.Equal(new Version(1, 0, 0, 0), assembly.GetName().Version);
        Assert.Equal("1.0.0.0", assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);
    }
}
