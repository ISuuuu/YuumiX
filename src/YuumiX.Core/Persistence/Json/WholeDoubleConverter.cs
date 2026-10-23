using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YuumiX.Core.Persistence.Json;

/// <summary>
/// 保持 <c>double</c> 的原文件书写形态。
///
/// Rust 的 <c>serde_json</c> 把 <c>f64</c> 的整数部分为 0 的值写成 <c>0.0</c>，
/// 而 System.Text.Json 会写成 <c>0</c>。两者互相都能解析，但如果不处理，
/// YuumiX 每次保存都会在 <c>AutoBanDelay</c> 之类的字段上产生纯格式差异，
/// 污染 diff、也让「写回是否无损」的验证无法用文本比对。
/// </summary>
public sealed class WholeDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        // 整数形态且处于可精确表示的范围内时，补写 .0 以对齐 Rust 输出。
        if (double.IsFinite(value) && value == Math.Floor(value) && Math.Abs(value) < 1e15)
        {
            writer.WriteRawValue(
                ((long)value).ToString(CultureInfo.InvariantCulture) + ".0",
                skipInputValidation: true);
            return;
        }

        writer.WriteNumberValue(value);
    }
}
