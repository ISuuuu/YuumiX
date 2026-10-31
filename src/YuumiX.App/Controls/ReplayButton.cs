using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Pages.Career;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Replay;
using YuumiX.Core.Interop;

namespace YuumiX.App.Controls;

public sealed record ReplayButtonProps(long GameId, bool Compact = false);

/// <summary>
/// 对局 ROFL 回放按钮组件：状态机 (idle / checking / downloading / watch / unsupported / error)
/// 严格对齐原 <c>ReplayButton.vue</c> 行为：
/// 1. 点击 idle/error 时先查状态，已就绪则直接观看，否则请求下载并启动 1.5s 轮询（最多 40 次）；
/// 2. 状态变为 watch 时提示可观看，点击拉起客户端观战进程；
/// 3. 支持 compact（仅图标）与普通两种模式。
/// </summary>
public sealed class ReplayButton : Component<ReplayButtonProps>
{
    public override Element Render()
    {
        var services = UseContext(AppHost.Context) ?? AppHost.Services;
        var replayService = services.Services.GetRequiredService<IReplayService>();
        var windowLogger = services.LoggerFactory.CreateLogger<ReplayButton>();

        var (status, setStatus) = UseState("idle", threadSafe: true);
        var (polling, setPolling) = UseState(false, threadSafe: true);
        var (notice, setNotice) = UseState<string?>(null, threadSafe: true);

        var (lang, setLang) = UseState(services.I18n.CurrentLanguage, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = services.I18n.CurrentLanguageChanged.Subscribe(setLang);
                return () => subscription.Dispose();
            },
            0);

        // 轮询回放状态
        UseEffect(
            () =>
            {
                if (!polling)
                {
                    return static () => { };
                }

                var cts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    int pollCount = 0;
                    while (!cts.Token.IsCancellationRequested && pollCount < 40)
                    {
                        pollCount++;
                        try
                        {
                            await Task.Delay(1500, cts.Token).ConfigureAwait(false);
                            string current = await replayService.GetReplayStatusAsync(Props.GameId, cts.Token).ConfigureAwait(false);
                            string s = current.ToLowerInvariant();

                            if (s == "watch" || s == "found")
                            {
                                setStatus("watch");
                                setPolling(false);
                                setNotice("回放下载完成，可点击观看");
                                break;
                            }
                            if (s == "unsupported" || s == "incompatible" || s == "missingorexpired" || s == "lost")
                            {
                                setStatus("unsupported");
                                setPolling(false);
                                setNotice(s == "incompatible" ? "版本不一致，无法播放" : "回放已过期或不受支持");
                                break;
                            }
                            if (s == "error")
                            {
                                setStatus("error");
                                setPolling(false);
                                setNotice("下载回放失败");
                                break;
                            }
                            if (s == "downloading" || s == "checking")
                            {
                                setStatus("downloading");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch
                        {
                            // 忽略单次轮询异常，继续等待
                        }
                    }

                    if (pollCount >= 40)
                    {
                        setStatus("idle");
                        setPolling(false);
                        setNotice("下载回放超时，请重试");
                    }
                }, cts.Token);

                return () =>
                {
                    cts.Cancel();
                    cts.Dispose();
                };
            },
            polling ? 1 : 0);

        void HandleClick()
        {
            if (status == "downloading" || status == "checking")
            {
                return;
            }
            if (status == "unsupported")
            {
                return;
            }
            if (status == "watch")
            {
                _ = Task.Run(async () =>
                {
                    // 客户端拉起回放进程链会闪控制台窗口（旧版同样存在），布防期间闪现即隐藏
                    ConsoleWindowSuppressor.Arm(windowLogger);
                    await replayService.WatchReplayAsync(Props.GameId);
                });
                return;
            }

            // idle 或 error
            setStatus("checking");
            _ = Task.Run(async () =>
            {
                try
                {
                    string cur = (await replayService.GetReplayStatusAsync(Props.GameId)).ToLowerInvariant();
                    if (cur == "watch" || cur == "found")
                    {
                        setStatus("watch");
                        ConsoleWindowSuppressor.Arm(windowLogger);
                        await replayService.WatchReplayAsync(Props.GameId);
                        return;
                    }
                    if (cur == "unsupported" || cur == "incompatible" || cur == "missingorexpired" || cur == "lost")
                    {
                        setStatus("unsupported");
                        setNotice(cur == "incompatible" ? "版本不一致，无法播放" : "回放不可用或已过期");
                        return;
                    }

                    setStatus("downloading");
                    ConsoleWindowSuppressor.Arm(windowLogger);
                    await replayService.DownloadReplayAsync(Props.GameId);
                    setPolling(true);
                }
                catch
                {
                    setStatus("error");
                    setNotice("下载请求失败");
                }
            });
        }

