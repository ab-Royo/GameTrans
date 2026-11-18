using System.Windows.Interop;
using System.Windows.Threading;

namespace GameTrans.Core;

/// <summary>
/// 全局热键。
/// 键盘键优先走 <c>RegisterHotKey</c>，同时用 <c>GetAsyncKeyState</c> 低频轮询兜底：
///   · 部分游戏使用 DirectInput / 独占全屏，按键不经过消息泵，注册成功也可能收不到 <c>WM_HOTKEY</c>；
///   · 热键可能已被其他程序独占（<c>RegisterHotKey</c> 直接失败）。
/// 两条通道对同一次物理按键只触发一次（按毫秒时间窗去重），因此按下一次不会重复弹窗。
/// 鼠标中键 / 侧键1 / 侧键2 无法通过 <c>RegisterHotKey</c> 注册（Windows 不支持鼠标按钮热键），只走轮询。
/// 全程不安装任何全局钩子（不使用 <c>SetWindowsHookEx</c>），不触碰反作弊关注的输入注入链路。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int IdTranslate = 0x4A01;
    private const int IdInput = 0x4A02;
    private const int PollIntervalMs = 15;

    /// <summary>轮询发现按下时，若原生 WM_HOTKEY 刚触发过同一次按键则跳过（毫秒）。</summary>
    private const long NativeHandledWindowMs = 80;

    /// <summary>WM_HOTKEY 到达时，若轮询已触发过同一次按键则跳过（毫秒）。</summary>
    private const long PollHandledWindowMs = 400;

    private readonly HwndSource _source;
    private readonly List<Binding> _bindings = new();
    private DispatcherTimer? _timer;
    private long _suspendUntilTick;

    public event Action? TranslatePressed;
    public event Action? InputPressed;

    /// <summary>翻译热键是否由 RegisterHotKey 原生注册成功（否则为轮询兼容模式）。</summary>
    public bool TranslateRegisteredNatively { get; private set; }

    /// <summary>输入热键是否由 RegisterHotKey 原生注册成功（否则为轮询兼容模式）。</summary>
    public bool InputRegisteredNatively { get; private set; }

    private sealed class Binding
    {
        public int Id;
        public uint Vk;
        public uint Mods;
        public Action Fire = null!;
        public string Label = "";
        public bool IsMouse;
        public bool RegisteredNatively;
        public bool WasDown;
        public long LastNativeFireTick;
        public long LastPollFireTick;
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

    /// <summary>
    /// 注册（或重新注册）两个热键。返回值区分「原生注册全部成功」「仅轮询兼容」「配置错误」三种情况：
    /// 配置错误（两个热键重复等）需要用户处理；轮询兼容不是致命错误，功能仍可用。
    /// </summary>
    public HotkeyRegistration Register(uint translateMods, uint translateVk, uint inputMods, uint inputVk)
    {
        Unregister();
        var errors = new List<string>();
        var notes = new List<string>();

        var translate = AddBinding(IdTranslate, translateMods, translateVk, Loc.T("翻译", "Translate"),
            () => TranslatePressed?.Invoke(), errors, notes);
        var input = AddBinding(IdInput, inputMods, inputVk, Loc.T("输入", "Input"),
            () => InputPressed?.Invoke(), errors, notes);

        TranslateRegisteredNatively = translate is { IsMouse: false, RegisteredNatively: true };
        InputRegisteredNatively = input is { IsMouse: false, RegisteredNatively: true };

        SyncPollTimer();

        var parts = new List<string>();
        parts.AddRange(errors);
        parts.AddRange(notes);
        return new HotkeyRegistration
        {
            HasError = errors.Count > 0,
            AllNativeRegistered = errors.Count == 0 && notes.Count == 0,
            Message = string.Join(Loc.T("；", "; "), parts),
        };
    }

    private Binding? AddBinding(int id, uint mods, uint vk, string label, Action fire,
        List<string> errors, List<string> notes)
    {
        if (vk == 0)
        {
            errors.Add(Loc.F("{0}热键未设置", "{0} hotkey is not set", label));
            return null;
        }

        // 重复绑定检测：同一按键 + 同一修饰键只允许绑定一个动作
        var duplicate = _bindings.FirstOrDefault(b => b.Vk == vk && b.Mods == mods);
        if (duplicate != null)
        {
            errors.Add(Loc.F("{0}热键 {1} 与{2}热键重复", "{0} hotkey {1} duplicates the {2} hotkey",
                label, KeyName.Format(mods, vk), duplicate.Label));
            return null;
        }

        var binding = new Binding
        {
            Id = id,
            Vk = vk,
            Mods = mods,
            Fire = fire,
            Label = label,
            IsMouse = Win32.IsMouseVk(vk),
            // 注册瞬间已经按住时不要立刻触发一次
            WasDown = Win32.IsKeyDown((int)vk),
        };
        _bindings.Add(binding);

        // 鼠标键注册不会产生 WM_HOTKEY，只依赖轮询
        if (binding.IsMouse) return binding;

        bool ok = Win32.RegisterHotKey(_source.Handle, id, mods | Win32.MOD_NOREPEAT, vk);
        binding.RegisteredNatively = ok;
        if (!ok)
        {
            notes.Add(Loc.F("{0}热键 {1} 注册失败（可能被其他程序占用），已自动启用轮询兼容模式",
                "{0} hotkey {1} could not be registered (possibly in use); polling fallback is active",
                label, KeyName.Format(mods, vk)));
        }

        return binding;
    }

    /// <summary>
    /// 临时挂起热键触发（例如程序正在向游戏模拟键入译文时，避免自身模拟按键再次触发翻译）。
    /// 挂起期间按键边沿仍会记录，不会被误判为「一直按住」。
    /// </summary>
    public void SuspendFor(int milliseconds)
    {
        long until = Environment.TickCount64 + Math.Clamp(milliseconds, 0, 60_000);
        if (until > _suspendUntilTick) _suspendUntilTick = until;
    }

    private bool IsSuspended => Environment.TickCount64 < _suspendUntilTick;

    // ---------------- 轮询（鼠标键 + 键盘兜底） ----------------

    private void SyncPollTimer()
    {
        if (_bindings.Count == 0)
        {
            _timer?.Stop();
            _timer = null;
            return;
        }

        if (_timer != null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs),
        };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        Log.Info($"热键轮询已启动：{_bindings.Count} 项，{PollIntervalMs} ms");
    }

    private void Poll()
    {
        if (_bindings.Count == 0) return;

        bool suspended = IsSuspended;
        uint mods = CurrentMods();
        long now = Environment.TickCount64;

        foreach (var b in _bindings)
        {
            // 修饰键必须与绑定时完全一致，且只响应「按下」边沿
            bool down = Win32.IsKeyDown((int)b.Vk) && mods == b.Mods;
            if (down && !b.WasDown)
            {
                b.WasDown = true;
                if (suspended) continue;
                // 同一次按下已由 WM_HOTKEY 处理过 → 去重
                if (!b.IsMouse && now - b.LastNativeFireTick < NativeHandledWindowMs) continue;
                Fire(b, fromPoll: true, now);
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

    private void Fire(Binding binding, bool fromPoll, long now)
    {
        if (fromPoll)
            binding.LastPollFireTick = now;
        else
            binding.LastNativeFireTick = now;

        try
        {
            binding.Fire();
        }
        catch (Exception ex)
        {
            Log.Error($"热键 {binding.Label} 回调异常: " + ex);
        }
    }

    // ---------------- 窗口消息 ----------------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            var binding = _bindings.FirstOrDefault(b => b.Id == id);
            if (binding != null)
            {
                handled = true;
                if (IsSuspended) return IntPtr.Zero;

                long now = Environment.TickCount64;
                // 轮询已经处理过同一次按下 → 去重
                if (now - binding.LastPollFireTick < PollHandledWindowMs) return IntPtr.Zero;
                Fire(binding, fromPoll: false, now);
            }
        }

        return IntPtr.Zero;
    }

    public void Unregister()
    {
        _timer?.Stop();
        _timer = null;

        foreach (var b in _bindings)
        {
            if (b.IsMouse || !b.RegisteredNatively) continue;
            Win32.UnregisterHotKey(_source.Handle, b.Id);
        }

        _bindings.Clear();
        TranslateRegisteredNatively = false;
        InputRegisteredNatively = false;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}

/// <summary>热键注册结果：区分「配置错误」与「轮询兜底」。</summary>
public sealed class HotkeyRegistration
{
    /// <summary>两个热键都由 RegisterHotKey 原生注册成功。</summary>
    public bool AllNativeRegistered { get; init; }

    /// <summary>存在需要用户处理的配置错误（例如两个热键重复、热键未设置）。</summary>
    public bool HasError { get; init; }

    /// <summary>面向用户的说明（错误或兜底提示）。</summary>
    public string Message { get; init; } = "";
}
