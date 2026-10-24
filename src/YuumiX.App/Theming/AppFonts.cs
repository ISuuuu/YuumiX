using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace YuumiX.App.Theming;

/// <summary>
/// 全局字体注入。替代逐元素 <c>.FontFamily(YuumiTypography.SansFont)</c> 的粘贴式写法。
/// <para>
/// 必须在任何页面元素创建之前调用（<c>AppShell.Render()</c> 开头），否则已创建的控件不受影响。
/// 两类目标分别处理：<c>ContentControl</c> 派生控件读主题资源；<c>TextBlock</c> 在 WinUI 里
/// 没有全局字体资源，只能靠隐式样式（资源键为控件类型本身）。
/// </para>
/// </summary>
public static class AppFonts
{
    private static bool _applied;

    public static void Apply()
    {
        if (_applied || Application.Current is not { } app)
        {
            return;
        }

        _applied = true;
        var font = new FontFamily(YuumiTypography.SansFont);

        // 1) Button / ComboBox / TextBox / SettingsCard 等所有 ContentControl 派生控件。
        app.Resources["ContentControlThemeFontFamily"] = font;

        // 2) TextBlock 的隐式样式。局部显式 FontFamily（图标字体）优先级更高，不受影响。
        //    若实测出现行高异常或图标被覆盖，删除这一段即可回退，不影响上一条。
        if (!app.Resources.ContainsKey(typeof(TextBlock)))
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, font));
            app.Resources[typeof(TextBlock)] = style;
        }
    }
}
