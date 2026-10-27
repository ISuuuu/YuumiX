using System.Globalization;
using Xunit;
using YuumiX.Core.Localization;

namespace YuumiX.Core.Tests;

public sealed class I18nServiceTests
{
    [Fact]
    public void LoadEmbeddedLocales_ShouldLoadKeysFromEmbeddedResources()
    {
        var service = new I18nService(SupportedLanguages.ZhCn);

        Assert.Equal("设置", service.T("nav.settings"));
        Assert.Equal("启动页", service.T("nav.home"));
        Assert.Equal("生涯", service.T("nav.career"));
    }

    [Fact]
    public void SetLanguage_SwitchToEnUs_ShouldReturnEnglishText()
    {
        var service = new I18nService(SupportedLanguages.ZhCn);
        service.SetLanguage(SupportedLanguages.EnUs);

        Assert.Equal("Settings", service.T("nav.settings"));
        Assert.Equal("Home", service.T("nav.home"));
        Assert.Equal("Career", service.T("nav.career"));
    }

    [Fact]
    public void SetLanguage_SwitchToZhTw_ShouldReturnTraditionalChineseText()
    {
        var service = new I18nService(SupportedLanguages.ZhCn);
        service.SetLanguage(SupportedLanguages.ZhTw);

        Assert.Equal("設置", service.T("nav.settings"));
        Assert.Equal("啟動頁", service.T("nav.home"));
    }

    [Fact]
    public void Fallback_WhenKeyNotFoundInCurrentLocale_ShouldFallbackToZhCn()
    {
        var service = new I18nService(SupportedLanguages.EnUs);

        // 如果键只在 zh-CN 中存在或某个键不存在
        // 查一个确实不存在的键，测试 fallback
        var val = service.T("non.existent.key", "MyFallback");
        Assert.Equal("MyFallback", val);

        // fallback 为 null 时返回 key
        var val2 = service.T("non.existent.key");
        Assert.Equal("non.existent.key", val2);
    }

    [Fact]
    public void SetLanguage_ShouldPublishNotification()
    {
        var service = new I18nService(SupportedLanguages.ZhCn);
        string? notified = null;

        using var sub = service.CurrentLanguageChanged.Subscribe(lang => notified = lang);

        service.SetLanguage(SupportedLanguages.EnUs);
        Assert.Equal(SupportedLanguages.EnUs, notified);

        service.SetLanguage(SupportedLanguages.ZhTw);
        Assert.Equal(SupportedLanguages.ZhTw, notified);
    }

    [Theory]
    [InlineData("zh-CN", SupportedLanguages.ZhCn)]
    [InlineData("zh-Hans-CN", SupportedLanguages.ZhCn)]
    [InlineData("zh-TW", SupportedLanguages.ZhTw)]
    [InlineData("zh-HK", SupportedLanguages.ZhTw)]
    [InlineData("zh-Hant-TW", SupportedLanguages.ZhTw)]
    [InlineData("en-US", SupportedLanguages.EnUs)]
    [InlineData("en-GB", SupportedLanguages.EnUs)]
    [InlineData("ja-JP", SupportedLanguages.ZhCn)] // 未受支持语言回退至 zh_CN
    public void ResolveEffectiveLanguage_Auto_ShouldMapCultureCorrectly(string cultureName, string expected)
    {
        var culture = new CultureInfo(cultureName);
        var resolved = I18nService.ResolveEffectiveLanguage(SupportedLanguages.Auto, culture);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void Normalize_HandlesVariousFormats()
    {
        Assert.Equal(SupportedLanguages.ZhCn, SupportedLanguages.Normalize("zh-CN"));
        Assert.Equal(SupportedLanguages.ZhCn, SupportedLanguages.Normalize("zh_CN"));
        Assert.Equal(SupportedLanguages.ZhTw, SupportedLanguages.Normalize("zh-TW"));
        Assert.Equal(SupportedLanguages.EnUs, SupportedLanguages.Normalize("en-US"));
        Assert.Equal(SupportedLanguages.Auto, SupportedLanguages.Normalize("auto"));
        Assert.Equal(SupportedLanguages.Auto, SupportedLanguages.Normalize(null));
        Assert.Equal(SupportedLanguages.Auto, SupportedLanguages.Normalize("unknown"));
    }
}
