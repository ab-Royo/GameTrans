using System.Windows.Interop;
using System.Windows.Threading;

namespace GameTrans.Core;

/// <summary>
/// 全局热键。
/// 键盘键优先走 <c>RegisterHotKey</c>，同时用 <c>GetAsyncKeyState</c> 低频轮询兜底：
///   · 部分游戏使用 DirectInput / 独占全屏，按键不经过消息泵，注册成功也可能收不到 <c>WM_HOTKEY</c>；
///   · 热键可能已被其他程序独占（<c>RegisterHotKey</c> 直接失败）。
/// 两条通道对同一次物理按键只触发一次（按毫秒时间窗去重），因此按下一次不会重复触发。
/// 鼠标中键 / 侧键1 / 侧键2 无法通过 <c>RegisterHotKey</c> 注册（Windows 不支持鼠标按钮热键），只走轮询。
///
/// 轮询运行在专用后台线程上，使用 <see cref="Thread"/> + <see cref="ManualResetEventSlim"/> 定时唤醒，
/// 不占用 WPF 的 Dispatcher 消息循环：空闲时只有一次只读的状态查询，命中按键后才通过
/// Dispatcher 把回调派发到 UI 线程。全程不安装任何全局钩子（不使用 <c>SetWindowsHookEx</c>），
/// 不触碰反作弊关注的输入注入链路。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int IdTranslate = 0x4A01;
    private const int IdInput = 0x4A02;
    private const int IdRegion = 0x4A03;
    private const int PollIntervalMs = 15;
    private const string PollThreadName = "GameTrans.HotkeyPoll";

    /// <summary>轮询发现按下时，若原生 WM_HOTKEY 刚触发过同一次按键则跳过（毫秒）。</summary>
    private const long NativeHandledWindowMs = 80;

    /// <summary>WM_HOTKEY 到达时，若轮询已触发过同一次按键则跳过（毫秒）。</summary>
    private const long PollHandledWindowMs = 400;

    private readonly HwndSource _source;
    private readonly Dispatcher _dispatcher;

    /// <summary>当前绑定的不可变快照：UI 线程整体替换，轮询线程只读，避免跨线程遍历可变集合。</summary>
    private Binding[] _bindings = Array.Empty<Binding>();

    private Thread? _pollThread;
    private ManualResetEventSlim? _pollStop;
    private long _suspendUntilTick;

    public event Action? TranslatePressed;
    public event Action? InputPressed;
    public event Action? RegionPressed;

    /// <summary>翻译热键是否由 RegisterHotKey 原生注册成功（否则为轮询兼容模式）。</summary>
    public bool TranslateRegisteredNatively { get; private set; }

    /// <summary>输入热键是否由 RegisterHotKey 原生注册成功（否则为轮询兼容模式）。</summary>
    public bool InputRegisteredNatively { get; private set; }

    /// <summary>选区热键是否由 RegisterHotKey 原生注册成功；未绑定或为鼠标键时为 false。</summary>
    public bool RegionRegisteredNatively { get; private set; }

    private sealed class Binding
    {
        public int Id;
        public uint Vk;
        public uint Mods;
        public Action Fire = null!;
        public string Label = "";
        public bool IsMouse;
        public bool RegisteredNatively;

        /// <summary>上一轮轮询时的按下状态（0/1），由轮询线程独占写入。</summary>
        public int WasDown;

        /// <summary>最后一次原生 WM_HOTKEY 触发时间；UI 线程写入、轮询线程读取。</summary>
        public long LastNativeFireTick;

        /// <summary>最后一次轮询触发时间；轮询线程写入、UI 线程读取。</summary>
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
        _dispatcher = _source.Dispatcher;
    }

    /// <summary>
    /// 注册（或重新注册）热键。<paramref name="regionVk"/> 为 0 表示不绑定「重新选区」热键（默认）；
    /// 返回值区分「原生注册全部成功」「仅轮询兼容」「配置错误」三种情况：
    /// 配置错误（热键重复等）需要用户处理；轮询兼容不是致命错误，功能仍可用。
    /// </summary>
    public HotkeyRegistration Register(uint translateMods, uint translateVk, uint inputMods, uint inputVk,
        uint regionMods = 0, uint regionVk = 0)
    {
        Unregister();
        var errors = new List<string>();
        var notes = new List<string>();
        var bindings = new List<Binding>(3);

        var translate = AddBinding(bindings, IdTranslate, translateMods, translateVk,
            Loc.T("翻译", "Translate"), () => TranslatePressed?.Invoke(), optional: false, errors, notes);
        var input = AddBinding(bindings, IdInput, inputMods, inputVk,
            Loc.T("输入", "Input"), () => InputPressed?.Invoke(), optional: false, errors, notes);
        var region = AddBinding(bindings, IdRegion, regionMods, regionVk,
            Loc.T("重新选区", "Select region"), () => RegionPressed?.Invoke(), optional: true, errors, notes);

        TranslateRegisteredNatively = translate is { IsMouse: false, RegisteredNatively: true };
        InputRegisteredNatively = input is { IsMouse: false, RegisteredNatively: true };
        RegionRegisteredNatively = region is { IsMouse: false, RegisteredNatively: true };

        Volatile.Write(ref _bindings, bindings.ToArray());
        SyncPollThread();

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

    private Binding? AddBinding(List<Binding> bindings, int id, uint mods, uint vk, string label, Action fire,
        bool optional, List<string> errors, List<string> notes)
    {
        if (vk == 0)
        {
            // 可选热键（重新选区）默认不绑定，未设置不是错误
            if (!optional) errors.Add(Loc.F("{0}热键未设置", "{0} hotkey is not set", label));
            return null;
        }

        // 重复绑定检测：同一按键 + 同一修饰键只允许绑定一个动作
        foreach (var existing in bindings)
        {
            if (existing.Vk != vk || existing.Mods != mods) continue;
            errors.Add(Loc.F("{0}热键 {1} 与{2}热键重复", "{0} hotkey {1} duplicates the {2} hotkey",
                label, KeyName.Format(mods, vk), existing.Label));
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
            WasDown = Win32.IsKeyDown((int)vk) ? 1 : 0,
        };
        bindings.Add(binding);

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
        while (true)
        {
            long current = Volatile.Read(ref _suspendUntilTick);
            if (until <= current) return;
            if (Interlocked.CompareExchange(ref _suspendUntilTick, until, current) == current) return;
        }
    }

    private bool IsSuspended => Environment.TickCount64 < Volatile.Read(ref _suspendUntilTick);

    // ---------------- 轮询线程（鼠标键 + 键盘兜底） ----------------

    private void SyncPollThread()
    {
        bool needed = Volatile.Read(ref _bindings).Length > 0;
        if (!needed)
        {
            StopPollThread();
            return;
        }

        if (_pollThread != null) return;

        var stop = new ManualResetEventSlim(false);
        _pollStop = stop;
        var thread = new Thread(() => PollLoop(stop))
        {
            IsBackground = true,
            Name = PollThreadName,
        };
        _pollThread = thread;
        thread.Start();
        Log.Info($"热键轮询线程已启动：{Volatile.Read(ref _bindings).Length} 项，{PollIntervalMs} ms，线程 {thread.ManagedThreadId}");
    }

    private void StopPollThread()
    {
        var thread = _pollThread;
        var stop = _pollStop;
        _pollThread = null;
        _pollStop = null;
        if (stop == null) return;

        stop.Set();
        if (thread != null && !thread.Join(1000))
            Log.Warn("热键轮询线程未在 1 秒内退出，后续注册可能短暂存在两个轮询线程");
        try
        {
            stop.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("释放轮询线程信号量失败: " + ex.Message);
        }
    }

    private void PollLoop(ManualResetEventSlim stop)
    {
        while (true)
        {
            try
            {
                PollOnce();
            }
            catch (Exception ex)
            {
                Log.Error("热键轮询异常: " + ex);
            }

            bool stopped;
            try
            {
                stopped = stop.Wait(PollIntervalMs);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (stopped) return;
        }
    }

    private void PollOnce()
    {
        var bindings = Volatile.Read(ref _bindings);
        if (bindings.Length == 0) return;

        bool suspended = IsSuspended;
        uint mods = CurrentMods();
        long now = Environment.TickCount64;

        foreach (var b in bindings)
        {
            // 修饰键必须与绑定时完全一致，且只响应「按下」边沿
            bool down = Win32.IsKeyDown((int)b.Vk) && mods == b.Mods;
            bool wasDown = Volatile.Read(ref b.WasDown) != 0;
            if (down && !wasDown)
            {
                Volatile.Write(ref b.WasDown, 1);
                if (suspended) continue;
                // 同一次按下已由 WM_HOTKEY 处理过 → 去重
                if (!b.IsMouse &&
                    now - Volatile.Read(ref b.LastNativeFireTick) < NativeHandledWindowMs) continue;

                Volatile.Write(ref b.LastPollFireTick, now);
                PostFire(b, fromPoll: true);
                continue;
            }

            Volatile.Write(ref b.WasDown, down ? 1 : 0);
        }
    }

    /// <summary>把轮询结果派发到 UI 线程执行（热键回调会操作窗口/托盘等 UI 对象）。</summary>
    private void PostFire(Binding binding, bool fromPoll)
    {
        if (_dispatcher.HasShutdownStarted) return;

        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                // 绑定可能已在派发前被重新注册
                if (Array.IndexOf(Volatile.Read(ref _bindings), binding) < 0) return;

                long now = Environment.TickCount64;
                if (fromPoll)
                {
                    // 派发期间原生通道可能已处理同一次按键 → 去重
                    if (!binding.IsMouse &&
                        now - Volatile.Read(ref binding.LastNativeFireTick) < NativeHandledWindowMs) return;
                    Volatile.Write(ref binding.LastPollFireTick, now);
                }

                Fire(binding, fromPoll, now);
            }));
        }
        catch (Exception ex)
        {
            // Dispatcher 已关闭（退出过程）时忽略
            if (!_dispatcher.HasShutdownStarted) Log.Error("热键回调派发失败: " + ex);
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
            Volatile.Write(ref binding.LastPollFireTick, now);
        else
            Volatile.Write(ref binding.LastNativeFireTick, now);

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
            foreach (var binding in Volatile.Read(ref _bindings))
            {
                if (binding.Id != id) continue;
                handled = true;
                if (IsSuspended) break;

                long now = Environment.TickCount64;
                // 轮询已经处理过同一次按下 → 去重
                if (now - Volatile.Read(ref binding.LastPollFireTick) < PollHandledWindowMs) break;
                Fire(binding, fromPoll: false, now);
                break;
            }
        }

        return IntPtr.Zero;
    }

    public void Unregister()
    {
        Volatile.Write(ref _bindings, Array.Empty<Binding>());
        StopPollThread();

        // 只注销原生注册成功的键盘热键（鼠标键未注册）
        if (TranslateRegisteredNatively) Win32.UnregisterHotKey(_source.Handle, IdTranslate);
        if (InputRegisteredNatively) Win32.UnregisterHotKey(_source.Handle, IdInput);
        if (RegionRegisteredNatively) Win32.UnregisterHotKey(_source.Handle, IdRegion);

        TranslateRegisteredNatively = false;
        InputRegisteredNatively = false;
        RegionRegisteredNatively = false;
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
    /// <summary>所有已绑定热键都由 RegisterHotKey 原生注册成功（未绑定的可选热键不计入）。</summary>
    public bool AllNativeRegistered { get; init; }

    /// <summary>存在需要用户处理的配置错误（例如两个热键重复、热键未设置）。</summary>
    public bool HasError { get; init; }

    /// <summary>面向用户的说明（错误或兜底提示）。</summary>
    public string Message { get; init; } = "";
}
