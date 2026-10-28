using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Tools.Models;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Tools.Sections;

/// <summary>英雄选择组的页内状态（选用/禁用/技能的分路选择与搜索词）。</summary>
internal sealed record ChampionUi(
    int PickLaneIndex,
    int BanLaneIndex,
    int SpellLaneIndex,
    int SpellSlotIndex,
    string PickSearchQuery,
    string BanSearchQuery);

/// <summary>
/// 工具箱「英雄选择」分组：自动接受对局 / 自动交换请求 / 自动选用预选 / 自动禁用 / 自动召唤师技能。
/// 状态与写回经 <see cref="ToolsUi"/> 传入，本文件只负责装配（R14 拆分）。
/// </summary>
internal static class ChampionSection
{
    private static readonly string[] LaneLabels = ["默认", "上单 (Top)", "打野 (Jungle)", "中单 (Mid)", "下路 (Bot)", "辅助 (Support)"];

    private static List<int> GetPickListByLane(FunctionsConfig cfg, int laneIndex) => laneIndex switch
    {
        1 => cfg.AutoSelectChampionTop,
        2 => cfg.AutoSelectChampionJug,
        3 => cfg.AutoSelectChampionMid,
        4 => cfg.AutoSelectChampionBot,
        5 => cfg.AutoSelectChampionSup,
        _ => cfg.AutoSelectChampion,
    };

    private static List<int> GetBanListByLane(FunctionsConfig cfg, int laneIndex) => laneIndex switch
    {
        1 => cfg.AutoBanChampionTop,
        2 => cfg.AutoBanChampionJug,
        3 => cfg.AutoBanChampionMid,
        4 => cfg.AutoBanChampionBot,
        5 => cfg.AutoBanChampionSup,
        _ => cfg.AutoBanChampion,
    };

    private static List<int> GetSpellListByLane(FunctionsConfig cfg, int laneIndex) => laneIndex switch
    {
        1 => cfg.AutoSetSummonerSpellTop,
        2 => cfg.AutoSetSummonerSpellJug,
        3 => cfg.AutoSetSummonerSpellMid,
        4 => cfg.AutoSetSummonerSpellBot,
        5 => cfg.AutoSetSummonerSpellSup,
        _ => cfg.AutoSetSummonerSpell,
    };

