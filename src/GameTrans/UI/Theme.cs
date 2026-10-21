using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameTrans.UI;

internal static class Theme
{
    public static readonly Color BgColor = (Color)ColorConverter.ConvertFromString("#15161A")!;
    public static readonly Color CardColor = (Color)ColorConverter.ConvertFromString("#22242B")!;
    public static readonly Color AccentColor = (Color)ColorConverter.ConvertFromString("#7C6BFF")!;
    public static readonly Color TextColor = (Color)ColorConverter.ConvertFromString("#ECEDF0")!;
    public static readonly Color SubColor = (Color)ColorConverter.ConvertFromString("#9AA0A6")!;

    public static readonly Brush Bg = new SolidColorBrush(BgColor);
    public static readonly Brush CardBg = new SolidColorBrush(CardColor);
    public static readonly Brush Accent = new SolidColorBrush(AccentColor);
    public static readonly Brush Text = new SolidColorBrush(TextColor);
    public static readonly Brush Sub = new SolidColorBrush(SubColor);
    public static readonly Brush Line = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#32353D")!);

    public static readonly FontFamily Font = new("Microsoft YaHei UI, Segoe UI, Microsoft YaHei");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    static Theme()
    {
        Bg.Freeze();
        CardBg.Freeze();
        Accent.Freeze();
        Text.Freeze();
        Sub.Freeze();
        Line.Freeze();
    }

    public static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex.Trim())!;
            return c;
        }
        catch
        {
            return fallback;
        }
    }

    public static Brush BrushFromHex(string? hex, double alpha = 1.0)
    {
        var c = ParseColor(hex, Colors.Black);
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    // ---------------- 控件工厂 ----------------

    public static TextBlock L(string text, double size = 13, Brush? fg = null, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = fg ?? Text,
        FontFamily = Font,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 0),
    };

    public static TextBox T(string text, double width = double.NaN, bool multiline = false, int lines = 1)
    {
        var tb = new TextBox
        {
            Text = text,
            FontFamily = Font,
            FontSize = 13,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B1D23")!),
            Foreground = Text,
            CaretBrush = Text,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (!double.IsNaN(width)) tb.Width = width;
        if (multiline)
        {
            tb.AcceptsReturn = true;
            tb.TextWrapping = TextWrapping.Wrap;
            tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            tb.Height = 20 * lines + 12;
            tb.VerticalContentAlignment = VerticalAlignment.Top;
        }

        return tb;
    }

    public static Button Btn(string text, Action click, bool primary = false, double width = double.NaN)
    {
        var b = new Button
        {
            Content = text,
            FontFamily = Font,
            FontSize = 13,
            Padding = new Thickness(14, 7, 14, 7),
            Background = primary ? Accent : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2C2F37")!),
            Foreground = Text,
            BorderBrush = primary ? Accent : Line,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        if (!double.IsNaN(width)) b.Width = width;
        b.Click += (_, _) => click();
        return b;
    }

    public static CheckBox Chk(string text, bool value, Action<bool> onChange)
    {
        var c = new CheckBox
        {
            Content = text,
            IsChecked = value,
            FontFamily = Font,
            FontSize = 13,
            Foreground = Text,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        c.Checked += (_, _) => onChange(true);
        c.Unchecked += (_, _) => onChange(false);
        return c;
    }

    public static Slider Sld(double min, double max, double val, Action<double> onChange, double width = 200)
    {
        var s = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = val,
            Width = width,
            VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = false,
        };
        s.ValueChanged += (_, e) => onChange(e.NewValue);
        return s;
    }

    public static ComboBox Cmb(IEnumerable<string> items, string selected, Action<string> onChange)
    {
        var cb = new ComboBox
        {
            FontFamily = Font,
            FontSize = 13,
            MinWidth = 160,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B1D23")!),
            Foreground = Text,
        };
        foreach (var it in items) cb.Items.Add(it);
        if (cb.Items.Contains(selected)) cb.SelectedItem = selected;
        else if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is string s) onChange(s);
        };
        return cb;
    }

    public static StackPanel Row(double gap, params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) items[i].SetValue(FrameworkElement.MarginProperty, new Thickness(gap, 0, 0, 0));
            p.Children.Add(items[i]);
        }

        return p;
    }

    public static StackPanel Col(double gap, params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical };
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) items[i].SetValue(FrameworkElement.MarginProperty, new Thickness(0, gap, 0, 0));
            p.Children.Add(items[i]);
        }

        return p;
    }

    public static Border Card(string? title, params UIElement[] items)
    {
        var inner = new StackPanel { Orientation = Orientation.Vertical };
        if (!string.IsNullOrEmpty(title))
        {
            inner.Children.Add(L(title, 14, Text, true));
            inner.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 8, 0, 10) });
        }

        foreach (var it in items) inner.Children.Add(it);

        return new Border
        {
            Background = CardBg,
            CornerRadius = new CornerRadius(8),
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 14),
            Child = inner,
        };
    }

    public static Border Field(string label, UIElement control, double labelWidth = 96)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lb = L(label, 13, Sub);
        Grid.SetColumn(lb, 0);
        Grid.SetColumn(control, 1);
        g.Children.Add(lb);
        g.Children.Add(control);
        return new Border { Child = g };
    }

    public static bool TryParseDouble(string s, out double v) =>
        double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
