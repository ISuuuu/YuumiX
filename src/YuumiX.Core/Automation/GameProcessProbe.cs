using System.ComponentModel;
using System.Diagnostics;

namespace YuumiX.Core.Automation;

/// <summary>
/// 对局客户端进程探测（<c>Game/League of Legends.exe</c>）。
/// </summary>
/// <remarks>
/// 用于区分「真实掉线」与「用户主动退出」：真实掉线时游戏进程仍在运行（只是与游戏服务器失联），
/// 而 Alt+F4 关闭游戏后进程消失、LCU 却会停留在 Reconnect 阶段等待服务端结算写入（实测 2~4 秒），
/// 此时发起重连只会把游戏重新拉起并卡在加载页。
/// </remarks>
public static class GameProcessProbe
{
    /// <summary>进程名（<see cref="Process.GetProcessesByName(string)"/> 不接受 .exe 后缀）。</summary>
    public const string GameProcessName = "League of Legends";

    /// <summary>
    /// 枚举系统进程，判断对局客户端是否在运行。
    /// </summary>
    /// <remarks>
    /// 枚举失败时保守返回 <c>true</c>：误禁真实掉线的自动重连，代价高于偶发一次多余的重连。
    /// </remarks>
    public static bool IsRunning()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(GameProcessName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return true;
        }

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }
}
