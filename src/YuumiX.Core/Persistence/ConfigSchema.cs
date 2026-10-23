using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YuumiX.Core.Persistence.Json;

namespace YuumiX.Core.Persistence;

/// <summary>
/// 配置文件的序列化契约：JSON 选项、遗留键名归一化、serde 缺省值语义复现。
/// </summary>
public static class ConfigSchema
{
    /// <summary>
    /// 全局序列化选项。与 Rust <c>serde_json::to_string_pretty</c> 的输出形态对齐：
    /// 两空格缩进、键名原样（不套命名策略）、整数值的 <c>double</c> 补写 <c>.0</c>。
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            // 显式声明以确保不受后续全局默认值变化影响
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            // serde_json 原样输出 UTF-8 非 ASCII 字符（中文路径很常见）。
            // System.Text.Json 默认会把它们转义成 \uXXXX，虽语义等价但会让文件不可读、
            // 且每次都产生无意义的文本 diff。此处对齐 Rust 的写法。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new WholeDoubleConverter());
        return options;
    }

    /// <summary>
    /// v1 遗留键名归一化为 v2+ 键名。
    ///
    /// 原 Rust 实现用 <c>#[serde(alias = ...)]</c> 在解析时兼容旧名，写回时一律用新名。
    /// System.Text.Json 的一个属性只能绑定一个 <c>JsonPropertyName</c>，因此改为在
    /// <see cref="JsonObject"/> 层面做键名重写——好处是归一化逻辑集中一处、可单测，
    /// 且不必为兼容旧名引入影子属性。
    /// </summary>
    public static void NormalizeLegacyKeys(JsonObject root)
    {
        if (root["General"] is not JsonObject general)
        {
            return;
        }

        Rename(general, "EnableGithubProxy", nameof(GeneralConfig.EnableHttpProxy));
        Rename(general, "GithubProxyAddr", nameof(GeneralConfig.HttpProxyAddr));
    }

    /// <summary>
    /// 复现 serde 的字段级缺省值语义。
    ///
    /// 原结构体同时标了容器级 <c>#[serde(default)]</c> 与个别字段的字段级 <c>#[serde(default)]</c>。
    /// 按 serde 规则，字段级 default 优先：缺键时取该字段类型的 <c>Default::default()</c>，
    /// 而不是容器 <c>Default</c> 实现里的值。全项目中唯一出现该冲突的是
    /// <see cref="FunctionsConfig.EnableReserveGameinfo"/>（容器默认 <c>true</c>，字段级默认 <c>false</c>）。
    ///
    /// 注意触发条件：仅当 <c>Functions</c> 对象存在、但内部缺少该键时才改写。
    /// 若整个 <c>Functions</c> 节点缺失，serde 会整节取容器默认值（<c>true</c>），此处亦保持不变。
    /// </summary>
    public static void ApplySerdeFieldDefaults(JsonObject root, AppConfig config)
    {
        if (root["Functions"] is not JsonObject functions)
        {
            return;
        }

        if (!functions.ContainsKey(nameof(FunctionsConfig.EnableReserveGameinfo)))
        {
            config.Functions.EnableReserveGameinfo = false;
        }
    }

    private static void Rename(JsonObject target, string legacyKey, string currentKey)
    {
        if (!target.TryGetPropertyValue(legacyKey, out var value))
        {
            return;
        }

        // 先摘除旧键，再以克隆写入新键：Node 有父节点约束，不能直接挂载已归属的节点。
        target.Remove(legacyKey);

        // 新旧键同时存在时以新键为准，旧键仅作丢弃处理（与 serde alias 的行为一致）。
        if (!target.ContainsKey(currentKey))
        {
            target[currentKey] = value?.DeepClone();
        }
    }
}
