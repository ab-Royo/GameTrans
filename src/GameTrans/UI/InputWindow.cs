using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameTrans.Core;

namespace GameTrans.UI;

/// <summary>
/// F9 输入翻译窗：在选区下方展开，输入文字回车 → 调模型翻译 → 结果写入剪贴板。
/// 关闭时把前台窗口还给游戏。
/// </summary>
public sealed class InputWindow : Window
{
    private readonly AppSettings _settings;
    private readonly DeepSeekClient _client;
    private readonly string _apiKey;
    private readonly RegionPx _region;

    private readonly TextBox _input;
    private readonly TextBlock _status;
    private readonly Button _sendBtn;
    private bool _busy;
    private bool _hasResult;
    private bool _copied;
    private IntPtr _prevForeground;
    private System.Windows.Threading.DispatcherTimer? _autoClose;

    public InputWindow(AppSettings settings, DeepSeekClient client, string apiKey, RegionPx region)
    {
        _settings = settings;
        _client = client;
        _apiKey = apiKey;
        _region = region;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = Loc.T("GameTrans 输入翻译", "GameTrans Input Translation");

        _input = new TextBox
        {
            FontFamily = Theme.Font,
            FontSize = 16,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B1D23")!),
            Foreground = Theme.Text,
            CaretBrush = Theme.Text,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Center,
            Height = 38,
        };
        _input.KeyDown += OnInputKey;

        _status = new TextBlock
        {
            FontFamily = Theme.Font,
            FontSize = 12,
            Foreground = Theme.Sub,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _sendBtn = Theme.Btn(Loc.T("翻译", "Translate"), () => _ = TranslateAsync(), true);
        var closeBtn = Theme.Btn(Loc.T("关闭", "Close"), Close);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(Math.Clamp(_settings.Behavior.InputOpacity, 0.3, 1.0) * 255), 21, 22, 26)),
            BorderBrush = Theme.Accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.55,
            },
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children =
                {
                    new TextBlock
                    {
                        Text = Loc.F("输入文本　{0} → {1}", "Input text  {0} → {1}", Languages.Display(_settings.Prompt.InputSourceLanguage), Languages.Display(_settings.Prompt.InputTargetLanguage)),
                        FontFamily = Theme.Font,
                        FontSize = 12,
                        Foreground = Theme.Sub,
                        Margin = new Thickness(0, 0, 0, 6),
                    },
                    _input,
                    new Grid
                    {
                        Margin = new Thickness(0, 8, 0, 0),
                        Children =
                        {
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                HorizontalAlignment = HorizontalAlignment.Right,
                                Children = { closeBtn, _sendBtn },
                            },
                            _status,
                        },
                    },
                },
            },
        };

        Content = card;
        Loaded += OnLoaded;
        Closing += (_, _) =>
        {
            _autoClose?.Stop();
            RestoreForeground();
        };
        PreviewKeyDown += (_, _) => _autoClose?.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var scale = ScreenCapture.ScaleForRegion(_region);
        var vs = ScreenCapture.VirtualScreen();

        // 锚点 = 选区左上角；宽度不小于 380 DIP，向右侧/下方展开
        double regionDipW = _region.Width / scale;
        double widthDip = Math.Max(regionDipW, 380);
        widthDip = Math.Min(widthDip, ((vs.Right - vs.Left) / scale) - 16);

        // 手动测量内容尺寸，避免 SizeToContent 在某些主题/DPI 下把窗口撑大
        var card = (FrameworkElement)Content;
        card.Measure(new Size(widthDip, double.PositiveInfinity));
        double h = Math.Ceiling(card.DesiredSize.Height);
        if (h < 80) h = 120;

        Width = widthDip;
        Height = h;

        double left = vs.Left / scale;
        double top = vs.Top / scale;
        double right = vs.Right / scale;
        double bottom = vs.Bottom / scale;

        double x = Math.Max(left + 4, _region.Left / scale);
        double y = Math.Max(top + 4, _region.Top / scale);

        if (x + widthDip > right - 8) x = Math.Max(left + 4, right - widthDip - 8);
        if (y + h > bottom - 8) y = Math.Max(top + 4, bottom - h - 8);

        Left = x;
        Top = y;

        _input.Focus();
        Keyboard.Focus(_input);
        _status.Text = Loc.F("Enter 翻译　·　Esc 关闭　·　{0}", "Enter to translate  ·  Esc to close  ·  {0}", _settings.Api.Model);
        Log.Info($"input pos: scale={scale:F3} region={_region.Width}x{_region.Height} " +
                 $"size={Width:F0}x{Height:F0} at ({Left:F0},{Top:F0})");
    }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && !_busy)
        {
            if (_hasResult)
            {
                CopyAndClose();
            }
            else
            {
                _ = TranslateAsync();
            }

            e.Handled = true;
        }
    }

    /// <summary>复制译文并关闭；复制失败时给出提示，但不阻塞关闭（避免「按 Enter 没反应」）。</summary>
    private void CopyAndClose()
    {
        bool copied = CopyToClipboard(_input.Text);
        if (!copied && !_copied)
            ToastWindow.ShowToast(Loc.T("剪贴板被其他程序占用，未能复制译文", "Clipboard is busy; the translation was not copied"), true);
        Close();
    }

    private async Task TranslateAsync()
    {
        if (_busy) return;

        var text = _input.Text.Trim();
        if (text.Length == 0)
        {
            _status.Text = Loc.T("请输入待翻译文本", "Enter text to translate");
            _input.Focus();
            return;
        }

        if (_hasResult && text == _input.Text)
        {
            // 已是结果，回车等于复制并关闭
            CopyAndClose();
            return;
        }

        _busy = true;
        _sendBtn.IsEnabled = false;
        _input.IsReadOnly = true;
        _status.Foreground = Theme.Sub;
        _status.Text = Loc.T("翻译中…", "Translating…");

        try
        {
            var r = await _client.TranslateTextAsync(text, _settings, _apiKey, CancellationToken.None);
            if (!r.Ok)
            {
                _status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
                _status.Text = r.Error ?? Loc.T("翻译失败", "Translation failed");
                return;
            }

            _hasResult = true;
            _input.Text = r.Text;
            _input.SelectAll();
            bool copied = _settings.Behavior.CopyInputResult && CopyToClipboard(r.Text);
            _copied = copied;

            _status.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xE0, 0x9E));
            if (copied)
            {
                _status.Text = Loc.F("已复制到剪贴板，3 秒后自动关闭；Enter 立即关闭 · {0} ms", "Copied to clipboard; closes in 3 s, Enter closes now · {0} ms", r.ElapsedMs);
                ArmAutoClose();
            }
            else if (_settings.Behavior.CopyInputResult)
            {
                // 剪贴板被其他程序占用：不静默失败，也不自动关闭，交给用户再按 Enter 重试或点关闭
                _status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x6B));
                _status.Text = Loc.F("剪贴板被占用，未能复制；Enter 重试复制并关闭 · {0} ms", "Clipboard busy; not copied. Enter retries and closes · {0} ms", r.ElapsedMs);
            }
            else
            {
                _status.Text = Loc.F("翻译完成；Enter 复制并关闭 · {0} ms", "Translation done; Enter copies and closes · {0} ms", r.ElapsedMs);
            }
        }
        finally
        {
            _busy = false;
            _sendBtn.IsEnabled = true;
            _input.IsReadOnly = false;
            _input.Focus();
        }
    }

    /// <summary>复制成功后自动关闭，方便直接回游戏粘贴；任何按键都会取消。</summary>
    private void ArmAutoClose()
    {
        _autoClose?.Stop();
        _autoClose = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _autoClose.Tick += (_, _) =>
        {
            _autoClose?.Stop();
            Close();
        };
        _autoClose.Start();
    }

    /// <summary>复制结果（带时间预算，剪贴板被占用时快速失败，不阻塞 UI）。</summary>
    public static bool CopyToClipboard(string text) => ClipboardService.TrySetText(text);

    public void RememberForeground()
    {
        _prevForeground = Win32.GetForegroundWindow();
    }

    private void RestoreForeground()
    {
        try
        {
            if (_prevForeground != IntPtr.Zero && Win32.IsWindowVisibleSafe(_prevForeground))
                Win32.SetForegroundWindow(_prevForeground);
        }
        catch
        {
        }
    }
}
