using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameTrans.Core;

public readonly record struct RegionPx(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public bool IsUsable => Width >= 8 && Height >= 8;
    public Int32Rect ToInt32Rect() => new(Left, Top, Width, Height);
}

public static class ScreenCapture
{
    /// <summary>虚拟桌面（所有显示器并集）的物理像素范围。</summary>
    public static RegionPx VirtualScreen()
    {
        int x = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int y = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int w = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int h = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0)
        {
            // 兜底：主屏
            w = 1920;
            h = 1080;
        }

        return new RegionPx(x, y, x + w, y + h);
    }

    /// <summary>以物理像素截取一块屏幕区域（CAPTUREBLT，含分层窗口/硬件覆盖层）。</summary>
    public static BitmapSource Capture(RegionPx r)
    {
        if (!r.IsUsable) throw new ArgumentException("截图区域过小");

        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC 失败");

        IntPtr memDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;
        try
        {
            memDc = Win32.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC 失败");

            var bmi = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                    biWidth = r.Width,
                    biHeight = -r.Height, // 负高度 = 自上而下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                },
            };

            dib = Win32.CreateDIBSection(memDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection 失败: " + Marshal.GetLastWin32Error());

            oldObj = Win32.SelectObject(memDc, dib);
            if (!Win32.BitBlt(memDc, 0, 0, r.Width, r.Height, screenDc, r.Left, r.Top,
                    Win32.SRCCOPY | Win32.CAPTUREBLT))
                throw new InvalidOperationException("BitBlt 失败: " + Marshal.GetLastWin32Error());

            int stride = r.Width * 4;
            var buffer = new byte[stride * r.Height];
            Marshal.Copy(bits, buffer, 0, buffer.Length);

            var bmp = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgra32, null,
                buffer, stride);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero) Win32.SelectObject(memDc, oldObj);
            if (dib != IntPtr.Zero) Win32.DeleteObject(dib);
            if (memDc != IntPtr.Zero) Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static byte[] ToPng(BitmapSource bmp)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>指定区域所在显示器的可用工作区（不含任务栏），像素坐标。</summary>
    public static RegionPx WorkAreaForRegion(RegionPx r)
    {
        var rc = new Win32.RECT
        {
            Left = r.Left,
            Top = r.Top,
            Right = r.Right,
            Bottom = r.Bottom,
        };
        IntPtr mon = Win32.MonitorFromRect(ref rc, Win32.MONITOR_DEFAULTTONEAREST);
        if (mon != IntPtr.Zero)
        {
            var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (Win32.GetMonitorInfo(mon, ref mi))
                return new RegionPx(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
        }

        return VirtualScreen();
    }
    /// <summary>指定像素点所在显示器的缩放系数（1.0 = 96 DPI）。</summary>
    public static double ScaleAt(int x, int y)
    {
        var pt = new Win32.POINT { X = x, Y = y };
        IntPtr mon = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
        return ScaleForMonitor(mon);
    }

    public static double ScaleForRegion(RegionPx r)
    {
        var rc = new Win32.RECT
        {
            Left = r.Left,
            Top = r.Top,
            Right = r.Right,
            Bottom = r.Bottom,
        };
        IntPtr mon = Win32.MonitorFromRect(ref rc, Win32.MONITOR_DEFAULTTONEAREST);
        return ScaleForMonitor(mon);
    }

    private static double ScaleForMonitor(IntPtr mon)
    {
        if (mon != IntPtr.Zero)
        {
            try
            {
                if (Win32.GetDpiForMonitor(mon, Win32.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 &&
                    dpiX > 0)
                    return dpiX / 96.0;
            }
            catch (DllNotFoundException)
            {
                // Win7 没有 shcore
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        return 1.0;
    }
}
