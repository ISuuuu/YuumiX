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
/// 不参与渲染的数据（代理草稿、待写颜色字段、防抖计数、logger）走 UseRef；
/// 异步回调消费"最新值"一律经镜像 ref，避免闭包捕获过期渲染。没有任何"踢重渲染"辅助。
/// DispatcherQueue 不缓存：它只在 UI 线程可得，缓存到 ref 反而给了"在线程池取到 null"的机会
/// （见下方 StartDebounce 注释）。
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
        var scale = UseContext(AppHost.ScaleContext)!;
        var config = services.Config;
        var i18n = services.I18n;
        var toast = UseContext(AppHost.ToastContext)!;
        var window = UseWindow()!;

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

        // 正文列宽度与左边距：跟随滚动区宽度收缩；边距按像素网格量化（原因见 ContentColumn 注释），
        // ComboBox 弹层继承这个锚点位置，落点半像素就会文字发虚。
        var (columnWidth, setColumnWidth) = UseState(ContentColumn.MaxWidth, threadSafe: true);
        var (columnLeft, setColumnLeft) = UseState(0.0, threadSafe: true);

        // 非渲染数据（AGENTS.md §5.10）：ref 持有，渲染期只做镜像写入
        var loggerRef = UseRef<ILogger<SettingsPage>>(null!);
        var logger = loggerRef.Current ??= services.LoggerFactory.CreateLogger<SettingsPage>();
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
        // forceRefresh: true —— 这条路径只由用户显式点击「检查更新 / 重新检查」触发，
        // 必须绕过 24h 缓存真正联网。否则缓存刚写完、新版刚发布时点击会得到「已是最新」的假结论
        // （详见 UpdateService.CheckForUpdateAsync 的 forceRefresh 注释）。
        var checkUpdate = UseMutation<object?, YuumiX.Core.Updates.UpdateInfo?>(
            (_, _) => updateService.CheckForUpdateAsync(services.Paths.IsPortable, forceRefresh: true),
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

        // 下载取消令牌：UseMutation 只暴露 IsPending/Error，没有 Cancel，暂停只能自己持令牌。
        // 必须与钩子传入的 ct 做 linked——页面卸载时框架取消的是它那个 ct，
        // 只取消自己的令牌会让「切走页面」变成「下载继续在后台跑」。
        var downloadCtsRef = UseRef<CancellationTokenSource?>(null);

        var downloadUpdate = UseMutation<YuumiX.Core.Updates.UpdateInfo, string>(
            async (info, ct) =>
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                downloadCtsRef.Current = cts;
                // 用 token（结构体）而不是 cts 做进度闸门：回调可能在 cts 释放后才被调度，
                // 读已释放的 CTS 属性是踩雷，CancellationToken 则是安全的快照。
                var token = cts.Token;
                try
                {
                    return await updateService.DownloadUpdateAsync(
                        info,
                        new Progress<YuumiX.Core.Updates.DownloadProgress>(p =>
                        {
                            // 暂停后仍可能有排队中的进度回调，别让它们把「继续下载」重新盖成进度条。
                            if (!token.IsCancellationRequested)
                            {
                                setUpdateProgress(p);
                            }
                        }),
                        token);
                }
                finally
                {
                    downloadCtsRef.Current = null;
                }
            },
            new MutationOptions<YuumiX.Core.Updates.UpdateInfo, string>(
                // 清掉上一次的进度：断点续传时首帧进度会从断点处跳上来，
                // 残留的旧进度会让「重新开始」与「续传」看起来一样。
                OnOptimistic: _ =>
                {
                    setUpdateCheckError(null);
                    setUpdateProgress(null);
                },
                OnSuccess: (path, _) =>
                {
                    setUpdateProgress(null);
                    setDownloadedUpdatePath(path);
                    toast.Success(i18n.T("settings.updateDownloadSuccess", "更新下载完成，可立即安装"));
                },
                OnError: (ex, info) =>
                {
                    // 必须清进度：否则 PrimaryAction 一直停在「进度条」分支，
                    // 用户既看不到失败原因也点不到重试，只能重启应用。
                    setUpdateProgress(null);
                    setUpdateCheckError(ex.Message);

                    // 下载器会保留残包，这里据实提示「可以接着下」，而不是让用户以为要从 0 重来。
                    long kept = updateService.GetResumableBytes(info);
                    if (kept > 0)
                    {
                        toast.Error(i18n.T("settings.updateDownloadInterrupted", "下载中断，已保留进度: ")
                            + ex.Message
                            + $"（已下载 {kept / 1024.0 / 1024.0:F1} MB，可继续下载）");
                        return;
                    }

                    toast.Error(i18n.T("settings.updateDownloadFailed", "下载更新失败: ") + ex.Message);
                }));

        // 暂停：取消是异步生效的，这里立刻清进度让按钮回到「继续下载」；
        // 钩子对取消是静默的（不触发 OnError），收尾只能自己做。
        void PauseDownload()
        {
            downloadCtsRef.Current?.Cancel();
            setUpdateProgress(null);
        }

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

        var ui = new SettingsUi(config, services, toast, theme, menu, updateNotice, palette, logger, Commit, setError, window);

        // ── 防抖 effect：序号每变一次就重置定时器，只有最后一次会真正落盘 ────────
        // 定时器回调在线程池，写盘与改状态一律 TryEnqueue 回 UI 线程（ADR-12 决策一）。

        // DispatcherQueue 必须在 UI 线程取好再交给定时器：ThreadPool 线程上
        // DispatcherQueue.GetForCurrentThread() 返回 null，回调里再取就等于解引用 null，
        // 而未处理异常在 ThreadPool 上会把整个进程打死（用户看到的是「点颜色后卡死」，实际是闪退）。
        // 故此处（UseEffect 体，UI 线程）取一次，随闭包交给回调；回调体也要兜住异常，
        // 写回链路的任何意外都不该演变成进程级崩溃。
        Timer? StartDebounce(int delayMs, Action work)
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is null)
            {
                logger.LogWarning("防抖写回未启动：当前线程没有 DispatcherQueue");
                return null;
            }

            return new Timer(
                _ => queue.TryEnqueue(() =>
                {
                    try
                    {
                        work();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "防抖写回执行失败");
                    }
                }),
                null,
                delayMs,
                Timeout.Infinite);
        }

        UseEffect(
            () =>
            {
                if (colorTick == 0)
                {
                    return static () => { };
                }

                var timer = StartDebounce(ColorCommitDelayMs, () =>
                {
                    var field = pendingColorFieldRef.Current;
                    if (field is null)
                    {
                        return;
                    }

                    pendingColorFieldRef.Current = null;
                    Commit(field);
                });
                return timer is null ? static () => { } : timer.Dispose;
            },
            colorTick);

        UseEffect(
            () =>
            {
                if (pathEditTick == 0)
                {
                    return static () => { };
                }

                var timer = StartDebounce(
                    PathCommitDelayMs,
                    () => SettingsWriteback.ApplyPathEdit(ui, pathEditRef, setPathEdit));
                return timer is null ? static () => { } : timer.Dispose;
            },
            pathEditTick);

        UseEffect(
            () =>
            {
                if (cloudTextTick == 0)
                {
                    return static () => { };
                }

                var timer = StartDebounce(
                    PathCommitDelayMs,
                    () => SettingsWriteback.ApplyCloudTextEdit(ui, cloudTextRef, setCloudText, InitialCloudText));
                return timer is null ? static () => { } : timer.Dispose;
            },
            cloudTextTick);

        // 卸载兜底：防抖窗口内切走页面时，直接把待写颜色/路径落盘（不碰 UI 状态，避免卸载后 setState）。
        UseEffect(
            () => () => SettingsWriteback.FlushPendingEdits(ui, pendingColorFieldRef, pathEditRef, cloudTextRef),
            0);

        // 断点余量：只在「有更新可下、且既不在下载也没下完」时需要读。
        // 直接读残包长度（一次 stat）而不做缓存——缓存要随下载成功/失败/取消三种状态失效，
        // 复杂度远大于它省下的那点 IO。
        long resumableBytes = updateInfo is not null && updateProgress is null && downloadedUpdatePath is null
            ? updateService.GetResumableBytes(updateInfo)
            : 0;

        var update = new UpdateUi(
            updateInfo, updateChecked, updateCheckError, updateProgress, downloadedUpdatePath, resumableBytes,
            checkUpdate, downloadUpdate, PauseDownload, setUpdateCheckError);

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
            // 正文列：显式宽度 + 整数左边距 + 左对齐。不要写回 HAlign(Center)：
            // 宽度差为奇数时 Center 产生 .5 偏移，独立 Popup 树里的 ComboBox 弹层被半像素重采样（见 ContentColumn）。
            .Width(columnWidth)
            .HAlign(HorizontalAlignment.Left)
            .Margin(left: columnLeft)
            .Padding(24)
        )
        // 点击页面空白处时，WinUI 会把焦点下发给滚动容器内容里的「第一个可聚焦控件」，
        // 本页即「LCU API 并发数」的 NumberBox —— 光标会莫名其妙跳进那个输入框。
        // 让滚动容器自身成为可聚焦元素后，焦点停在容器上，不再下发给子控件。
        // 用修饰符而非 .Set(...)：控件走池回收，Set 写入会在重渲染时被重置（REACTOR_POOL_001）。
        .IsTabStop(true);
        // 弹层落点的像素网格 = 系统 DPI 缩放 × 界面缩放档位（Viewbox 会把子树坐标再乘一次），见 ContentColumn.CenterOffset。
        double PixelScale() => Math.Max(1.0, window.DipScale) * scale.Current;
        mainScroll = mainScroll.OnSizeChanged((_, e) =>
        {
            var targetWidth = ContentColumn.Fit(e.NewSize.Width);
            var targetLeft = ContentColumn.CenterOffset(e.NewSize.Width, targetWidth, PixelScale());
            if (Math.Abs(targetWidth - columnWidth) >= 0.5)
            {
                setColumnWidth(targetWidth);
            }

            if (Math.Abs(targetLeft - columnLeft) >= 0.5)
            {
                setColumnLeft(targetLeft);
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
                        catch (Exception ex)
                        {
                            // 不能只记「已清空」：OP.GG 的磁盘缓存清不掉时旧图仍会被命中，
                            // 用户看到的是「清了但没生效」。
                            logger.LogWarning(ex, "清理 OP.GG 缓存失败（游戏资源缓存已清理）");
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
