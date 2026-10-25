using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Pages.Career;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Replay;

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

        var (status, setStatus) = UseState("idle", threadSafe: true);
        var (polling, setPolling) = UseState(false, threadSafe: true);
        var (notice, setNotice) = UseState<string?>(null, threadSafe: true);

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

        if (Props.Compact)
        {
            return RenderCompact(status, notice, HandleClick);
        }

        return RenderNormal(status, notice, HandleClick);
    }

    private const string IconFontFamily = "Segoe Fluent Icons";

    private static TextBlockElement Glyph(string glyph, string? foreground = null)
    {
        var text = Caption(glyph).FontFamily(IconFontFamily);
        return foreground is null ? text : text.Foreground(foreground);
    }

    private static TextBlockElement Glyph(string glyph, ThemeRef themeRef) =>
        Caption(glyph).FontFamily(IconFontFamily).Foreground(themeRef);

    private static Element RenderCompact(string status, string? notice, Action onClick)
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
                Button(Glyph("\uE768", CareerVisuals.WinText), onClick)
                    .Width(28)
                    .Height(28)
                    .Padding(2, 0)
                    .AutomationName("观看回放")
                    .WithToolTip(TextBlock("点击启动对局回放")),
            "unsupported" =>
                Button(Glyph("\uE711", Theme.SecondaryText), onClick)
                    .Width(28)
                    .Height(28)
                    .Padding(2, 0)
                    .IsEnabled(false)
                    .AutomationName("回放不可用")
                    .WithToolTip(TextBlock(notice ?? "该对局回放不可用")),
            "error" =>
                Button(Glyph("\uE783", CareerVisuals.LoseText), onClick)
                    .Width(28)
                    .Height(28)
                    .Padding(2, 0)
                    .AutomationName("下载失败重试")
                    .WithToolTip(TextBlock(notice ?? "下载失败，点击重试")),
            _ =>
                Button(Glyph("\uE896", Theme.SecondaryText), onClick)
                    .Width(28)
                    .Height(28)
                    .Padding(2, 0)
                    .AutomationName("回放")
                    .WithToolTip(TextBlock("回放")),
        };
    }

    private static Element RenderNormal(string status, string? notice, Action onClick)
    {
        return status switch
        {
            "downloading" or "checking" =>
                Button(HStack(6, ProgressRing().Width(14).Height(14), Caption("下载中…")), onClick)
                    .Height(28)
                    .Padding(10, 0)
                    .AutomationName("回放下载中"),
            "watch" =>
                Button(HStack(6, Glyph("\uE768", CareerVisuals.WinText), Caption("观看回放")), onClick)
                    .Height(28)
                    .Padding(10, 0)
                    .AutomationName("观看回放"),
            "unsupported" =>
                Button(HStack(6, Glyph("\uE711"), Caption("不可用")), onClick)
                    .Height(28)
                    .Padding(10, 0)
                    .IsEnabled(false)
                    .AutomationName("回放不可用")
                    .WithToolTip(TextBlock(notice ?? "该对局回放不可用")),
            "error" =>
                Button(HStack(6, Glyph("\uE783", CareerVisuals.LoseText), Caption("重试")), onClick)
                    .Height(28)
                    .Padding(10, 0)
                    .AutomationName("重试")
                    .WithToolTip(TextBlock(notice ?? "下载失败，点击重试")),
            _ =>
                Button(HStack(6, Glyph("\uE896"), Caption("回放")), onClick)
                    .Height(28)
                    .Padding(10, 0)
                    .AutomationName("回放")
                    .WithToolTip(TextBlock("回放")),
        };
    }
}
