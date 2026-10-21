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
        Width = 640;
        Height = 486;
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

        var statusCard = Theme.Card("运行状态",
            StatusRow("模型接口", _apiValue),
            _apiHint,
            new Border { Height = 8 },
            StatusRow("识别区域", _regionValue),
            new Border { Height = 8 },
            StatusRow("快捷键", _hotkeyValue),
            new Border { Height = 8 },
            StatusRow("术语表", _glossaryValue));

        var steps = Theme.Card("开始使用",
            Theme.L("1. 在设置中配置 DeepSeek API Key（仅需一次）", 12, Theme.Sub),
            Theme.L("2. 框选游戏内聊天或字幕区域，作为识别范围", 12, Theme.Sub),
            Theme.L("3. 游戏中按 F8 翻译画面；按 F9 将中文输入翻译为英文", 12, Theme.Sub));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                Theme.Btn("设置", _onOpenSettings, false, 88),
                Theme.Btn("术语表", _onOpenGlossary, false, 96),
                Theme.Btn("设置识别区域", _onPickRegion, false, 132),
                Theme.Btn("开始使用", Hide, true, 120),
            },
        };

        _showOnStartup = new CheckBox
        {
            Content = "启动时显示本页",
            IsChecked = s.Behavior.ShowStartupWindow,
            FontFamily = Theme.Font,
            FontSize = 12,
            Foreground = Theme.Sub,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _showOnStartup.Checked += (_, _) => _onShowOnStartupChanged(true);
        _showOnStartup.Unchecked += (_, _) => _onShowOnStartupChanged(false);

        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(_showOnStartup);
        var tip = Theme.L("关闭本页后程序驻留系统托盘；双击托盘图标可再次打开", 12, Theme.Sub);
        tip.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(tip);

        var body = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 0) };
        body.Children.Add(statusCard);
        body.Children.Add(new Border { Height = 12 });
        body.Children.Add(steps);
        body.Children.Add(buttons);
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

    private static StackPanel StatusRow(string label, TextBlock value)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var lb = Theme.L(label, 12, Theme.Sub);
        lb.Width = 72;
        value.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(lb);
        sp.Children.Add(value);
        return sp;
    }

    public void Refresh(AppSettings s)
    {
        bool hasKey = !string.IsNullOrEmpty(s.Api.ApiKeyProtected);
        _apiValue.Text = hasKey ? $"已配置（{s.Api.Model}）" : "未配置";
        _apiValue.Foreground = hasKey ? new SolidColorBrush(Color.FromRgb(0x8B, 0xE0, 0x9E))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x66));
        _apiHint.Text = hasKey
            ? $"目标语言 {s.Prompt.TargetLanguage}；F9 输入翻译 {s.Prompt.InputSourceLanguage} → {s.Prompt.InputTargetLanguage}"
            : "尚未配置，请在「设置」中填写 DeepSeek API Key";

        bool hasRegion = s.Region.IsUsable;
        _regionValue.Text = hasRegion
            ? $"已设置：{s.Region.Width} × {s.Region.Height} @ ({s.Region.Left},{s.Region.Top})"
            : "未设置";
        _regionValue.Foreground = hasRegion ? new SolidColorBrush(Color.FromRgb(0x8B, 0xE0, 0x9E))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x66));

        _hotkeyValue.Text = $"{s.Hotkeys.TranslateText} 画面翻译 / {s.Hotkeys.InputText} 输入翻译";

        int n = s.Prompt.Glossary?.Count(g => !string.IsNullOrWhiteSpace(g.Term)) ?? 0;
        _glossaryValue.Text = n == 0 ? "未配置（可在「术语表」中添加）" : $"{n} 条";
    }

    public void ShowAgain()
    {
        Show();
        Activate();
    }
}
