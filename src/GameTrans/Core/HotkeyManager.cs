using System.Windows.Interop;

namespace GameTrans.Core;

/// <summary>全局热键：用 RegisterHotKey（不是低级键盘钩子），不触碰反作弊关注的输入链路。</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int IdTranslate = 0x4A01;
    private const int IdInput = 0x4A02;

    private readonly HwndSource _source;
    private bool _translateRegistered;
    private bool _inputRegistered;

    public event Action? TranslatePressed;
    public event Action? InputPressed;

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

    /// <summary>返回 false 表示热键被其他程序占用。</summary>
    public bool Register(uint translateMods, uint translateVk, uint inputMods, uint inputVk,
        out string message)
    {
        Unregister();
        var sb = new List<string>();

        _translateRegistered = Win32.RegisterHotKey(_source.Handle, IdTranslate,
            translateMods | Win32.MOD_NOREPEAT, translateVk);
        if (!_translateRegistered) sb.Add($"翻译热键 {KeyName.Format(translateMods, translateVk)} 注册失败（可能被占用）");

        _inputRegistered = Win32.RegisterHotKey(_source.Handle, IdInput,
            inputMods | Win32.MOD_NOREPEAT, inputVk);
        if (!_inputRegistered) sb.Add($"输入热键 {KeyName.Format(inputMods, inputVk)} 注册失败（可能被占用）");

        message = string.Join("；", sb);
        return sb.Count == 0;
    }

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
