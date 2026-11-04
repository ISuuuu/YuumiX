using System.Text.Json;
using System.Text.Json.Serialization;
using YuumiX.Core.Features.Tools;

namespace YuumiX.Core.Persistence;

/// <summary>
/// 应用配置根模型，字段与原项目 <c>src-tauri/src/config.rs</c> 逐项对齐。
///
/// 对齐要点：
/// <list type="number">
///   <item>原实现用 <c>#[serde(rename_all = "PascalCase")]</c>，本模型属性名本就是 PascalCase，
///         故不采用任何命名策略，序列化键名与原文件逐字一致；</item>
///   <item>原实现每个节都标了容器级 <c>#[serde(default)]</c>，缺键时取该节 <c>Default</c> 实现的值，
///         本模型用属性初始化器等价表达；</item>
///   <item>属性声明顺序与原 Rust 结构体字段顺序保持一致，保证写回的文件在排版上也与原来一致；</item>
///   <item><see cref="ExtraFields"/> 收集本模型未建模的键并原样写回。这是与 Rust 的有意差异：
///         首次启动会从旧版 Yuumi 复制 config.json，其中可能携带本模型未建模的键
///         （旧版也可能在自己目录里继续新增字段）；若写回时丢弃未知键，这些内容会被静默抹掉。</item>
/// </list>
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// 当前配置版本号。配置结构发生破坏性变更（字段重命名/移动/类型变化）时递增，
    /// 并在 <see cref="ConfigMigration"/> 中实现从旧版本到新版本的幂等迁移。
    /// </summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// <c>LolPath</c> 列表中的特殊标记：表示该条目为「启动 WeGame」而非真实客户端路径。
    /// 真实路径不可能恰好等于该字符串，可作为安全的判别值。
    /// </summary>
    public const string WeGameMarker = "WeGame";

    /// <summary>配置版本号，缺键时视为当前版本（对齐 <c>default = "default_config_version"</c>）。</summary>
    public int Version { get; set; } = CurrentVersion;

    public GeneralConfig General { get; set; } = new();

    public PersonalizationConfig Personalization { get; set; } = new();

    public FunctionsConfig Functions { get; set; } = new();

    public OtherConfig Other { get; set; } = new();

    /// <summary>本模型未建模的键，原样保留以便无损写回。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }

    /// <summary>
    /// 修复反序列化可能引入的 <c>null</c>（JSON 中显式写了 <c>null</c> 时）。
    /// Rust 侧对非 <c>Option</c> 字段遇到 <c>null</c> 会直接解析失败；此处选择容错为默认值，
    /// 因为配置文件是用户可手改的，宁可降级也不要整份配置被判为损坏。
    /// </summary>
    public void Normalize()
    {
        General ??= new GeneralConfig();
        Personalization ??= new PersonalizationConfig();
        Functions ??= new FunctionsConfig();
        Other ??= new OtherConfig();

        General.LolPath ??= [];
        General.HttpProxyAddr ??= string.Empty;
        General.SignalrServerUrl ??= string.Empty;
        General.SignalrUserId ??= string.Empty;
        General.UploadApiUrl ??= string.Empty;

        Personalization.DpiScale ??= "Auto";
        Personalization.Language ??= "Auto";
        Personalization.ThemeMode ??= "Auto";
        Personalization.WinCardColor ??= "#3339b01b";
        Personalization.LoseCardColor ??= "#33d3190c";
        Personalization.RemakeCardColor ??= "#33a2a2a2";
        Personalization.LightDeathsNumberColor ??= "#ffb60000";
        Personalization.DarkDeathsNumberColor ??= "#ffff8d8d";

        Functions.AutoSelectChampion = NormalizeChampionIds(Functions.AutoSelectChampion ??= []);
        Functions.AutoSelectChampionTop = NormalizeChampionIds(Functions.AutoSelectChampionTop ??= []);
        Functions.AutoSelectChampionJug = NormalizeChampionIds(Functions.AutoSelectChampionJug ??= []);
        Functions.AutoSelectChampionMid = NormalizeChampionIds(Functions.AutoSelectChampionMid ??= []);
        Functions.AutoSelectChampionBot = NormalizeChampionIds(Functions.AutoSelectChampionBot ??= []);
        Functions.AutoSelectChampionSup = NormalizeChampionIds(Functions.AutoSelectChampionSup ??= []);
        Functions.AutoBanChampion = NormalizeChampionIds(Functions.AutoBanChampion ??= []);
        Functions.AutoBanChampionTop = NormalizeChampionIds(Functions.AutoBanChampionTop ??= []);
        Functions.AutoBanChampionJug = NormalizeChampionIds(Functions.AutoBanChampionJug ??= []);
        Functions.AutoBanChampionMid = NormalizeChampionIds(Functions.AutoBanChampionMid ??= []);
        Functions.AutoBanChampionBot = NormalizeChampionIds(Functions.AutoBanChampionBot ??= []);
        Functions.AutoBanChampionSup = NormalizeChampionIds(Functions.AutoBanChampionSup ??= []);
        Functions.AutoSetSummonerSpell ??= [];
        Functions.AutoSetSummonerSpellTop ??= [];
        Functions.AutoSetSummonerSpellJug ??= [];
        Functions.AutoSetSummonerSpellMid ??= [];
        Functions.AutoSetSummonerSpellBot ??= [];
        Functions.AutoSetSummonerSpellSup ??= [];
        Functions.ScreenshotOnMultikillLevels ??= [3, 4, 5, 8];
        Functions.ScreenshotSavePath ??= string.Empty;
        Functions.WatchedFriendPuuids ??= [];
        Functions.LcuUserId ??= string.Empty;

        Other.LastNoticeSha ??= string.Empty;
        Other.SearchHistory ??= string.Empty;
    }

    /// <summary>
    /// 把英雄 id 统一到「当前版本」形态并去重。
    /// <para>
    /// 选择器历史上把经典模式（Jade）英雄也列了出来，用户可能已经存下 60000+原id 的经典 id；
    /// 现在列表只列当前版本，若不折算，这些条目在选择器里认不出来（会显示成 <c>ID:60001</c>）、
    /// 选中态也对不上。真正发往 LCU 时由 <see cref="ChampionVariants"/> 按会话形态映射。
    /// </para>
    /// </summary>
    private static List<int> NormalizeChampionIds(List<int> ids)
    {
        var normalized = new List<int>(ids.Count);
        foreach (var raw in ids)
        {
            int id = ChampionVariants.ToCurrent(raw);
            if (id > 0 && !normalized.Contains(id))
            {
                normalized.Add(id);
            }
        }

        return normalized;
    }
}

