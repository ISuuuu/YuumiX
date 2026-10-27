using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Tools.Models;

namespace YuumiX.App.Pages.Tools.Sections;

/// <summary>个性签名与皮肤 ID 的输入草稿。</summary>
internal sealed record ProfileDraftUi(string Signature, string SkinId);

/// <summary>生涯背景皮肤选择器状态（英雄输入、皮肤列表与选中项、加载中）。</summary>
internal sealed record ProfileBgUi(
    string ChampInput,
    IReadOnlyList<ChampionSkinItem> Skins,
    int SkinIndex,
    bool Loading);

/// <summary>段位伪装的三个下拉选择。</summary>
internal sealed record SpoofUi(int QueueIndex, int TierIndex, int DivisionIndex);

/// <summary>挑战勋章装配状态（搜索词、档位筛选、称号输入、已选勋章）。</summary>
internal sealed record BadgeUi(
    string SearchQuery,
    int TierIndex,
    string TitleInput,
    List<long> SelectedIds);

/// <summary>
/// 工具箱「个人主页」分组：个性签名 / 生涯背景 / 段位伪装 / 在线状态 / 挑战勋章与头像框。
/// 挑战列表数据由 UseResource 提供（R7），经 <paramref name="challenges"/>/ 传入。
/// </summary>
internal static class ProfileSection
{
    private static readonly string[] SpoofTiers =
    [
        "CHALLENGER", "GRANDMASTER", "MASTER", "DIAMOND", "EMERALD",
        "PLATINUM", "GOLD", "SILVER", "BRONZE", "IRON", "UNRANKED"
    ];
    private static readonly string[] SpoofTierLabels =
    [
        "王者 (Challenger)", "宗师 (Grandmaster)", "大师 (Master)", "钻石 (Diamond)",
        "翡翠 (Emerald)", "铂金 (Platinum)", "黄金 (Gold)", "白银 (Silver)", "青铜 (Bronze)", "黑铁 (Iron)", "未定级 (Unranked)"
    ];
    private static readonly string[] SpoofDivisions = ["I", "II", "III", "IV"];
    private static readonly string[] SpoofDivisionLabels = ["第 1 阶 (I)", "第 2 阶 (II)", "第 3 阶 (III)", "第 4 阶 (IV)"];
    private static readonly string[] SpoofQueues = ["RANKED_SOLO_5x5", "RANKED_FLEX_SR", "RANKED_TFT"];
    private static readonly string[] SpoofQueueLabels = ["单双排排位", "灵活排位", "云顶之弈"];

    public static Element Render(
        ToolsUi ui,
        ProfileDraftUi draft,
        Action<ProfileDraftUi> setDraft,
        ProfileBgUi bg,
        Action<ProfileBgUi> setBg,
        SpoofUi spoof,
        Action<SpoofUi> setSpoof,
        ConfirmUi confirm,
        Action<ConfirmUi> setConfirm,
        BadgeUi badge,
        Action<BadgeUi> setBadge,
        IReadOnlyList<ChampionSummaryItem> champions,
        IReadOnlyList<ChallengeItem> challenges,
        bool challengesLoading,
        Action onReloadChallenges)
    {
        var i18n = ui.I18n;
        var clickToExpand = i18n.T("tools.clickToExpand", "点击展开");
        var isEn = i18n.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.EnUs;

        var spoofQueueLabels = isEn
            ? new[] { "Ranked Solo/Duo", "Ranked Flex", "TFT" }
            : (i18n.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.ZhTw
                ? new[] { "單雙排排位", "靈活排位", "雲頂之弈" }
                : new[] { "单双排排位", "灵活排位", "云顶之弈" });

        var spoofTierLabels = isEn
            ? new[] { "Challenger", "Grandmaster", "Master", "Diamond", "Emerald", "Platinum", "Gold", "Silver", "Bronze", "Iron", "Unranked" }
            : (i18n.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.ZhTw
                ? new[] { "王者 (Challenger)", "宗師 (Grandmaster)", "大師 (Master)", "鑽石 (Diamond)", "翡翠 (Emerald)", "鉑金 (Platinum)", "黃金 (Gold)", "白銀 (Silver)", "青銅 (Bronze)", "黑鐵 (Iron)", "未定級 (Unranked)" }
                : new[] { "王者 (Challenger)", "宗师 (Grandmaster)", "大师 (Master)", "钻石 (Diamond)", "翡翠 (Emerald)", "铂金 (Platinum)", "黄金 (Gold)", "白银 (Silver)", "青铜 (Bronze)", "黑铁 (Iron)", "未定级 (Unranked)" });

        var spoofDivisionLabels = isEn
            ? new[] { "Division I", "Division II", "Division III", "Division IV" }
            : (i18n.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.ZhTw
                ? new[] { "第 1 階 (I)", "第 2 階 (II)", "第 3 階 (III)", "第 4 階 (IV)" }
                : new[] { "第 1 阶 (I)", "第 2 阶 (II)", "第 3 阶 (III)", "第 4 阶 (IV)" });

        // ══════════════════════════════════════════════════════
        // 4. 个人主页组
        // ══════════════════════════════════════════════════════
        var signatureCard = ToolsVisuals.ToolCard(
            ui,
            "signature",
            i18n.T("tools.signature.title", "个性签名"),
            i18n.T("tools.signature.desc", "自定义修改你在客户端好友列表中展示的个性状态签名"),
            clickToExpand,
            false,
            VStack(8,
                Body(i18n.T("tools.enterSignaturePlaceholder", "输入你想展示的个性化签名并应用："))
                    .Foreground(Theme.SecondaryText),
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [
                        TextBox(draft.Signature, v => setDraft(draft with { Signature = v }), i18n.T("tools.enterSignaturePlaceholder", "输入新个性签名内容..."))
                            .Margin(new Thickness(0, 0, 8, 0))
                            .AutomationName("签名内容输入")
                            .Grid(row: 0, column: 0),
                        Button(i18n.T("tools.updateSignatureBtn", "更新签名"), async () =>
                        {
                            if (string.IsNullOrWhiteSpace(draft.Signature))
                            {
                                ui.Toast.Error("请输入有效的签名内容");
                                return;
                            }
                            try
                            {
                                await ui.Tools.SetStatusMessageAsync(draft.Signature.Trim());
                                setDraft(draft with { Signature = string.Empty });
                                ui.Toast.Success("个性签名已成功更新");
                            }
                            catch (Exception ex)
                            {
                                ui.Toast.Error($"更新个性签名失败: {ex.Message}");
                            }
                        }).AutomationName("更新签名按钮").Grid(row: 0, column: 1),
                    ])
            ));

