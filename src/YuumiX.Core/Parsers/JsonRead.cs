using System.Text.Json;

namespace YuumiX.Core.Parsers;

/// <summary>
/// 容错的 JSON 读取助手。
///
/// 为什么不直接用 <c>JsonSerializer.Deserialize&lt;T&gt;</c>：原项目用 serde 强类型结构体反序列化，
/// 好处是字段类型错了会立刻失败，坏处是**一处缺失就整页失败**——真实载荷里
/// <c>stats</c> 有 118 个字段、<c>mapId</c> / <c>timeline</c> / <c>subteamPlacement</c> 都可能是
/// <c>null</c> 或直接不出现（见 <c>synthetic/minimal-420.json</c>）。一旦某个可选字段类型变了，
/// serde 会把整批战绩一起丢掉，而界面只是少了几个数字。
///
/// 因此这里统一按"缺省即降级"读取：缺失 / 类型不符 / 越界一律返回调用方给的兜底值，
/// 由上层决定这个字段是否真的可缺省。**只做取值，不做业务判断**——业务降级规则留在解析器里，
/// 否则"为什么这个字段变成 0"会埋在读取层，排障时看不到。
/// </summary>
internal static class JsonRead
{
    /// <summary>取属性；对象不是 object、属性不存在或值为 <c>null</c> 时返回 <c>null</c>。</summary>
    public static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Null ? null : value;
    }

    /// <summary>取字符串；非字符串（含数字）返回 <c>null</c>，不做隐式转换。</summary>
    public static string? String(JsonElement element, string name)
    {
        JsonElement? value = Property(element, name);
        return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
    }

    /// <summary>取整数，缺失时用 <paramref name="fallback"/>。</summary>
    public static int Int(JsonElement element, string name, int fallback = 0) =>
        NullableInt(element, name) ?? fallback;

    /// <summary>取整数，缺失时为 <c>null</c>。</summary>
    public static int? NullableInt(JsonElement element, string name) => AsInt(Property(element, name));

    /// <summary>取长整数，缺失时用 <paramref name="fallback"/>。</summary>
    public static long Long(JsonElement element, string name, long fallback = 0) =>
        AsLong(Property(element, name)) ?? fallback;

    /// <summary>取布尔，缺失时用 <paramref name="fallback"/>。</summary>
    public static bool Bool(JsonElement element, string name, bool fallback = false) =>
        Property(element, name) is { } value
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            }
            : fallback;

    /// <summary>取整数数组；非数组或元素非数字时跳过该元素（原载荷出现过 <c>[7010, 0]</c> 这类含零值数组）。</summary>
    public static IReadOnlyList<int> IntArray(JsonElement element, string name)
    {
        JsonElement? value = Property(element, name);
        if (value?.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<int>(value.Value.GetArrayLength());
        foreach (JsonElement item in value.Value.EnumerateArray())
        {
            if (AsInt(item) is { } number)
            {
                result.Add(number);
            }
        }

        return result;
    }

    /// <summary>取数组元素，越界或非数组时返回 <c>null</c>。</summary>
    public static JsonElement? ElementAt(JsonElement? array, int index)
    {
        if (array?.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement value = array.Value;
        return index >= 0 && index < value.GetArrayLength() ? value[index] : null;
    }

    /// <summary>把属性当数组取，便于 <see cref="ElementAt"/> 串联。</summary>
    public static JsonElement? Array(JsonElement element, string name)
    {
        JsonElement? value = Property(element, name);
        return value?.ValueKind == JsonValueKind.Array ? value : null;
    }

    /// <summary>数组长度；非数组时为 0。</summary>
    public static int Count(JsonElement element, string name) => Array(element, name)?.GetArrayLength() ?? 0;

    /// <summary>
    /// 数值 → <see cref="int"/>。LCU 偶尔把整数写成小数（如 <c>13500.0</c>），
    /// 直接 <c>GetInt32</c> 会抛异常，这里回落为四舍五入。
    /// </summary>
    public static int? AsInt(JsonElement? element)
    {
        if (element?.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        JsonElement value = element.Value;
        if (value.TryGetInt32(out int exact))
        {
            return exact;
        }

        double raw = value.GetDouble();
        return double.IsFinite(raw) && raw is >= int.MinValue and <= int.MaxValue
            ? (int)Math.Round(raw, MidpointRounding.AwayFromZero)
            : null;
    }

    /// <summary>数值 → <see cref="long"/>，语义同 <see cref="AsInt"/>。</summary>
    public static long? AsLong(JsonElement? element)
    {
        if (element?.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        JsonElement value = element.Value;
        if (value.TryGetInt64(out long exact))
        {
            return exact;
        }

        double raw = value.GetDouble();
        return double.IsFinite(raw) && raw is >= long.MinValue and <= long.MaxValue
            ? (long)Math.Round(raw, MidpointRounding.AwayFromZero)
            : null;
    }
}
