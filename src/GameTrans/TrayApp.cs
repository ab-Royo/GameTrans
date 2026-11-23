using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using GameTrans.Core;
using GameTrans.UI;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace GameTrans;

/// <summary>托盘应用：把热键、截图、模型调用、叠加层、输入窗串起来。</summary>
public sealed class TrayApp : IDisposable
{
    private readonly DeepSeekClient _client = new();
    private readonly HotkeyManager _hotkeys = new();
    private NotifyIcon? _tray;
    private OverlayWindow? _overlay;
    private InputWindow? _inputWindow;
    private SettingsWindow? _settingsWindow;
    private StartupWindow? _startup;
    private AppSettings _settings;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>最近一次网络探测结果，用于启动页与托盘菜单展示。</summary>
    private NetworkProbe? _networkProbe;

    /// <summary>进行中的探测任务：重复调用时复用，避免同时发起多次 DNS / TCP 探测。</summary>
    private Task<NetworkProbe?>? _networkTask;

    /// <summary>选区窗口是模态的：正在选区时忽略热键，避免连按叠出多个选区窗口。</summary>
    private bool _regionPicking;

    /// <summary>
    /// 同一次按键可能同时被 RegisterHotKey 与轮询兜底看到（极少数线程繁忙时会跨通道重复），
    /// 这里再做一次 180 ms 去抖，确保连按只响应一次、不会叠窗口。
    /// </summary>
    private const long DuplicatePressWindowMs = 180;

    private long _lastTranslateFireTick;
    private long _lastInputFireTick;
    private long _lastRegionFireTick;