        // 宽度内容自适应（对齐旧版 ReplayButton.vue：无 min-width，文字贴边）；
        // 代价是状态切换时按钮宽度会变、右侧组（金币/时间列）随之横移，旧版同样如此。
        var element = Props.Compact
            ? RenderCompact(status, notice, HandleClick)
            : RenderNormal(status, notice, HandleClick);

        return element.OnTapped((_, args) => args.Handled = true);
    }

    private static TextBlockElement Glyph(string glyph, string? foreground = null)
    {
        var text = Caption(glyph).FontFamily(Icons.FontFamily);
        return foreground is null ? text : text.Foreground(foreground);
    }

    private static TextBlockElement Glyph(string glyph, ThemeRef themeRef) =>
        Caption(glyph).FontFamily(Icons.FontFamily).Foreground(themeRef);

    private static ButtonElement RenderCompact(string status, string? notice, Action onClick)
    {
        return status switch
        {
            "downloading" or "checking" =>
                Button(ProgressRing().Width(14).Height(14), onClick)
                    .Width(28)
                    .Height(28)
                    .Padding(2, 0)
                    .AutomationName("回放下载中")
                    .WithToolTip(TextBlock("回放下载中…")),
            "watch" =>
                Button(Glyph(Icons.Play, CareerVisuals.WinText), onClick)
                    .Width(28)
                    .Height(28)
                    .Padding(2, 0)
                    .AutomationName("观看回放")
                    .WithToolTip(TextBlock("点击启动对局回放")),
            "unsupported" =>
                Button(Glyph(Icons.Cancel, Theme.SecondaryText), onClick)
                    .Width(28)
                    .Height(28)
                    .Padding(2, 0)
                    .IsEnabled(false)
                    .AutomationName("回放不可用")
                    .WithToolTip(TextBlock(notice ?? "该对局回放不可用")),
            "error" =>
                Button(Glyph(Icons.Error, CareerVisuals.LoseText), onClick)
                    .Width(28)
                    .Height(28)
                    .Padding(2, 0)
                    .AutomationName("下载失败重试")
                    .WithToolTip(TextBlock(notice ?? "下载失败，点击重试")),
            _ =>
                Button(Glyph(Icons.Replay, Theme.SecondaryText), onClick)
                    .Width(28)
                    .Height(28)
                    .Padding(2, 0)
                    .AutomationName("回放")
                    .WithToolTip(TextBlock("回放")),
        };
    }

    private static ButtonElement RenderNormal(string status, string? notice, Action onClick)
    {
        var i18n = Composition.AppHost.Services?.I18n;
        var replayText = i18n?.T("replay.btn", "回放") ?? "回放";
        var watchText = i18n?.T("replay.watch", "观看回放") ?? "观看回放";
        var checkingText = i18n?.T("replay.checking", "检查中…") ?? "检查中…";
        var downloadingText = i18n?.T("replay.downloading", "下载中…") ?? "下载中…";
        var unavailableText = i18n?.T("replay.unavailable", "不可用") ?? "不可用";
        var retryText = i18n?.T("replay.retry", "重试") ?? "重试";

        return status switch
        {
            "checking" =>
                Button(HStack(4, ProgressRing().Width(14).Height(14), Caption(checkingText)), onClick)
                    .Height(28)
                    .Padding(6, 0)
                    .AutomationName(checkingText),
            "downloading" =>
                Button(HStack(4, ProgressRing().Width(14).Height(14), Caption(downloadingText)), onClick)
                    .Height(28)
                    .Padding(6, 0)
                    .AutomationName(downloadingText),
            "watch" =>
                Button(HStack(4, Glyph(Icons.Play, CareerVisuals.WinText), Caption(watchText)), onClick)
                    .Height(28)
                    .Padding(6, 0)
                    .AutomationName(watchText),
            "unsupported" =>
                Button(HStack(4, Glyph(Icons.Cancel), Caption(unavailableText)), onClick)
                    .Height(28)
                    .Padding(6, 0)
                    .IsEnabled(false)
                    .AutomationName(unavailableText)
                    .WithToolTip(TextBlock(notice ?? unavailableText)),
            "error" =>
                Button(HStack(4, Glyph(Icons.Error, CareerVisuals.LoseText), Caption(retryText)), onClick)
                    .Height(28)
                    .Padding(6, 0)
                    .AutomationName(retryText)
                    .WithToolTip(TextBlock(notice ?? retryText)),
            _ =>
                Button(HStack(4, Glyph(Icons.Replay), Caption(replayText)), onClick)
                    .Height(28)
                    .Padding(6, 0)
                    .AutomationName(replayText)
                    .WithToolTip(TextBlock(replayText)),
        };
    }
}
