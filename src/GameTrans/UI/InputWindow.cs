using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GameTrans.Core;

namespace GameTrans.UI;

/// <summary>
/// F9 输入翻译窗：在选区下方展开，输入文字回车 → 调模型翻译 → 结果写入剪贴板。
/// 关闭时把前台窗口还给游戏。
///
/// 1.4.0 针对「游戏里窗口关不掉 / 点不中」的修复点：
///   · 前台焦点：单纯 <c>Show()+Activate()</c> 在游戏持前台锁时可能失败，导致 Enter / Esc 全进游戏、
///     窗口既不会关也没法按回车复制。改为 <see cref="Win32.ForceForeground"/> 并在失败时短时重试。
///   · 自动关闭：以前只有「复制成功」才启动 3 秒倒计时；游戏客户端占用剪贴板时复制失败，
///     窗口就永远不关。现在任何一次翻译成功都会启动倒计时，复制失败时给 5 秒并提示可回车重试。
///   · 键盘路由：Esc / Enter 提到窗口级 PreviewKeyDown，不再只挂在输入框上（点了按钮后焦点离开
///     输入框，原先 Esc 就失效了）。
///   · 点击偏移：窗口改用物理像素 <c>SetWindowPos</c> 定位（与选区窗口同一思路），避免逐显示器
///     DPI 下 WPF DIP 换算与显示器缩放不一致，出现「看得见、点不到」；状态文字不参与命中测试，
///     且与按钮分列，避免长文案盖住按钮吞掉点击。
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
    private readonly Action<int>? _suspendHotkeys;
    private bool _busy;
    private bool _hasResult;
    private bool _closing;
    private bool _simulating;
    private bool _placing;
    private IntPtr _prevForeground;
    private DispatcherTimer? _autoClose;
    private DispatcherTimer? _foregroundRetry;

    /// <param name="suspendHotkeys">
    /// 模拟键入前临时挂起热键（传入毫秒数）。由 TrayApp 注入；离屏演示等场景可为 null。
    /// </param>
    public InputWindow(AppSettings settings, DeepSeekClient client, string apiKey, RegionPx region,
        Action<int>? suspendHotkeys = null)
    {
        _settings = settings;
        _client = client;
        _apiKey = apiKey;
        _region = region;
        _suspendHotkeys = suspendHotkeys;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Focusable = true;
        Width = 420;  // 仅作首次显示前的占位尺寸，实际尺寸在 Loaded 里按物理像素重算
        Height = 160;
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

        _status = new TextBlock
        {
            FontFamily = Theme.Font,
            FontSize = 12,
            Foreground = Theme.Sub,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            // 状态栏只是展示，不参与命中测试：否则长文案会盖住右侧按钮、吞掉关闭点击
            IsHitTestVisible = false,
            Margin = new Thickness(0, 0, 12, 0),
        };

        _sendBtn = Theme.Btn(Loc.T("翻译", "Translate"), () => _ = TranslateAsync(), true);
        var closeBtn = Theme.Btn(Loc.T("关闭", "Close"), Close);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { closeBtn, _sendBtn },
        };

        // 状态文字与按钮各占一列，互不重叠
        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_status, 0);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(_status);
        footer.Children.Add(buttons);

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
                        Text = Loc.F("输入文本　{0} → {1}", "Input text  {0} → {1}",
                            Languages.Display(_settings.Prompt.InputSourceLanguage),
                            Languages.Display(_settings.Prompt.InputTargetLanguage)),
                        FontFamily = Theme.Font,
                        FontSize = 12,
                        Foreground = Theme.Sub,
                        Margin = new Thickness(0, 0, 0, 6),
                    },
                    _input,
                    footer,
                },
            },
        };

        Content = card;

        Loaded += OnLoaded;
        // 跨屏/改缩放时重新按物理像素摆放
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(PlaceWindow), DispatcherPriority.Loaded);
        Deactivated += (_, _) => StopForegroundRetry();
        PreviewKeyDown += OnPreviewKey;
        Closing += OnClosing;
    }

    // ---------------- 窗口生命周期 ----------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceWindow();
        SetHintStatus();
        BringToForegroundWithRetry();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        _autoClose?.Stop();
        _autoClose = null;
        StopForegroundRetry();
        RestoreForeground();
    }

    private void PlaceWindow()
    {
        if (_closing || _placing) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        _placing = true;
        try
        {
            double scale = ScreenCapture.ScaleForRegion(_region);
            if (scale <= 0.1) scale = 1.0;

            var work = ScreenCapture.WorkAreaForRegion(_region);

            // 宽度：识别区域宽度与最小 380 DIP 取大
            double widthDip = Math.Max(_region.Width / scale, 380.0);
            var card = (FrameworkElement)Content;
            card.Measure(new Size(widthDip, double.PositiveInfinity));
            double heightDip = Math.Max(120, Math.Ceiling(card.DesiredSize.Height));

            int widthPx = (int)Math.Round(widthDip * scale);
            int heightPx = (int)Math.Round(heightDip * scale);
            widthPx = Math.Min(widthPx, Math.Max(240, work.Width - 16));
            heightPx = Math.Min(heightPx, Math.Max(120, work.Height - 16));

            int x = Math.Max(work.Left + 4, _region.Left);
            int y = Math.Max(work.Top + 4, _region.Top);
            if (x + widthPx > work.Right - 8) x = Math.Max(work.Left + 4, work.Right - widthPx - 8);
            if (y + heightPx > work.Bottom - 8) y = Math.Max(work.Top + 4, work.Bottom - heightPx - 8);

            // 以物理像素定位/定尺寸：逐显示器 DPI 下，WPF 的 DIP 换算系数可能与显示器实际缩放不一致，
            // 会出现「窗口看得见、点击落到别处」的偏移；物理像素摆放与截图/选区坐标同一套坐标。
            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, y, widthPx, heightPx,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

            // 同步 WPF 的 DIP 尺寸（用目标显示器缩放换算），避免 Window 自动按内容改尺寸
            Width = widthPx / scale;
            Height = heightPx / scale;

            Log.Info($"input pos(px): scale={scale:F3} region={_region.Width}x{_region.Height} " +
                     $"size={widthPx}x{heightPx} at ({x},{y})");
        }
        catch (Exception ex)
        {
            Log.Error("输入框定位失败: " + ex);
        }
        finally
        {
            _placing = false;
        }
    }

    private void SetHintStatus()
    {
        if (_settings.Behavior.SimulateInput && !AdminHelper.IsElevated)
        {
            _status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x66));
            _status.Text = Loc.T("模拟输入需管理员权限，可能被游戏拦截　·　Enter 翻译　·　Esc 关闭",
                "Simulated input needs admin rights and may be blocked  ·  Enter to translate  ·  Esc to close");
            return;
        }

        _status.Foreground = Theme.Sub;
        _status.Text = Loc.F("Enter 翻译　·　Esc 关闭　·　{0}",
            "Enter to translate  ·  Esc to close  ·  {0}", _settings.Api.Model);
    }

    /// <summary>让输入窗真正拿到前台键盘焦点；游戏持前台锁时短时重试（用户切走后立即停止）。</summary>
    private void BringToForegroundWithRetry()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        Activate();
        if (Win32.ForceForeground(hwnd))
        {
            FocusInput();
            return;
        }

        int attempts = 0;
        StopForegroundRetry();
        _foregroundRetry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _foregroundRetry.Tick += (_, _) =>
        {
            attempts++;
            if (_closing || !IsVisible || attempts > 8)
            {
                StopForegroundRetry();
                if (attempts > 8)
                    Log.Warn("输入框未能取得前台焦点（前台锁或权限差异），Enter/Esc 可能送不到本窗口");
                return;
            }

            Activate();
            if (Win32.ForceForeground(hwnd))
            {
                StopForegroundRetry();
                FocusInput();
            }
        };
        _foregroundRetry.Start();
    }

    private void StopForegroundRetry()
    {
        _foregroundRetry?.Stop();
        _foregroundRetry = null;
    }

    private void FocusInput()
    {
        try
        {
            _input.Focus();
            Keyboard.Focus(_input);
        }
        catch
        {
            // 窗口已关闭时忽略
        }
    }

    // ---------------- 键盘 ----------------

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        // 任何按键都取消自动关闭（用户还在操作，别抢着关）
        if (e.Key != Key.Escape) _autoClose?.Stop();

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key != Key.Enter) return;

        e.Handled = true;
        if (_busy || _closing) return;
        if (_hasResult) HandleResultEnter();
        else _ = TranslateAsync();
    }

    private void HandleResultEnter()
    {
        if (_settings.Behavior.SimulateInput) _ = SimulateSendAndCloseAsync(_input.Text);
        else CopyAndClose();
    }

    // ---------------- 翻译 ----------------

    /// <summary>复制译文并关闭；复制失败时给出提示，但不阻塞关闭（避免「按 Enter 没反应」）。</summary>
    private void CopyAndClose()
    {
        if (_closing) return;
        bool copied = CopyToClipboard(_input.Text);
        if (!copied)
            ToastWindow.ShowToast(Loc.T("剪贴板被其他程序占用，未能复制译文",
                "Clipboard is busy; the translation was not copied"), true);
        Close();
    }

    private async Task TranslateAsync()
    {
        if (_busy || _closing) return;

        var text = _input.Text.Trim();
        if (text.Length == 0)
        {
            _status.Text = Loc.T("请输入待翻译文本", "Enter text to translate");
            FocusInput();
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
            if (_closing) return;

            if (!r.Ok)
            {
                _status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
                _status.Text = r.Error ?? Loc.T("翻译失败", "Translation failed");
                return;
            }

            _hasResult = true;
            _input.Text = r.Text;
            _input.SelectAll();

            if (_settings.Behavior.SimulateInput)
            {
                // 模拟输入：不写剪贴板，短暂展示译文后直接把文字键入游戏
                _status.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xE0, 0x9E));
                _status.Text = Loc.F("正在模拟输入… · {0} ms", "Typing the translation into the game… · {0} ms",
                    r.ElapsedMs);
                await Task.Delay(300);
                if (!_closing) await SimulateSendAndCloseAsync(r.Text);
                return;
            }

            bool copyWanted = _settings.Behavior.CopyInputResult;
            bool copied = copyWanted && CopyToClipboard(r.Text);

            if (copied)
            {
                _status.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xE0, 0x9E));
                _status.Text = Loc.F("已复制到剪贴板，3 秒后自动关闭；Enter 立即关闭 · {0} ms",
                    "Copied to clipboard; closes in 3 s, Enter closes now · {0} ms", r.ElapsedMs);
                ArmAutoClose(3);
            }
            else if (copyWanted)
            {
                // 剪贴板被游戏客户端/加速器占用：不静默失败，也不留下关不掉的窗口——
                // 给 5 秒自动关闭，期间按 Enter 可重试复制并立即关闭。
                _status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x6B));
                _status.Text = Loc.F("剪贴板被占用，未能复制，5 秒后自动关闭；Enter 重试 · {0} ms",
                    "Clipboard busy; not copied. Closes in 5 s; Enter retries · {0} ms", r.ElapsedMs);
                ArmAutoClose(5);
            }
            else
            {
                _status.Text = Loc.F("翻译完成，3 秒后自动关闭；Enter 复制并关闭 · {0} ms",
                    "Translation done; closes in 3 s, Enter copies and closes · {0} ms", r.ElapsedMs);
                ArmAutoClose(3);
            }
        }
        finally
        {
            _busy = false;
            _sendBtn.IsEnabled = true;
            _input.IsReadOnly = false;
            if (!_closing) FocusInput();
        }
    }

    /// <summary>翻译成功后自动关闭；任何按键都会取消，避免用户还在操作时被抢关。</summary>
    private void ArmAutoClose(int seconds)
    {
        _autoClose?.Stop();
        _autoClose = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30)),
        };
        _autoClose.Tick += (_, _) =>
        {
            _autoClose?.Stop();
            _autoClose = null;
            Close();
        };
        _autoClose.Start();
    }

    /// <summary>
    /// 模拟输入：先把前台还给游戏（Closing 中的 RestoreForeground），确认游戏窗口真的回到前台后，
    /// 用 SendInput 键入译文。目标没拿到前台时绝不往随机窗口打字，退回复制剪贴板。
    /// </summary>
    private async Task SimulateSendAndCloseAsync(string text)
    {
        if (_simulating || _closing) return;
        _simulating = true;

        string payload = InputSimulator.Sanitize(text);
        IntPtr target = _prevForeground;
        bool ownProcess = IsOwnProcessWindow(target);

        try
        {
            Close(); // Closing → RestoreForeground 把前台还给游戏

            if (payload.Length == 0 || target == IntPtr.Zero || ownProcess)
            {
                Log.Warn($"模拟输入跳过：payload={payload.Length} target=0x{target.ToInt64():X} ownProcess={ownProcess}");
                FallbackToClipboard(text);
                return;
            }

            // 等前台真正回到游戏（部分游戏切回需要一点时间）
            bool focused = false;
            for (int i = 0; i < 12; i++)
            {
                if (Win32.GetForegroundWindow() == target)
                {
                    focused = true;
                    break;
                }

                await Task.Delay(50);
            }

            if (!focused)
            {
                Log.Warn($"模拟输入跳过：前台未切回目标窗口 0x{target.ToInt64():X}");
                FallbackToClipboard(text);
                return;
            }

            // 模拟键入期间挂起热键：译文里的字符可能命中所绑定的热键（例如绑到字母键）
            _suspendHotkeys?.Invoke(payload.Length * 20 + 500);
            Log.Info($"模拟输入 {payload.Length} 字 -> 窗口 0x{target.ToInt64():X}");

            if (!InputSimulator.SendText(payload))
                FallbackToClipboard(text);
        }
        finally
        {
            _simulating = false;
        }
    }

    /// <summary>模拟输入不可用时的兜底：复制到剪贴板并提示。</summary>
    private static void FallbackToClipboard(string text)
    {
        bool copied = ClipboardService.TrySetText(text);
        var message = copied
            ? Loc.T("模拟输入未执行，译文已复制到剪贴板",
                "Simulated input was not performed; the translation was copied to the clipboard")
            : Loc.T("模拟输入未执行，剪贴板也被占用，请手动复制译文",
                "Simulated input was not performed and the clipboard is busy; copy the translation manually");

        // 普通权限下最常见的拦截原因是目标游戏 / 加速器以管理员运行（UIPI），提示提权方向。
        if (!AdminHelper.IsElevated)
            message += Loc.T("；游戏可能以管理员运行，可尝试以管理员身份重启 GameTrans",
                "; the game may run elevated — try restarting GameTrans as administrator");

        ToastWindow.ShowToast(message, true);
    }

    private static bool IsOwnProcessWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid != 0 && pid == (uint)Environment.ProcessId;
    }

    /// <summary>复制结果（带时间预算，剪贴板被占用时快速失败，不阻塞 UI）。</summary>
    public static bool CopyToClipboard(string text) => ClipboardService.TrySetText(text);

    public void RememberForeground()
    {
        _prevForeground = Win32.GetForegroundWindow();
    }

    /// <summary>把已打开的输入窗重新拉到前台并聚焦输入框（再次按 F9 时调用，不叠新窗口）。</summary>
    public void Reactivate()
    {
        if (!IsVisible || _closing) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            }

            BringToForegroundWithRetry();
        }
        catch (Exception ex)
        {
            Log.Warn("输入框重新激活失败: " + ex.Message);
        }
    }

    private void RestoreForeground()
    {
        try
        {
            if (_prevForeground != IntPtr.Zero && Win32.IsWindowVisibleSafe(_prevForeground))
                Win32.ForceForeground(_prevForeground);
        }
        catch
        {
            // 目标窗口已销毁时忽略
        }
    }
}
