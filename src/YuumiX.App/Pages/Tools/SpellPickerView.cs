using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Tools.Models;

namespace YuumiX.App.Pages.Tools;

/// <summary>
/// 召唤师技能选择器视图（用于自动设置技能配置，对应原 SpellPicker.vue）。
/// </summary>
internal static class SpellPickerView
{
    public static Element Render(
        IReadOnlyList<SummonerSpellItem> allSpells,
        IReadOnlyList<int> selectedSpellIds,
        int activeSlot, // 0 for D, 1 for F
        Action<int> onActiveSlotChanged,
        Action<int, int> onSelectSpellForSlot,
        bool isLoading,
        YuumiPalette palette)
    {
        var spellMap = allSpells.ToDictionary(s => s.Id);

        int dId = selectedSpellIds.ElementAtOrDefault(0);
        int fId = selectedSpellIds.ElementAtOrDefault(1);

        string dName = spellMap.TryGetValue(dId, out var sD) ? sD.Name : (dId > 0 ? $"ID:{dId}" : "未配置");
        string dIcon = spellMap.TryGetValue(dId, out var sD2) ? sD2.IconPath : $"/lol-game-data/assets/v1/summoner-spells/{dId}.png";

        string fName = spellMap.TryGetValue(fId, out var sF) ? sF.Name : (fId > 0 ? $"ID:{fId}" : "未配置");
        string fIcon = spellMap.TryGetValue(fId, out var sF2) ? sF2.IconPath : $"/lol-game-data/assets/v1/summoner-spells/{fId}.png";

        Element dSlot = Button(
            HStack(8,
                Caption("D 键:").VAlign(VerticalAlignment.Center),
                Component<LcuImage, LcuImageProps>(new LcuImageProps(dIcon, Size: 28, CornerRadius: 4)).VAlign(VerticalAlignment.Center),
                TextBlock(dName).FontSize(12).VAlign(VerticalAlignment.Center)
            ),
            () => onActiveSlotChanged(0)
        )
        .Background(activeSlot == 0 ? Theme.Ref("LayerFillColorDefaultBrush") : Theme.Ref("CardBackgroundFillColorDefaultBrush"))
        .BorderBrush(activeSlot == 0 ? palette.Accent : "#20808080")
        .BorderThickness(activeSlot == 0 ? 1.5 : 1)
        .CornerRadius(6)
        .Padding(12, 6)
        .Margin(new Thickness(0, 0, 12, 0))
        .AutomationName("D键技能槽位");

        Element fSlot = Button(
            HStack(8,
                Caption("F 键:").VAlign(VerticalAlignment.Center),
                Component<LcuImage, LcuImageProps>(new LcuImageProps(fIcon, Size: 28, CornerRadius: 4)).VAlign(VerticalAlignment.Center),
                TextBlock(fName).FontSize(12).VAlign(VerticalAlignment.Center)
            ),
            () => onActiveSlotChanged(1)
        )
        .Background(activeSlot == 1 ? Theme.Ref("LayerFillColorDefaultBrush") : Theme.Ref("CardBackgroundFillColorDefaultBrush"))
        .BorderBrush(activeSlot == 1 ? palette.Accent : "#20808080")
        .BorderThickness(activeSlot == 1 ? 1.5 : 1)
        .CornerRadius(6)
        .Padding(12, 6)
        .AutomationName("F键技能槽位");

        Element slotsHeader = HStack(0,
            dSlot,
            fSlot,
            Caption(activeSlot == 0 ? "（正在设置 D 键技能，点击下方替换）" : "（正在设置 F 键技能，点击下方替换）")
                .Foreground(Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center)
                .Margin(new Thickness(12, 0, 0, 0))
        ).Margin(new Thickness(0, 4, 0, 8));

        if (isLoading && allSpells.Count == 0)
        {
            return VStack(0,
                slotsHeader,
                TextBlock("正在从客户端拉取召唤师技能列表...").FontSize(12).Foreground(Theme.SecondaryText).Margin(new Thickness(0, 8, 0, 8))
            );
        }

        // 技能列表网格
        var items = new List<Element>();
        foreach (var spell in allSpells)
        {
            int currentTargetId = activeSlot == 0 ? dId : fId;
            bool isCurrent = spell.Id == currentTargetId;

            items.Add(
                Button(
                    HStack(6,
                        Component<LcuImage, LcuImageProps>(new LcuImageProps(spell.IconPath, Size: 26, CornerRadius: 4)).VAlign(VerticalAlignment.Center),
                        TextBlock(spell.Name).FontSize(12).VAlign(VerticalAlignment.Center)
                    ),
                    () => onSelectSpellForSlot(activeSlot, spell.Id)
                )
                .Background(isCurrent ? Theme.Ref("LayerFillColorDefaultBrush") : Theme.Ref("SubtleFillColorTransparentBrush"))
                .BorderBrush(isCurrent ? palette.Accent : "#20808080")
                .BorderThickness(isCurrent ? 1.5 : 1)
                .CornerRadius(6)
                .Padding(8, 4)
                .Margin(new Thickness(0, 0, 6, 6))
                .AutomationName($"选择技能_{spell.Name}")
            );
        }

        var spellGrid = ScrollViewer(
            HStack(0, items.ToArray())
        ).Height(44);

        return VStack(0,
            slotsHeader,
            spellGrid
        );
    }
}
