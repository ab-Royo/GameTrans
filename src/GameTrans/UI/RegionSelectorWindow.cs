using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GameTrans.Core;
using Path = System.Windows.Shapes.Path;

namespace GameTrans.UI;

/// <summary>
/// 全屏选区：沿用原版思路——先把整个虚拟桌面截一张图当背景，再叠半透明遮罩，
/// 这样不依赖分层窗口也能得到「游戏画面变暗 + 框选」的效果，且选区期间画面是冻结的。
/// </summary>
public sealed class RegionSelectorWindow : Window
{
    private readonly Grid _root = new();
    private readonly Image _shot = new() { Stretch = Stretch.Fill };
    private readonly Path _dim = new() { IsHitTestVisible = false };
    private readonly Canvas _handles = new() { IsHitTestVisible = false };
    private readonly Border _selBorder = new()
    {
        BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B5EFF")!),
        BorderThickness = new Thickness(1.5),
        Background = Brushes.Transparent,
        IsHitTestVisible = false,
    };

    private readonly TextBlock _sizeLabel = new()
    {
        Foreground = Brushes.White,
        FontFamily = Theme.Font,
        FontSize = 12,
        Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 26)),
        Padding = new Thickness(6, 2, 6, 2),
        IsHitTestVisible = false,
    };

    private readonly TextBlock _hint = new()
    {
        Text = "拖动以框选识别区域　·　Enter 确认　·　Esc 取消",
        Foreground = Brushes.White,
        FontFamily = Theme.Font,
        FontSize = 14,
        Padding = new Thickness(14, 8, 14, 8),
        Background = new SolidColorBrush(Color.FromArgb(190, 20, 20, 26)),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 24, 0, 0),
        IsHitTestVisible = false,
    };

    private readonly Border _toolbar;
    private readonly List<Rectangle> _handleRects = new();

    private RegionPx _virtual;
    private double _pxPerDip = 1.0;
    private double _dipWidth;
    private double _dipHeight;
    private Point _start;
    private bool _dragging;
    private Rect _sel = Rect.Empty;

    public RegionPx? Result { get; private set; }

    public RegionSelectorWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Title = "GameTrans 选区";
        UseLayoutRounding = true;

        var cancel = MakeToolButton("取消", false, () =>
        {
            Result = null;
            Close();
        });
        var full = MakeToolButton("全屏", false, () =>
        {
            _sel = new Rect(0, 0, _dipWidth, _dipHeight);
            UpdateVisuals();
        });
        var reselect = MakeToolButton("重选", false, () =>
        {
            _sel = Rect.Empty;
            UpdateVisuals();
        });
        var ok = MakeToolButton("确定", true, Confirm);

        _toolbar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 21, 22, 26)),
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { reselect, full, cancel, ok },
            },
        };

        _root.Children.Add(_shot);
        _root.Children.Add(_dim);
        _root.Children.Add(_selBorder);
        _root.Children.Add(_handles);
        _root.Children.Add(_sizeLabel);
        _root.Children.Add(_toolbar);
        _root.Children.Add(_hint);
        Content = _root;

        for (int i = 0; i < 8; i++)
        {
            var r = new Rectangle
            {
                Width = 8,
                Height = 8,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B5EFF")!),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
            };
            _handleRects.Add(r);
            _handles.Children.Add(r);
        }

        _root.MouseLeftButtonDown += OnDown;
        _root.MouseMove += OnMove;
        _root.MouseLeftButtonUp += OnUp;
        _root.MouseRightButtonUp += (_, _) =>
        {
            Result = null;
            Close();
        };
        KeyDown += OnKey;
    }

    private Button MakeToolButton(string text, bool primary, Action click)
    {
        var b = new Button
        {
            Content = text,
            FontFamily = Theme.Font,
            FontSize = 13,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(6, 0, 0, 0),
            Background = primary
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF7C6BFF")!)
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2C2F37")!),
            Foreground = Brushes.White,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>打开选区，返回物理像素坐标（相对虚拟桌面）。</summary>
    public static RegionPx? Pick(RegionPx? initial = null)
    {
        var w = new RegionSelectorWindow();
        w.Prepare(initial);
        w.ShowDialog();
        return w.Result;
    }

    /// <summary>仅测试用：准备好但不进入模态循环。</summary>
    public void PrepareForDemo() => Prepare(null);

    private void Prepare(RegionPx? initial)
    {
        _virtual = ScreenCapture.VirtualScreen();
        _pxPerDip = ScreenCapture.ScaleAt(_virtual.Left + _virtual.Width / 2, _virtual.Top + _virtual.Height / 2);
        if (_pxPerDip <= 0) _pxPerDip = 1.0;

        _dipWidth = _virtual.Width / _pxPerDip;
        _dipHeight = _virtual.Height / _pxPerDip;

        Left = _virtual.Left / _pxPerDip;
        Top = _virtual.Top / _pxPerDip;
        Width = _dipWidth;
        Height = _dipHeight;

        try
        {
            var bmp = ScreenCapture.Capture(_virtual);
            _shot.Source = bmp;
        }
        catch (Exception ex)
        {
            Log.Error("选区背景截图失败: " + ex.Message);
        }

        if (initial is { IsUsable: true })
        {
            var x0 = (initial.Value.Left - _virtual.Left) / _pxPerDip;
            var y0 = (initial.Value.Top - _virtual.Top) / _pxPerDip;
            var w0 = initial.Value.Width / _pxPerDip;
            var h0 = initial.Value.Height / _pxPerDip;
            _sel = new Rect(x0, y0, w0, h0);
        }

        Loaded += (_, _) =>
        {
            // 用物理像素强制铺满整个虚拟桌面，再按实测 DIP 尺寸推导换算系数，
            // 这样即便 WPF 的逐显示器 DPI 与预期不同，鼠标坐标映射依然正确。
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Win32.SetWindowPos(hwnd, IntPtr.Zero, _virtual.Left, _virtual.Top,
                _virtual.Width, _virtual.Height,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ActualWidth > 0 && ActualHeight > 0)
                {
                    _dipWidth = ActualWidth;
                    _dipHeight = ActualHeight;
                    _pxPerDip = _virtual.Width / _dipWidth;
                }

                UpdateVisuals();
                Focus();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        // 点工具栏不开始新框选
        if (_toolbar.IsVisible && _toolbar.IsMouseOver) return;

        _dragging = true;
        _start = e.GetPosition(_root);
        _sel = new Rect(_start, new Size(0, 0));
        _toolbar.Visibility = Visibility.Collapsed;
        _root.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(_root);
        _sel = new Rect(
            Math.Min(_start.X, p.X),
            Math.Min(_start.Y, p.Y),
            Math.Abs(p.X - _start.X),
            Math.Abs(p.Y - _start.Y));
        UpdateVisuals();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _root.ReleaseMouseCapture();

        if (_sel.Width * _pxPerDip < 8 || _sel.Height * _pxPerDip < 8)
        {
            _sel = Rect.Empty;
            UpdateVisuals();
            return;
        }

        UpdateVisuals();
        ShowToolbar();
    }

    private void ShowToolbar()
    {
        _toolbar.Visibility = Visibility.Visible;
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _toolbar.DesiredSize;

        double x = _sel.Right - size.Width;
        double y = _sel.Bottom + 10;
        if (y + size.Height > _dipHeight - 8) y = _sel.Top - size.Height - 10;
        x = Math.Max(8, Math.Min(x, _dipWidth - size.Width - 8));
        y = Math.Max(8, Math.Min(y, _dipHeight - size.Height - 8));

        _toolbar.HorizontalAlignment = HorizontalAlignment.Left;
        _toolbar.VerticalAlignment = VerticalAlignment.Top;
        _toolbar.Margin = new Thickness(x, y, 0, 0);
    }

    private void UpdateVisuals()
    {
        var full = new Rect(0, 0, _dipWidth, _dipHeight);
        var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geo.Children.Add(new RectangleGeometry(full));
        bool has = _sel.Width > 0 && _sel.Height > 0;
        if (has) geo.Children.Add(new RectangleGeometry(_sel));
        _dim.Data = geo;
        _dim.Fill = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0));

        if (has)
        {
            _selBorder.Visibility = Visibility.Visible;
            _selBorder.Margin = new Thickness(_sel.X, _sel.Y, 0, 0);
            _selBorder.HorizontalAlignment = HorizontalAlignment.Left;
            _selBorder.VerticalAlignment = VerticalAlignment.Top;
            _selBorder.Width = Math.Max(0, _sel.Width);
            _selBorder.Height = Math.Max(0, _sel.Height);

            var pxW = (int)Math.Round(_sel.Width * _pxPerDip);
            var pxH = (int)Math.Round(_sel.Height * _pxPerDip);
            _sizeLabel.Text = $"{pxW} × {pxH}";
            _sizeLabel.Visibility = Visibility.Visible;
            _sizeLabel.HorizontalAlignment = HorizontalAlignment.Left;
            _sizeLabel.VerticalAlignment = VerticalAlignment.Top;
            _sizeLabel.Margin = new Thickness(_sel.Left, Math.Max(0, _sel.Top - 26), 0, 0);

            UpdateHandles(_sel);
        }
        else
        {
            _selBorder.Visibility = Visibility.Collapsed;
            _sizeLabel.Visibility = Visibility.Collapsed;
            _toolbar.Visibility = Visibility.Collapsed;
            foreach (var r in _handleRects) r.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateHandles(Rect r)
    {
        var pts = new (double X, double Y)[]
        {
            (r.Left, r.Top), (r.Left + r.Width / 2, r.Top), (r.Right, r.Top),
            (r.Left, r.Top + r.Height / 2), (r.Right, r.Top + r.Height / 2),
            (r.Left, r.Bottom), (r.Left + r.Width / 2, r.Bottom), (r.Right, r.Bottom),
        };

        for (int i = 0; i < _handleRects.Count; i++)
        {
            var h = _handleRects[i];
            h.Visibility = Visibility.Visible;
            Canvas.SetLeft(h, pts[i].X - 4);
            Canvas.SetTop(h, pts[i].Y - 4);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Result = null;
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
        else if (e.Key == Key.F)
        {
            _sel = new Rect(0, 0, _dipWidth, _dipHeight);
            UpdateVisuals();
        }
    }

    private void Confirm()
    {
        if (_sel.Width * _pxPerDip < 8 || _sel.Height * _pxPerDip < 8)
        {
            _hint.Text = "选区尺寸过小（小于 8 × 8 像素），请重新框选";
            return;
        }

        int left = (int)Math.Round(_sel.Left * _pxPerDip) + _virtual.Left;
        int top = (int)Math.Round(_sel.Top * _pxPerDip) + _virtual.Top;
        int right = (int)Math.Round(_sel.Right * _pxPerDip) + _virtual.Left;
        int bottom = (int)Math.Round(_sel.Bottom * _pxPerDip) + _virtual.Top;

        Result = new RegionPx(left, top, right, bottom);
        Close();
    }
}
