using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Pages.Settings.Sections;
using YuumiX.App.Theming;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Settings;

/// <summary>
/// 设置页：状态编排与写回（R6/R7 hooks 化 + R14 拆分后的聚合层）。
///
/// 写回路径（ADR-12）：控件回调 → 改内存 <see cref="AppConfig"/> → 应用即时副作用
/// （主题 / 日志等级）→ <see cref="ConfigStore.SaveWithBackup"/>（会话首写自动备份）→ 状态条反馈。
///
/// 状态约定（AGENTS.md §5.10）：草稿与防抖序号走 UseState（不可变 record），
/// 不参与渲染的数据（代理草稿、待写颜色字段、防抖计数、dispatcher、logger）走 UseRef；
/// 异步回调消费"最新值"一律经镜像 ref，避免闭包捕获过期渲染。没有任何"踢重渲染"辅助。
/// 防抖落地的纯过程部分在 <see cref="SettingsWriteback"/>；页面超 300 行的原因：
/// 27 个 hook 的声明与三个防抖 UseEffect 必须留在组件渲染函数内（过渡期上限 400）。
/// </summary>
public sealed class SettingsPage : Component
{
    /// <summary>
    /// 取色器的合并写盘延迟。ColorPicker 的 ColorChanged 在拖动时每帧触发，
    /// 逐次同步写盘既打断拖动又没有信息增量；停手 400 ms 后写一次就够了。
    /// </summary>
    private const int ColorCommitDelayMs = 400;

    /// <summary>
    /// 路径条目的合并写盘延迟。TextBox 的文本回调逐字符触发，逐字符落盘会把半截路径写进配置；
    /// 停手 600 ms 后写一次（取色器同款机制，见 <see cref="ColorCommitDelayMs"/>）。
    /// </summary>
    private const int PathCommitDelayMs = 600;

    private static readonly CloudTextUi InitialCloudText = new(null, null);

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var menu = UseContext(AppHost.MenuContext);
        var updateNotice = UseContext(AppHost.UpdateNoticeContext);
        var config = services.Config;
        var i18n = services.I18n;
        var toast = UseContext(AppHost.ToastContext)!;
        var window = UseWindow();

        // threadSafe: true —— 保存与重渲染的调用点虽在 UI 线程，但保留统一语义，
        // 以免将来把写回挪到线程池时漏改（ADR-07 的成本是崩溃，不是降级）。
        var (savedAt, setSavedAt) = UseState<DateTime?>(null, threadSafe: true);
        var (error, setError) = UseState<string?>(null, threadSafe: true);
        var (clearDialogOpen, setClearDialogOpen) = UseState(false, threadSafe: true);

        // 页面缓存开启后组件实例常驻：切走时若「清除缓存」确认弹窗还开着，
        // 真实 ContentDialog 挂在 XamlRoot 的 Popup 里，会跟着留到别的页面上。
        // 离开时强制关掉。委托必须跨渲染同一实例（否则 lifecycle 反复重注册），
        // 闭包里的页面状态因此走 action ref —— 每次渲染刷新，永远读到最新值。
        var closeDialogRef = UseRef<Action?>(null);
        closeDialogRef.Current = () =>
        {
            if (clearDialogOpen)
            {
                setClearDialogOpen(false);
            }
        };
        var navigatedFromRef = UseRef<Action<NavigatedFromContext>?>(null);
        navigatedFromRef.Current ??= _ => closeDialogRef.Current?.Invoke();
        UseNavigationLifecycle(onNavigatedFrom: navigatedFromRef.Current);

