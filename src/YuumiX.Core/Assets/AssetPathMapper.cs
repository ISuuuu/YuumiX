namespace YuumiX.Core.Assets;

/// <summary>
/// 资源路径规则：白名单、LCU 路径归一、CDragon URL 映射、content-type 推断。
///
/// 对应原实现 <c>lcu/client.rs</c> 的 <c>resolve_asset</c>（路径校验部分）、
/// <c>map_asset_path_to_cdragon_url</c>、<c>guess_content_type</c> 三个函数。
/// 全部为纯函数，无 IO、无状态——这一层最适合被大量单测覆盖，因为它的每条分支
/// 都对应一类「某张图不显示」的真实故障。
/// </summary>
public static class AssetPathMapper
{
    /// <summary>LCU 游戏资源前缀（图标、JSON 描述文件都在这下面）。</summary>
    public const string LcuGameDataPrefix = "/lol-game-data/assets/";

    /// <summary>战利品资源前缀。与游戏资源分开是因为它的大小写敏感、且不参与小写归一。</summary>
    public const string LootPrefix = "/fe/lol-loot/assets/";

    /// <summary>
    /// 战利品在 CDragon 上的映射基准。刻意比 <see cref="LootPrefix"/> 少一段 <c>assets/</c>：
    /// 原实现剥掉的是 <c>/fe/lol-loot/</c>，保留 <c>assets/...</c> 拼在插件目录之后。
    /// 两者不一致不是笔误，是 CDragon 上该插件的真实目录结构。
    /// </summary>
    private const string LootCdnPrefix = "/fe/lol-loot/";

    /// <summary>CDragon 的 latest 根地址。用 <c>latest</c> 而非具体版本号：图标只需「最新可用」。</summary>
    public const string DefaultCdnBase = "https://raw.communitydragon.org/latest";

    /// <summary>游戏资源在 CDragon 上的插件路径（<c>/v1/...</c> 走这里）。</summary>
    private const string GameDataPluginPath = "/plugins/rcp-be-lol-game-data/global/default";

    /// <summary>战利品资源在 CDragon 上的插件路径。</summary>
    private const string LootPluginPath = "/plugins/rcp-fe-lol-loot/global/default";

