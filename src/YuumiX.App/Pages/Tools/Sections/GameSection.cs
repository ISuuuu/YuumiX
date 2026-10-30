using YuumiX.App.Controls;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Tools.Models;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Tools.Sections;

/// <summary>观战卡片的输入草稿（目标名称 + 通道选择）。</summary>
internal sealed record SpectateUi(string Name, int MethodIndex);

/// <summary>好友管理与僵尸号清理的页内状态（列表数据 + 筛选 + 勾选 + 二次确认 + 扫描进度）。</summary>
internal sealed record FriendUi(
    IReadOnlyList<FriendEntry> AllFriends,
    string SearchQuery,
    int FilterIndex,
    HashSet<string> SelectedIds,
    bool ConfirmingDelete,
    int ScanCompleted,
    int ScanTotal);

/// <summary>
/// 工具箱「游戏」分组：自动游戏流程 / 观战 / 好友管理与僵尸号清理 / 大乱斗板凳席 / 自动重连 /
/// 自动创建大厅 / 游戏设置锁定。好友扫描与批量删除的执行态由 UseMutation 承载（R7），
/// 本文件只消费 <see cref="Microsoft.UI.Reactor.Core.Mutation{TInput,TResult}"/>.IsPending 与 RunAsync。
/// </summary>
internal static class GameSection
{
    private static readonly string[] SpectateMethodLabels =
    [
        "LCU 方式 (客户端内置观战，失败自动切换 CMD)",
        "CMD 方式 (直接启动客户端进程，绕开限制)"
    ];

    private static readonly int[] LobbyModes = [2400, 2450, 450, 430, 420, 440, 900, 1020, 1300, 1700, 4300];
    private static readonly string[] LobbyModeLabels =
    [
        "海克斯大乱斗 (2400)", "经典海斗 (2450)", "极地大乱斗 (450)", "匹配模式 (430)",
        "单双排位 (420)", "灵活排位 (440)", "无限火力 (900)", "克隆模式 (1020)",
        "极限闪击 (1300)", "斗魂竞技场 (1700)", "经典模式 (4300)"
    ];

    private static int IndexOfLobbyMode(int mode)
    {
        for (var i = 0; i < LobbyModes.Length; i++)
        {
            if (LobbyModes[i] == mode) return i;
        }
        return 0;
    }

