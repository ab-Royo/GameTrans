using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameTrans.Core;

namespace GameTrans.UI;

/// <summary>
/// 「模拟输入需要管理员权限」提示框：保存设置后弹出，提供「以管理员身份重启」按钮。
/// 不强制提权；用户取消时设置已保存，可继续使用剪贴板流程。
/// </summary>
public sealed class AdminPromptWindow : Window
{
    private bool _restart;

    private AdminPromptWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Title = Loc.T("GameTrans 管理员权限提示", "GameTrans Administrator Rights");
        FontFamily = Theme.Font;
        Foreground = Theme.Text;

        var title = Theme.L(Loc.T("⚠ 模拟输入需要管理员权限", "⚠ Simulated input needs administrator rights"),
            16, new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x66)), true);

        var body = Theme.L(AdminHelper.SimulateInputFullWarning, 13, Theme.Text);
        body.LineHeight = 21;
        body.Margin = new Thickness(0, 10, 0, 0);

        var saved = Theme.L(Loc.T("设置已保存；重启后会以管理员身份运行。",
            "Settings are saved; the app will run elevated after the restart."), 12, Theme.Sub);
        saved.Margin = new Thickness(0, 10, 0, 0);

        var cancel = Theme.Btn(Loc.T("暂不重启", "Not now"), Close, false, 96);
        var restart = Theme.Btn(Loc.T("以管理员身份重启", "Restart as administrator"), () =>
        {
            _restart = true;
            Close();
        }, true, 168);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { cancel, restart },
        };

        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(252, 21, 22, 26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x6B, 0x2E)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(20, 18, 20, 18),
                Children =
                {
                    title,
                    body,
                    saved,
                    buttons,
                },
            },
        };

        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    /// <summary>显示提示；返回用户是否选择「以管理员身份重启」。</summary>
    public static bool ConfirmRestart(Window? owner = null)
    {
        var window = new AdminPromptWindow();
        if (owner is { IsVisible: true }) window.Owner = owner;
        window.ShowDialog();
        return window._restart;
    }
}
