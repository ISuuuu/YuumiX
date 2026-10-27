namespace YuumiX.Core.Runtime;

/// <summary>
/// 运行时路径解析：区分「安装版」与「便携版」两种数据目录形态。
///
/// <list type="bullet">
///   <item>便携版：exe 同目录存在 <c>portable.flag</c> 空文件，数据落在 <c>&lt;exe&gt;/data</c>；</item>
///   <item>安装版：数据落在 <c>%APPDATA%/YuumiX</c>，与旧版 Yuumi（<c>%APPDATA%/Yuumi</c>）互不干扰。</item>
/// </list>
///
/// 首次启动时若自己的数据目录还没有 <c>config.json</c>，会从旧版 Yuumi 目录一次性复制
/// 配置与用户数据（见 <see cref="Persistence.LegacyDataMigrator"/>）；此后两个版本各自独立，
/// 旧目录只读不写，旧版 Yuumi 不受影响。便携版的数据目录本就随各自 exe 隔离，
/// 没有可探测的旧版目录，<see cref="LegacyDataDirectory"/> 为 <c>null</c>、不做迁移。
///
/// 日志目录始终为 <c>&lt;exe&gt;/log</c>（与原实现的 <c>logging.rs</c> 一致）。
/// 两个版本的可执行文件位于不同目录，因此日志天然隔离，无需按进程名区分。
/// </summary>
public sealed class RuntimePaths
{
    /// <summary>便携版标记文件名，放置于 exe 同目录。</summary>
    public const string PortableMarkerFile = "portable.flag";

    /// <summary>安装版数据目录名（YuumiX 自己的）。</summary>
    public const string DataFolderName = "YuumiX";

    /// <summary>旧版 Yuumi 的安装版数据目录名，仅供一次性迁移读取。</summary>
    public const string LegacyDataFolderName = "Yuumi";

    private RuntimePaths(bool isPortable, string executableDirectory, string dataDirectory, string? legacyDataDirectory)
    {
        IsPortable = isPortable;
        ExecutableDirectory = executableDirectory;
        DataDirectory = dataDirectory;
        LegacyDataDirectory = legacyDataDirectory;
    }

    /// <summary>是否为便携版。</summary>
    public bool IsPortable { get; }

    /// <summary>可执行文件所在目录。</summary>
    public string ExecutableDirectory { get; }

    /// <summary>数据根目录（配置、缓存、数据库均落于此）。</summary>
    public string DataDirectory { get; }

    /// <summary>
    /// 旧版 Yuumi 的数据根目录，供一次性迁移读取；便携版没有可探测的旧目录，为 <c>null</c>。
    /// </summary>
    public string? LegacyDataDirectory { get; }

    /// <summary>日志目录：恒定位于 exe 旁的 <c>log</c> 子目录。</summary>
    public string LogDirectory => Path.Combine(ExecutableDirectory, "log");

    /// <summary>配置文件完整路径：<c>&lt;DataDirectory&gt;/config.json</c>。</summary>
    public string ConfigFilePath => Path.Combine(DataDirectory, "config.json");

    /// <summary>
    /// 探测当前运行形态。
    /// </summary>
    /// <param name="executableDirectory">
    /// 可执行文件目录。传 <c>null</c> 时取 <see cref="AppContext.BaseDirectory"/>。
    /// 测试必须显式传入临时目录，避免读写真实的 <c>%APPDATA%</c>。
    /// </param>
    /// <param name="roamingAppData">
    /// 漫游 AppData 根目录。传 <c>null</c> 时取 <see cref="Environment.SpecialFolder.ApplicationData"/>
    /// （Windows 上等价于 <c>%APPDATA%</c>，与 Rust <c>dirs::config_dir()</c> 一致）。
    /// </param>
    public static RuntimePaths Detect(string? executableDirectory = null, string? roamingAppData = null)
    {
        var exeDir = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(executableDirectory ?? AppContext.BaseDirectory));

        var isPortable = File.Exists(Path.Combine(exeDir, PortableMarkerFile));
        var appDataRoot = roamingAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dataDir = isPortable
            ? Path.Combine(exeDir, "data")
            : Path.Combine(appDataRoot, DataFolderName);
        var legacyDataDir = isPortable
            ? null
            : Path.Combine(appDataRoot, LegacyDataFolderName);

        return new RuntimePaths(isPortable, exeDir, dataDir, legacyDataDir);
    }

    public override string ToString() =>
        $"RuntimePaths(Portable={IsPortable}, Data={DataDirectory}, Legacy={LegacyDataDirectory ?? "-"}, Log={LogDirectory})";
}