    /// <summary>
    /// 路径是否允许解析。三道口子与原实现一致：
    /// LCU 游戏资源、LCU 战利品资源、以及**已经在 CDragon 上的绝对地址**。
    /// 其余（含任意 <c>file://</c>、其他协议）一律拒绝——这是防止把本类当成通用下载器的唯一闸门。
    /// </summary>
    public static bool IsAllowedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return IsHttpPath(path)
               || path.StartsWith(LcuGameDataPrefix, StringComparison.Ordinal)
               || path.StartsWith(LootPrefix, StringComparison.Ordinal);
    }

    /// <summary>是否为 http(s) 绝对地址。</summary>
    public static bool IsHttpPath(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 归一化发给 LCU 的路径。
    ///
    /// 游戏资源统一转小写：LCU 的资源接口在 Windows 上大小写不敏感，但 <c>iconPath</c>
    /// 里混着 <c>ASSETS/Items/Icons2D</c> 这类写法，不同版本的响应大小写并不稳定，
    /// 归一后既避免重复缓存，也让 CDragon 的路径（其本身全小写）能直接对应上。
    /// 战利品路径**不转**：那一支是大小写敏感的。
    /// </summary>
    public static string NormalizeLcuPath(string path)
    {
        if (path.StartsWith(LcuGameDataPrefix, StringComparison.Ordinal))
        {
            return LcuGameDataPrefix + path[LcuGameDataPrefix.Length..].ToLowerInvariant();
        }

        return path;
    }

    /// <summary>
    /// 把 LCU 资源路径映射为 CDragon 地址。已经是 http(s) 的原样返回。
    ///
    /// 三条规则对应 LCU 的三种资源形态：
    /// <list type="bullet">
    ///   <item><c>assets/...</c> → <c>/game/assets/...</c>（游戏本体资源，贴图/模型）；</item>
    ///   <item><c>v1/...</c> → <c>/plugins/rcp-be-lol-game-data/global/default/v1/...</c>（元数据与图标）；</item>
    ///   <item>其余 → 退化为 <c>/game/assets/...</c>，对应 <c>iconPath</c> 不带 <c>assets/</c> 前缀的历史形态。</item>
    /// </list>
    /// </summary>
    /// <param name="path">LCU 路径或 http(s) 地址。</param>
    /// <param name="cdnBase">CDragon 根地址；测试可指向本地假服务端。</param>
    public static string ToCdnUrl(string path, string cdnBase = DefaultCdnBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(cdnBase);

        var baseUrl = cdnBase.TrimEnd('/');

        if (IsHttpPath(path))
        {
            return path;
        }

        if (path.StartsWith(LcuGameDataPrefix, StringComparison.Ordinal))
        {
            var sub = path[LcuGameDataPrefix.Length..].ToLowerInvariant();
            return sub.StartsWith("assets/", StringComparison.Ordinal)
                ? $"{baseUrl}/game/{sub}"
                : sub.StartsWith("v1/", StringComparison.Ordinal)
                    ? $"{baseUrl}{GameDataPluginPath}/{sub}"
                    : $"{baseUrl}/game/assets/{sub}";
        }

        if (path.StartsWith("/lol-game-data/", StringComparison.Ordinal))
        {
            var sub = path["/lol-game-data/".Length..].ToLowerInvariant();
            return sub.StartsWith("assets/", StringComparison.Ordinal)
                ? $"{baseUrl}/game/{sub}"
                : $"{baseUrl}{GameDataPluginPath}/{sub}";
        }

        if (path.StartsWith(LootCdnPrefix, StringComparison.Ordinal))
        {
            // 战利品保持原始大小写：CDragon 上该目录的文件名大小写与 LCU 一致
            return $"{baseUrl}{LootPluginPath}/{path[LootCdnPrefix.Length..]}";
        }

        return path;
    }

    /// <summary>
    /// 按扩展名推断 content-type。默认 png——LCU 的图标绝大多数是 png，
    /// 猜错时也只是解码器多试一次，代价远小于「返回空 content-type 让上层无从判断」。
    /// </summary>
    public static string GuessContentType(string path)
    {
        var extension = ExtractExtension(path);

        return extension switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "svg" => "image/svg+xml",
            _ => "image/png",
        };
    }

    /// <summary>
    /// content-type 对应的落盘扩展名（含点）。
    ///
    /// 为什么缓存文件要带扩展名：WinRT 的解码链对无扩展名文件的处理依赖 WIC 嗅探，
    /// 不同版本行为不一致；带上扩展名是零成本的确定性保障。未知类型退化为 <c>.png</c>，
    /// 与 <see cref="GuessContentType"/> 的默认值保持一致。
    /// </summary>
    public static string ExtensionForContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return ".png";
        }

        var normalized = contentType.Split(';')[0].Trim().ToLowerInvariant();

        return normalized switch
        {
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            _ => ".png",
        };
    }

    /// <summary>取路径或 URL 的扩展名（不含点，小写，仅字母数字）。无扩展名返回空串。</summary>
    public static string ExtractExtension(string path)
    {
        var lastSegment = path.AsSpan();
        var separator = Math.Max(lastSegment.LastIndexOf('/'), lastSegment.LastIndexOf('\\'));
        if (separator >= 0)
        {
            lastSegment = lastSegment[(separator + 1)..];
        }

        var dot = lastSegment.LastIndexOf('.');
        if (dot < 0 || dot == lastSegment.Length - 1)
        {
            return string.Empty;
        }

        var extension = lastSegment[(dot + 1)..];
        Span<char> buffer = stackalloc char[extension.Length];
        var length = 0;

        foreach (var ch in extension)
        {
            if (!char.IsLetterOrDigit(ch))
            {
                break;
            }

            buffer[length++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..length]);
    }
}