    public static Element Render(
        ToolsUi ui,
        ChampionUi st,
        Action<ChampionUi> set,
        IReadOnlyList<ChampionSummaryItem> champions,
        bool championsLoading,
        IReadOnlyList<SummonerSpellItem> spells,
        bool spellsLoading)
    {
        var config = ui.Config;
        var i18n = ui.I18n;

        var enabledText = i18n.T("tools.enabledText", "已启用");
        var disabledText = i18n.T("tools.disabledText", "未启用");

        // 1. 自动接受对局卡片
        var autoAcceptStatus = config.Functions.EnableAutoAcceptMatching
            ? $"{enabledText} ({config.Functions.AutoAcceptMatchingDelay}s)"
            : disabledText;

        var autoAcceptCard = ToolsVisuals.ToolCard(
            ui,
            "autoAccept",
            i18n.T("tools.autoAccept.title", "自动接受对局"),
            i18n.T("tools.autoAccept.desc", "在你设置的秒数之后自动接受对局匹配"),
            autoAcceptStatus,
            config.Functions.EnableAutoAcceptMatching,
            VStack(4,
                ToolsVisuals.CardRow(i18n.T("tools.acceptDelayLabel", "在对局找到后接受对局前延迟的秒数:"),
                    CompactStepperControl.Render(
                        config.Functions.AutoAcceptMatchingDelay,
                        0,
                        11,
                        1,
                        next =>
                        {
                            if (next == config.Functions.AutoAcceptMatchingDelay) return;
                            config.Functions.AutoAcceptMatchingDelay = next;
                            ui.Commit(nameof(FunctionsConfig.AutoAcceptMatchingDelay));
                        },
                        ui.Palette,
                        "自动接受延迟")),
                ToolsVisuals.CardRow(i18n.T("tools.enableAutoAcceptLabel", "启用自动接受对局:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.EnableAutoAcceptMatching, v =>
                    {
                        if (v == config.Functions.EnableAutoAcceptMatching) return;
                        config.Functions.EnableAutoAcceptMatching = v;
                        ui.Commit(nameof(FunctionsConfig.EnableAutoAcceptMatching));
                    }, "启用自动接受对局"))
            ));

        // 2. 自动接受交换请求卡片
        var autoSwapEnabled = config.Functions.AutoAcceptCeilSwap || config.Functions.AutoAcceptChampTrade;
        var autoSwapStatus = autoSwapEnabled ? enabledText : disabledText;

        var autoSwapCard = ToolsVisuals.ToolCard(
            ui,
            "autoSwap",
            i18n.T("tools.autoSwap.title", "自动接受交换请求"),
            i18n.T("tools.autoSwap.desc", "自动接受队友的交换楼层或英雄的请求"),
            autoSwapStatus,
            autoSwapEnabled,
            VStack(4,
                ToolsVisuals.CardRow(i18n.T("tools.autoAcceptSwapCeil", "自动接受楼层交换请求:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.AutoAcceptCeilSwap, v =>
                    {
                        if (v == config.Functions.AutoAcceptCeilSwap) return;
                        config.Functions.AutoAcceptCeilSwap = v;
                        ui.Commit(nameof(FunctionsConfig.AutoAcceptCeilSwap));
                    }, "自动接受楼层交换")),
                ToolsVisuals.CardRow(i18n.T("tools.autoAcceptSwapChamp", "自动接受英雄交换请求:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.AutoAcceptChampTrade, v =>
                    {
                        if (v == config.Functions.AutoAcceptChampTrade) return;
                        config.Functions.AutoAcceptChampTrade = v;
                        ui.Commit(nameof(FunctionsConfig.AutoAcceptChampTrade));
                    }, "自动接受英雄交换"))
            ));

        // 3. 自动选用/预选英雄卡片
        var autoHoverEnabled = config.Functions.EnableAutoSelectChampion || config.Functions.EnableAutoHoverChampion;
        var autoHoverStatus = autoHoverEnabled ? enabledText : disabledText;

        var autoHoverCard = ToolsVisuals.ToolCard(
            ui,
            "autoHover",
            i18n.T("tools.autoHover.title", "自动选用/预选英雄"),
            i18n.T("tools.autoHover.desc", "在你进入英雄选择时自动亮起/预选英雄"),
            autoHoverStatus,
            autoHoverEnabled,
            VStack(4,
                ToolsVisuals.CardRow(i18n.T("tools.enableHoverLabel", "启用自动亮起/预选:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.EnableAutoHoverChampion, v =>
                    {
                        if (v == config.Functions.EnableAutoHoverChampion) return;
                        config.Functions.EnableAutoHoverChampion = v;
                        ui.Commit(nameof(FunctionsConfig.EnableAutoHoverChampion));
                    }, "启用自动预选")),
                ToolsVisuals.CardRow(i18n.T("tools.confirmOnTimeoutLabel", "在结束时确认选择 (防秒退):"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.AutoSelectConfirmOnTimeout, v =>
                    {
                        if (v == config.Functions.AutoSelectConfirmOnTimeout) return;
                        config.Functions.AutoSelectConfirmOnTimeout = v;
                        ui.Commit(nameof(FunctionsConfig.AutoSelectConfirmOnTimeout));
                    }, "超时自动确认")),
                ToolsVisuals.CardRow(i18n.T("tools.enableAutoPickLabel", "选人阶段自动选择英雄:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.EnableAutoSelectChampion, v =>
                    {
                        if (v == config.Functions.EnableAutoSelectChampion) return;
                        config.Functions.EnableAutoSelectChampion = v;
                        ui.Commit(nameof(FunctionsConfig.EnableAutoSelectChampion));
                    }, "自动选择英雄")),
                ToolsVisuals.CardRow(i18n.T("tools.showDodgeBtnLabel", "在对局信息中显示秒退按钮:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.EnableDodgeInGameInfo, v =>
                    {
                        if (v == config.Functions.EnableDodgeInGameInfo) return;
                        config.Functions.EnableDodgeInGameInfo = v;
                        ui.Commit(nameof(FunctionsConfig.EnableDodgeInGameInfo));
                    }, "对局信息秒退按钮")),
                Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(new Thickness(0, 6, 0, 6)),
                Caption(i18n.T("tools.pickCandidatesLabel", "分路预选候选英雄库:")).Foreground(Theme.SecondaryText),
                LaneTabs(ui, st.PickLaneIndex, idx => set(st with { PickLaneIndex = idx })),
                ChampionPickerView.Render(
                    champions,
                    GetPickListByLane(config.Functions, st.PickLaneIndex),
                    champId => TogglePickChampion(ui, st, champId),
                    () => ClearPickSelection(ui, st),
                    st.PickSearchQuery,
                    q => set(st with { PickSearchQuery = q }),
                    championsLoading,
                    ui.Palette)
            ));

        // 4. 自动禁用英雄卡片
        var autoBanStatus = config.Functions.EnableAutoBanChampion ? enabledText : disabledText;

        var autoBanCard = ToolsVisuals.ToolCard(
            ui,
            "autoBan",
            i18n.T("tools.autoBan.title", "自动禁用英雄"),
            i18n.T("tools.autoBan.desc", "在你的禁用环节开始时自动禁用英雄"),
            autoBanStatus,
            config.Functions.EnableAutoBanChampion,
            VStack(4,
                ToolsVisuals.CardRow(i18n.T("tools.enableAutoBanLabel", "禁用环节自动禁用设定英雄:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.EnableAutoBanChampion, v =>
                    {
                        if (v == config.Functions.EnableAutoBanChampion) return;
                        config.Functions.EnableAutoBanChampion = v;
                        ui.Commit(nameof(FunctionsConfig.EnableAutoBanChampion));
                    }, "自动禁用开关")),
                ToolsVisuals.CardRow(i18n.T("tools.pretendBanLabel", "避开队友已预选的英雄 (假禁用):"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.PretendBan, v =>
                    {
                        if (v == config.Functions.PretendBan) return;
                        config.Functions.PretendBan = v;
                        ui.Commit(nameof(FunctionsConfig.PretendBan));
                    }, "避开队友预选")),
                ToolsVisuals.CardRow(i18n.T("tools.banDelayLabel", "自动禁用前等待延迟 (秒):"),
                    CompactStepperControl.RenderDouble(
                        config.Functions.AutoBanDelay,
                        0,
                        15,
                        1,
                        next =>
                        {
                            if (Math.Abs(next - config.Functions.AutoBanDelay) < 0.01) return;
                            config.Functions.AutoBanDelay = next;
                            ui.Commit(nameof(FunctionsConfig.AutoBanDelay));
                        },
                        ui.Palette,
                        "自动禁用延迟")),
                Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(new Thickness(0, 6, 0, 6)),
                Caption(i18n.T("tools.banCandidatesLabel", "分路禁用候选英雄库:")).Foreground(Theme.SecondaryText),
                LaneTabs(ui, st.BanLaneIndex, idx => set(st with { BanLaneIndex = idx })),
                ChampionPickerView.Render(
                    champions,
                    GetBanListByLane(config.Functions, st.BanLaneIndex),
                    champId => ToggleBanChampion(ui, st, champId),
                    () => ClearBanSelection(ui, st),
                    st.BanSearchQuery,
                    q => set(st with { BanSearchQuery = q }),
                    championsLoading,
                    ui.Palette)
            ));

        // 5. 自动设置召唤师技能卡片
        var autoSpellsStatus = config.Functions.EnableAutoSetSpells ? enabledText : disabledText;

        var autoSpellsCard = ToolsVisuals.ToolCard(
            ui,
            "autoSpells",
            i18n.T("tools.autoSpells.title", "自动设置召唤师技能"),
            i18n.T("tools.autoSpells.desc", "当你的英雄选择开始时自动设置召唤师技能"),
            autoSpellsStatus,
            config.Functions.EnableAutoSetSpells,
            VStack(4,
                ToolsVisuals.CardRow(i18n.T("tools.autoSetSpellsLabel", "锁定英雄后自动写入配置好的技能组:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.EnableAutoSetSpells, v =>
                    {
                        if (v == config.Functions.EnableAutoSetSpells) return;
                        config.Functions.EnableAutoSetSpells = v;
                        ui.Commit(nameof(FunctionsConfig.EnableAutoSetSpells));
                    }, "自动召唤师技能")),
                Border(Empty()).Height(1).Background(Theme.DividerStroke).Margin(new Thickness(0, 6, 0, 6)),
                Caption(i18n.T("tools.spellsByLaneLabel", "分路召唤师技能组:")).Foreground(Theme.SecondaryText),
                LaneTabs(ui, st.SpellLaneIndex, idx => set(st with { SpellLaneIndex = idx })),
                SpellPickerView.Render(
                    spells,
                    GetSpellListByLane(config.Functions, st.SpellLaneIndex),
                    st.SpellSlotIndex,
                    slot => set(st with { SpellSlotIndex = slot }),
                    (slotIndex, spellId) => SetSpellForSlot(ui, st, slotIndex, spellId),
                    spellsLoading,
                    ui.Palette)
            ));

        return VStack(8, autoAcceptCard, autoSwapCard, autoHoverCard, autoBanCard, autoSpellsCard);
    }

