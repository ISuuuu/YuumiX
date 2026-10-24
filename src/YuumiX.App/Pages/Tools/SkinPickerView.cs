using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Tools.Models;

namespace YuumiX.App.Pages.Tools;

/// <summary>
/// 皮肤选择器视图（对应原 ProfileBackgroundCard.vue 的皮肤模态窗）。
/// </summary>
internal static class SkinPickerView
{
    public static Element Render(
        string champIdInput,
        Action<string> onChampIdInputChanged,
        bool isLoading,
        IReadOnlyList<ChampionSkinItem> skins,
        int activeSkinIndex,
        Action<int> onSelectSkinIndex,
        Action<int> onApplySkin,
        Action onSearchSkins,
        YuumiPalette palette)
    {
        Element searchBar = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                TextBox(champIdInput, onChampIdInputChanged, "输入英雄 ID（如 1=安妮, 157=亚索, 99=拉克丝, 222=金克丝）...")
                    .Margin(new Thickness(0, 0, 8, 0))
                    .AutomationName("英雄ID输入框")
                    .Grid(row: 0, column: 0),
                Button(isLoading ? "加载中..." : "获取皮肤列表", onSearchSkins)
                    .IsEnabled(!isLoading)
                    .AutomationName("获取皮肤按钮")
                    .Grid(row: 0, column: 1),
            ]
        ).Margin(new Thickness(0, 0, 0, 10));

        if (skins.Count == 0)
        {
            return VStack(0,
                searchBar,
                Border(
                    TextBlock(isLoading ? "正在从客户端加载皮肤资源..." : "输入英雄 ID 并点击「获取皮肤列表」以预览并设置背景")
                        .Foreground(Theme.SecondaryText)
                        .FontSize(12)
                        .HAlign(HorizontalAlignment.Center)
                        .Margin(new Thickness(0, 16, 0, 16))
                )
            );
        }

        var activeSkin = skins[Math.Clamp(activeSkinIndex, 0, skins.Count - 1)];

        int prevIdx = activeSkinIndex > 0 ? activeSkinIndex - 1 : skins.Count - 1;
        int nextIdx = activeSkinIndex < skins.Count - 1 ? activeSkinIndex + 1 : 0;

        Element carouselControls = HStack(8,
            Button(Caption("\uE76B"), () => onSelectSkinIndex(prevIdx))
                .Padding(8, 4)
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .AutomationName("上一款皮肤"),
            TextBlock($"{activeSkinIndex + 1} / {skins.Count}")
                .FontSize(12)
                .Foreground(Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center),
            Button(Caption("\uE76C"), () => onSelectSkinIndex(nextIdx))
                .Padding(8, 4)
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .AutomationName("下一款皮肤")
        ).VAlign(VerticalAlignment.Center);

        Element previewSection = Border(
            Grid(
                [GridSize.Star(), GridSize.Auto, GridSize.Auto],
                [GridSize.Auto],
                [
                    VStack(4,
                        Body(activeSkin.Name),
                        Caption($"皮肤 ID: {activeSkin.Id} {(activeSkin.IsBase ? "（默认原皮）" : "")}")
                            .Foreground(Theme.SecondaryText)
                    ).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 0),

                    carouselControls.Margin(new Thickness(12, 0, 12, 0)).Grid(row: 0, column: 1),

                    Button("应用此背景", () => onApplySkin(activeSkin.Id))
                        .Background(palette.Accent)
                        .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush"))
                        .AutomationName("应用当前皮肤背景")
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 2),
                ]
            ).Padding(12, 8)
        )
        .CornerRadius(6)
        .Background(Theme.Ref("LayerFillColorDefaultBrush"))
        .WithBorder(Theme.CardStroke, 1)
        .Margin(new Thickness(0, 0, 0, 10));

        var skinCards = skins.Select((skin, idx) =>
        {
            bool isCurrent = idx == activeSkinIndex;
            var text = TextBlock(skin.Name).FontSize(12);
            if (isCurrent)
            {
                text = text.Foreground(palette.Accent);
            }

            return Button(
                text,
                () => onSelectSkinIndex(idx)
            )
            .Background(isCurrent ? Theme.Ref("LayerFillColorDefaultBrush") : Theme.Ref("SubtleFillColorTransparentBrush"))
            .BorderThickness(isCurrent ? 1 : 0)
            .BorderBrush(palette.Accent)
            .Padding(10, 6)
            .CornerRadius(4)
            .AutomationName($"选择皮肤_{skin.Name}");
        }).ToArray();

        Element skinList = ScrollViewer(
            VStack(4, skinCards)
        ).Height(180);

        return VStack(0,
            searchBar,
            previewSection,
            skinList);
    }
}
