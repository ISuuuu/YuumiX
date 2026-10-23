namespace YuumiX.Core.Common;
/// <summary>
/// 配置里的颜色字符串归一化（ADR-12）。
///
/// 配置文件里同时存在 6 位（<c>"#009faa"</c>）与 8 位（<c>"#3339b01b"</c>）两种形态：
/// 前者是早期约定的不透明色，后者是带 alpha 的卡片底色。透传给 UI 前必须补齐 alpha，
/// 否则同一个值在不同控件上会落到不同的默认不透明度（有的按不透明处理，有的按半透明）。
///
/// 放在 Core 而不是 App：这是纯字符串逻辑，没有平台类型，能被单测覆盖（§4.3 归属规则）。
/// </summary>
public static class ColorHex
{
    /// <summary>
    /// 归一化为 8 位 <c>#AARRGGBB</c>。非法或空值返回 <paramref name="fallback"/>。
    /// </summary>
    /// <param name="value">配置中的原始字符串，允许带或不带 <c>#</c>。</param>
    /// <param name="fallback">无法解析时的兜底颜色（同样会被归一化）。</param>
    public static string NormalizeArgb(string? value, string fallback)
    {
        if (TryNormalize(value, out var normalized))
        {
            return normalized;
        }

        // 兜底值本身若不合法，说明调用方给错了常量；此处不再递归，直接按不透明黑返回，
        // 让错误以「看得见但明显不对」的方式暴露，而不是抛异常打断渲染。
        return TryNormalize(fallback, out var fallbackNormalized) ? fallbackNormalized : "#FF000000";
    }

    /// <summary>
    /// 尝试归一化。支持 <c>#RGB</c>、<c>#RRGGBB</c>、<c>#AARRGGBB</c>，<c>#</c> 可省略。
    /// </summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.AsSpan().Trim();
        if (!text.IsEmpty && text[0] == '#')
        {
            text = text[1..];
        }

        if (!IsHex(text))
        {
            return false;
        }

        var upper = text.ToString().ToUpperInvariant();
        switch (upper.Length)
        {
            case 3:
                // #RGB → #FFRRGGBB
                normalized = $"#FF{upper[0]}{upper[0]}{upper[1]}{upper[1]}{upper[2]}{upper[2]}";
                return true;
            case 6:
                normalized = "#FF" + upper;
                return true;
            case 8:
                normalized = "#" + upper;
                return true;
            default:
                return false;
        }
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        foreach (var c in text)
        {
            var isHex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