/// <summary>通用设置，对应 Rust <c>GeneralConfig</c>。</summary>
public sealed class GeneralConfig
{
    public List<string> LolPath { get; set; } = [];

    /// <summary>WeGame 客户端安装路径（对应 <c>LolPath</c> 中的 <see cref="AppConfig.WeGameMarker"/> 条目，用户可手动修改）。</summary>
    public string? WegamePath { get; set; }

    public bool EnableStartLolWithApp { get; set; }

    /// <summary>
    /// 「关闭到托盘」的平台默认值：默认启用。
    /// <para>
    /// 集中定义在此，避免设置页开关与窗口关闭拦截各自写死 <c>true</c>/<c>false</c> 而出现漂移
    /// （历史上两处都按 <c>false</c> 处理，改默认为启用时漏改任一处都会造成「开关显示开、行为不生效」）。
    /// </para>
    /// </summary>
    public const bool DefaultEnableCloseToTray = true;

    /// <summary>
    /// 三元语义：<c>null</c> 表示老配置未表态，交由 UI 侧按 <see cref="DefaultEnableCloseToTray"/> 处理。
    /// 初始化器同样取该默认值，因此全新配置（无该键）与显式 <c>null</c> 都表现为启用；
    /// 用户主动关闭后写回的 <c>false</c> 不受影响。
    /// </summary>
    public bool? EnableCloseToTray { get; set; } = DefaultEnableCloseToTray;

