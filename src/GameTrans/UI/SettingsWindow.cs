using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameTrans.Core;

namespace GameTrans.UI;

public sealed class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly Action<AppSettings> _onSave;
    private readonly DeepSeekClient _client = new();

    private readonly OverlayContent _preview = new();
    private readonly PasswordBox _keyBox = new();
    private readonly TextBlock _testLabel = new() { FontSize = 12, Foreground = Theme.Sub, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _regionLabel = new() { FontSize = 12, Foreground = Theme.Sub };
    private readonly Button _hotkeyTranslateBtn;
    private readonly Button _hotkeyInputBtn;

    private int _capturingHotkey; // 0=无 1=翻译 2=输入

    public SettingsWindow(AppSettings current, Action<AppSettings> onSave)
    {
        _s = Clone(current);
        _onSave = onSave;

        WindowStyle = WindowStyle.SingleBorderWindow;
        Title = "GameTrans 设置";
        Width = 760;
        Height = 820;
        MinWidth = 680;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Bg;
        FontFamily = Theme.Font;
        Foreground = Theme.Text;
        ShowInTaskbar = true;

        _hotkeyTranslateBtn = Theme.Btn(_s.Hotkeys.TranslateText, StartCaptureTranslate, false, 130);
        _hotkeyInputBtn = Theme.Btn(_s.Hotkeys.InputText, StartCaptureInput, false, 130);

        var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(16) };

        root.Children.Add(BuildApiCard());
        root.Children.Add(Gap(12));
        root.Children.Add(BuildPromptCard());
        root.Children.Add(Gap(12));
        root.Children.Add(BuildOverlayCard());
        root.Children.Add(Gap(12));
        root.Children.Add(BuildHotkeyCard());
        root.Children.Add(Gap(12));
        root.Children.Add(BuildAboutCard());

        var scroll = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Theme.Bg,
        };

        var saveBtn = Theme.Btn("保存", Save, true, 96);
        var cancelBtn = Theme.Btn("取消", Close, false, 96);
        var bottom = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A1C22")!),
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 10, 16, 10),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { cancelBtn, saveBtn },
            },
        };

        var layout = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(bottom);
        layout.Children.Add(scroll);
        Content = layout;

        _keyBox.Password = SettingsStore.ReadApiKey(_s);
        _keyBox.FontFamily = Theme.Mono;
        _keyBox.FontSize = 13;
        _keyBox.PasswordChar = '●';
        _keyBox.Padding = new Thickness(6, 4, 6, 4);
        _keyBox.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B1D23")!);
        _keyBox.Foreground = Theme.Text;
        _keyBox.BorderBrush = Theme.Line;
        _keyBox.BorderThickness = new Thickness(1);

        PreviewKeyDown += OnPreviewKey;
        RefreshRegionLabel();
        RefreshPreview();
    }

    private static StackPanel Gap(double h) =>
        new() { Height = h, Orientation = Orientation.Vertical };

    private static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, AppSettings.JsonOpts),
            AppSettings.JsonOpts) ?? new AppSettings();

    // ---------------- 接口 ----------------

    private UIElement BuildApiCard()
    {
        var keyRow = new Grid();
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_keyBox, 0);
        var testBtn = Theme.Btn("测试连接", () => _ = TestAsync(), false, 96);
        testBtn.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(testBtn, 1);
        keyRow.Children.Add(_keyBox);
        keyRow.Children.Add(testBtn);

        var baseUrl = Theme.T("", 380);
        baseUrl.Text = _s.Api.BaseUrl;
        baseUrl.TextChanged += (_, _) => _s.Api.BaseUrl = baseUrl.Text;

        var model = Theme.T("", 200);
        model.Text = _s.Api.Model;
        model.TextChanged += (_, _) => _s.Api.Model = model.Text;

        var detail = new DropDown(new[] { "high", "original", "low", "auto" }, _s.Api.Detail,
            v => _s.Api.Detail = v, 150);

        var maxTokens = Theme.T(_s.Api.MaxTokens.ToString(), 100);
        maxTokens.TextChanged += (_, _) =>
        {
            if (int.TryParse(maxTokens.Text, out var v)) _s.Api.MaxTokens = v;
        };

        var temp = Theme.T(_s.Api.Temperature.ToString("0.##"), 100);
        temp.TextChanged += (_, _) =>
        {
            if (Theme.TryParseDouble(temp.Text, out var v)) _s.Api.Temperature = v;
        };

        var timeout = Theme.T(_s.Api.TimeoutSeconds.ToString(), 100);
        timeout.TextChanged += (_, _) =>
        {
            if (int.TryParse(timeout.Text, out var v)) _s.Api.TimeoutSeconds = v;
        };

        var note = Theme.L(
            "Base URL 默认为 https://api.deepseek.com；模型 deepseek-flash 支持 PNG / JPEG 图像输入。\n" +
            "detail 取 high 或 original 时保留原图像素；取 low 时缩放至 512×512，小字号文本可能损失细节。",
            12, Theme.Sub);

        return Theme.Card("模型接口",
            Theme.Field("API Key", keyRow, 88),
            _testLabel,
            Theme.Field("Base URL", baseUrl, 88),
            Theme.Field("模型", Theme.Row(10, model, detail), 88),
            Theme.Field("max_tokens", Theme.Row(10, maxTokens, Theme.L("temperature", 12, Theme.Sub), temp,
                Theme.L("超时(秒)", 12, Theme.Sub), timeout), 88),
            new Border { Height = 6 },
            note);
    }

    private async Task TestAsync()
    {
        _testLabel.Foreground = Theme.Sub;
        _testLabel.Text = "测试中…";
        var probe = Clone(_s);
        probe.Api.TimeoutSeconds = Math.Max(15, probe.Api.TimeoutSeconds);
        var r = await _client.TestAsync(probe, _keyBox.Password, CancellationToken.None);
        if (r.Ok)
        {
            _testLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xE0, 0x9E));
            _testLabel.Text = $"连接正常 · {r.ElapsedMs} ms · 返回：{r.Text.Replace("\n", " ")}";
        }
        else
        {
            _testLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            _testLabel.Text = "连接失败：" + r.Error;
        }
    }

    private UIElement BuildPromptCard()
    {
        var gameBox = Theme.T("", 200);
        gameBox.Text = _s.Prompt.GameName;
        gameBox.TextChanged += (_, _) => _s.Prompt.GameName = gameBox.Text;

        var src = Theme.T("", 160);
        src.Text = _s.Prompt.SourceLanguage;
        src.TextChanged += (_, _) => _s.Prompt.SourceLanguage = src.Text;

        var dst = Theme.T("", 160);
        dst.Text = _s.Prompt.TargetLanguage;
        dst.TextChanged += (_, _) => _s.Prompt.TargetLanguage = dst.Text;

        var inSrc = Theme.T("", 160);
        inSrc.Text = _s.Prompt.InputSourceLanguage;
        inSrc.TextChanged += (_, _) => _s.Prompt.InputSourceLanguage = inSrc.Text;

        var inDst = Theme.T("", 160);
        inDst.Text = _s.Prompt.InputTargetLanguage;
        inDst.TextChanged += (_, _) => _s.Prompt.InputTargetLanguage = inDst.Text;

        int GlossaryCount() => _s.Prompt.Glossary?.Count(g => !string.IsNullOrWhiteSpace(g.Term)) ?? 0;
        Button? glossBtn = null;
        glossBtn = Theme.Btn($"编辑术语 / 黑话对照表（{GlossaryCount()} 项）", () =>
        {
            var edited = GlossaryWindow.Edit(_s.Prompt.Glossary, this);
            if (edited == null) return;
            _s.Prompt.Glossary = edited;
            if (glossBtn != null) glossBtn.Content = $"编辑术语 / 黑话对照表（{GlossaryCount()} 项）";
            RefreshPreview();
        }, false, 300);

        var modeValues = new[] { "translated", "ocr" };
        var modeLabels = new[] { "仅译文（中文）", "仅识别（不翻译）" };
        int modeIndex = Math.Max(0, Array.IndexOf(modeValues, _s.Prompt.OutputMode));
        var mode = new DropDown(modeLabels, modeLabels[modeIndex],
            v =>
            {
                int i = Array.IndexOf(modeLabels, v);
                _s.Prompt.OutputMode = modeValues[Math.Max(0, i)];
            }, 200);

        var modeHint = Theme.L("仅译文：仅输出中文译文（默认）；仅识别：仅转写原文，不做翻译。",
            12, Theme.Sub);

        var extra = Theme.T(_s.Prompt.ExtraInstruction, double.NaN, true, 3);
        extra.TextChanged += (_, _) => _s.Prompt.ExtraInstruction = extra.Text;

        var sys = Theme.T(_s.Prompt.SystemPrompt, double.NaN, true, 5);
        sys.TextChanged += (_, _) => _s.Prompt.SystemPrompt = sys.Text;

        return Theme.Card("翻译行为",
            Theme.Field("目标游戏", Theme.Row(10, gameBox,
                Theme.L("写入提示词，用于约束该作品的术语与语气；切换作品时仅需修改此项", 12, Theme.Sub)), 88),
            Theme.Field("源语言", src, 88),
            Theme.Field("目标语言", dst, 88),
            Theme.Field("输入翻译", Theme.Row(10, inSrc, Theme.L("→", 13, Theme.Sub), inDst,
                Theme.L("F9：中文输入 → 英文输出", 12, Theme.Sub)), 88),
            Theme.Field("输出模式", mode, 88),
            modeHint,
            Theme.Field("术语表", glossBtn!, 88),
            new Border { Height = 8 },
            Theme.Field("附加指令", extra, 88),
            Theme.Field("自定义提示词", sys, 88),
            Theme.L("留空时使用内置模板（含分行保持与术语约束）。", 12, Theme.Sub));
    }

    // ---------------- 叠加层 ----------------

    private UIElement BuildOverlayCard()
    {
        var previewHost = new Border
        {
            Width = 680,
            Height = 170,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#101014")!),
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = _preview,
        };
        _preview.Margin = new Thickness(8);
        _preview.HorizontalAlignment = HorizontalAlignment.Left;
        _preview.VerticalAlignment = VerticalAlignment.Top;
        _preview.Width = 664;
        _preview.Height = 154;

        var bg = ColorField(_s.Overlay.BgColor, v => _s.Overlay.BgColor = v);
        var bgOpacity = Theme.Sld(0, 1, _s.Overlay.BgOpacity, v =>
        {
            _s.Overlay.BgOpacity = v;
            RefreshPreview();
        });
        var textColor = ColorField(_s.Overlay.TextColor, v => _s.Overlay.TextColor = v);
        var font = FontField();
        var fontSize = Theme.Sld(10, 48, _s.Overlay.FontSize, v =>
        {
            _s.Overlay.FontSize = v;
            RefreshPreview();
        });
        var padding = Theme.Sld(0, 40, _s.Overlay.Padding, v =>
        {
            _s.Overlay.Padding = v;
            RefreshPreview();
        });
        var radius = Theme.Sld(0, 32, _s.Overlay.CornerRadius, v =>
        {
            _s.Overlay.CornerRadius = v;
            RefreshPreview();
        });
        var borderW = Theme.Sld(0, 6, _s.Overlay.BorderThickness, v =>
        {
            _s.Overlay.BorderThickness = v;
            RefreshPreview();
        });
        var borderColor = ColorField(_s.Overlay.BorderColor, v => _s.Overlay.BorderColor = v);

        var align = new DropDown(new[] { "left", "center", "right" }, _s.Overlay.TextAlign, v =>
        {
            _s.Overlay.TextAlign = v;
            RefreshPreview();
        }, 130);
        var valign = new DropDown(new[] { "center", "top", "bottom" }, _s.Overlay.VerticalAlign, v =>
        {
            _s.Overlay.VerticalAlign = v;
            RefreshPreview();
        }, 130);

        var showStatus = Theme.Chk("显示状态（耗时）", _s.Overlay.ShowStatus, v =>
        {
            _s.Overlay.ShowStatus = v;
            RefreshPreview();
        });
        var shadow = Theme.Chk("文字投影", _s.Overlay.TextShadow, v =>
        {
            _s.Overlay.TextShadow = v;
            RefreshPreview();
        });
        var autoFont = Theme.Chk("字号随区域自适应", _s.Overlay.AutoScaleFont, v =>
        {
            _s.Overlay.AutoScaleFont = v;
            RefreshPreview();
        });

        var overflowLabels = new[] { "向下扩展（默认，字号不变）", "缩字以适配选区" };
        var overflowValues = new[] { "expand", "shrink" };
        int overflowIndex = Math.Max(0, Array.IndexOf(overflowValues, _s.Overlay.OverflowMode));
        var overflow = new DropDown(overflowLabels, overflowLabels[overflowIndex],
            v =>
            {
                int i = Array.IndexOf(overflowLabels, v);
                _s.Overlay.OverflowMode = overflowValues[Math.Max(0, i)];
            }, 240);

        var minFont = new DropDown(new[] { "10", "12", "13", "14", "16", "18" },
            ((int)_s.Overlay.MinFontSize).ToString(),
            v =>
            {
                if (double.TryParse(v, out var d)) _s.Overlay.MinFontSize = d;
            }, 90);
        var clickThrough = Theme.Chk("点击穿透（不拦截游戏输入）", _s.Overlay.ClickThrough, v =>
        {
            _s.Overlay.ClickThrough = v;
        });

        return Theme.Card("文本叠加层",
            previewHost,
            new Border { Height = 10 },
            Theme.Field("背景色", Theme.Row(10, bg, Theme.L("不透明度", 12, Theme.Sub), bgOpacity), 88),
            Theme.Field("文字色", Theme.Row(10, textColor, Theme.L("字号", 12, Theme.Sub), fontSize), 88),
            Theme.Field("边框", Theme.Row(10, borderColor, Theme.L("粗细", 12, Theme.Sub), borderW), 88),
            Theme.Field("字体", font, 88),
            Theme.Field("内边距/圆角", Theme.Row(10, padding, Theme.L("圆角", 12, Theme.Sub), radius), 88),
            Theme.Field("对齐", Theme.Row(10, align, Theme.L("垂直", 12, Theme.Sub), valign), 88),
            Theme.Field("超出选区", Theme.Row(10, overflow,
                Theme.L("缩字下限", 12, Theme.Sub), minFont), 88),
            Theme.Row(18, showStatus, shadow, autoFont, clickThrough));
    }

    private StackPanel ColorField(string initial, Action<string> onChange)
    {
        var box = Theme.T(initial, 100);
        box.FontFamily = Theme.Mono;
        box.Margin = new Thickness(8, 0, 0, 0);

        var swatch = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(3),
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            Background = Theme.BrushFromHex(initial),
            VerticalAlignment = VerticalAlignment.Center,
        };

        box.TextChanged += (_, _) =>
        {
            var norm = ColorUtil.NormalizeHex(box.Text, initial);
            swatch.Background = Theme.BrushFromHex(norm);
            onChange(norm);
            RefreshPreview();
        };

        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(swatch);
        sp.Children.Add(box);
        return sp;
    }

    private DropDown FontField()
    {
        var fonts = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Segoe UI", "Consolas" };
        return new DropDown(fonts, _s.Overlay.FontFamily, v =>
        {
            _s.Overlay.FontFamily = v;
            RefreshPreview();
        }, 200);
    }

    private void RefreshPreview()
    {
        _s.Normalize();
        _preview.Width = 664;
        _preview.Height = 154;
        _preview.ApplyStyle(_s.Overlay, 664, 1.0);
        _preview.SetText("示例：收到，前往 B 点支援，注意埋伏。", "86 ms · deepseek-flash");
    }

    // ---------------- 热键 / 区域 ----------------

    private UIElement BuildHotkeyCard()
    {
        var hint = Theme.L("点击后按下目标按键组合（可含 Ctrl / Alt / Shift 修饰键）；按 Esc 取消录制。", 12, Theme.Sub);

        var regionRow = new StackPanel { Orientation = Orientation.Horizontal };
        regionRow.Children.Add(Theme.Btn("重新选区", () => _ = PickRegion(), false, 96));
        var clear = Theme.Btn("清除", () =>
        {
            _s.Region.HasRegion = false;
            _s.Region.Left = _s.Region.Top = _s.Region.Right = _s.Region.Bottom = 0;
            RefreshRegionLabel();
        }, false, 72);
        clear.Margin = new Thickness(8, 0, 0, 0);
        regionRow.Children.Add(clear);
        regionRow.Children.Add(_regionLabel);
        _regionLabel.Margin = new Thickness(12, 0, 0, 0);

        return Theme.Card("热键与识别区域",
            Theme.Field("翻译热键", Theme.Row(10, _hotkeyTranslateBtn,
                Theme.L("截图 → 识别 → 叠加显示；再次按下隐藏", 12, Theme.Sub)), 88),
            Theme.Field("输入热键", Theme.Row(10, _hotkeyInputBtn,
                Theme.L("以识别区域左上角为锚点弹出输入框", 12, Theme.Sub)), 88),
            hint,
            new Border { Height = 10 },
            Theme.Field("识别区域", regionRow, 88));
    }

    private async Task PickRegion()
    {
        var picked = RegionSelectorWindow.Pick(_s.Region.HasRegion
            ? new RegionPx(_s.Region.Left, _s.Region.Top, _s.Region.Right, _s.Region.Bottom)
            : null);
        await Task.Yield();
        if (picked is { IsUsable: true })
        {
            _s.Region.HasRegion = true;
            _s.Region.Left = picked.Value.Left;
            _s.Region.Top = picked.Value.Top;
            _s.Region.Right = picked.Value.Right;
            _s.Region.Bottom = picked.Value.Bottom;
            RefreshRegionLabel();
        }
    }

    private void RefreshRegionLabel()
    {
        _regionLabel.Text = _s.Region.IsUsable
            ? $"当前：{_s.Region.Width} × {_s.Region.Height} @ ({_s.Region.Left},{_s.Region.Top})"
            : "当前：未设置（按下翻译热键时先打开选区）";
    }

    private void StartCaptureTranslate()
    {
        _capturingHotkey = 1;
        _hotkeyTranslateBtn.Content = "按下按键…";
        Focus();
    }

    private void StartCaptureInput()
    {
        _capturingHotkey = 2;
        _hotkeyInputBtn.Content = "按下按键…";
        Focus();
    }

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (_capturingHotkey == 0) return;

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            _capturingHotkey = 0;
            _hotkeyTranslateBtn.Content = _s.Hotkeys.TranslateText;
            _hotkeyInputBtn.Content = _s.Hotkeys.InputText;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return; // 只按修饰键，等待主键

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        uint mods = 0;
        var m = Keyboard.Modifiers;
        if ((m & ModifierKeys.Control) != 0) mods |= Win32.MOD_CONTROL;
        if ((m & ModifierKeys.Alt) != 0) mods |= Win32.MOD_ALT;
        if ((m & ModifierKeys.Shift) != 0) mods |= Win32.MOD_SHIFT;
        if ((m & ModifierKeys.Windows) != 0) mods |= Win32.MOD_WIN;

        if (_capturingHotkey == 1)
        {
            _s.Hotkeys.TranslateVk = vk;
            _s.Hotkeys.TranslateMods = mods;
            _hotkeyTranslateBtn.Content = KeyName.Format(mods, vk);
        }
        else
        {
            _s.Hotkeys.InputVk = vk;
            _s.Hotkeys.InputMods = mods;
            _hotkeyInputBtn.Content = KeyName.Format(mods, vk);
        }

        _capturingHotkey = 0;
    }

    private UIElement BuildAboutCard()
    {
        var openBtn = Theme.Btn("打开配置目录", () =>
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.DataDir);
                Process.Start(new ProcessStartInfo("explorer.exe", SettingsStore.DataDir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "打开失败");
            }
        }, false, 130);

        return Theme.Card("关于",
            Theme.L($"GameTrans {AppInfo.Version} · 独立 64 位进程 · 无需管理员权限 · 仅调用 DeepSeek API", 12, Theme.Sub),
            Theme.L("快捷键通过 RegisterHotKey 注册，不使用低级键盘钩子；叠加层点击穿透并已从屏幕截图中排除。", 12, Theme.Sub),
            new Border { Height = 10 },
            openBtn);
    }

    // ---------------- 保存 ----------------

    private void Save()
    {
        _s.Normalize();
        if (string.IsNullOrWhiteSpace(_s.Prompt.TargetLanguage)) _s.Prompt.TargetLanguage = "中文（简体）";
        SettingsStore.SaveApiKey(_s, _keyBox.Password);
        SettingsStore.Save(_s);
        _onSave(_s);
        Close();
    }
}