        var profileBgCard = ToolsVisuals.ToolCard(
            ui,
            "profileBg",
            i18n.T("tools.background.title", "生涯背景"),
            i18n.T("tools.background.desc", "在个人生涯主页展示任意指定的英雄皮肤插画背景"),
            clickToExpand,
            false,
            VStack(12,
                Body(i18n.T("tools.browseSkinsDesc", "浏览英雄皮肤并预览应用，或直接输入皮肤 ID："))
                    .Foreground(Theme.SecondaryText),
                SkinPickerView.Render(
                    bg.ChampInput,
                    v => setBg(bg with { ChampInput = v }),
                    bg.Loading,
                    bg.Skins,
                    bg.SkinIndex,
                    idx => setBg(bg with { SkinIndex = idx }),
                    async skinId =>
                    {
                        try
                        {
                            await ui.Tools.SetProfileBackgroundAsync(skinId);
                            ui.Toast.Success($"已成功将生涯背景设置为皮肤 [{skinId}]");
                        }
                        catch (Exception ex)
                        {
                            ui.Toast.Error($"设置背景失败: {ex.Message}");
                        }
                    },
                    async () =>
                    {
                        var text = bg.ChampInput.Trim();
                        int champId = 0;
                        if (!int.TryParse(text, out champId))
                        {
                            var matched = champions.FirstOrDefault(c =>
                                c.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                                || c.Alias.Contains(text, StringComparison.OrdinalIgnoreCase)
                                || c.Title.Contains(text, StringComparison.OrdinalIgnoreCase));
                            if (matched is not null)
                            {
                                champId = matched.Id;
                            }
                        }

                        if (champId <= 0)
                        {
                            ui.Toast.Error("未匹配到英雄，请输入有效英雄名称（如「安妮」）或数字 ID");
                            return;
                        }
                        setBg(bg with { Loading = true });
                        try
                        {
                            var skins = await ui.Tools.GetChampionSkinsAsync(champId);
                            setBg(bg with { Skins = skins, SkinIndex = 0, Loading = false });
                            ui.Toast.Success($"获取到 {skins.Count} 款皮肤");
                        }
                        catch (Exception ex)
                        {
                            ui.Toast.Error($"获取皮肤列表失败: {ex.Message}");
                            setBg(bg with { Loading = false });
                        }
                    },
                    ui.Palette
                ),
                Border(Empty()).Height(1).Background(Theme.CardStroke).Margin(new Thickness(0, 4, 0, 4)),
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [
                        TextBox(draft.SkinId, v => setDraft(draft with { SkinId = v }), i18n.T("tools.skinIdPlaceholder", "或直接输入皮肤 ID，如 1001"))
                            .Margin(new Thickness(0, 0, 8, 0))
                            .AutomationName("皮肤ID输入")
                            .Grid(row: 0, column: 0),
                        Button(i18n.T("tools.applySkinIdBtn", "直接应用皮肤 ID"), async () =>
                        {
                            if (!int.TryParse(draft.SkinId.Trim(), out var skinId))
                            {
                                ui.Toast.Error("请输入有效的数字皮肤 ID");
                                return;
                            }
                            try
                            {
                                await ui.Tools.SetProfileBackgroundAsync(skinId);
                                ui.Toast.Success($"已设置个人生涯背景为皮肤 [{skinId}]");
                            }
                            catch (Exception ex)
                            {
                                ui.Toast.Error($"设置背景失败: {ex.Message}");
                            }
                        }).AutomationName("直接应用皮肤ID").Grid(row: 0, column: 1),
                    ])
            ));

        bool isApexTier = SpoofTiers[spoof.TierIndex] is "CHALLENGER" or "GRANDMASTER" or "MASTER" or "UNRANKED";

        var rankSpoofCard = ToolsVisuals.ToolCard(
            ui,
            "rankSpoof",
            i18n.T("tools.rankSpoof.title", "段位伪装"),
            i18n.T("tools.rankSpoof.desc", "在本地客户端及好友列表中伪造段位展示 (仅限视觉效果)"),
            clickToExpand,
            false,
            VStack(12,
                ToolsVisuals.CardRow(i18n.T("tools.spoofQueueLabel", "排位队列类型:"),
                    ComboBox(spoofQueueLabels, spoof.QueueIndex, index =>
                    {
                        if (index >= 0 && index < spoofQueueLabels.Length)
                        {
                            setSpoof(spoof with { QueueIndex = index });
                        }
                    })
                        .Width(180).AutomationName("伪装队列选择")),
                ToolsVisuals.CardRow(i18n.T("tools.spoofTierLabel", "伪装段位级别:"),
                    ComboBox(spoofTierLabels, spoof.TierIndex, index =>
                    {
                        if (index >= 0 && index < spoofTierLabels.Length)
                        {
                            setSpoof(spoof with { TierIndex = index });
                        }
                    })
                        .Width(220).AutomationName("伪装段位选择")),
                ToolsVisuals.CardRow(i18n.T("tools.spoofDivisionLabel", "伪装小段级别 (Division):"),
                    ComboBox(spoofDivisionLabels, spoof.DivisionIndex, index =>
                    {
                        if (index >= 0 && index < spoofDivisionLabels.Length)
                        {
                            setSpoof(spoof with { DivisionIndex = index });
                        }
                    })
                        .Width(180).IsEnabled(!isApexTier).AutomationName("伪装小段选择")),
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [
                        Body(i18n.T("tools.spoofNotice", "应用后好友列表中的名片即时显示此段位")).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 0),
                        Button(i18n.T("tools.applySpoofBtn", "应用伪装"), async () =>
                        {
                            var q = SpoofQueues[spoof.QueueIndex];
                            var t = SpoofTiers[spoof.TierIndex];
                            var d = SpoofDivisions[spoof.DivisionIndex];
                            try
                            {
                                await ui.Tools.SetRankSpoofAsync(q, t, d);
                                ui.Toast.Success($"段位伪装已生效: {SpoofTierLabels[spoof.TierIndex]} {d}");
                            }
                            catch (Exception ex)
                            {
                                ui.Toast.Error($"应用段位伪装失败: {ex.Message}");
                            }
                        }).AutomationName("应用段位伪装").Grid(row: 0, column: 1),
                    ])
            ));

        var onlineStatusCard = ToolsVisuals.ActionCard(
            ui,
            i18n.T("tools.status.title", "在线状态切换"),
            i18n.T("tools.status.desc", "在好友列表中切换你的展示状态（在线 / 离开 / 隐身）"),
            HStack(8,
                StatusButton(ui, i18n.T("tools.statusOnline", "在线"), "chat", "已切换为在线状态", "设为在线"),
                StatusButton(ui, i18n.T("tools.statusAway", "离开"), "away", "已切换为离开状态", "设为离开"),
                StatusButton(ui, i18n.T("tools.statusOffline", "隐身"), "offline", "已切换为隐身离线状态", "设为隐身")));

        Element clearBadgesAction = confirm.ClearBadges
            ? (Element)HStack(6,
                Caption("🏅 确定清除所有勋章？").Foreground(Theme.Ref("SystemFillColorCriticalBrush")).VAlign(VerticalAlignment.Center),
                Button(Caption(i18n.T("common.cancel", "取消")), () => setConfirm(confirm with { ClearBadges = false })).Padding(4, 0).AutomationName("取消卸下勋章"),
                Button(Caption(i18n.T("common.confirm", "确认卸下")), async () =>
                {
                    setConfirm(confirm with { ClearBadges = false });
                    try
                    {
                        await ui.Tools.ClearChallengeBadgesAsync();
                        ui.Toast.Success("已卸下展示的全部挑战勋章");
                    }
                    catch (Exception ex)
                    {
                        ui.Toast.Error($"卸下勋章失败: {ex.Message}");
                    }
                }).Background(Theme.Ref("SystemFillColorCriticalBrush")).Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush")).Padding(4, 0).AutomationName("确认卸下勋章"))
            : (Element)Button(i18n.T("tools.clearAllBadgesBtn", "卸下全部勋章"), () => setConfirm(confirm with { ClearBadges = true })).AutomationName("卸下勋章");

        Element clearBorderAction = confirm.ClearBorder
            ? (Element)HStack(6,
                Caption("🖼️ 确定清除头像框？").Foreground(Theme.Ref("SystemFillColorCriticalBrush")).VAlign(VerticalAlignment.Center),
                Button(Caption(i18n.T("common.cancel", "取消")), () => setConfirm(confirm with { ClearBorder = false })).Padding(4, 0).AutomationName("取消卸下头像框"),
                Button(Caption(i18n.T("common.confirm", "确认卸下")), async () =>
                {
                    setConfirm(confirm with { ClearBorder = false });
                    try
                    {
                        await ui.Tools.ClearProfileTokensAsync();
                        ui.Toast.Success("已卸下生涯头像框");
                    }
                    catch (Exception ex)
                    {
                        ui.Toast.Error($"卸下头像框失败: {ex.Message}");
                    }
                }).Background(Theme.Ref("SystemFillColorCriticalBrush")).Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush")).Padding(4, 0).AutomationName("确认卸下头像框"))
            : (Element)Button(i18n.T("tools.clearBorderBtn", "卸下头像框"), () => setConfirm(confirm with { ClearBorder = true })).AutomationName("卸下头像框");

        var badgesCard = ToolsVisuals.ToolCard(
            ui,
            "badges",
            i18n.T("tools.badges.title", "挑战勋章与头像框"),
            i18n.T("tools.badges.desc", "装配/卸下展示的挑战勋章、称号与头像框"),
            clickToExpand,
            false,
            VStack(12,
                HStack(8,
                    clearBadgesAction,
                    clearBorderAction
                ),
                Border(Empty()).Height(1).Background(Theme.CardStroke).Margin(new Thickness(0, 4, 0, 4)),
                ChallengePreferencesView.Render(
                    challenges,
                    challengesLoading,
                    badge.SearchQuery,
                    q => setBadge(badge with { SearchQuery = q }),
                    badge.TierIndex,
                    idx => setBadge(badge with { TierIndex = idx }),
                    badge.TitleInput,
                    v => setBadge(badge with { TitleInput = v }),
                    badge.SelectedIds,
                    id =>
                    {
                        var next = new List<long>(badge.SelectedIds);
                        if (next.Contains(id))
                        {
                            next.Remove(id);
                        }
                        else
                        {
                            if (next.Count >= 3)
                            {
                                ui.Toast.Error("最多只能同时佩戴 3 枚挑战勋章");
                                return;
                            }
                            next.Add(id);
                        }
                        setBadge(badge with { SelectedIds = next });
                    },
                    async () =>
                    {
                        try
                        {
                            await ui.Tools.UpdatePlayerPreferencesAsync(badge.TitleInput.Trim(), badge.SelectedIds);
                            ui.Toast.Success("已成功更新挑战称号与勋章配置");
                        }
                        catch (Exception ex)
                        {
                            ui.Toast.Error($"保存挑战配置失败: {ex.Message}");
                        }
                    },
                    onReloadChallenges,
                    ui.Palette
                )
            ));

        return VStack(8, signatureCard, profileBgCard, rankSpoofCard, onlineStatusCard, badgesCard);
    }

    /// <summary>在线状态三联按钮：按钮组无法被 ActionCard 的 IsEnabled 级联禁用，各自再禁一次。</summary>
    private static Element StatusButton(ToolsUi ui, string label, string status, string successMessage, string automationName) =>
        Button(label, async () =>
        {
            try
            {
                await ui.Tools.SetOnlineStatusAsync(status);
                ui.Toast.Success(successMessage);
            }
            catch (Exception ex)
            {
                ui.Toast.Error($"切换{label}状态失败: {ex.Message}");
            }
        }).IsEnabled(ui.IsLcuConnected).AutomationName(automationName);
}
