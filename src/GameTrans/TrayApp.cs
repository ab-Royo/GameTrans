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
    private SettingsWindow? _settingsWindow;
    private StartupWindow? _startup;
    private AppSettings _settings;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public TrayApp(AppSettings settings)
    {
        _settings = settings;

        _hotkeys.TranslatePressed += () => TranslateHotkey();
        _hotkeys.InputPressed += () => InputHotkey();

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
                Text = "GameTrans 游戏聊天翻译",
                Visible = true,
            };
            _tray.DoubleClick += (_, _) => OpenSettings();
            _tray.ContextMenuStrip = BuildMenu();
        }
        catch (Exception ex)
        {
            Log.Error("托盘初始化失败: " + ex);
        }

        ApplyHotkeys(notify: true);

        if (!startupShown && !_settings.Region.IsUsable)
            Toast("尚未设置识别区域，请按 " + _settings.Hotkeys.TranslateText + " 打开选区", false);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"翻译当前区域 ({_settings.Hotkeys.TranslateText})",
            null, (_, _) => TranslateHotkey()));
        menu.Items.Add(new ToolStripMenuItem($"输入翻译 ({_settings.Hotkeys.InputText})",
            null, (_, _) => InputHotkey()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("启动页…", null, (_, _) => ShowStartup()));
        menu.Items.Add(new ToolStripMenuItem("重新设置识别区域…", null, (_, _) => PickRegion(showToast: true)));
        menu.Items.Add(new ToolStripMenuItem("术语 / 黑话对照表…", null, (_, _) => OpenGlossary()));
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => Shutdown()));
        return menu;
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

    private void ApplyHotkeys(bool notify)
    {
        bool ok = _hotkeys.Register(_settings.Hotkeys.TranslateMods, _settings.Hotkeys.TranslateVk,
            _settings.Hotkeys.InputMods, _settings.Hotkeys.InputVk, out var msg);
        if (!ok && notify) Toast(msg, true);
        Log.Info($"热键注册: {_settings.Hotkeys.TranslateText} / {_settings.Hotkeys.InputText} -> {ok}");
    }

    private static void Toast(string text, bool error) => ToastWindow.ShowToast(text, error);

    private string ApiKey => SettingsStore.ReadApiKey(_settings);

    private bool EnsureApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey)) return true;
        Toast("尚未配置 DeepSeek API Key，请在设置中填写", true);
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
        var current = _settings.Region.IsUsable
            ? new RegionPx(_settings.Region.Left, _settings.Region.Top, _settings.Region.Right,
                _settings.Region.Bottom)
            : (RegionPx?)null;

        var picked = RegionSelectorWindow.Pick(current);
        if (picked is not { IsUsable: true })
        {
            if (showToast) Toast("已取消选区操作", false);
            return false;
        }

        _settings.Region.HasRegion = true;
        _settings.Region.Left = picked.Value.Left;
        _settings.Region.Top = picked.Value.Top;
        _settings.Region.Right = picked.Value.Right;
        _settings.Region.Bottom = picked.Value.Bottom;
        SettingsStore.Save(_settings);
        Toast($"识别区域已更新：{_settings.Region.Width} × {_settings.Region.Height}", false);
        return true;
    }

    /// <summary>F8：显示中则隐藏；否则截图→识别→叠加显示。</summary>
    private void TranslateHotkey()
    {
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
            Toast("识别区域无效，请重新设置", true);
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
            Toast("截图失败：" + ex.Message, true);
            return;
        }

        if (ct.IsCancellationRequested) return;

        _overlay.ShowText(region, _settings, "识别中…", "模型请求中");
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
            _overlay.UpdateText("翻译失败\n" + r.Error, null);
            Log.Warn("翻译失败: " + r.Error);
        }
    }

    /// <summary>F9：在选区下方展开输入框。</summary>
    private void InputHotkey()
    {
        if (!EnsureRegion()) return;
        if (!EnsureApiKey()) return;

        var region = new RegionPx(_settings.Region.Left, _settings.Region.Top,
            _settings.Region.Right, _settings.Region.Bottom);

        var w = new InputWindow(_settings, _client, ApiKey, region);
        w.RememberForeground();
        w.Show();
        w.Activate();
    }

    private void ShowStartup()
    {
        if (_startup is { IsVisible: true })
        {
            _startup.Activate();
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
    }

    private void OpenGlossary()
    {
        var edited = GlossaryWindow.Edit(_settings.Prompt.Glossary, _startup ?? (Window?)_settingsWindow);
        if (edited == null) return;
        _settings.Prompt.Glossary = edited;
        SettingsStore.Save(_settings);
        _startup?.Refresh(_settings);
        Toast($"术语表已保存（{edited.Count} 项）", false);
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
            _settings = updated;
            ApplyHotkeys(notify: true);
            _startup?.Refresh(_settings);
        });
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
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
