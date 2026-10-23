using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Persistence;

/// <summary>
/// 配置版本迁移。
///
/// 迁移脚本必须满足两条不变量：
/// <list type="number">
///   <item><b>幂等</b>：在同一版本上重复执行不产生任何变化；</item>
///   <item><b>逐级</b>：从任意旧版本出发，依次走完每个中间版本，不允许跳跃。</item>
/// </list>
/// 逐级迁移的意义在于：将来若 v4 需要基于 v3 的字段形态做变换，v1 用户也必须先经过 v3 的变换。
/// </summary>
public static class ConfigMigration
{
    private const int FirstKnownVersion = 1;

    /// <summary>
    /// 把配置就地升级到 <see cref="AppConfig.CurrentVersion"/>。
    /// </summary>
    /// <returns>是否发生了迁移。调用方据此决定是否需要落盘。</returns>
    public static bool Migrate(AppConfig config, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Version >= AppConfig.CurrentVersion)
        {
            return false;
        }

        var from = config.Version;

        // 版本号理论上不会是 0 或负数（Rust 侧为 u32 且缺键时取当前版本），
        // 但配置文件是用户可手改的，此处做一次收敛，避免异常值导致循环次数失控。
        if (config.Version < FirstKnownVersion)
        {
            logger?.LogWarning("配置版本号为 {Version}，低于已知最早版本，按 v1 处理", config.Version);
            config.Version = FirstKnownVersion;
        }

        while (config.Version < AppConfig.CurrentVersion)
        {
            switch (config.Version)
            {
                case 1:
                    // v1 → v2：HTTP 代理字段重命名（EnableGithubProxy/GithubProxyAddr
                    // → EnableHttpProxy/HttpProxyAddr）。重命名已在解析期由
                    // ConfigSchema.NormalizeLegacyKeys 完成，此处无需数据变换，
                    // 仅递增版本号以触发配置落盘重写为新格式。
                    break;

                case 2:
                    // v2 → v3：日志等级语义与设置页下拉选项对齐（0=Debug, 1=Info, 2=Error）。
                    // 旧版本的 0 实际映射到 Info（Debug 选项从未生效），因此迁移为 1 以保持
                    // 用户既有观测行为；1/2 语义不变。
                    if (config.General.LogLevel == 0)
                    {
                        config.General.LogLevel = 1;
                    }

                    break;

                default:
                    logger?.LogWarning("未知配置版本 {Version}，直接升级到当前版本", config.Version);
                    break;
            }

            config.Version++;
        }

        logger?.LogInformation("配置版本 {From} → {To}，已应用迁移", from, AppConfig.CurrentVersion);
        return true;
    }
}
