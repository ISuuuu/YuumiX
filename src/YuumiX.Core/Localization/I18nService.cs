using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Common;

namespace YuumiX.Core.Localization;

/// <summary>
/// 支持的语言代码常量。
/// </summary>
public static class SupportedLanguages
{
    public const string Auto = "Auto";
    public const string ZhCn = "zh_CN";
    public const string ZhTw = "zh_TW";
    public const string EnUs = "en_US";

    /// <summary>所有标准配置语言代码。</summary>
    public static readonly IReadOnlyList<string> All = [Auto, ZhCn, ZhTw, EnUs];

    /// <summary>标准语言对应的嵌入资源文件名。</summary>
    public static string ToResourceFileName(string lang) => lang switch
    {
        ZhCn => "zh-CN.json",
        ZhTw => "zh-TW.json",
        EnUs => "en-US.json",
        _ => "zh-CN.json",
    };

    /// <summary>
    /// 标准化输入的语言配置值，未知值回退为 Auto。
    /// </summary>
    public static string Normalize(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
        {
            return Auto;
        }

        // 兼容 zh-CN, zh_CN, en-US, en_US, zh-TW, zh_TW 等输入
        var normalized = lang.Replace('-', '_');
        if (string.Equals(normalized, ZhCn, StringComparison.OrdinalIgnoreCase)) return ZhCn;
        if (string.Equals(normalized, ZhTw, StringComparison.OrdinalIgnoreCase)) return ZhTw;
        if (string.Equals(normalized, EnUs, StringComparison.OrdinalIgnoreCase)) return EnUs;
        if (string.Equals(normalized, Auto, StringComparison.OrdinalIgnoreCase)) return Auto;

        return Auto;
    }
}

/// <summary>
/// 轻量级多语言本地化服务。
/// 支持嵌入式资源加载、点分键查值、缺键自动回退至 zh_CN、语言切换通知。
/// </summary>
public sealed class I18nService
{
    private const string FallbackLang = SupportedLanguages.ZhCn;
    private readonly EventStream<string> _languageChanges;
    private readonly ILogger? _logger;
    private readonly object _lock = new();

    private readonly Dictionary<string, Dictionary<string, string>> _bundles = new(StringComparer.OrdinalIgnoreCase);

    private string _configuredLanguage = SupportedLanguages.Auto;
    private string _effectiveLanguage = SupportedLanguages.ZhCn;

    public I18nService(string initialLanguage = SupportedLanguages.Auto, ILogger<I18nService>? logger = null)
    {
        _logger = logger;
        _languageChanges = new EventStream<string>(ex => _logger?.LogError(ex, "多语言订阅者抛异常"));

        LoadEmbeddedLocales();
        SetLanguage(initialLanguage, suppressNotification: true);
    }

    /// <summary>用户配置的语言设置（Auto, zh_CN, zh_TW, en_US）。</summary>
    public string ConfiguredLanguage => _configuredLanguage;

    /// <summary>当前生效的解析语言（zh_CN, zh_TW, en_US）。</summary>
    public string CurrentLanguage => _effectiveLanguage;

    /// <summary>语言切换事件流。</summary>
    public EventStream<string> CurrentLanguageChanged => _languageChanges;

    /// <summary>
    /// 切换语言设置。
    /// </summary>
    /// <param name="language">目标语言（Auto, zh_CN, zh_TW, en_US）</param>
    /// <param name="suppressNotification">是否抑制事件发布（仅用于构造初始化）</param>
    public void SetLanguage(string language, bool suppressNotification = false)
    {
        var normalizedConfig = SupportedLanguages.Normalize(language);
        var effective = ResolveEffectiveLanguage(normalizedConfig);

        bool changed;
        lock (_lock)
        {
            changed = _configuredLanguage != normalizedConfig || _effectiveLanguage != effective;
            _configuredLanguage = normalizedConfig;
            _effectiveLanguage = effective;
        }

        if (changed && !suppressNotification)
        {
            _languageChanges.Publish(_effectiveLanguage);
        }
    }

    /// <summary>
    /// 获取多语言文本。
    /// </summary>
    /// <param name="key">点分路径键（如 "nav.settings"）</param>
    /// <param name="fallback">查找失败时的回退值（若为 null，则返回 key 本身）</param>
    public string T(string key, string? fallback = null)
    {
        if (string.IsNullOrEmpty(key))
        {
            return fallback ?? string.Empty;
        }

        lock (_lock)
        {
            // 1. 在当前有效语言包中查
            if (_bundles.TryGetValue(_effectiveLanguage, out var currentBundle) &&
                currentBundle.TryGetValue(key, out var val))
            {
                return val;
            }

            // 2. 若当前不是 zh_CN，回退到 zh_CN 查
            if (!string.Equals(_effectiveLanguage, FallbackLang, StringComparison.OrdinalIgnoreCase) &&
                _bundles.TryGetValue(FallbackLang, out var fallbackBundle) &&
                fallbackBundle.TryGetValue(key, out var fallbackVal))
            {
                return fallbackVal;
            }
        }

        return fallback ?? key;
    }

    /// <summary>
    /// 将配置的语言设置解析为实际语言（Auto 根据系统 Culture 解析）。
    /// </summary>
    public static string ResolveEffectiveLanguage(string configuredLanguage, CultureInfo? culture = null)
    {
        var normalized = SupportedLanguages.Normalize(configuredLanguage);
        if (normalized != SupportedLanguages.Auto)
        {
            return normalized;
        }

        culture ??= CultureInfo.CurrentUICulture;
        var name = culture.Name;

        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            // 繁体中文匹配
            if (name.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("HK", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("MO", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Hant", StringComparison.OrdinalIgnoreCase))
            {
                return SupportedLanguages.ZhTw;
            }

            return SupportedLanguages.ZhCn;
        }

        if (name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedLanguages.EnUs;
        }

        // 其它语言默认回退至简体中文（对齐原版应用行为）
        return SupportedLanguages.ZhCn;
    }

    private void LoadEmbeddedLocales()
    {
        var assembly = typeof(I18nService).Assembly;
        var names = assembly.GetManifestResourceNames();

        string[] targets = [SupportedLanguages.ZhCn, SupportedLanguages.ZhTw, SupportedLanguages.EnUs];
        foreach (var lang in targets)
        {
            var fileName = SupportedLanguages.ToResourceFileName(lang);
            var resourceName = names.FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                _logger?.LogWarning("未找到语言包嵌入式资源: {FileName}", fileName);
                continue;
            }

            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(stream);
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                FlattenJsonElement(string.Empty, doc.RootElement, dict);
                _bundles[lang] = dict;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "加载语言包 {ResourceName} 失败", resourceName);
            }
        }
    }

    private static void FlattenJsonElement(string prefix, JsonElement element, Dictionary<string, string> dict)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var nextPrefix = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                    FlattenJsonElement(nextPrefix, prop.Value, dict);
                }
                break;

            case JsonValueKind.String:
                dict[prefix] = element.GetString() ?? string.Empty;
                break;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                dict[prefix] = element.GetRawText();
                break;
        }
    }
}
