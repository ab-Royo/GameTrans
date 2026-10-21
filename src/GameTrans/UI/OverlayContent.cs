using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using GameTrans.Core;

namespace GameTrans.UI;

/// <summary>
/// 叠加层内容：背景色/透明度/字色/字体/字号/内边距/圆角/边框/对齐 全部可调。
/// 文本不再用 Viewbox 等比缩放（那会把长译文压成看不清的小字），
/// 改为由调用方按 FormattedText 实测高度来决定窗口大小。
/// </summary>
public sealed class OverlayContent : Border
{
    private readonly TextBlock _text;
    private readonly TextBlock _status;
    private readonly Grid _grid;
    private double _innerWidth = 400;

    public OverlayContent()
    {
        _text = new TextBlock
        {
            FontFamily = Theme.Font,
            FontSize = 20,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Left,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        _status = new TextBlock
        {
            FontFamily = Theme.Font,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _grid = new Grid();
        _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_text, 0);
        Grid.SetRow(_status, 1);
        _grid.Children.Add(_text);
        _grid.Children.Add(_status);

        Child = _grid;
        Padding = new Thickness(10);
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Background = new SolidColorBrush(Color.FromArgb(160, 11, 11, 15));
        BorderBrush = Theme.Accent;
    }

    public double StatusHeight => _status.Visibility == Visibility.Visible ? 18 : 0;

    public void ApplyStyle(OverlayOptions o, double innerWidth, double fontScale = 1.0)
    {
        _innerWidth = Math.Max(32, innerWidth);
        Background = Theme.BrushFromHex(o.BgColor, o.BgOpacity);
        BorderBrush = Theme.BrushFromHex(o.BorderColor, Math.Min(1, o.BgOpacity + 0.25));
        BorderThickness = new Thickness(o.BorderThickness);
        CornerRadius = new CornerRadius(o.CornerRadius);
        Padding = new Thickness(o.Padding);

        _text.FontFamily = new FontFamily(o.FontFamily);
        _text.FontSize = Math.Clamp(o.FontSize * fontScale, 8, 200);
        _text.Foreground = Theme.BrushFromHex(o.TextColor, 1.0);
        _text.TextAlignment = o.TextAlign switch
        {
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
        _text.Width = Math.Max(24, _innerWidth - o.Padding * 2 - o.BorderThickness * 2);
        _text.Effect = o.TextShadow
            ? new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 4,
                ShadowDepth = 0,
                Opacity = 0.9,
            }
            : null;

        _text.VerticalAlignment = o.VerticalAlign switch
        {
            "top" => VerticalAlignment.Top,
            "bottom" => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Center,
        };

        _status.Visibility = o.ShowStatus ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>为指定字号设置文本宽度与字体（用于"扩展/缩字"求解）。</summary>
    public void SetFontSize(double size)
    {
        _text.FontSize = Math.Clamp(size, 6, 200);
    }

    public double FontSize => _text.FontSize;
    public double TextWidth => _text.Width;

    /// <summary>用 FormattedText 实测当前文本在给定宽度下需要的高度（DIP）。</summary>
    public double MeasureTextHeight()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (dpi <= 0) dpi = 1.0;

            var typeface = new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight,
                _text.FontStretch);
            var ft = new FormattedText(
                string.IsNullOrEmpty(_text.Text) ? " " : _text.Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                _text.FontSize,
                Brushes.White,
                dpi)
            {
                MaxTextWidth = Math.Max(16, _text.Width),
                TextAlignment = _text.TextAlignment,
            };
            return Math.Ceiling(ft.Height);
        }
        catch
        {
            return _text.FontSize * 8; // 兜底：按 8 行估
        }
    }

    public void SetText(string? text, string? status = null)
    {
        _text.Text = string.IsNullOrEmpty(text) ? " " : text;
        _status.Text = status ?? "";
    }

    public void SetStatus(string? status)
    {
        _status.Text = status ?? "";
    }
}
