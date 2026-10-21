using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GameTrans.Core;

namespace GameTrans.UI;

/// <summary>轻量提示气泡：贴在鼠标附近，不抢焦点，自动消失。</summary>
public sealed class ToastWindow : Window
{
    private static ToastWindow? _current;
    private readonly DispatcherTimer _timer;

    private ToastWindow(string text, bool error)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "GameTrans Toast";

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(240, 24, 25, 30)),
            BorderBrush = error
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B))
                : new SolidColorBrush(Color.FromRgb(0x7C, 0x6B, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 9, 14, 9),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = Theme.Font,
                FontSize = 13,
                Foreground = Theme.Text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
            },
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(error ? 3600 : 2200) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Close();
        };
        Loaded += (_, _) =>
        {
            PositionNearCursor();
            _timer.Start();
        };
    }

    public static void ShowToast(string text, bool error = false)
    {
        try
        {
            _current?.Close();
            _current = new ToastWindow(text, error);
            _current.Show();
        }
        catch
        {
            // 提示失败不影响功能
        }
    }

    private void PositionNearCursor()
    {
        Win32.GetCursorPos(out var pt);
        var scale = Core.ScreenCapture.ScaleAt(pt.X, pt.Y);
        var vs = Core.ScreenCapture.VirtualScreen();

        double dipW = ActualWidth > 0 ? ActualWidth : 260;
        double dipH = ActualHeight > 0 ? ActualHeight : 44;
        double x = pt.X / scale + 16;
        double y = pt.Y / scale + 20;

        if (x + dipW > vs.Right / scale - 8) x = vs.Right / scale - dipW - 12;
        if (y + dipH > vs.Bottom / scale - 8) y = pt.Y / scale - dipH - 14;

        Left = Math.Max(vs.Left / scale + 8, x);
        Top = Math.Max(vs.Top / scale + 8, y);
    }
}
