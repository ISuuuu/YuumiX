namespace YuumiX.Core.Assets;

/// <summary>一次资源获取的结果。<see cref="Source"/> 只用于日志与诊断（判断「这张图是 LCU 给的还是 CDN 兜的」）。</summary>
public sealed record AssetPayload(byte[] Bytes, string ContentType, string Source)
{
    public int Length => Bytes.Length;
}

/// <summary>
/// 资源来源抽象。两个实现（LCU / CDragon）按顺序串在 <see cref="AssetService"/> 里，
/// 先命中者胜出；返回 <c>null</c> 表示「我这个来源没有」，不是错误。
/// </summary>
public interface IAssetSource
{
    /// <summary>来源名称，用于日志。</summary>
    string Name { get; }

    /// <summary>
    /// 尝试取资源。<paramref name="assetPath"/> 是 LCU 返回的 <c>iconPath</c> 形态（以 <c>/</c> 开头）
    /// 或 http(s) 绝对地址。不适用的路径应快速返回 <c>null</c>，不要抛异常。
    /// </summary>
    Task<AssetPayload?> TryGetAsync(string assetPath, CancellationToken cancellationToken = default);
}
