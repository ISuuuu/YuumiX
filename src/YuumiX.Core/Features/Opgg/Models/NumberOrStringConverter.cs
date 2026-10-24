using System.Text.Json;
using System.Text.Json.Serialization;

namespace YuumiX.Core.Features.Opgg.Models;

/// <summary>
/// 支持数字或字符串兼容反序列化为 string 的 JsonConverter。
/// 例如 OP.GG tier 字段可能是数字 1 也可能是字符串 "1" 或 "OP"。
/// </summary>
public sealed class NumberOrStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString(),
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
