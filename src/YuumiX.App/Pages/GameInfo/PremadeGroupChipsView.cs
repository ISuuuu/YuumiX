using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.Core;
using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.App.Pages.GameInfo;

public sealed record PremadeGroupChipsProps(IReadOnlyList<PremadeGroup> Groups);

/// <summary>
/// 预组队开黑胶囊芯片组件（对应旧版 <c>PremadeGroupChips.vue</c>）。
/// </summary>
public sealed class PremadeGroupChipsView : Component<PremadeGroupChipsProps>
{
    public override Element Render()
    {
        if (Props.Groups is null || Props.Groups.Count == 0)
        {
            return HStack(0);
        }

        var chips = Props.Groups.Select(RenderGroupChip).ToArray();
        return HStack(6, chips);
    }

    private static Element RenderGroupChip(PremadeGroup group)
    {
        var color = PremadeColors.GetColor(group.ColorIdx);

        var dot = Border(Empty())
            .Width(8)
            .Height(8)
            .CornerRadius(4)
            .Background(color.Dot)
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);

        var memberIcons = new List<Element>();
        for (int i = 0; i < group.Members.Count; i++)
        {
            var member = group.Members[i];
            int champId = member.ChampionId;
            string asset = champId > 0
                ? $"/lol-game-data/assets/v1/champion-icons/{champId}.png"
                : "/lol-game-data/assets/v1/profile-icons/29.png";

            var icon = Component<LcuImage, LcuImageProps>(new LcuImageProps(asset, Size: 18, CornerRadius: 9))
                .Margin(i > 0 ? -6 : 0, 0, 0, 0);

            if (!string.IsNullOrEmpty(member.DisplayName))
            {
                icon = icon.WithToolTip(TextBlock(member.DisplayName));
            }

            memberIcons.Add(icon);
        }

        var membersPanel = HStack(0, memberIcons.ToArray())
            .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);

        return Border(
            HStack(4, dot, membersPanel)
                .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center)
        )
        .Background(color.Bg)
        .WithBorder(color.Border, 1)
        .CornerRadius(10)
        .Padding(6, 2)
        .WithToolTip(TextBlock($"组队 {group.ColorIdx + 1}"));
    }
}
