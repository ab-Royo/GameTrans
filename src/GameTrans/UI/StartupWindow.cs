using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameTrans.Core;

namespace GameTrans.UI;

/// <summary>启动页：告诉用户当前状态（API/选区/热键/黑话表），并给出下一步入口。</summary>
public sealed class StartupWindow : Window
{
    private readonly Action _onPickRegion;
    private readonly Action _onOpenGlossary;
    private readonly Action _onOpenSettings;
    private readonly Action<bool> _onShowOnStartupChanged;

    private readonly TextBlock _apiValue;
    private readonly TextBlock _apiHint;
    private readonly TextBlock _regionValue;
    private readonly TextBlock _hotkeyValue;
    private readonly TextBlock _glossaryValue;
    private readonly CheckBox _showOnStartup;

    public StartupWindow(AppSettings s,
        Action onPickRegion, Action onOpenGlossary, Action onOpenSettings,
        Action<bool> onShowOnStartupChanged)
    {
        _onPickRegion = onPickRegion;
        _onOpenGlossary = onOpenGlossary;
        _onOpenSettings = onOpenSettings;
        _onShowOnStartupChanged = onShowOnStartupChanged;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Width = 660;
        Height = 548;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Title = "GameTrans";
        FontFamily = Theme.Font;
        Foreground = Theme.Text;

        _apiValue = Theme.L("", 13, Theme.Text, true);
        _apiHint = Theme.L("", 12, Theme.Sub);
        _regionValue = Theme.L("", 13, Theme.Text, true);
        _hotkeyValue = Theme.L("", 13, Theme.Text);
        _glossaryValue = Theme.L("", 13, Theme.Text, true);

        var header = new Grid { Height = 64 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(22, 16, 0, 0) };
        titleStack.Children.Add(Theme.L($"GameTrans {AppInfo.Version}", 22, Theme.Text, true));
        titleStack.Children.Add(Theme.L(AppInfo.Tagline, 12, Theme.Sub));
        Grid.SetColumn(titleStack, 0);
        header.Children.Add(titleStack);

        var close = new Button
        {
            Content = "✕",
            Width = 34,
            Height = 28,
            Margin = new Thickness(0, 12, 12, 0),
            FontSize = 14,
            Foreground = Theme.Sub,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        close.Click += (_, _) => Hide();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        var statusCard = Theme.Card(Loc.T("运行状态", "Status"),
            StatusRow(Loc.T("模型接口", "Model API"), _apiValue),
            _apiHint,
            new Border { Height = 8 },
            StatusRow(Loc.T("识别区域", "Capture region"), _regionValue),
            new Border { Height = 8 },
            StatusRow(Loc.T("快捷键", "Hotkeys"), _hotkeyValue),
            new Border { Height = 8 },
            StatusRow(Loc.T("术语表", "Glossary"), _glossaryValue));

        var steps = Theme.Card(Loc.T("开始使用", "Getting started"),
            Theme.L(Loc.T("1. 在设置中配置 DeepSeek API Key（仅需一次）",
                "1. Add your DeepSeek API key in Settings (once)"), 12, Theme.Sub),
            Theme.L(Loc.T("2. 框选游戏内聊天或字幕区域，作为识别范围",
                "2. Select the in-game chat or subtitle area as the capture region"), 12, Theme.Sub),
            Theme.L(Loc.T("3. 游戏中按 F8 翻译画面；按 F9 翻译输入文本",
                "3. In game, press F8 to translate the screen; F9 to translate typed text"), 12, Theme.Sub));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                Theme.Btn(Loc.T("设置", "Settings"), _onOpenSettings, false, 88),
                Theme.Btn(Loc.T("术语表", "Glossary"), _onOpenGlossary, false, 96),
                Theme.Btn(Loc.T("设置识别区域", "Select region"), _onPickRegion, false, 132),
                Theme.Btn(Loc.T("开始使用", "Start"), Hide, true, 120),
            },
        };

