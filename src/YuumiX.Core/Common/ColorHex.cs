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

    /// <summary>
    /// 解析为 ARGB 四分量。供颜色选择器等需要数值形态的控件使用（UI 侧再转成
    /// <c>Windows.UI.Color</c>，Core 不引入任何平台类型）。
    /// </summary>
    public static bool TryParseArgb(string? value, out byte a, out byte r, out byte g, out byte b)
    {
        a = 255;
        r = 0;
        g = 0;
        b = 0;

        if (!TryNormalize(value, out var normalized))
        {
            return false;
        }

        // TryNormalize 的产物恒为 #AARRGGBB
        a = ParseByte(normalized, 1);
        r = ParseByte(normalized, 3);
        g = ParseByte(normalized, 5);
        b = ParseByte(normalized, 7);
        return true;

        static byte ParseByte(string text, int offset) =>
            byte.Parse(text.AsSpan(offset, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>由 ARGB 四分量拼回 <c>#AARRGGBB</c>（大写），与配置文件里的写法一致。</summary>
    public static string ToArgb(byte a, byte r, byte g, byte b) =>
        $"#{a:X2}{r:X2}{g:X2}{b:X2}";

    /// <summary>由 RGB 三分量拼成 <c>#RRGGBB</c>（大写）。</summary>
    public static string ToRgb(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    /// <summary>
    /// 丢掉 alpha，拼成 <c>#RRGGBB</c>。主题色、死亡数字色在配置里保存为 8 位，
    /// 但原实现的取色控件按 6 位呈现（不透明），两者互转时用本方法。
    /// </summary>
    public static string ToRgb(string? value, string fallback)
    {
        var normalized = NormalizeArgb(value, fallback);
        return "#" + normalized[3..];
    }

    private static bool IsHex(ReadOnlySpan<char> text)    {
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
