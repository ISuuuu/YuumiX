using Xunit;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Tests;

/// <summary>
/// 配置版本迁移。用例逐条对应原项目 <c>config.rs</c> 的 <c>#[cfg(test)] mod tests</c>，
/// 保证同一份旧配置在两个实现下得到相同的升级结果。
/// </summary>
public sealed class ConfigMigrationTests
{
    /// <summary>
    /// v2 的 LogLevel=0 在旧实现里实际映射到 Info，迁移后应变为 1(Info)，
    /// 避免修复级别映射后存量用户被静默切到 Debug。
    /// </summary>
    [Fact]
    public void Migrate_V2_LogLevelZero_BecomesInfo()
    {
        var config = new AppConfig { Version = 2 };
        config.General.LogLevel = 0;

        Assert.True(ConfigMigration.Migrate(config));
        Assert.Equal(AppConfig.CurrentVersion, config.Version);
        Assert.Equal(1, config.General.LogLevel);
    }

    /// <summary>Info(1) 与 Error(2) 的语义在 v3 中不变，迁移不得改写。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Migrate_V2_PreservesExplicitLogLevels(int level)
    {
        var config = new AppConfig { Version = 2 };
        config.General.LogLevel = level;

        Assert.True(ConfigMigration.Migrate(config));
        Assert.Equal(level, config.General.LogLevel);
    }

    /// <summary>v1 同样要经过 v2→v3 这一步，因此 LogLevel=0 也会被改写。</summary>
    [Fact]
    public void Migrate_V1_AlsoAppliesV2Step()
    {
        var config = new AppConfig { Version = 1 };
        config.General.LogLevel = 0;

        Assert.True(ConfigMigration.Migrate(config));
        Assert.Equal(AppConfig.CurrentVersion, config.Version);
        Assert.Equal(1, config.General.LogLevel);
    }

    [Fact]
    public void Migrate_AtCurrentVersion_IsNoOp()
    {
        var config = new AppConfig();
        Assert.Equal(AppConfig.CurrentVersion, config.Version);
        Assert.False(ConfigMigration.Migrate(config));
    }

    [Fact]
    public void Migrate_FutureVersion_IsIgnored()
    {
        var config = new AppConfig { Version = AppConfig.CurrentVersion + 1 };
        Assert.False(ConfigMigration.Migrate(config));
        Assert.Equal(AppConfig.CurrentVersion + 1, config.Version);
    }

    /// <summary>
    /// 版本号异常（0 或负数）时需要收敛处理，不得让循环失控。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Migrate_InvalidVersion_ConvergesToCurrent(int version)
    {
        var config = new AppConfig { Version = version };
        config.General.LogLevel = 0;

        Assert.True(ConfigMigration.Migrate(config));
        Assert.Equal(AppConfig.CurrentVersion, config.Version);
        Assert.Equal(1, config.General.LogLevel);
    }
}
