using Microsoft.UI.Reactor;

using SettingsXamlMetadataProvider =
    CommunityToolkit.WinUI.Controls.SettingsControlsRns
        .CommunityToolkit_WinUI_Controls_SettingsControls_XamlTypeInfo.XamlMetaDataProvider;

namespace YuumiX.App.Controls;

/// <summary>
/// CommunityToolkit 控件的 XAML 元数据注册。
/// <para>
/// Reactor 是非 XAML 应用，第三方的 WinRT 控件（SettingsCard/SettingsExpander）自带编译期
/// 模板资源（.pri），运行时必须注册其 <c>IXamlMetadataProvider</c>，否则模板解析失败、
/// 控件挂载后为空白。必须在 <c>ReactorApp.Run</c> 之前调用。
/// </para>
/// </summary>
internal static class ToolkitXamlMetadata
{
    public static void Register() =>
        ReactorApp.RegisterControlAssembly(new SettingsXamlMetadataProvider());
}