        // 本页自身也订阅主题状态：配色（战绩卡片/死亡数字）变化时编号步进器等
        // 消费 palette 的元素要跟着重渲染（主题模式由 AppShell 统一施加，不在此列）。
        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);

        // 云端服务：SignalR 四态徽标状态（EventStream 推送来自后台线程，threadSafe 消费，ADR-07）
        var signalr = services.Services.GetRequiredService<YuumiX.Core.Realtime.SignalrService>();
        var (signalrStatus, setSignalrStatus) = UseState(signalr.Current, threadSafe: true);

        // 软件更新：检查结果 / 错误 / 下载进度 / 已就绪的安装包路径
        //（检查中与下载中的执行态由下方两个 UseMutation 的 IsPending 承载，R7）
        // 初值取自全局更新提示状态：启动检查已经查到的版本，进页面就该直接显示版本详情与下载按钮，
        // 而不是让用户对着「已是最新」再手动查一次。
        var updateService = services.Services.GetRequiredService<YuumiX.Core.Updates.UpdateService>();
        var (updateInfo, setUpdateInfo) = UseState(updateNotice.Pending, threadSafe: true);
        var (updateChecked, setUpdateChecked) = UseState(updateNotice.HasUpdate, threadSafe: true);
        var (updateCheckError, setUpdateCheckError) = UseState<string?>(null, threadSafe: true);
        var (updateProgress, setUpdateProgress) = UseState<YuumiX.Core.Updates.DownloadProgress?>(null, threadSafe: true);
        var (downloadedUpdatePath, setDownloadedUpdatePath) = UseState<string?>(null, threadSafe: true);

        // 防抖序号（effect 依赖）：每次编辑 +1；计数本体在 ref 里单调递增不回零
        //（旧实现把 cloudTick 清零会踩中"setState 同值跳过"导致下一次编辑不触发防抖，R6 一并修掉）。
        var (colorTick, setColorTick) = UseState(0, threadSafe: true);
        var (pathEditTick, setPathEditTick) = UseState(0, threadSafe: true);
        var (cloudTextTick, setCloudTextTick) = UseState(0, threadSafe: true);

        // 编辑草稿（参与渲染：防抖期间的重渲染要继续显示用户输入的半截文本）
        var (pathEdit, setPathEdit) = UseState<PathEditUi?>(null, threadSafe: true);
        var (cloudText, setCloudText) = UseState(InitialCloudText, threadSafe: true);

        // 正文列宽度：跟随滚动区宽度收缩，超过上限后固定并居中（原因见 ContentColumn 注释）。
        var (columnWidth, setColumnWidth) = UseState(ContentColumn.MaxWidth, threadSafe: true);

        // 非渲染数据（AGENTS.md §5.10）：ref 持有，渲染期只做镜像写入
        var loggerRef = UseRef<ILogger<SettingsPage>>(null!);
        var logger = loggerRef.Current ??= services.LoggerFactory.CreateLogger<SettingsPage>();
        var dispatcherRef = UseRef<Microsoft.UI.Dispatching.DispatcherQueue?>(null);
        var colorTickRef = UseRef(0);
        var pendingColorFieldRef = UseRef<string?>(null);
        var pathTickRef = UseRef(0);
        var cloudTickRef = UseRef(0);
        // 代理地址草稿：只在点「应用」时读它，不参与渲染，避免受控文本框的光标跳动。
        var proxyDraftRef = UseRef<string?>(null);
        // 草稿最新值镜像：防抖定时器与卸载兜底在异步时点读取（setter 无 updater 形态，闭包值可能过期）
        var pathEditRef = UseRef<PathEditUi?>(null);
        pathEditRef.Current = pathEdit;
        var cloudTextRef = UseRef(InitialCloudText);
        cloudTextRef.Current = cloudText;

        // ── 软件更新（R7）：检查与下载改走 UseMutation，执行态与异常由 hook 承载
        var checkUpdate = UseMutation<object?, YuumiX.Core.Updates.UpdateInfo?>(
            (_, _) => updateService.CheckForUpdateAsync(services.Paths.IsPortable),
            new MutationOptions<object?, YuumiX.Core.Updates.UpdateInfo?>(
                OnOptimistic: _ =>
                {
                    setUpdateCheckError(null);
                    setUpdateInfo(null);
                },
                OnSuccess: (info, _) =>
                {
                    setUpdateInfo(info);
                    setUpdateChecked(true);
                    // 把结果回灌全局状态：侧边栏红点与设置页必须说同一件事。
                    // info 为 null（已是最新）时 Clear 掉红点，避免「查完发现没更新，红点还在呼吸」。
                    updateNotice.Publish(info);
                    toast.Info(info is null
                        ? i18n.T("settings.alreadyLatest", "当前已是最新版本")
                        : i18n.T("settings.newVersionFound", "发现新版本") + $" v{info.Version}");
                },
                OnError: (ex, _) =>
                {
                    setUpdateCheckError(ex.Message);
                    toast.Error(i18n.T("settings.updateCheckFailed", "检查更新失败: ") + ex.Message);
                }));

        var downloadUpdate = UseMutation<YuumiX.Core.Updates.UpdateInfo, string>(
            (info, _) => updateService.DownloadUpdateAsync(
                info,
                new Progress<YuumiX.Core.Updates.DownloadProgress>(p =>
                {
                    // Progress<T> 已封送回 UI 线程（ADR-07 无需 threadSafe 处理，但保留统一语义）
                    setUpdateProgress(p);
                })),
            new MutationOptions<YuumiX.Core.Updates.UpdateInfo, string>(
                OnOptimistic: _ => setUpdateCheckError(null),
                OnSuccess: (path, _) =>
                {
                    setDownloadedUpdatePath(path);
                    toast.Success(i18n.T("settings.updateDownloadSuccess", "更新下载完成，可立即安装"));
                },
                OnError: (ex, _) =>
                {
                    setUpdateCheckError(ex.Message);
                    toast.Error(i18n.T("settings.updateDownloadFailed", "下载更新失败: ") + ex.Message);
                }));

        UseEffect(
            () =>
            {
                var subscription = theme.Subscribe(setPalette);
                return () =>
                {
                    subscription.Dispose();
                };
            },
            0);

        // SignalR 状态流订阅（四态徽标）
        UseEffect(
            () =>
            {
                var subscription = signalr.Status.Subscribe(setSignalrStatus);
                return () => subscription.Dispose();
            },
            0);

        // 全局更新提示订阅：周期复查在本页停留期间发现新版本时，版本详情要立即出现
        //（threadSafe: true —— 复查在后台线程发布，ADR-07）。
        // 清除（关闭开关 / 已是最新）时只清结果，不把「检查过」的标记一起丢掉：
        // 那会把状态文案退回「尚未检查」，与用户刚点过的操作矛盾。
        UseEffect(
            () =>
            {
                var subscription = updateNotice.Subscribe(state =>
                {
                    setUpdateInfo(state.Pending);
                    if (state.HasUpdate)
                    {
                        setUpdateChecked(true);
                    }
                });
                return () => subscription.Dispose();
            },
            0);

        // 多语言切换订阅：语言变更时刷新本页文本与标题
        var (currentLang, setCurrentLang) = UseState(services.I18n.CurrentLanguage, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = services.I18n.CurrentLanguageChanged.Subscribe(setCurrentLang);
                return () => subscription.Dispose();
            },
            0);

        // 单次写回：先落盘（必要时备份），成功与失败都只改状态，不抛给渲染流程。
        // savedAt/error 的值不参与渲染，但承担 Commit 后的重渲染触发（error 兼任首次保存
        // 即失败时的失败路径触发器）；备份文件名只进日志，不再另设死状态。
        void Commit(string field)
        {
            try
            {
                var backup = services.ConfigStore.SaveWithBackup(config);
                setError(null);
                setSavedAt(DateTime.Now);

                toast.Success(services.I18n.T("settings.autoSave", "设置已自动保存"));
                logger.LogInformation("设置已写回: {Field}（备份 {Backup}）", field, backup is null ? "无" : Path.GetFileName(backup));
            }
            catch (Exception ex)
            {
                // 写失败必须让用户看见：静默吞掉会让人以为设置已保存（ConfigStore.Save 的约定）
                setError(ex.Message);
                setSavedAt(null);
                toast.Error($"{services.I18n.T("settings.saveFailed", "设置保存失败")}: {ex.Message}");
                logger.LogError(ex, "设置写回失败: {Field}", field);
            }
        }

        // ── 防抖请求入口（计数在 ref 单调递增；序号进 state 驱动 effect）────────

        // 取色变化：内存与界面即时生效，写盘延后合并（见 ColorCommitDelayMs）。
        void RequestColorCommit(string field)
        {
            pendingColorFieldRef.Current = field;
            setColorTick(++colorTickRef.Current);
        }

        // 路径条目文本变化：先记草稿（渲染时回填用），写盘延后合并（见 PathCommitDelayMs）。
        void RequestPathCommit(int index, bool isWeGame, string draft)
        {
            setPathEdit(new PathEditUi(index, isWeGame, draft));
            setPathEditTick(++pathTickRef.Current);
        }

        // 云端服务文本变化：记草稿，防抖后落盘并触发 Hub 重启/停止。
        // 两个草稿可独立编辑：null 表示"本次没有改动这个框"。
        void RequestCloudTextCommit(string? urlDraft, string? userIdDraft)
        {
            setCloudText(new CloudTextUi(
                urlDraft ?? cloudText.Url,
                userIdDraft ?? cloudText.UserId));
            setCloudTextTick(++cloudTickRef.Current);
        }

        Microsoft.UI.Dispatching.DispatcherQueue Dispatcher() =>
            dispatcherRef.Current ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        var ui = new SettingsUi(config, services, toast, theme, menu, updateNotice, palette, logger, Commit, setError, window);

        // ── 防抖 effect：序号每变一次就重置定时器，只有最后一次会真正落盘 ────────
        // 定时器回调在线程池，写盘与改状态一律 TryEnqueue 回 UI 线程（ADR-12 决策一）。

        UseEffect(
            () =>
            {
                if (colorTick == 0)
                {
                    return static () => { };
                }

                var timer = new Timer(
                    _ => Dispatcher().TryEnqueue(() =>
                    {
                        var field = pendingColorFieldRef.Current;
                        if (field is null)
                        {
                            return;
                        }

                        pendingColorFieldRef.Current = null;
                        Commit(field);
                    }),
                    null,
                    ColorCommitDelayMs,
                    Timeout.Infinite);
                return () => timer.Dispose();
            },
            colorTick);

        UseEffect(
            () =>
            {
                if (pathEditTick == 0)
                {
                    return static () => { };
                }

                var timer = new Timer(
                    _ => Dispatcher().TryEnqueue(() => SettingsWriteback.ApplyPathEdit(ui, pathEditRef, setPathEdit)),
                    null,
                    PathCommitDelayMs,
                    Timeout.Infinite);
                return () => timer.Dispose();
            },
            pathEditTick);

        UseEffect(
            () =>
            {
                if (cloudTextTick == 0)
                {
                    return static () => { };
                }

                var timer = new Timer(
                    _ => Dispatcher().TryEnqueue(() => SettingsWriteback.ApplyCloudTextEdit(ui, cloudTextRef, setCloudText, InitialCloudText)),
                    null,
                    PathCommitDelayMs,
                    Timeout.Infinite);
                return () => timer.Dispose();
            },
            cloudTextTick);

        // 卸载兜底：防抖窗口内切走页面时，直接把待写颜色/路径落盘（不碰 UI 状态，避免卸载后 setState）。
        UseEffect(
            () => () => SettingsWriteback.FlushPendingEdits(ui, pendingColorFieldRef, pathEditRef, cloudTextRef),
            0);

        var update = new UpdateUi(
            updateInfo, updateChecked, updateCheckError, updateProgress, downloadedUpdatePath,
            checkUpdate, downloadUpdate, setUpdateCheckError);

        var mainScroll = ScrollViewer(
            VStack(8,
                PageHeaders.PageHeader(
                    services.I18n.T("settings.title", "设置"),
                    includeBottomMargin: false)
                    .Margin(bottom: 8),

                // 1. 功能
                SettingsVisuals.GroupHeader(services.I18n.T("settings.groupFeatures", "功能")),
                FunctionSection.Render(ui),

                // 2. OP.GG
                SettingsVisuals.GroupHeader(services.I18n.T("settings.opggGroup", "OP.GG")),
                OpggSection.Render(ui),

                // 3. 通用设置
                SettingsVisuals.GroupHeader(services.I18n.T("settings.generalGroup", "通用设置")),
                GeneralSection.Render(ui, pathEdit, RequestPathCommit, cloudText, RequestCloudTextCommit, signalrStatus, update, () => setClearDialogOpen(true)),

                // 4. 高级与隐藏
                SettingsVisuals.GroupHeader(services.I18n.T("settings.advancedGroup", "高级与隐藏")),
                AdvancedSection.Render(ui),

                // 5. 日志与网络
                SettingsVisuals.GroupHeader(services.I18n.T("settings.logNetworkGroup", "日志与网络")),
                LogNetworkSection.Render(ui, proxyDraftRef),

                // 6. 外观与个性化
                SettingsVisuals.GroupHeader(services.I18n.T("settings.personalizationGroup", "外观与个性化")),
                AppearanceSection.Render(ui, RequestColorCommit),

                // 7. 关于
                SettingsVisuals.GroupHeader(services.I18n.T("settings.aboutTitle", "关于")),
                AboutSection.Render(ui)
            )
            // 正文列：显式宽度 + 居中（不要写成 MaxWidth + Stretch，原因见 ContentColumn 注释）。
            .Width(columnWidth)
            .HAlign(HorizontalAlignment.Center)
            .Padding(24)
        )
        // 点击页面空白处时，WinUI 会把焦点下发给滚动容器内容里的「第一个可聚焦控件」，
        // 本页即「LCU API 并发数」的 NumberBox —— 光标会莫名其妙跳进那个输入框。
        // 让滚动容器自身成为可聚焦元素后，焦点停在容器上，不再下发给子控件。
        // 用修饰符而非 .Set(...)：控件走池回收，Set 写入会在重渲染时被重置（REACTOR_POOL_001）。
        .IsTabStop(true);
        mainScroll = mainScroll.OnSizeChanged((_, e) =>
        {
            var target = ContentColumn.Fit(e.NewSize.Width);
            if (Math.Abs(target - columnWidth) >= 0.5)
            {
                setColumnWidth(target);
            }
        });

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                mainScroll.Grid(row: 0, column: 0),
                // 清除缓存确认弹窗（声明式：IsOpen 驱动显隐）
                // 必须走 Factories.ContentDialog：Reactor 的 handler 注册是惰性的，只有工厂调用
                // 才会把 ContentDialogElement 注册进 ControlRegistry；直接
                // new Microsoft.UI.Reactor.Core.ContentDialogElement(...) 能编译但在挂载时抛
                // InvalidOperationException（No handler is registered for element type ...）。
                (ContentDialog(
                    services.I18n.T("settings.clearCacheTitle", "清除缓存"),
                    Body(services.I18n.T("settings.clearCacheConfirm", "确定要清除所有游戏资源缓存吗？清除后需要重新加载游戏资源。"))
                        .TextWrapping(),
                    primaryButtonText: services.I18n.T("common.confirm", "确定")) with
                {
                    IsOpen = clearDialogOpen,
                    CloseButtonText = services.I18n.T("common.cancel", "取消"),
                })
                // 对话框宿主在 XamlRoot 的 Popup 里，不在根 Grid 的视觉树内，
                // 且 ContentDialogElement 的 Modifiers（.RequestedTheme）只作用在主树不可见的占位 StackPanel 上，
                // 真实弹窗只接收 Setters（OverlayLifecycle.ShowContentDialogCore），故统一走 DialogTheme 扩展。
                .DialogTheme(palette.ElementTheme)
                .Closed(result =>
                {
                    setClearDialogOpen(false);
                    if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                    {
                        return;
                    }

                    try
                    {
                        var removed = services.AssetCache.Clear();
                        try
                        {
                            services.OpggHttpClient?.ClearMemoryCache();
                            services.OpggHttpClient?.ClearDiskCache();
                        }
                        catch
                        {
                        }
                        logger.LogInformation("已清空游戏资源与 OP.GG 缓存: {Count} 个文件", removed);
                        toast.Success(removed == 0
                            ? i18n.T("settings.clearCacheEmpty", "缓存目录不存在，无需清除")
                            : i18n.T("settings.clearCacheSuccessCount", "已清除 {count} 个缓存文件").Replace("{count}", removed.ToString()));
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "清除游戏资源缓存失败");
                        toast.Error(i18n.T("settings.clearCacheFailed", "清除缓存失败: ") + ex.Message);
                    }
                }),
            ]
        );
    }
}