        _showOnStartup = new CheckBox
        {
            Content = Loc.T("启动时显示本页", "Show this page on startup"),
            IsChecked = s.Behavior.ShowStartupWindow,
            FontFamily = Theme.Font,
            FontSize = 12,
            Foreground = Theme.Sub,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _showOnStartup.Checked += (_, _) => _onShowOnStartupChanged(true);
        _showOnStartup.Unchecked += (_, _) => _onShowOnStartupChanged(false);

        var footer = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(_showOnStartup);
        var tip = Theme.L(Loc.T("关闭本页后程序驻留系统托盘；双击托盘图标可再次打开",
            "The app keeps running in the tray after closing; double-click the tray icon to reopen"), 12, Theme.Sub);
        tip.Margin = new Thickness(0, 4, 0, 0);
        footer.Children.Add(tip);

        var body = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 0) };
        body.Children.Add(statusCard);
        body.Children.Add(new Border { Height = 12 });
        body.Children.Add(steps);
        body.Children.Add(buttons);
        body.Children.Add(BuildCommunityRow());
        body.Children.Add(footer);

        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(252, 21, 22, 26)),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3A3D47")!),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children =
                {
                    header,
                    new Border
                    {
                        Height = 1,
                        Background = Theme.Line,
                        Margin = new Thickness(18, 0, 18, 0),
                    },
                    new Border { Padding = new Thickness(18, 14, 18, 16), Child = body },
                },
            },
        };

        Content = root;
        Refresh(s);
        Loaded += (_, _) => Log.Info(
            $"启动页已显示 {ActualWidth:F0}x{ActualHeight:F0} at ({Left:F0},{Top:F0}) visible={IsVisible}");
    }

    /// <summary>首页底部的项目 / 社区入口：GitHub、QQ 群（点击复制群号）、Bilibili。</summary>
    private StackPanel BuildCommunityRow()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 12, 0, 0),
        };
        var caption = Theme.L(Loc.T("社区：", "Community:"), 12, Theme.Sub);
        caption.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(caption);
        row.Children.Add(Theme.Link("GitHub ↗", AppLinks.Repository));
        row.Children.Add(new Border { Width = 16 });
        row.Children.Add(Theme.LinkAction(
            Loc.T("QQ 群 " + AppLinks.QQGroup + "（点击复制）", "QQ group " + AppLinks.QQGroup + " (click to copy)"),
            CopyQQGroup, tooltip: AppLinks.QQGroup));
        row.Children.Add(new Border { Width = 16 });
        row.Children.Add(Theme.Link(Loc.T("Bilibili 空间 ↗", "Bilibili ↗"), AppLinks.Bilibili));
        return row;
    }

    private static void CopyQQGroup()
    {
        bool ok = AppLinks.CopyQQGroup();
        ToastWindow.ShowToast(ok
                ? Loc.T("QQ 群号已复制：" + AppLinks.QQGroup, "QQ group number copied: " + AppLinks.QQGroup)
                : Loc.T("剪贴板被占用，未能复制群号", "Clipboard busy; group number was not copied"),
            !ok);
    }

    private static StackPanel StatusRow(string label, TextBlock value)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var lb = Theme.L(label, 12, Theme.Sub);
        lb.Width = Loc.IsEnglish ? 92 : 72;
        value.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(lb);
        sp.Children.Add(value);
        return sp;
    }

    public void Refresh(AppSettings s)
    {
        bool hasKey = !string.IsNullOrEmpty(s.Api.ApiKeyProtected);
        var site = ApiSites.Resolve(s.Api.Site);
        _apiValue.Text = hasKey
            ? Loc.F("已配置（{0} · {1}）", "Configured ({0} · {1})", s.Api.Model, site.Display)
            : Loc.T("未配置", "Not configured");
        _apiValue.Foreground = hasKey ? new SolidColorBrush(Color.FromRgb(0x8B, 0xE0, 0x9E))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x66));
        _apiHint.Text = hasKey
            ? Loc.F("目标语言 {0}；F9 输入翻译 {1} → {2}",
                "Target language {0}; F9 input translation {1} → {2}",
                Languages.Display(s.Prompt.TargetLanguage),
                Languages.Display(s.Prompt.InputSourceLanguage),
                Languages.Display(s.Prompt.InputTargetLanguage))
            : Loc.T("尚未配置，请在「设置」中填写 DeepSeek API Key",
                "Not configured yet. Add your DeepSeek API key in Settings.");

        bool hasRegion = s.Region.IsUsable;
        _regionValue.Text = hasRegion
            ? Loc.F("已设置：{0} × {1} @ ({2},{3})", "Set: {0} × {1} @ ({2},{3})",
                s.Region.Width, s.Region.Height, s.Region.Left, s.Region.Top)
            : Loc.T("未设置", "Not set");
        _regionValue.Foreground = hasRegion ? new SolidColorBrush(Color.FromRgb(0x8B, 0xE0, 0x9E))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x66));

        _hotkeyValue.Text = Loc.F("{0} 画面翻译 / {1} 输入翻译",
            "{0} screen translation / {1} input translation",
            s.Hotkeys.TranslateText, s.Hotkeys.InputText);

        int n = s.Prompt.Glossary?.Count(g => !string.IsNullOrWhiteSpace(g.Term)) ?? 0;
        _glossaryValue.Text = n == 0
            ? Loc.T("未配置（可在「术语表」中添加）", "Not configured (add entries in Glossary)")
            : Loc.F("{0} 条", "{0} entries", n);
    }

    public void ShowAgain()
    {
        Show();
        Activate();
    }
}