    private static Element LaneTabs(ToolsUi ui, int currentLane, Action<int> onSelectLane)
    {
        var i18n = ui.I18n;
        string[] laneLabels =
        [
            i18n.T("tools.lanes.default", "默认"),
            i18n.T("tools.lanes.top", "上单 (Top)"),
            i18n.T("tools.lanes.jungle", "打野 (Jungle)"),
            i18n.T("tools.lanes.mid", "中单 (Mid)"),
            i18n.T("tools.lanes.bot", "下路 (Bot)"),
            i18n.T("tools.lanes.support", "辅助 (Support)"),
        ];

        var buttons = new List<Element>();
        for (int i = 0; i < laneLabels.Length; i++)
        {
            int idx = i;
            bool isCurrent = idx == currentLane;
            buttons.Add(
                Button(laneLabels[idx], () => onSelectLane(idx))
                    .Background(isCurrent ? Theme.Ref("LayerFillColorDefaultBrush") : Theme.Ref("SubtleFillColorTransparentBrush"))
                    .BorderBrush(isCurrent ? Theme.Accent : Theme.ControlStroke)
                    .BorderThickness(isCurrent ? 1.5 : 1)
                    .CornerRadius(YuumiTheme.OverlayRadius)
                    .Padding(8, 4)
                    .Margin(new Thickness(0, 0, 6, 0))
                    .AutomationName($"分路_{laneLabels[idx]}")
            );
        }
        return HStack(0, [.. buttons]).Margin(new Thickness(0, 6, 0, 8));
    }

