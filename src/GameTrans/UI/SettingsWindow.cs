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

    // 所有可复用状态都由 BuildBody 重建，切换界面语言时整页重建，避免控件被重复挂载。
    private PasswordBox? _keyBox;
    private string _keyPassword = "";
    private OverlayContent? _preview;
    private TextBlock? _testLabel;
    private TextBlock? _regionLabel;
    private Button? _hotkeyTranslateBtn;
    private Button? _hotkeyInputBtn;
    private ScrollViewer? _scroll;
    private TextBox? _baseUrlBox;
    private StackPanel? _siteLinks;

    private bool _siteTouched;
    private bool _saved;
    private readonly string _languageAtOpen;

    private int _capturingHotkey; // 0=无 1=翻译 2=输入

    /// <summary>英文标签更长，标签列适当加宽。</summary>
    private static double LabelW => Loc.IsEnglish ? 122 : 88;

    public SettingsWindow(AppSettings current, Action<AppSettings> onSave)
    {
        _s = Clone(current);
        _onSave = onSave;
        _languageAtOpen = _s.Ui.Language;
        _keyPassword = SettingsStore.ReadApiKey(_s);
        // 已经手工偏离过默认站的配置视为「用户明确选择」，切换界面语言时不再自动跟随
        _siteTouched = _s.Api.Site != ApiSites.DefaultFor(_s.Ui.Language).Code;

        WindowStyle = WindowStyle.SingleBorderWindow;
        Width = 780;
        Height = 830;
        MinWidth = 700;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Bg;
        FontFamily = Theme.Font;
        Foreground = Theme.Text;
        ShowInTaskbar = true;

        PreviewKeyDown += OnPreviewKey;
        PreviewMouseDown += OnPreviewMouseDown;
        Closed += (_, _) =>
        {
            // 取消/关闭而不保存时还原临时切换的界面语言
            if (!_saved) Loc.SetLanguage(_languageAtOpen);
        };

        BuildBody();
    }

    private static StackPanel Gap(double h) =>
        new() { Height = h, Orientation = Orientation.Vertical };

    private static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, AppSettings.JsonOpts),
            AppSettings.JsonOpts) ?? new AppSettings();

    // ---------------- 整体布局 ----------------

    /// <summary>按当前界面语言重建整个设置页（切换语言时就地刷新）。</summary>
    private void BuildBody()
    {
        if (_capturingHotkey != 0) _capturingHotkey = 0;

        Title = Loc.T("GameTrans 设置", "GameTrans Settings");
        double scrollOffset = _scroll?.VerticalOffset ?? 0;

        // 先卸下旧内容，避免旧控件仍挂在逻辑树上
        if (Content != null) Content = null;

        var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(16) };
        root.Children.Add(BuildInterfaceCard());
        root.Children.Add(Gap(12));
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
        _scroll = scroll;

        var saveBtn = Theme.Btn(Loc.T("保存", "Save"), Save, true, 96);
        var cancelBtn = Theme.Btn(Loc.T("取消", "Cancel"), Close, false, 96);
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

        RefreshRegionLabel();
        RefreshPreview();

        Dispatcher.BeginInvoke(new Action(() => scroll.ScrollToVerticalOffset(scrollOffset)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---------------- 界面语言 ----------------

    private UIElement BuildInterfaceCard()
    {
        var labels = new[] { Loc.NativeName(Loc.Chinese), Loc.NativeName(Loc.English) };
        var current = Loc.NativeName(_s.Ui.Language);
        var dd = new DropDown(labels, current, v =>
        {
            var code = string.Equals(v, labels[1], StringComparison.Ordinal) ? Loc.English : Loc.Chinese;
            SelectLanguage(code);
        }, 160);

        var hint = Theme.L(Loc.T("切换后界面立即生效，无需重启。", "Applies immediately; no restart needed."),
            12, Theme.Sub);

        return Theme.Card(Loc.T("界面", "Interface"),
            Theme.Field(Loc.T("界面语言", "Language"), Theme.Row(12, dd, hint), LabelW),
            Theme.L(Loc.T(
                "语言决定全部界面文案；中文界面默认使用 DeepSeek 中国站，其它语言默认使用国际站（可在「模型接口」中修改）。",
                "The language applies to the whole UI. Chinese defaults to DeepSeek China; other languages default to DeepSeek International (changeable under Model API)."),
                12, Theme.Sub));
    }

    /// <summary>切换界面语言（下拉框与自检共用）：更新配置、按需跟随默认站点，并就地重建页面。</summary>
    private void SelectLanguage(string code)
    {
        if (code == _s.Ui.Language) return;

        _s.Ui.Language = code;
        Loc.SetLanguage(code);
        if (!_siteTouched)
        {
            var site = ApiSites.DefaultFor(code);
            _s.Api.Site = site.Code;
            _s.Api.BaseUrl = site.BaseUrl;
        }

        BuildBody();
    }

    /// <summary>测试钩子：按与下拉框相同的流程切换语言。</summary>
    internal void SelectLanguageForTest(string code) => SelectLanguage(Loc.NormalizeLanguage(code));

    // ---------------- 接口 ----------------

    private PasswordBox CreateKeyBox()
    {
        var box = new PasswordBox
        {
            Password = _keyPassword,
            FontFamily = Theme.Mono,
            FontSize = 13,
            PasswordChar = '●',
            Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B1D23")!),
            Foreground = Theme.Text,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
        };
        box.PasswordChanged += (_, _) => _keyPassword = box.Password;
        return box;
    }

    private UIElement BuildApiCard()
    {
        var keyBox = CreateKeyBox();
        _keyBox = keyBox;
        var testLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = Theme.Sub,
            TextWrapping = TextWrapping.Wrap,
        };
        _testLabel = testLabel;

        var keyRow = new Grid();
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(keyBox, 0);
        var testBtn = Theme.Btn(Loc.T("测试连接", "Test connection"), () => _ = TestAsync(), false, 110);
        testBtn.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(testBtn, 1);
        keyRow.Children.Add(keyBox);
        keyRow.Children.Add(testBtn);

        var baseUrlBox = Theme.T("", 380);
        _baseUrlBox = baseUrlBox;
        baseUrlBox.Text = _s.Api.BaseUrl;
        baseUrlBox.TextChanged += (_, _) => _s.Api.BaseUrl = baseUrlBox.Text;

        var model = Theme.T("", 200);
        model.Text = _s.Api.Model;
        model.TextChanged += (_, _) => _s.Api.Model = model.Text;

        var detail = new DropDown(new[] { "high", "original", "low", "auto" }, _s.Api.Detail,
            v => _s.Api.Detail = v, 150);

        var siteLabels = ApiSites.All.Select(s => s.Display).ToArray();
        var site = new DropDown(siteLabels, ApiSites.Resolve(_s.Api.Site).Display, v =>
        {
            var picked = ApiSites.Resolve(ApiSites.CodeFromDisplay(v));
            _siteTouched = true;
            _s.Api.Site = picked.Code;
            _s.Api.BaseUrl = picked.BaseUrl;
            if (_baseUrlBox != null) _baseUrlBox.Text = picked.BaseUrl;
            RefreshSiteLinks();
        }, 210);

        var siteLinks = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(LabelW, 0, 0, 10),
        };
        _siteLinks = siteLinks;
        RefreshSiteLinks();

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

        var note = Theme.L(Loc.T(
            "Base URL 默认使用所选站点预设；模型 deepseek-flash 支持 PNG / JPEG 图像输入。\n" +
            "detail 取 high 或 original 时保留原图像素；取 low 时缩放至 512×512，小字号文本可能损失细节。",
            "Base URL follows the selected site preset; the deepseek-flash model accepts PNG / JPEG images.\n" +
            "detail=high/original keeps the original pixels; low downscales to 512×512 and may lose small text."),
            12, Theme.Sub);

        var siteNote = Theme.L(Loc.T(
            "中国站与国际站共用官方 API 端点 https://api.deepseek.com；此选项决定开放平台入口、文档语言与账号区域，API Key 也按站点对应的账号申请。",
            "Both sites share the official endpoint https://api.deepseek.com. This option selects the open-platform entry, docs language and account region; request the API key on the matching platform."),
            12, Theme.Sub);

        return Theme.Card(Loc.T("模型接口", "Model API"),
            Theme.Field("API Key", keyRow, LabelW),
            testLabel,
            Theme.Field(Loc.T("目标 API", "Target API"), site, LabelW),
            siteLinks,
            siteNote,
            new Border { Height = 6 },
            Theme.Field("Base URL", baseUrlBox, LabelW),
            Theme.Field(Loc.T("模型", "Model"), Theme.Row(10, model, detail), LabelW),
            Theme.Field("max_tokens", Theme.Row(10, maxTokens,
                Theme.L("temperature", 12, Theme.Sub), temp,
                Theme.L(Loc.T("超时(秒)", "Timeout (s)"), 12, Theme.Sub), timeout), LabelW),
            new Border { Height = 6 },
            note);
    }

    /// <summary>按当前目标 API 刷新「开放平台 / 文档 / 官网」跳转链接。</summary>
    private void RefreshSiteLinks()
    {
        if (_siteLinks == null) return;
        _siteLinks.Children.Clear();
        var site = ApiSites.Resolve(_s.Api.Site);

        _siteLinks.Children.Add(Theme.Link(
            Loc.F("开放平台 {0} ↗", "Open platform {0} ↗", site.PlatformHost), site.PlatformUrl));
        _siteLinks.Children.Add(new Border { Width = 14 });
        _siteLinks.Children.Add(Theme.Link(Loc.T("API 文档 ↗", "API docs ↗"), site.DocsUrl));
        _siteLinks.Children.Add(new Border { Width = 14 });
        _siteLinks.Children.Add(Theme.Link(Loc.T("官网 ↗", "Website ↗"), site.WebsiteUrl));
    }

    private async Task TestAsync()
    {
        if (_testLabel == null) return;
        _testLabel.Foreground = Theme.Sub;
        _testLabel.Text = Loc.T("测试中…", "Testing…");
        var probe = Clone(_s);
        probe.Api.TimeoutSeconds = Math.Max(15, probe.Api.TimeoutSeconds);
        var r = await _client.TestAsync(probe, _keyPassword, CancellationToken.None);
        if (_testLabel == null) return;
        if (r.Ok)
        {
            _testLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xE0, 0x9E));
            _testLabel.Text = Loc.F("连接正常 · {0} ms · 返回：{1}", "Connected · {0} ms · reply: {1}",
                r.ElapsedMs, r.Text.Replace("\n", " "));
        }
        else
        {
            _testLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            _testLabel.Text = Loc.T("连接失败：", "Connection failed: ") + r.Error;
        }
    }

    // ---------------- 翻译行为 ----------------

    private UIElement BuildPromptCard()
    {
        var gameBox = Theme.T("", 200);
        gameBox.Text = _s.Prompt.GameName;
        gameBox.TextChanged += (_, _) => _s.Prompt.GameName = gameBox.Text;

        var src = LanguageDrop(_s.Prompt.SourceLanguage, allowAuto: true,
            v => _s.Prompt.SourceLanguage = v, 150);
        var dst = LanguageDrop(_s.Prompt.TargetLanguage, allowAuto: false,
            v => _s.Prompt.TargetLanguage = v, 150);
        var inSrc = LanguageDrop(_s.Prompt.InputSourceLanguage, allowAuto: true,
            v => _s.Prompt.InputSourceLanguage = v, 150);
        var inDst = LanguageDrop(_s.Prompt.InputTargetLanguage, allowAuto: false,
            v => _s.Prompt.InputTargetLanguage = v, 150);

        int GlossaryCount() => _s.Prompt.Glossary?.Count(g => !string.IsNullOrWhiteSpace(g.Term)) ?? 0;
        Button? glossBtn = null;
        glossBtn = Theme.Btn(GlossaryButtonText(GlossaryCount()), () =>
        {
            var edited = GlossaryWindow.Edit(_s.Prompt.Glossary, this);
            if (edited == null) return;
            _s.Prompt.Glossary = edited;
            if (glossBtn != null) glossBtn.Content = GlossaryButtonText(GlossaryCount());
            RefreshPreview();
        }, false, 300);

        var modeValues = new[] { "translated", "bilingual", "ocr" };
        var modeLabels = new[]
        {
            Loc.T("仅译文（默认）", "Translated only (default)"),
            Loc.T("原文 + 译文", "Original + translation"),
            Loc.T("仅识别（不翻译）", "Recognize only (no translation)"),
        };
        var mode = MappedDrop(modeValues, modeLabels, _s.Prompt.OutputMode, v => _s.Prompt.OutputMode = v, 230);

        var modeHint = Theme.L(Loc.T(
            "仅译文：只输出目标语言译文（默认）；原文 + 译文：逐行输出「原文 ⇢ 译文」；仅识别：只转写原文，不做翻译。",
            "Translated only: output the target language only (default); Original + translation: one line per pair; Recognize only: transcribe the original text."),
            12, Theme.Sub);

        var extra = Theme.T(_s.Prompt.ExtraInstruction, double.NaN, true, 3);
        extra.TextChanged += (_, _) => _s.Prompt.ExtraInstruction = extra.Text;

        var sys = Theme.T(_s.Prompt.SystemPrompt, double.NaN, true, 5);
        sys.TextChanged += (_, _) => _s.Prompt.SystemPrompt = sys.Text;

        return Theme.Card(Loc.T("翻译行为", "Translation"),
            Theme.Field(Loc.T("目标游戏", "Target game"), gameBox, LabelW),
            Theme.L(Loc.T("写入提示词，用于约束该作品的术语与语气；切换作品时仅需修改此项",
                "Added to the prompt to constrain terms and tone; change only this when switching games"),
                12, Theme.Sub),
            new Border { Height = 8 },
            Theme.Field(Loc.T("源语言", "Source language"), src, LabelW),
            Theme.Field(Loc.T("目标语言", "Target language"), dst, LabelW),
            Theme.Field(Loc.T("输入翻译", "Input translation"), Theme.Row(10, inSrc,
                Theme.L("→", 13, Theme.Sub), inDst,
                Theme.L(Loc.T("F9：输入源语言 → 目标语言", "F9: source → target"), 12, Theme.Sub)), LabelW),
            Theme.Field(Loc.T("输出模式", "Output mode"), mode, LabelW),
            modeHint,
            Theme.Field(Loc.T("术语表", "Glossary"), glossBtn!, LabelW),
            new Border { Height = 8 },
            Theme.Field(Loc.T("附加指令", "Extra instructions"), extra, LabelW),
            Theme.Field(Loc.T("自定义提示词", "Custom prompt"), sys, LabelW),
            Theme.L(Loc.T("留空时使用内置模板（含分行保持与术语约束）。",
                "Leave empty to use the built-in template (keeps line breaks and glossary constraints)."), 12, Theme.Sub));
    }

    private static string GlossaryButtonText(int count) =>
        Loc.F("编辑术语 / 黑话对照表（{0} 项）", "Edit glossary ({0} entries)", count);

    /// <summary>语言下拉框：显示名随界面语言变化，存储为语言代码。</summary>
    private DropDown LanguageDrop(string currentCode, bool allowAuto, Action<string> onChange, double width)
    {
        var codes = allowAuto ? Languages.SourceCodes : Languages.TargetCodes;
        var labels = codes.Select(Languages.Display).ToArray();
        var current = Languages.Display(currentCode);
        return new DropDown(labels, current, v =>
        {
            var code = Languages.CodeFromDisplay(v);
            if (Array.IndexOf(codes, code) < 0) code = allowAuto ? Languages.Auto : Languages.Chinese;
            onChange(code);
        }, width);
    }

    /// <summary>值 → 本地化标签 的下拉框（内部值保持英文代码）。</summary>
    private static DropDown MappedDrop(string[] values, string[] labels, string current,
        Action<string> onChange, double width)
    {
        int index = Math.Max(0, Array.IndexOf(values, current));
        return new DropDown(labels, labels[index], v =>
        {
            int i = Array.IndexOf(labels, v);
            if (i >= 0) onChange(values[i]);
        }, width);
    }

    // ---------------- 叠加层 ----------------

    private UIElement BuildOverlayCard()
    {
        _preview = new OverlayContent
        {
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Width = 604,
            Height = 154,
        };

        var previewHost = new Border
        {
            Width = 620,
            Height = 170,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#101014")!),
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = _preview,
        };

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

        var alignValues = new[] { "left", "center", "right" };
        var alignLabels = new[]
        {
            Loc.T("左对齐", "Left"), Loc.T("居中", "Center"), Loc.T("右对齐", "Right"),
        };
        var align = MappedDrop(alignValues, alignLabels, _s.Overlay.TextAlign, v =>
        {
            _s.Overlay.TextAlign = v;
            RefreshPreview();
        }, 130);

        var valignValues = new[] { "center", "top", "bottom" };
        var valignLabels = new[]
        {
            Loc.T("居中", "Center"), Loc.T("顶部", "Top"), Loc.T("底部", "Bottom"),
        };
        var valign = MappedDrop(valignValues, valignLabels, _s.Overlay.VerticalAlign, v =>
        {
            _s.Overlay.VerticalAlign = v;
            RefreshPreview();
        }, 130);

        var showStatus = Theme.Chk(Loc.T("显示状态（耗时）", "Show status (latency)"), _s.Overlay.ShowStatus, v =>
        {
            _s.Overlay.ShowStatus = v;
            RefreshPreview();
        });
        var shadow = Theme.Chk(Loc.T("文字投影", "Text shadow"), _s.Overlay.TextShadow, v =>
        {
            _s.Overlay.TextShadow = v;
            RefreshPreview();
        });
        var autoFont = Theme.Chk(Loc.T("字号随区域自适应", "Auto-scale font to region"), _s.Overlay.AutoScaleFont, v =>
        {
            _s.Overlay.AutoScaleFont = v;
            RefreshPreview();
        });

        var overflowValues = new[] { "expand", "shrink" };
        var overflowLabels = new[]
        {
            Loc.T("向下扩展（默认，字号不变）", "Expand downward (default, same font size)"),
            Loc.T("缩字以适配选区", "Shrink text to fit the region"),
        };
        var overflow = MappedDrop(overflowValues, overflowLabels, _s.Overlay.OverflowMode, v =>
        {
            _s.Overlay.OverflowMode = v;
        }, 290);

        var minFont = new DropDown(new[] { "10", "12", "13", "14", "16", "18" },
            ((int)_s.Overlay.MinFontSize).ToString(),
            v =>
            {
                if (double.TryParse(v, out var d)) _s.Overlay.MinFontSize = d;
            }, 90);
        var clickThrough = Theme.Chk(Loc.T("点击穿透（不拦截游戏输入）", "Click-through (game keeps input)"),
            _s.Overlay.ClickThrough, v => _s.Overlay.ClickThrough = v);

        return Theme.Card(Loc.T("文本叠加层", "Text overlay"),
            previewHost,
            new Border { Height = 10 },
            Theme.Field(Loc.T("背景色", "Background"), Theme.Row(10, bg,
                Theme.L(Loc.T("不透明度", "Opacity"), 12, Theme.Sub), bgOpacity), LabelW),
            Theme.Field(Loc.T("文字色", "Text color"), Theme.Row(10, textColor,
                Theme.L(Loc.T("字号", "Font size"), 12, Theme.Sub), fontSize), LabelW),
            Theme.Field(Loc.T("边框", "Border"), Theme.Row(10, borderColor,
                Theme.L(Loc.T("粗细", "Width"), 12, Theme.Sub), borderW), LabelW),
            Theme.Field(Loc.T("字体", "Font"), font, LabelW),
            Theme.Field(Loc.T("内边距/圆角", "Padding / radius"), Theme.Row(10, padding,
                Theme.L(Loc.T("圆角", "Radius"), 12, Theme.Sub), radius), LabelW),
            Theme.Field(Loc.T("对齐", "Alignment"), Theme.Row(10, align,
                Theme.L(Loc.T("垂直", "Vertical"), 12, Theme.Sub), valign), LabelW),
            Theme.Field(Loc.T("超出选区", "Overflow"), Theme.Row(10, overflow,
                Theme.L(Loc.T("缩字下限", "Min font"), 12, Theme.Sub), minFont), LabelW),
            Theme.Row(18, showStatus, shadow),
            Theme.Row(18, autoFont, clickThrough));
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
        if (_preview == null) return;
        _s.Normalize();
        _preview.Width = 604;
        _preview.Height = 154;
        _preview.ApplyStyle(_s.Overlay, 604, 1.0);
        _preview.SetText(Loc.T("示例：收到，前往 B 点支援，注意埋伏。", "Example: Copy that, moving to point B, watch for ambush."),
            Loc.T("86 ms · deepseek-flash", "86 ms · deepseek-flash"));
    }

    // ---------------- 热键 / 区域 ----------------

    private UIElement BuildHotkeyCard()
    {
        _hotkeyTranslateBtn = Theme.Btn(_s.Hotkeys.TranslateText, StartCaptureTranslate, false, 150);
        _hotkeyInputBtn = Theme.Btn(_s.Hotkeys.InputText, StartCaptureInput, false, 150);

        var hint = Theme.L(Loc.T(
            "点击后按下目标按键组合（可含 Ctrl / Alt / Shift 修饰键）；也支持鼠标滚轮按下、鼠标侧键1、鼠标侧键2；按 Esc 取消录制。",
            "Click, then press the desired key combination (Ctrl / Alt / Shift allowed); mouse middle, side 1 and side 2 are supported; Esc cancels."),
            12, Theme.Sub);

        _regionLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = Theme.Sub,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var regionRow = new StackPanel { Orientation = Orientation.Horizontal };
        regionRow.Children.Add(Theme.Btn(Loc.T("重新选区", "Select region"), () => _ = PickRegion(), false, 110));
        var clear = Theme.Btn(Loc.T("清除", "Clear"), () =>
        {
            _s.Region.HasRegion = false;
            _s.Region.Left = _s.Region.Top = _s.Region.Right = _s.Region.Bottom = 0;
            RefreshRegionLabel();
        }, false, 80);
        clear.Margin = new Thickness(8, 0, 0, 0);
        regionRow.Children.Add(clear);
        regionRow.Children.Add(_regionLabel);

        return Theme.Card(Loc.T("热键与识别区域", "Hotkeys & capture region"),
            Theme.Field(Loc.T("翻译热键", "Translate hotkey"), Theme.Row(10, _hotkeyTranslateBtn,
                Theme.L(Loc.T("截图 → 识别 → 叠加显示；再次按下隐藏", "Capture → recognize → overlay; press again to hide"),
                    12, Theme.Sub)), LabelW),
            Theme.Field(Loc.T("输入热键", "Input hotkey"), Theme.Row(10, _hotkeyInputBtn,
                Theme.L(Loc.T("以识别区域左上角为锚点弹出输入框", "Opens the input box anchored to the region"),
                    12, Theme.Sub)), LabelW),
            hint,
            new Border { Height = 10 },
            Theme.Field(Loc.T("识别区域", "Capture region"), regionRow, LabelW));
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
        if (_regionLabel == null) return;
        _regionLabel.Text = _s.Region.IsUsable
            ? Loc.F("当前：{0} × {1} @ ({2},{3})", "Current: {0} × {1} @ ({2},{3})",
                _s.Region.Width, _s.Region.Height, _s.Region.Left, _s.Region.Top)
            : Loc.T("当前：未设置（按下翻译热键时先打开选区）",
                "Current: not set (the picker opens on the translate hotkey)");
    }

    private void StartCaptureTranslate()
    {
        _capturingHotkey = 1;
        if (_hotkeyTranslateBtn != null) _hotkeyTranslateBtn.Content = Loc.T("按下按键…", "Press a key…");
        Focus();
    }

    private void StartCaptureInput()
    {
        _capturingHotkey = 2;
        if (_hotkeyInputBtn != null) _hotkeyInputBtn.Content = Loc.T("按下按键…", "Press a key…");
        Focus();
    }

    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (_capturingHotkey == 0) return;

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            CancelCapture();
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return; // 只按修饰键，等待主键

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        CommitCapture(vk, CurrentMods());
    }

    /// <summary>录制期间接受鼠标中键、侧键1、侧键2（键盘热键无法覆盖的按键）。</summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_capturingHotkey == 0) return;

        uint vk = e.ChangedButton switch
        {
            MouseButton.Middle => Win32.VK_MBUTTON,
            MouseButton.XButton1 => Win32.VK_XBUTTON1,
            MouseButton.XButton2 => Win32.VK_XBUTTON2,
            _ => 0u,
        };
        if (vk == 0) return;

        e.Handled = true;
        CommitCapture(vk, CurrentMods());
    }

    private static uint CurrentMods()
    {
        uint mods = 0;
        var m = Keyboard.Modifiers;
        if ((m & ModifierKeys.Control) != 0) mods |= Win32.MOD_CONTROL;
        if ((m & ModifierKeys.Alt) != 0) mods |= Win32.MOD_ALT;
        if ((m & ModifierKeys.Shift) != 0) mods |= Win32.MOD_SHIFT;
        if ((m & ModifierKeys.Windows) != 0) mods |= Win32.MOD_WIN;
        return mods;
    }

    private void CommitCapture(uint vk, uint mods)
    {
        if (_capturingHotkey == 1)
        {
            _s.Hotkeys.TranslateVk = vk;
            _s.Hotkeys.TranslateMods = mods;
            if (_hotkeyTranslateBtn != null) _hotkeyTranslateBtn.Content = KeyName.Format(mods, vk);
        }
        else
        {
            _s.Hotkeys.InputVk = vk;
            _s.Hotkeys.InputMods = mods;
            if (_hotkeyInputBtn != null) _hotkeyInputBtn.Content = KeyName.Format(mods, vk);
        }

        _capturingHotkey = 0;
    }

    private void CancelCapture()
    {
        _capturingHotkey = 0;
        if (_hotkeyTranslateBtn != null) _hotkeyTranslateBtn.Content = _s.Hotkeys.TranslateText;
        if (_hotkeyInputBtn != null) _hotkeyInputBtn.Content = _s.Hotkeys.InputText;
    }

    private UIElement BuildAboutCard()
    {
        var openBtn = Theme.Btn(Loc.T("打开配置目录", "Open config folder"), () =>
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.DataDir);
                Process.Start(new ProcessStartInfo("explorer.exe", SettingsStore.DataDir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Loc.T("打开失败", "Failed to open"));
            }
        }, false, 140);

        var links = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Theme.Link(Loc.T("项目主页 ↗", "Repository ↗"), AppLinks.Repository),
                new Border { Width = 14 },
                Theme.Link(Loc.T("下载新版本 ↗", "Releases ↗"), AppLinks.Releases),
                new Border { Width = 14 },
                Theme.Link(Loc.T("问题反馈 ↗", "Issues ↗"), AppLinks.Issues),
            },
        };

        var community = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                Theme.LinkAction(
                    Loc.T("QQ 群 " + AppLinks.QQGroup + "（点击复制群号）",
                        "QQ group " + AppLinks.QQGroup + " (click to copy)"),
                    CopyQQGroup, tooltip: AppLinks.QQGroup),
                new Border { Width = 14 },
                Theme.Link(Loc.T("Bilibili 空间 ↗", "Bilibili ↗"), AppLinks.Bilibili),
            },
        };

        return Theme.Card(Loc.T("关于", "About"),
            Theme.L(Loc.F("GameTrans {0} · 独立 64 位进程 · 无需管理员权限 · 仅调用 DeepSeek API",
                "GameTrans {0} · standalone 64-bit process · no admin rights · calls the DeepSeek API only",
                AppInfo.Version), 12, Theme.Sub),
            Theme.L(Loc.T("快捷键通过 RegisterHotKey 注册，不使用低级键盘钩子；叠加层点击穿透并已从屏幕截图中排除。",
                "Hotkeys use RegisterHotKey (no low-level hooks); the overlay is click-through and excluded from screen capture."),
                12, Theme.Sub),
            new Border { Height = 8 },
            links,
            community,
            new Border { Height = 10 },
            openBtn);
    }

    private static void CopyQQGroup()
    {
        bool ok = AppLinks.CopyQQGroup();
        ToastWindow.ShowToast(ok
                ? Loc.T("QQ 群号已复制：" + AppLinks.QQGroup, "QQ group number copied: " + AppLinks.QQGroup)
                : Loc.T("剪贴板被占用，未能复制群号", "Clipboard busy; group number was not copied"),
            !ok);
    }

    // ---------------- 保存 ----------------

    private void Save()
    {
        _s.Normalize();
        if (string.IsNullOrWhiteSpace(_s.Prompt.TargetLanguage)) _s.Prompt.TargetLanguage = Languages.Chinese;
        SettingsStore.SaveApiKey(_s, _keyPassword);
        SettingsStore.Save(_s);
        _saved = true;
        Loc.SetLanguage(_s.Ui.Language);
        _onSave(_s);
        Close();
    }
}
