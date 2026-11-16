using System.Windows.Interop;
using System.Windows.Threading;

namespace GameTrans.Core;

/// <summary>
/// 全局热键。
/// 键盘键走 <c>RegisterHotKey</c>（不使用低级键盘钩子，不触碰反作弊关注的输入链路）；
/// 鼠标中键 / 侧键1 / 侧键2 无法通过 <c>RegisterHotKey</c> 注册（Windows 不支持鼠标按钮热键），
/// 改用低频 <c>GetAsyncKeyState</c> 轮询（15 ms 一次，只在按下瞬间触发一次），同样不安装任何全局钩子。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int IdTranslate = 0x4A01;
    private const int IdInput = 0x4A02;
    private const int MousePollIntervalMs = 15;

    private readonly HwndSource _source;
    private readonly List<MouseBinding> _mouseBindings = new();
    private DispatcherTimer? _mouseTimer;
    private bool _translateRegistered;
    private bool _inputRegistered;

    public event Action? TranslatePressed;
    public event Action? InputPressed;

    private sealed class MouseBinding
    {
        public uint Vk;
        public uint Mods;
        public Action Fire = null!;

        /// <summary>上一轮轮询时该键是否处于按下状态，用于只响应“按下”边沿。</summary>
        public bool WasDown;
    }

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("GameTrans.HotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>返回 false 表示有热键注册失败（被占用 / 重复）。</summary>
    public bool Register(uint translateMods, uint translateVk, uint inputMods, uint inputVk,
        out string message)
    {
        Unregister();
        var errors = new List<string>();

        _translateRegistered = RegisterOne(IdTranslate, translateMods, translateVk, Loc.T("翻译", "Translate"),
            () => TranslatePressed?.Invoke(), errors);
        _inputRegistered = RegisterOne(IdInput, inputMods, inputVk, Loc.T("输入", "Input"),
            () => InputPressed?.Invoke(), errors);

        SyncMouseWatch();
        message = string.Join(Loc.T("；", "; "), errors);
        return errors.Count == 0;
    }

    private bool RegisterOne(int id, uint mods, uint vk, string label, Action fire, List<string> errors)
    {
        if (Win32.IsMouseVk(vk))
        {
            if (_mouseBindings.Any(b => b.Vk == vk && b.Mods == mods))
            {
                errors.Add(Loc.F("{0}热键 {1} 与另一个热键重复", "{0} hotkey {1} duplicates another binding",
                    label, KeyName.Format(mods, vk)));
                return false;
            }

            _mouseBindings.Add(new MouseBinding
            {
                Vk = vk,
                Mods = mods,
                Fire = fire,
                // 注册瞬间已经按住时不要立刻触发一次
                WasDown = Win32.IsKeyDown((int)vk),
            });
            return true;
        }

        bool ok = Win32.RegisterHotKey(_source.Handle, id, mods | Win32.MOD_NOREPEAT, vk);
        if (!ok)
            errors.Add(Loc.F("{0}热键 {1} 注册失败（可能被占用）",
                "{0} hotkey {1} could not be registered (already in use)",
                label, KeyName.Format(mods, vk)));
        return ok;
    }

    // ---------------- 鼠标键轮询 ----------------

    private void SyncMouseWatch()
    {
        if (_mouseBindings.Count == 0)
        {
            _mouseTimer?.Stop();
            _mouseTimer = null;
            return;
        }

        if (_mouseTimer != null) return;
        _mouseTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(MousePollIntervalMs),
        };
        _mouseTimer.Tick += (_, _) => PollMouse();
        _mouseTimer.Start();
        Log.Info($"鼠标键热键轮询已启动：{_mouseBindings.Count} 项，{MousePollIntervalMs} ms");
    }

    private void PollMouse()
    {
        if (_mouseBindings.Count == 0) return;

        uint mods = CurrentMods();
        foreach (var b in _mouseBindings)
        {
            // 修饰键必须与绑定时完全一致，且只响应“按下”边沿
            bool down = Win32.IsKeyDown((int)b.Vk) && mods == b.Mods;
            if (down && !b.WasDown)
            {
                b.WasDown = true;
                try
                {
                    b.Fire();
                }
                catch (Exception ex)
                {
                    Log.Error("鼠标热键回调异常: " + ex);
                }

                continue;
            }

            b.WasDown = down;
        }
    }

    private static uint CurrentMods()
    {
        uint mods = 0;
        if (Win32.IsKeyDown(Win32.VK_CONTROL)) mods |= Win32.MOD_CONTROL;
        if (Win32.IsKeyDown(Win32.VK_MENU)) mods |= Win32.MOD_ALT;
        if (Win32.IsKeyDown(Win32.VK_SHIFT)) mods |= Win32.MOD_SHIFT;
        if (Win32.IsKeyDown(Win32.VK_LWIN) || Win32.IsKeyDown(Win32.VK_RWIN)) mods |= Win32.MOD_WIN;
        return mods;
    }

    // ---------------- 窗口消息 ----------------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (id == IdTranslate)
            {
                handled = true;
                TranslatePressed?.Invoke();
            }
            else if (id == IdInput)
            {
                handled = true;
                InputPressed?.Invoke();
            }
        }

        return IntPtr.Zero;
    }

    public void Unregister()
    {
        _mouseTimer?.Stop();
        _mouseTimer = null;
        _mouseBindings.Clear();

        // 鼠标键绑定不会调用 RegisterHotKey，这里注销失败无副作用
        if (_translateRegistered)
        {
            Win32.UnregisterHotKey(_source.Handle, IdTranslate);
            _translateRegistered = false;
        }

        if (_inputRegistered)
        {
            Win32.UnregisterHotKey(_source.Handle, IdInput);
            _inputRegistered = false;
        }
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
