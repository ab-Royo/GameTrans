using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GameTrans.Core;

namespace GameTrans.UI;

/// <summary>
/// 文本叠加层：置顶、点击穿透、不抢焦点、并且从屏幕截图中排除（避免把自己的译文再识别一遍）。
///
/// 尺寸策略（v1.1）：
///   以选区为锚点，宽度=选区宽度；高度取 max(选区高度, 实测文本高度)。
///   译文比选区高时**优先向下扩展**，字号保持不变；只有扩到屏幕边缘仍放不下，
///   才按 minFontSize 兜底缩小（旧版无条件等比缩小，导致小选区里字看不清）。
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly OverlayContent _content;
    private RegionPx _region;
    private AppSettings? _settings;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "GameTrans Overlay";

        _content = new OverlayContent();
        Content = _content;

        SourceInitialized += (_, _) => ApplyNativeStyles();
        DpiChanged += (_, _) => Relayout();
    }

    private void ApplyNativeStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        ex |= Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, (IntPtr)ex);

        // 关键：叠加层不进入任何截图（Win10 2004+）；不支持时静默降级
        if (!Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_EXCLUDEFROMCAPTURE))
            Win32.SetWindowDisplayAffinity(hwnd, Win32.WDA_NONE);
    }

    public bool IsShowing => _region.IsUsable && IsVisible;

    public void ShowText(RegionPx region, AppSettings settings, string text, string? status = null)
    {
        _region = region;
        _settings = settings;
        _content.SetText(text, status);
        Relayout();
        if (!IsVisible) Show();
    }

    public void UpdateText(string text, string? status = null)
    {
        _content.SetText(text, status);
        Relayout(); // 结果长度变化 → 重新量高并调整窗口
    }

    public void UpdateStatus(string? status) => _content.SetStatus(status);

    public void Dismiss()
    {
        if (IsVisible) Hide();
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

    /// <summary>按文本实测高度重新计算窗口大小/位置。</summary>
    private void Relayout()
    {
        if (!_region.IsUsable || _settings == null) return;

        var s = _settings;
        var o = s.Overlay;

        double scale = ScreenCapture.ScaleForRegion(_region);
        if (scale <= 0.1) scale = 1.0;

        double regionDipW = _region.Width / scale;
        double regionDipH = _region.Height / scale;
        double regionDipX = _region.Left / scale;
        double regionDipY = _region.Top / scale;

        var work = ScreenCapture.WorkAreaForRegion(_region);
        double workTop = work.Top / scale;
        double workBottom = work.Bottom / scale;
        double workLeft = work.Left / scale;
        double workRight = work.Right / scale;

        // 字号自适应：同时看宽度与高度，小选区会缩小、大选区会放大（0.75~2.5 倍）
        double fontScale = o.AutoScaleFont
            ? Clamp(Math.Min(regionDipW / 420.0, regionDipH / 260.0), 0.75, 2.5)
            : 1.0;
        _content.ApplyStyle(o, regionDipW, fontScale);

        double statusH = _content.StatusHeight;
        double chrome = o.Padding * 2 + o.BorderThickness * 2;

        double needH = _content.MeasureTextHeight() + statusH + chrome;
        double maxDown = workBottom - regionDipY;      // 向下可用的高度
        double maxFull = workBottom - workTop;         // 整个显示器高度

        double winH = Math.Max(regionDipH, needH);
        double limit = needH > maxDown ? Math.Min(needH, maxFull) : needH;
        winH = Math.Max(regionDipH, limit);
        winH = Math.Min(winH, maxFull);

        // 仍然放不下（极小区域 + 极长译文）：按 minFontSize 兜底缩小，绝不无限缩
        if (needH > winH + 0.5)
        {
            double minFont = Math.Max(6, Math.Min(o.MinFontSize, o.FontSize));
            for (double f = _content.FontSize; f >= minFont; f -= 1.0)
            {
                _content.SetFontSize(f);
                double h = _content.MeasureTextHeight() + statusH + chrome;
                if (h <= winH) break;
            }

            if (o.OverflowMode == "expand")
            {
                // 扩展模式：如果缩到最小仍放不下，就再借一点高度（最多到屏幕底）
                double h = _content.MeasureTextHeight() + statusH + chrome;
                if (h > winH) winH = Math.Min(maxFull, h);
            }
        }

        Width = Math.Max(32, regionDipW);
        Height = Math.Max(24, winH);

        double left = regionDipX;
        double top = regionDipY;

        if (top + Height > workBottom) top = Math.Max(workTop, workBottom - Height);
        if (left + Width > workRight) left = Math.Max(workLeft, workRight - Width);

        Left = left;
        Top = top;
    }
}