    public static Element Render(
        ToolsUi ui,
        SpectateUi spectate,
        Action<SpectateUi> setSpectate,
        FriendUi friend,
        Action<FriendUi> setFriend,
        Mutation<object?, IReadOnlyList<FriendEntry>> scanFriends,
        Mutation<List<string>, int> deleteFriends,
        bool? isSettingsLocked,
        Action<bool?> setSettingsLocked)
    {
        var config = ui.Config;
        var i18n = ui.I18n;

        var enabledText = i18n.T("tools.enabledText", "已启用");
        var disabledText = i18n.T("tools.disabledText", "未启用");

        // 6. 自动游戏流程卡片
        var autoFlowEnabled = config.Functions.EnableAutoHandleInvite
            || config.Functions.EnableAutoHonor
            || config.Functions.EnableAutoPlayAgain
            || config.Functions.EnableAutoAramTeamSide
            || config.Functions.EnableAutoTagReminder;
        var autoFlowStatus = autoFlowEnabled ? enabledText : disabledText;

        var autoGameflowCard = ToolsVisuals.ToolCard(
            ui,
            "autoGameflow",
            i18n.T("tools.autoGameflow.title", "自动游戏流程"),
            i18n.T("tools.autoGameflow.desc", "自动处理邀请、点赞、再来一局与 ARAM 报边"),
            autoFlowStatus,
            autoFlowEnabled,
            VStack(4,
                ToolsVisuals.CardRow(i18n.T("tools.autoInviteLabel", "自动接受好友邀请:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.EnableAutoHandleInvite, v =>
                    {
                        if (v == config.Functions.EnableAutoHandleInvite) return;
                        config.Functions.EnableAutoHandleInvite = v;
                        ui.Commit(nameof(FunctionsConfig.EnableAutoHandleInvite));
                    }, "自动接受邀请")),
                ToolsVisuals.CardRow(i18n.T("tools.autoHonorLabel", "对局结束后自动点赞:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.EnableAutoHonor, v =>
                    {
                        if (v == config.Functions.EnableAutoHonor) return;
                        config.Functions.EnableAutoHonor = v;
                        ui.Commit(nameof(FunctionsConfig.EnableAutoHonor));
                    }, "自动点赞")),
                ToolsVisuals.CardRow(i18n.T("tools.autoPlayAgainLabel", "对局结束后自动再来一局:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.EnableAutoPlayAgain, v =>
                    {
                        if (v == config.Functions.EnableAutoPlayAgain) return;
                        config.Functions.EnableAutoPlayAgain = v;
                        ui.Commit(nameof(FunctionsConfig.EnableAutoPlayAgain));
                    }, "自动再来一局")),
                ToolsVisuals.CardRow(i18n.T("tools.autoAramSideLabel", "ARAM 选人时自动报边:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.EnableAutoAramTeamSide, v =>
                    {
                        if (v == config.Functions.EnableAutoAramTeamSide) return;
                        config.Functions.EnableAutoAramTeamSide = v;
                        ui.Commit(nameof(FunctionsConfig.EnableAutoAramTeamSide));
                    }, "ARAM报边")),
                ToolsVisuals.CardRow(i18n.T("tools.aramSideVisibleLabel", "报边消息对队友可见:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.AramTeamSideVisibleToTeam, v =>
                    {
                        if (v == config.Functions.AramTeamSideVisibleToTeam) return;
                        config.Functions.AramTeamSideVisibleToTeam = v;
                        ui.Commit(nameof(FunctionsConfig.AramTeamSideVisibleToTeam));
                    }, "报边队友可见")),
                ToolsVisuals.CardRow(i18n.T("tools.autoTagReminderLabel", "选人阶段自动提醒标记玩家:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.EnableAutoTagReminder, v =>
                    {
                        if (v == config.Functions.EnableAutoTagReminder) return;
                        config.Functions.EnableAutoTagReminder = v;
                        ui.Commit(nameof(FunctionsConfig.EnableAutoTagReminder));
                    }, "标记玩家提醒"))
            ));

        var clickToExpand = i18n.T("tools.clickToExpand", "点击展开");
        var spectateMethodLabels = new[]
        {
            i18n.T("tools.spectateLcu", "LCU 方式 (客户端内置观战，失败自动切换 CMD)"),
            i18n.T("tools.spectateCmd", "CMD 方式 (直接启动客户端进程，绕开限制)"),
        };

        // 7. 观战卡片
        var spectateCard = ToolsVisuals.ToolCard(
            ui,
            "spectate",
            i18n.T("tools.spectate.title", "观战"),
            i18n.T("tools.spectate.desc", "观战同大区玩家正在进行的实时游戏 (支持 CMD 与 LCU 双通道)"),
            clickToExpand,
            false,
            VStack(8,
                Body(i18n.T("tools.spectateIntro", "输入目标召唤师的 Riot ID 或游戏名称，启动实时对局观战："))
                    .Foreground(Theme.SecondaryText),
                ToolsVisuals.CardRow(i18n.T("tools.spectateMethodLabel", "观战通道:"),
                    PixelComboBox.Render(spectateMethodLabels, spectate.MethodIndex, idx =>
                    {
                        if (idx >= 0 && idx < spectateMethodLabels.Length)
                        {
                            setSpectate(spectate with { MethodIndex = idx });
                        }
                    }, automationName: "观战方式选择", width: 340)),
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [
                        TextBox(spectate.Name, v => setSpectate(spectate with { Name = v }), i18n.T("tools.spectateTargetPlaceholder", "输入召唤师名称，如 Hide on bush#KR1"))
                            .Margin(new Thickness(0, 0, 8, 0))
                            .AutomationName("观战目标输入")
                            .Grid(row: 0, column: 0),
                        Button(i18n.T("tools.launchSpectateBtn", "启动观战"), async () =>
                        {
                            if (string.IsNullOrWhiteSpace(spectate.Name))
                            {
                                ui.Toast.Error("请输入有效的召唤师名称");
                                return;
                            }

                            // 清洗 LTR/RTL 特殊控制字符并去除首尾空白
                            var target = spectate.Name.Replace("\u2066", "").Replace("\u2069", "").Trim();
                            var method = spectate.MethodIndex == 0 ? SpectateMethod.Lcu : SpectateMethod.Cmd;
                            ui.Toast.Info($"正在发起对「{target}」的观战请求...");
                            try
                            {
                                var res = await ui.Tools.LaunchSpectateAsync(target, method);
                                if (!res.Success && method == SpectateMethod.Lcu)
                                {
                                    ui.Toast.Info("LCU 内置观战通道未就绪，正在自动切换至 CMD 方式重试...");
                                    res = await ui.Tools.LaunchSpectateAsync(target, SpectateMethod.Cmd);
                                }
                                if (res.Success)
                                {
                                    ui.Toast.Success(res.Message);
                                }
                                else
                                {
                                    ui.Toast.Error(res.Message);
                                }
                            }
                            catch (Exception ex)
                            {
                                ui.Toast.Error($"观战异常: {ex.Message}");
                            }
                        }).AutomationName("启动观战按钮").Grid(row: 0, column: 1),
                    ])
            ));