    /// <summary>
    /// 静默启动：开启后主窗口在启动阶段即隐藏到托盘，进程与后台服务照常运行
    /// （消费方见 <c>App.cs</c> 的 <c>ActivateOnOpen</c> 与 <c>AppShell</c> 的启动隐藏 effect）。
    /// <para>
    /// 字段名沿用 Rust 版 <c>EnableGameStartMinimize</c> 以保持配置互通，但它<b>与对局无关</b>：
    /// 旧版 App.vue 是在启动时按此值决定要不要 <c>window.show()</c>，语义就是「启动时最小化」。
    /// 按字段名理解成「对局开始时最小化」会导致实现落在错误的时机上。
    /// </para>
    /// </summary>
    public bool EnableGameStartMinimize { get; set; }

    public bool EnableCheckUpdate { get; set; } = true;

    /// <summary>日志等级，与设置页下拉选项对齐：0=Debug，1=Info，2=Error。</summary>
    public int LogLevel { get; set; } = 1;

    /// <summary>v1 名为 <c>EnableGithubProxy</c>，由 <see cref="ConfigSchema.NormalizeLegacyKeys"/> 兼容。</summary>
    public bool EnableHttpProxy { get; set; }

    /// <summary>v1 名为 <c>GithubProxyAddr</c>，由 <see cref="ConfigSchema.NormalizeLegacyKeys"/> 兼容。</summary>
    public string HttpProxyAddr { get; set; } = "127.0.0.1:7897";

    public bool EnableSignalrHub { get; set; }

    public string SignalrServerUrl { get; set; } = string.Empty;

    public string SignalrUserId { get; set; } = string.Empty;

