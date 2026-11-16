using System.Runtime.InteropServices;
using System.Text;

namespace GameTrans.Core;

/// <summary>
/// 剪贴板文本写入。
/// 不用 WPF 的 <c>Clipboard.SetText</c>：剪贴板被其他进程占用（游戏加速器、录屏/overlay 工具常见）时，
/// WPF 会在内部重试约 10 次 × 100 ms，期间阻塞 UI 线程，导致 F9 结果框按 Enter 关闭要卡好几秒甚至看起来没反应。
/// 这里改为自带时间预算的 Win32 重试：最多阻塞 budgetMs，失败即返回。
/// </summary>
public static class ClipboardService
{
    /// <summary>把文本写入剪贴板；预算内成功返回 true，剪贴板被占用则在预算耗尽后返回 false（不会长时间阻塞）。</summary>
    public static bool TrySetText(string text, int budgetMs = 250)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        var hMem = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (UIntPtr)(uint)bytes.Length);
        if (hMem == IntPtr.Zero)
        {
            Log.Warn("复制失败：无法为剪贴板分配内存");
            return false;
        }

        var destination = Win32.GlobalLock(hMem);
        if (destination == IntPtr.Zero)
        {
            Win32.GlobalFree(hMem);
            Log.Warn("复制失败：无法锁定剪贴板内存");
            return false;
        }

        try
        {
            Marshal.Copy(bytes, 0, destination, bytes.Length);
        }
        finally
        {
            Win32.GlobalUnlock(hMem);
        }

        var deadline = Environment.TickCount64 + Math.Max(50, budgetMs);
        try
        {
            while (true)
            {
                if (Win32.OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        Win32.EmptyClipboard();
                        if (Win32.SetClipboardData(Win32.CF_UNICODETEXT, hMem) != IntPtr.Zero)
                        {
                            hMem = IntPtr.Zero; // 所有权已转交系统，不能再释放
                            return true;
                        }
                    }
                    finally
                    {
                        Win32.CloseClipboard();
                    }
                }

                if (Environment.TickCount64 >= deadline) break;
                Thread.Sleep(10);
            }
        }
        finally
        {
            if (hMem != IntPtr.Zero) Win32.GlobalFree(hMem);
        }

        Log.Warn("复制失败：剪贴板被其他程序占用");
        return false;
    }
}