        // 8. 好友管理与僵尸号清理卡片（扫描/删除执行态来自 UseMutation，R7）
        var filteredFriendsCount = FriendCleanerView.FilterFriends(friend.AllFriends, friend.SearchQuery, friend.FilterIndex).Count;
        var friendCleanerStatus = friend.AllFriends.Count > 0
            ? i18n.T("tools.friendsCountSummary", $"共 {friend.AllFriends.Count} 人 / 筛选出 {filteredFriendsCount} 人")
                .Replace("{total}", friend.AllFriends.Count.ToString())
                .Replace("{filtered}", filteredFriendsCount.ToString())
            : clickToExpand;
        var friendCleanerCard = ToolsVisuals.ToolCard(
            ui,
            "friendCleaner",
            i18n.T("tools.friendCleaner.title", "好友管理与僵尸号清理"),
            i18n.T("tools.friendCleaner.desc", "扫描好友列表，筛选并批量清理长期未上线的离线好友 (支持安全保护)"),
            friendCleanerStatus,
            friend.AllFriends.Count > 0,
            FriendCleanerView.Render(
                friend.AllFriends,
                scanFriends.IsPending,
                deleteFriends.IsPending,
                friend.ScanCompleted,
                friend.ScanTotal,
                friend.SearchQuery,
                q => setFriend(friend with { SearchQuery = q }),
                friend.FilterIndex,
                idx => setFriend(friend with { FilterIndex = idx }),
                friend.SelectedIds,
                id =>
                {
                    var next = new HashSet<string>(friend.SelectedIds);
                    if (!next.Add(id)) next.Remove(id);
                    setFriend(friend with { SelectedIds = next });
                },
                () =>
                {
                    var filtered = FriendCleanerView.FilterFriends(friend.AllFriends, friend.SearchQuery, friend.FilterIndex);
                    var next = new HashSet<string>(friend.SelectedIds);
                    foreach (var f in filtered)
                    {
                        if (f.CanDelete) next.Add(f.Friend.Id);
                    }
                    setFriend(friend with { SelectedIds = next });
                },
                () => setFriend(friend with { SelectedIds = [] }),
                friend.ConfirmingDelete,
                () => setFriend(friend with { ConfirmingDelete = true }),
                () => setFriend(friend with { ConfirmingDelete = false }),
                () =>
                {
                    if (friend.SelectedIds.Count == 0) return;
                    _ = deleteFriends.RunAsync([.. friend.SelectedIds]);
                },
                () => _ = scanFriends.RunAsync(null),
                ui.Palette
            ));

        var benchOverlayCard = ToolsVisuals.ActionCard(
            ui,
            i18n.T("tools.benchOverlay.title", "大乱斗板凳席悬浮窗"),
            i18n.T("tools.benchOverlay.desc", "在大乱斗选人阶段显示半透明置顶悬浮窗，可无 CD 抢板凳席英雄"),
            ToolsVisuals.CleanToggle(ui, config.Functions.EnableBenchOverlay, v =>
            {
                if (v == config.Functions.EnableBenchOverlay) return;
                config.Functions.EnableBenchOverlay = v;
                ui.Commit(nameof(FunctionsConfig.EnableBenchOverlay));
            }, "大乱斗悬浮窗开关"));