    private static void TogglePickChampion(ToolsUi ui, ChampionUi st, int champId)
    {
        var list = GetPickListByLane(ui.Config.Functions, st.PickLaneIndex);
        if (list.Contains(champId))
        {
            list.Remove(champId);
        }
        else
        {
            if (list.Count >= 5)
            {
                ui.Toast.Error("每个分路最多选用 5 位候选英雄");
                return;
            }
            list.Add(champId);
        }
        ui.Commit(nameof(FunctionsConfig.AutoSelectChampion));
    }

    private static void ClearPickSelection(ToolsUi ui, ChampionUi st)
    {
        GetPickListByLane(ui.Config.Functions, st.PickLaneIndex).Clear();
        ui.Commit(nameof(FunctionsConfig.AutoSelectChampion));
    }

    private static void ToggleBanChampion(ToolsUi ui, ChampionUi st, int champId)
    {
        var list = GetBanListByLane(ui.Config.Functions, st.BanLaneIndex);
        if (list.Contains(champId))
        {
            list.Remove(champId);
        }
        else
        {
            if (list.Count >= 5)
            {
                ui.Toast.Error("每个分路最多禁用 5 位候选英雄");
                return;
            }
            list.Add(champId);
        }
        ui.Commit(nameof(FunctionsConfig.AutoBanChampion));
    }

    private static void ClearBanSelection(ToolsUi ui, ChampionUi st)
    {
        GetBanListByLane(ui.Config.Functions, st.BanLaneIndex).Clear();
        ui.Commit(nameof(FunctionsConfig.AutoBanChampion));
    }

    private static void SetSpellForSlot(ToolsUi ui, ChampionUi st, int slotIndex, int spellId)
    {
        var list = GetSpellListByLane(ui.Config.Functions, st.SpellLaneIndex);
        while (list.Count < 2) list.Add(4);
        list[slotIndex] = spellId;
        ui.Commit(nameof(FunctionsConfig.AutoSetSummonerSpell));
    }
}
