namespace YuumiX.Core.Features.Tools;

/// <summary>
/// 英雄 id 的「当前版本 / 经典模式」两种形态映射。
///
/// <para><b>数据实测</b>（CN 客户端「英雄联盟（含经典模式）」，
/// <c>/lol-game-data/assets/v1/champion-summary.json</c> 共 255 条，去掉 id=-1 的「无」剩 254 条）：</para>
/// <list type="bullet">
///   <item>当前版本：id 1..999，共 173 个，别名即英雄本名（<c>Annie</c>）。</item>
///   <item>经典模式：id = <see cref="ClassicBase"/> + 当前 id，共 81 个，别名统一加 <c>Jade_</c> 前缀
///         （<c>Jade_Annie</c> = 60001 对应安妮 1）。</item>
/// </list>
/// <para>
/// 界面只展示当前版本形态，落盘配置也只存当前版本 id；真正发往 LCU 前再按本次会话的形态映射一次，
/// 这样「选的是新英雄，经典模式照样生效」。
/// </para>
/// </summary>
public static class ChampionVariants
{
    /// <summary>经典模式英雄 id 的偏移量（经典 id = 当前 id + 该偏移）。</summary>
    public const int ClassicBase = 60000;

    /// <summary>经典 id 的上界（不含）。当前版本 id 远小于 1000，留足余量不会误判。</summary>
    private const int ClassicLimit = 61000;

    /// <summary>该 id 是否为经典模式形态。</summary>
    public static bool IsClassic(int championId) => championId >= ClassicBase && championId < ClassicLimit;

    /// <summary>折算成当前版本 id（本来就是当前版本的原样返回）。</summary>
    public static int ToCurrent(int championId) => IsClassic(championId) ? championId - ClassicBase : championId;

    /// <summary>折算成经典模式 id（本来就是经典形态的原样返回）。</summary>
    public static int ToClassic(int championId) => IsClassic(championId) ? championId : championId + ClassicBase;

    /// <summary>按会话形态取 id：经典模式会话取经典 id，否则取当前版本 id。</summary>
    public static int Resolve(int championId, bool classicSession) =>
        classicSession ? ToClassic(championId) : ToCurrent(championId);
}