        var autoReconnectCard = ToolsVisuals.ActionCard(
            ui,
            i18n.T("tools.autoReconnect.title", "自动重连"),
            i18n.T("tools.autoReconnect.desc", "当你意外掉线退出游戏时自动重新连接"),
            ToolsVisuals.CleanToggle(ui, config.Functions.EnableAutoReconnect, v =>
            {
                if (v == config.Functions.EnableAutoReconnect) return;
                config.Functions.EnableAutoReconnect = v;
                ui.Commit(nameof(FunctionsConfig.EnableAutoReconnect));
            }, "自动重连开关"));

        var autoLobbyCard = ToolsVisuals.ToolCard(
            ui,
            "autoLobby",
            i18n.T("tools.autoCreateLobby.title", "自动创建大厅"),
            i18n.T("tools.autoCreateLobby.desc", "启动 LOL 客户端后自动创建默认模式的大厅"),
            config.Functions.EnableAutoCreateLobby ? enabledText : disabledText,
            config.Functions.EnableAutoCreateLobby,
            VStack(4,
                ToolsVisuals.CardRow(i18n.T("tools.autoLobbyRoomLabel", "客户端就绪后自动拉入指定大厅房间:"),
                    ToolsVisuals.CleanToggle(ui, config.Functions.EnableAutoCreateLobby, v =>
                    {
                        if (v == config.Functions.EnableAutoCreateLobby) return;
                        config.Functions.EnableAutoCreateLobby = v;
                        ui.Commit(nameof(FunctionsConfig.EnableAutoCreateLobby));
                    }, "自动创建大厅开关")),
                ToolsVisuals.CardRow(i18n.T("tools.defaultModeLabel", "默认模式选择:"),
                    PixelComboBox.Render(LobbyModeLabels, IndexOfLobbyMode(config.Functions.DefaultGameMode), idx =>
                    {
                        if (idx < 0 || idx >= LobbyModes.Length) return;
                        var mode = LobbyModes[idx];
                        if (mode == config.Functions.DefaultGameMode) return;
                        config.Functions.DefaultGameMode = mode;
                        ui.Commit(nameof(FunctionsConfig.DefaultGameMode));
                    }, automationName: "默认队列选择", width: 220, isEnabled: config.Functions.EnableAutoCreateLobby).HAlign(HorizontalAlignment.Right)),
                HStack(8,
                    Button(i18n.T("tools.createLobbyBtn", "立即创建该模式大厅"), async () =>
                    {
                        try
                        {
                            await ui.Tools.CreateLobbyAsync(config.Functions.DefaultGameMode);
                            ui.Toast.Success($"已创建模式 [{config.Functions.DefaultGameMode}] 大厅");
                        }
                        catch (Exception ex)
                        {
                            ui.Toast.Error($"创建大厅失败: {ex.Message}");
                        }
                    }).AutomationName("立即创建大厅按钮")
                ).Margin(new Thickness(0, 4, 0, 4))
            ));

        var lockStatusText = isSettingsLocked switch
        {
            true => i18n.T("tools.settingsLocked", "已锁定 (只读保护中)"),
            false => i18n.T("tools.settingsUnlocked", "未锁定 (可写入)"),
            null => i18n.T("tools.settingsDetecting", "检测中"),
        };

        var lockSettingsCard = ToolsVisuals.ActionCard(
            ui,
            i18n.T("tools.lockGameSettings.title", "游戏设置锁定"),
            i18n.T("tools.settingsLockDesc", $"锁定游戏内配置 (PersistedSettings.json)，防止被意外篡改 [{lockStatusText}]").Replace("{status}", lockStatusText),
            Button(isSettingsLocked == true ? i18n.T("tools.unlockBtn", "解除锁定") : i18n.T("tools.lockBtn", "立即锁定"), () =>
            {
                var nextState = !(isSettingsLocked ?? false);
                try
                {
                    ui.Tools.SetGameSettingsReadonly(ui.Config.General.LolPath, nextState);
                    setSettingsLocked(nextState);
                    ui.Toast.Success(nextState ? "已锁定游戏配置文件 (只读保护生效)" : "已解除锁定 (配置恢复可写)");
                }
                catch (Exception ex)
                {
                    ui.Toast.Error($"设置锁定失败: {ex.Message}");
                }
            }).AutomationName("锁定游戏配置"));

        return VStack(8,
            autoGameflowCard,
            spectateCard,
            friendCleanerCard,
            benchOverlayCard,
            autoReconnectCard,
            autoLobbyCard,
            lockSettingsCard);
    }
}
