using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using YuumiX.App.Composition;

namespace YuumiX.App.Windowing;

/// <summary>
/// 全局显隐热键监听（基于 GetAsyncKeyState 物理按键状态轮询）：
/// 绕过游戏 RawInput / DirectInput 对 WM_HOTKEY 的拦截，在英雄联盟无边框对局中也能稳定按一下切换显隐。
/// </summary>
public static class AramHotkeyMonitor
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    private static readonly Lock _lock = new();
    private static Thread? _thread;
    private static volatile bool _running;
    private static DispatcherQueue? _dispatcher;
    private static Func<int?>? _currentChampProvider;
    private static uint _mods;
    private static uint _vk;
    private static string _currentHotkey = "Alt+X";

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static void Start(
        DispatcherQueue dispatcher,
        string hotkey,
        Func<int?> currentChampProvider)
    {
        lock (_lock)
        {
            _dispatcher = dispatcher;
            _currentChampProvider = currentChampProvider;
            ApplyHotkeyLocked(hotkey);

            _running = true;
            if (_thread is { IsAlive: true })
            {
                return;
            }

            _thread = new Thread(PollLoop)
            {
                Name = "AramHotkeyThread",
                IsBackground = true
            };
            _thread.Start();
        }
    }

    public static void UpdateHotkey(string hotkey)
    {
        lock (_lock)
        {
            ApplyHotkeyLocked(hotkey);
        }
    }

    public static void Stop()
    {
        _running = false;
    }

    private static void ApplyHotkeyLocked(string hotkey)
    {
        _currentHotkey = string.IsNullOrWhiteSpace(hotkey) ? "Alt+X" : hotkey.Trim();
        if (!TryParseHotkey(_currentHotkey, out var mods, out var vk))
        {
            AppHost.Services?.LoggerFactory
                .CreateLogger(typeof(AramHotkeyMonitor))
                .LogWarning("[AramHotkey] 无法解析热键配置：{Hotkey}，回退为 Alt+X", _currentHotkey);
            mods = 0x0001; // Alt
            vk = 0x58;     // X
        }

        _mods = mods;
        _vk = vk;
        AppHost.Services?.LoggerFactory
            .CreateLogger(typeof(AramHotkeyMonitor))
            .LogInformation("[AramHotkey] 全局显隐热键已启用：{Hotkey}", _currentHotkey);
    }

    private static void PollLoop()
    {
        var wasPressed = false;

        while (_running)
        {
            uint mods;
            uint vk;
            lock (_lock)
            {
                mods = _mods;
                vk = _vk;
            }

            var pressedNow = vk != 0 && IsHotkeyHeld(mods, vk);

            if (pressedNow && !wasPressed)
            {
                wasPressed = true;
                var dispatcher = _dispatcher;
                if (dispatcher != null)
                {
                    var champId = _currentChampProvider?.Invoke();
                    dispatcher.TryEnqueue(() => AramDataWindowHost.ToggleHoldPreview(champId));
                }
            }
            else if (!pressedNow && wasPressed)
            {
                wasPressed = false;
            }

            Thread.Sleep(35);
        }
    }

    private static bool IsHotkeyHeld(uint mods, uint vk)
    {
        static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

        if (!IsDown((int)vk)) return false;

        const uint modAlt = 0x0001;
        const uint modControl = 0x0002;
        const uint modShift = 0x0004;

        if ((mods & modAlt) != 0 && !IsDown(VkMenu)) return false;
        if ((mods & modControl) != 0 && !IsDown(VkControl)) return false;
        if ((mods & modShift) != 0 && !IsDown(VkShift)) return false;
        return true;
    }

    public static bool TryParseHotkey(string? hotkey, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(hotkey)) return false;

        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "ALT":
                    modifiers |= 0x0001;
                    break;
                case "CTRL":
                case "CONTROL":
                    modifiers |= 0x0002;
                    break;
                case "SHIFT":
                    modifiers |= 0x0004;
                    break;
                default:
                    return false;
            }
        }

        var keyToken = parts[^1].ToUpperInvariant();
        if (keyToken.Length == 1)
        {
            var ch = keyToken[0];
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = ch;
                return true;
            }
            if (ch is '`' or '~')
            {
                vk = 0xC0; // VK_OEM_3
                return true;
            }
        }

        if (keyToken.StartsWith('F') && int.TryParse(keyToken[1..], out var fn) && fn is >= 1 and <= 12)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        if (keyToken == "TAB")
        {
            vk = 0x09;
            return true;
        }

        return false;
    }
}