    public TrayApp(AppSettings settings)
    {
        _settings = settings;
        Loc.SetLanguage(_settings.Ui.Language);

        _hotkeys.TranslatePressed += () => TranslateHotkey();
        _hotkeys.InputPressed += () => InputHotkey();
        _hotkeys.RegionPressed += () => RegionHotkey();

        // 先把启动页显示出来（用户双击后立刻有反馈），
        // 托盘图标/WinForms 初始化较慢，放到 UI 空闲时再做，避免"静默几秒"。
        bool firstRun = !File.Exists(SettingsStore.SettingsPath);
        bool showStartup = firstRun || _settings.Behavior.ShowStartupWindow;
        if (showStartup)
            ShowStartup();

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null)
            dispatcher.BeginInvoke(new Action(() => InitTray(showStartup)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        else
            InitTray(showStartup);
    }

    private void InitTray(bool startupShown)
    {
        try
        {
            _tray = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = Loc.T("GameTrans 游戏聊天翻译", "GameTrans Game Chat Translator"),
                Visible = true,
            };
            _tray.DoubleClick += (_, _) => ShowStartup();
            _tray.ContextMenuStrip = BuildMenu();
        }
        catch (Exception ex)
        {
            Log.Error("托盘初始化失败: " + ex);
        }

        ApplyHotkeys(notify: true);

        if (!startupShown && !_settings.Region.IsUsable)
        {
            var hint = _settings.Hotkeys.RegionVk != 0
                ? Loc.F("尚未设置识别区域，请按 {0} 打开选区", "No capture region yet; press {0} to select one",
                    _settings.Hotkeys.RegionText)
                : Loc.F("尚未设置识别区域，请按 {0} 打开选区", "No capture region yet; press {0} to select one",
                    _settings.Hotkeys.TranslateText);
            Toast(hint, false);
        }

        // 启动后在后台做一次网络探测：启动页显示状态；未显示启动页时，网络异常才弹出提示。
        // 用加速器 / 代理后 DNS 可能整体失效，提前发现比等用户按 F8 才报错更友好。
        _ = RefreshNetworkAsync(notify: !startupShown);

        // 已开启模拟输入但当前不是管理员：启动页会显示醒目警示；没开启动页时用气泡提醒一次。
        if (!startupShown && _settings.Behavior.SimulateInput && !AdminHelper.IsElevated)
            Toast(AdminHelper.SimulateInputShortWarning, true);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem(Loc.F("翻译当前区域 ({0})", "Translate current region ({0})", _settings.Hotkeys.TranslateText),
            null, (_, _) => TranslateHotkey()));
        menu.Items.Add(new ToolStripMenuItem(Loc.F("输入翻译 ({0})", "Input translation ({0})", _settings.Hotkeys.InputText),
            null, (_, _) => InputHotkey()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Loc.T("启动页…", "Startup page…"), null, (_, _) => ShowStartup()));
        menu.Items.Add(new ToolStripMenuItem(RegionMenuText(), null, (_, _) => PickRegion(showToast: true)));
        menu.Items.Add(new ToolStripMenuItem(Loc.T("术语 / 黑话对照表…", "Glossary / Slang…"), null, (_, _) => OpenGlossary()));
        menu.Items.Add(new ToolStripMenuItem(Loc.T("网络诊断…", "Network diagnostics…"), null, (_, _) => _ = DiagnoseNetworkAsync()));
        menu.Items.Add(new ToolStripMenuItem(Loc.T("设置…", "Settings…"), null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripSeparator());

        var links = new ToolStripMenuItem(Loc.T("社区 / 链接", "Community / links"));
        links.DropDownItems.Add(new ToolStripMenuItem("GitHub  " + AppLinks.Repository,
            null, (_, _) => Theme.OpenUrl(AppLinks.Repository)));
        links.DropDownItems.Add(new ToolStripMenuItem(
            Loc.T("QQ 群 " + AppLinks.QQGroup + "（复制群号）", "QQ group " + AppLinks.QQGroup + " (copy)"),
            null, (_, _) => CopyQQGroup()));
        links.DropDownItems.Add(new ToolStripMenuItem(Loc.T("Bilibili 空间", "Bilibili space"),
            null, (_, _) => Theme.OpenUrl(AppLinks.Bilibili)));
        menu.Items.Add(links);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Loc.T("退出", "Exit"), null, (_, _) => Shutdown()));
        return menu;
    }

    private static void CopyQQGroup()
    {
        bool ok = AppLinks.CopyQQGroup();
        Toast(ok
                ? Loc.T("QQ 群号已复制：" + AppLinks.QQGroup, "QQ group number copied: " + AppLinks.QQGroup)
                : Loc.T("剪贴板被占用，未能复制群号", "Clipboard busy; group number was not copied"),
            !ok);
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                using var extracted = Drawing.Icon.ExtractAssociatedIcon(path);
                if (extracted != null)
                    return new Drawing.Icon(extracted, new Drawing.Size(32, 32));
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取应用图标失败，改用运行时绘制: " + ex.Message);
        }

        return CreateTrayIcon();
    }
    private static Drawing.Icon CreateTrayIcon()
    {
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Drawing.Color.Transparent);
                using var bg = new SolidBrush(Drawing.Color.FromArgb(255, 124, 107, 255));
                g.FillEllipse(bg, 1, 1, 30, 30);
                using var fg = new SolidBrush(Drawing.Color.White);
                using var font = new Font("Microsoft YaHei UI", 15, System.Drawing.FontStyle.Bold,
                    GraphicsUnit.Pixel);
                var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                g.DrawString("译", font, fg, new RectangleF(0, 0, 32, 32), sf);
            }

            return Drawing.Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private string RegionMenuText() => _settings.Hotkeys.RegionVk != 0
        ? Loc.F("重新设置识别区域 ({0})…", "Set capture region ({0})…", _settings.Hotkeys.RegionText)
        : Loc.T("重新设置识别区域…", "Set capture region…");

    private void ApplyHotkeys(bool notify)
    {
        var result = _hotkeys.Register(_settings.Hotkeys.TranslateMods, _settings.Hotkeys.TranslateVk,
            _settings.Hotkeys.InputMods, _settings.Hotkeys.InputVk,
            _settings.Hotkeys.RegionMods, _settings.Hotkeys.RegionVk);

        if (result.HasError)
        {
            if (notify && !string.IsNullOrEmpty(result.Message)) Toast(result.Message, true);
            Log.Warn("热键配置错误: " + result.Message);
        }
        else if (!result.AllNativeRegistered)
        {
            // 轮询兜底不算错误：功能仍可用，只是提示用户按键可能被其他程序占用
            if (notify && !string.IsNullOrEmpty(result.Message)) Toast(result.Message, false);
            Log.Warn("热键兼容模式: " + result.Message);
        }

        Log.Info($"热键注册: {_settings.Hotkeys.TranslateText} / {_settings.Hotkeys.InputText} / " +
                 $"{_settings.Hotkeys.RegionText} " +
                 $"-> activate={result.AllNativeRegistered} " +
                 $"(translate native={_hotkeys.TranslateRegisteredNatively}, " +
                 $"input native={_hotkeys.InputRegisteredNatively}, " +
                 $"region native={_hotkeys.RegionRegisteredNatively})");
    }

    private static void Toast(string text, bool error) => ToastWindow.ShowToast(text, error);

    private string ApiKey => SettingsStore.ReadApiKey(_settings);

    /// <summary>在后台做一次网络探测，并把结果同步到启动页；网络异常时可弹出提示。</summary>
    private Task<NetworkProbe?> RefreshNetworkAsync(bool notify)
    {
        // 探测进行中时复用同一个任务：托盘「网络诊断…」在启动探测未完成时也不会拿到空结果。
        if (_networkTask is { IsCompleted: false }) return _networkTask;
        _networkTask = ProbeNetworkCoreAsync(notify);
        return _networkTask;
    }

    private async Task<NetworkProbe?> ProbeNetworkCoreAsync(bool notify)
    {
        _startup?.SetNetworkChecking();
        try
        {
            var probe = await NetworkDiagnostics.ProbeAsync(_settings.Api.BaseUrl);
            _networkProbe = probe;
            _startup?.SetNetwork(probe);

            if (!probe.Ok)
            {
                Log.Warn("网络诊断: " + probe.FullText.Replace('\n', ' '));
                if (notify) Toast(probe.FullText, true);
            }

            return probe;
        }
        catch (Exception ex)
        {
            Log.Warn("网络诊断异常: " + ex.Message);
            return _networkProbe;
        }
    }

    /// <summary>托盘菜单「网络诊断…」：立即探测并给出可执行的排查建议。</summary>
    private async Task DiagnoseNetworkAsync()
    {
        var probe = await RefreshNetworkAsync(notify: false);
        if (probe == null)
        {
            Toast(Loc.T("网络诊断失败，请查看日志", "Network diagnostics failed; see the log"), true);
            return;
        }

        var text = probe.FullText +
                   "\n\n" + Loc.F("目标接口：{0}", "Endpoint: {0}", _settings.Api.BaseUrl) +
                   "\n" + Loc.F("耗时：{0} ms", "Elapsed: {0} ms", probe.ElapsedMs);
        Window? owner = _startup is { IsVisible: true } startup
            ? startup
            : _settingsWindow is { IsVisible: true } settings
                ? settings
                : null;
        var title = Loc.T("GameTrans 网络诊断", "GameTrans Network Diagnostics");
        if (owner != null)
            System.Windows.MessageBox.Show(owner, text, title, MessageBoxButton.OK,
                probe.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        else
            System.Windows.MessageBox.Show(text, title, MessageBoxButton.OK,
                probe.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private bool EnsureApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey)) return true;
        Toast(Loc.T("尚未配置 DeepSeek API Key，请在设置中填写", "No DeepSeek API key yet; add one in Settings"), true);
        OpenSettings();
        return false;
    }

    private bool EnsureRegion(bool interactive = true)
    {
        if (_settings.Region.IsUsable) return true;
        if (!interactive) return false;
        return PickRegion(showToast: false);
    }

    private bool PickRegion(bool showToast)
    {
        // 选区窗口是 ShowDialog 模态：它有自己的消息循环，热键仍会被派发到这里。
        // 不做防重入的话，快速连按 F8/F9 会在选区窗口之上再叠一个选区窗口。
        if (_regionPicking) return false;
        _regionPicking = true;
        try
        {
            var current = _settings.Region.IsUsable
                ? new RegionPx(_settings.Region.Left, _settings.Region.Top, _settings.Region.Right,
                    _settings.Region.Bottom)
                : (RegionPx?)null;

            var picked = RegionSelectorWindow.Pick(current);
            if (picked is not { IsUsable: true })
            {
                if (showToast) Toast(Loc.T("已取消选区操作", "Region selection cancelled"), false);
                return false;
            }

            _settings.Region.HasRegion = true;
            _settings.Region.Left = picked.Value.Left;
            _settings.Region.Top = picked.Value.Top;
            _settings.Region.Right = picked.Value.Right;
            _settings.Region.Bottom = picked.Value.Bottom;
            SettingsStore.Save(_settings);
            Toast(Loc.F("识别区域已更新：{0} × {1}", "Capture region updated: {0} × {1}", _settings.Region.Width, _settings.Region.Height), false);
            return true;
        }
        finally
        {
            _regionPicking = false;
        }
    }

    /// <summary>同一次按键的重复投递去抖：连按（或两条热键通道同时命中）时只响应第一次。</summary>
    private static bool IsDuplicatePress(ref long lastTick)
    {
        long now = Environment.TickCount64;
        if (now - lastTick < DuplicatePressWindowMs) return true;
        lastTick = now;
        return false;
    }

    /// <summary>重新选区热键（默认未绑定）：打开选区窗口；不依赖 API Key。</summary>
    private void RegionHotkey()
    {
        if (IsDuplicatePress(ref _lastRegionFireTick)) return;
        if (_regionPicking) return;

        if (_overlay is { IsVisible: true })
        {
            _cts?.Cancel();
            _overlay.Dismiss();
        }

        if (PickRegion(showToast: true)) _startup?.Refresh(_settings);
    }

    /// <summary>F8：显示中则隐藏；否则截图→识别→叠加显示。</summary>
    private void TranslateHotkey()
    {
        if (IsDuplicatePress(ref _lastTranslateFireTick)) return;
        if (_regionPicking) return;

        if (_overlay is { IsVisible: true })
        {
            _cts?.Cancel();
            _overlay.Dismiss();
            return;
        }

        if (!EnsureRegion()) return;
        if (!EnsureApiKey()) return;
        _ = TranslateRegionAsync();
    }

    private async Task TranslateRegionAsync()
    {
        var region = new RegionPx(_settings.Region.Left, _settings.Region.Top,
            _settings.Region.Right, _settings.Region.Bottom);
        if (!region.IsUsable)
        {
            Toast(Loc.T("识别区域无效，请重新设置", "Invalid capture region; please set it again"), true);
            return;
        }

        var cts = new CancellationTokenSource();
        var previous = _cts;
        _cts = cts;
        previous?.Cancel(); // 只取消，不 Dispose：旧任务可能仍在使用它的 token
        var ct = cts.Token;

        _overlay ??= new OverlayWindow();
        _overlay.Dismiss();

        // 让叠加层先真正消失，避免把自己截进去（双保险：叠加层本身已 WDA_EXCLUDEFROMCAPTURE）
        await Task.Delay(80, ct).ConfigureAwait(true);

        byte[] png;
        try
        {
            png = ScreenCapture.ToPng(ScreenCapture.Capture(region));
            Log.Info($"截图 {region.Width}x{region.Height} -> {png.Length / 1024} KB");
        }
        catch (Exception ex)
        {
            Log.Error("截图失败: " + ex);
            Toast(Loc.T("截图失败：", "Capture failed: ") + ex.Message, true);
            return;
        }

        if (ct.IsCancellationRequested) return;

        _overlay.ShowText(region, _settings, Loc.T("识别中…", "Recognizing…"), Loc.T("模型请求中", "Waiting for model"));
        var r = await _client.TranslateImageAsync(png, _settings, ApiKey, ct);

        if (ct.IsCancellationRequested) return;

        if (r.Ok)
        {
            var status = _settings.Behavior.ShowLatency
                ? $"{r.ElapsedMs} ms · {_settings.Api.Model}"
                : null;
            _overlay.UpdateText(r.Text, status);
            Log.Info($"翻译成功 {r.ElapsedMs}ms, {r.Text.Length} 字");
        }
        else
        {
            _overlay.UpdateText(Loc.T("翻译失败\n", "Translation failed\n") + r.Error, null);
            Log.Warn("翻译失败: " + r.Error);

            // 网络层面的失败（status=0）顺带刷新网络状态，启动页/托盘能直接看到排查建议。
            if (r.StatusCode == 0) _ = RefreshNetworkAsync(notify: false);
        }
    }

    /// <summary>F9：在选区下方展开输入框；已打开时只把它重新拉到前台，不叠新窗口。</summary>
    private void InputHotkey()
    {
        if (IsDuplicatePress(ref _lastInputFireTick)) return;
        if (_regionPicking) return;

        if (_inputWindow is { IsVisible: true } opened)
        {
            opened.Reactivate();
            return;
        }

        if (!EnsureRegion()) return;
        if (!EnsureApiKey()) return;

        var region = new RegionPx(_settings.Region.Left, _settings.Region.Top,
            _settings.Region.Right, _settings.Region.Bottom);

        var w = new InputWindow(_settings, _client, ApiKey, region, _hotkeys.SuspendFor);
        _inputWindow = w;
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(_inputWindow, w)) _inputWindow = null;
        };
        w.RememberForeground();
        w.Show();
        w.Reactivate();
    }

    private void ShowStartup()
    {
        // 启动页右上角关闭走 Hide，对象仍缓存着：再次打开必须复用（ShowAgain），
        // 否则每次双击托盘都会再建一个隐藏窗口，旧窗口永远留在 Application.Windows 里。
        if (_startup != null)
        {
            _startup.ShowAgain();
            if (_networkProbe != null) _startup.SetNetwork(_networkProbe);
            _ = RefreshNetworkAsync(notify: false);
            return;
        }

        _startup = new StartupWindow(_settings,
            onPickRegion: () =>
            {
                if (PickRegion(showToast: true)) _startup?.Refresh(_settings);
            },
            onOpenGlossary: OpenGlossary,
            onOpenSettings: OpenSettings,
            onShowOnStartupChanged: v =>
            {
                _settings.Behavior.ShowStartupWindow = v;
                SettingsStore.Save(_settings);
            });
        _startup.Closed += (_, _) => _startup = null;
        _startup.Show();
        _startup.Activate();
        if (_networkProbe != null) _startup.SetNetwork(_networkProbe);
        _ = RefreshNetworkAsync(notify: false);
    }

    private void OpenGlossary()
    {
        var edited = GlossaryWindow.Edit(_settings.Prompt.Glossary, _startup ?? (Window?)_settingsWindow);
        if (edited == null) return;
        _settings.Prompt.Glossary = edited;
        SettingsStore.Save(_settings);
        _startup?.Refresh(_settings);
        Toast(Loc.F("术语表已保存（{0} 项）", "Glossary saved ({0} entries)", edited.Count), false);
    }
    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, updated =>
        {
            var languageChanged = !string.Equals(updated.Ui.Language, _settings.Ui.Language,
                StringComparison.Ordinal);
            _settings = updated;
            ApplyHotkeys(notify: true);
            if (languageChanged) ApplyLanguage();
            else
            {
                RebuildTray();
                _startup?.Refresh(_settings);
            }
        }, RestartAsAdmin);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>界面语言变化后：刷新托盘菜单/提示，并在启动页可见时用新语言重建。</summary>
    private void ApplyLanguage()
    {
        Loc.SetLanguage(_settings.Ui.Language);
        RebuildTray();

        if (_startup is { IsVisible: true })
        {
            _startup.Close();
            ShowStartup();
        }
    }

    private void RebuildTray()
    {
        if (_tray == null) return;
        _tray.Text = Loc.T("GameTrans 游戏聊天翻译", "GameTrans Game Chat Translator");
        _tray.ContextMenuStrip = BuildMenu();
    }

    /// <summary>设置页请求以管理员身份重启：成功拉起新进程后退出当前实例。</summary>
    private void RestartAsAdmin()
    {
        var error = AdminHelper.RestartElevated();
        if (error != null)
        {
            Toast(error, true);
            return;
        }

        Shutdown();
    }

    public void Shutdown()
    {
        Dispose();
        System.Windows.Application.Current?.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _hotkeys.Dispose();
            _inputWindow?.Close();
            _overlay?.Close();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("退出清理异常: " + ex.Message);
        }
    }
}