    public string UploadApiUrl { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

/// <summary>个性化设置，对应 Rust <c>PersonalizationConfig</c>。</summary>
public sealed class PersonalizationConfig
{
    public bool MicaEnabled { get; set; } = true;

    public string DpiScale { get; set; } = "Auto";

    public string Language { get; set; } = "Auto";

    public string ThemeMode { get; set; } = "Auto";

    public string WinCardColor { get; set; } = "#3339b01b";

    public string LoseCardColor { get; set; } = "#33d3190c";

    public string RemakeCardColor { get; set; } = "#33a2a2a2";

    public string LightDeathsNumberColor { get; set; } = "#ffb60000";

    public string DarkDeathsNumberColor { get; set; } = "#ffff8d8d";

    /// <summary>
    /// 侧边导航展开状态。YuumiX 自加字段，原版 Rust 侧没有（跨启动记住导航栏开合）。
    /// <c>false</c> = 收起（64px 图标栏，对齐旧版 <c>.sidebar.collapsed</c>，也是默认值）；
    /// <c>true</c> = 展开（210px，含文字标签）。
    /// </summary>
    public bool NavigationPaneOpen { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

/// <summary>功能设置，对应 Rust <c>FunctionsConfig</c>。</summary>
public sealed class FunctionsConfig
{
    public int CareerGamesNumber { get; set; } = 20;

    public int ApiConcurrencyNumber { get; set; } = 5;

    public bool GameInfoFilter { get; set; }

    public bool ShowTierInGameInfo { get; set; }

    public bool AutoShowOpgg { get; set; }

    public bool EnableOpggOnTop { get; set; }

    public bool EnableAutoAcceptMatching { get; set; }

    public bool EnableAutoReconnect { get; set; }

    public bool EnableAutoCreateLobby { get; set; }

    public int DefaultGameMode { get; set; } = 2400;

    public int AutoAcceptMatchingDelay { get; set; }

    public bool EnableRandomSkin { get; set; }

    // 自动选人（通用 + 分路）
    public bool EnableAutoSelectChampion { get; set; }

    public List<int> AutoSelectChampion { get; set; } = [];

    public List<int> AutoSelectChampionTop { get; set; } = [];

    public List<int> AutoSelectChampionJug { get; set; } = [];

    public List<int> AutoSelectChampionMid { get; set; } = [];

    public List<int> AutoSelectChampionBot { get; set; } = [];

    public List<int> AutoSelectChampionSup { get; set; } = [];

    // 自动禁人（通用 + 分路）
    public bool EnableAutoBanChampion { get; set; }

    public List<int> AutoBanChampion { get; set; } = [];

    public List<int> AutoBanChampionTop { get; set; } = [];

    public List<int> AutoBanChampionJug { get; set; } = [];

    public List<int> AutoBanChampionMid { get; set; } = [];

    public List<int> AutoBanChampionBot { get; set; } = [];

    public List<int> AutoBanChampionSup { get; set; } = [];

    public double AutoBanDelay { get; set; }

    public bool PretendBan { get; set; }

    // 自动交换
    public bool AutoAcceptCeilSwap { get; set; }

    public bool AutoAcceptChampTrade { get; set; }

    // 自动召唤师技能（通用 + 分路）
    public bool EnableAutoSetSpells { get; set; }

    public List<int> AutoSetSummonerSpell { get; set; } = [];

    public List<int> AutoSetSummonerSpellTop { get; set; } = [];

    public List<int> AutoSetSummonerSpellJug { get; set; } = [];

    public List<int> AutoSetSummonerSpellMid { get; set; } = [];

    public List<int> AutoSetSummonerSpellBot { get; set; } = [];

    public List<int> AutoSetSummonerSpellSup { get; set; } = [];

    // 对局信息保留 & LCU 实时查询
    /// <summary>
    /// 缺键时的默认值为 <c>false</c>（而非 <see cref="FunctionsConfig"/> 整体默认实例里的 <c>true</c>）。
    /// 原 Rust 结构体对本字段单独标了裸 <c>#[serde(default)]</c>，按 serde 语义字段级 default 优先于容器级 default。
    /// 由 <see cref="ConfigSchema.ApplySerdeFieldDefaults"/> 精确复现。
    /// </summary>
    public bool EnableReserveGameinfo { get; set; } = true;

    public bool LcuRealtimeEnabled { get; set; }

    public string LcuUserId { get; set; } = string.Empty;

    /// <summary>自动亮起英雄（仅预选，不锁定）。</summary>
    public bool EnableAutoHoverChampion { get; set; }

    /// <summary>倒计时结束时自动确认选择（配合 hover 模式）。</summary>
    public bool AutoSelectConfirmOnTimeout { get; set; } = true;

    /// <summary>战绩查询页浏览时的对局自动补传（默认关闭）。</summary>
    public bool UploadEnabled { get; set; } = false;

    /// <summary>隐藏云顶之弈模式（默认隐藏）。</summary>
    public bool HideTft { get; set; } = true;

    /// <summary>隐藏侧边栏「路人集」入口（默认显示）。</summary>
    public bool HideSavedPlayers { get; set; }

    /// <summary>大乱斗板凳席悬浮窗（默认开启）。</summary>
    public bool EnableBenchOverlay { get; set; } = true;

    public bool EnableScreenshotOnMultikill { get; set; }

    public List<int> ScreenshotOnMultikillLevels { get; set; } = [3, 4, 5, 8];

    public string ScreenshotSavePath { get; set; } = string.Empty;

    // 自动游戏流程
    public bool EnableAutoHandleInvite { get; set; }

    public bool EnableAutoHonor { get; set; }

    public bool EnableAutoPlayAgain { get; set; }

    public bool EnableAutoAramTeamSide { get; set; }

    public bool AramTeamSideVisibleToTeam { get; set; }

    /// <summary>选人阶段对带标记的玩家发送聊天提醒（默认关闭）。</summary>
    public bool EnableAutoTagReminder { get; set; }

    public List<string> WatchedFriendPuuids { get; set; } = [];

    public bool EnableFriendRadar { get; set; }

    /// <summary>
    /// 在对局信息中显示秒退按钮。**已废弃**：2026-10-24 确认不做该项（见 docs/requirements-gameinfo.md §19），
    /// 工具箱的开关行已撤除；字段仅为与旧版 Yuumi 配置往返保留，读写它不会有任何界面或行为变化。
    /// </summary>
    public bool EnableDodgeInGameInfo { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

/// <summary>其他设置，对应 Rust <c>OtherConfig</c>。</summary>
public sealed class OtherConfig
{
    public string LastNoticeSha { get; set; } = string.Empty;

    public string SearchHistory { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}
