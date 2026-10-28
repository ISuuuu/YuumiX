using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;

namespace YuumiX.App.Controls;

/// <summary>
/// 统一页头组件（R3）：用于各主要页面顶部，确立视觉锚点与标题无障碍大纲（AutomationHeadingLevel.Level1）。
/// </summary>
public static class PageHeaders
{
    public const double BottomMargin = 16;

    /// <summary>
    /// 构造统一页头：主标题 + 可选徽标 + 可选说明 + 可选右侧操作区。
    /// </summary>
    public static Element Create(
        string title,
        string? description = null,
        Element? badge = null,
        Element? actions = null,
        bool includeBottomMargin = true)
    {
        var titleElement = Title(title)
            .HeadingLevel(AutomationHeadingLevel.Level1);

        Element titleRow = badge is not null
            ? HStack(8, titleElement, badge.VAlign(VerticalAlignment.Center))
            : titleElement;

        Element left = description is { Length: > 0 }
            ? VStack(4,
                titleRow,
                Body(description).Foreground(Theme.SecondaryText))
            : titleRow;

        Element root = actions is not null
            ? Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    left.VAlign(VerticalAlignment.Center).Grid(column: 0),
                    actions.VAlign(VerticalAlignment.Center).Grid(column: 1),
                ])
            : left;

        return includeBottomMargin
            ? root.Margin(0, 0, 0, BottomMargin)
            : root;
    }

    /// <summary>
    /// <see cref="Create"/> 的别名工厂。
    /// </summary>
    public static Element PageHeader(
        string title,
        string? description = null,
        Element? badge = null,
        Element? actions = null,
        bool includeBottomMargin = true) =>
        Create(title, description, badge, actions, includeBottomMargin);
}
