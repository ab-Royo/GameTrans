using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace GameTrans.UI;

/// <summary>
/// 轻量下拉：用 Popup + 深色按钮列表，避免 WPF 默认 ComboBox 在深色主题下发白、文字不可见。
/// </summary>
public sealed class DropDown : Button
{
    private readonly string[] _items;
    private readonly Action<string> _onChange;
    private readonly Popup _popup;
    private readonly StackPanel _list;

    public string Value { get; private set; }

    public DropDown(IEnumerable<string> items, string selected, Action<string> onChange,
        double width = 160)
    {
        _items = items.ToArray();
        _onChange = onChange;
        Value = _items.Contains(selected) ? selected : (_items.FirstOrDefault() ?? "");

        Content = Value + "   ▾";
        Width = width;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        Padding = new Thickness(10, 6, 10, 6);
        FontFamily = Theme.Font;
        FontSize = 13;
        Foreground = Theme.Text;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B1D23")!);
        BorderBrush = Theme.Line;
        BorderThickness = new Thickness(1);
        Cursor = System.Windows.Input.Cursors.Hand;

        _list = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(4) };
        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(250, 30, 32, 39)),
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = _list,
                MinWidth = width,
            },
        };

        RebuildList();
        Click += (_, _) => _popup.IsOpen = !_popup.IsOpen;
    }

    private void RebuildList()
    {
        _list.Children.Clear();
        foreach (var it in _items)
        {
            var b = new Button
            {
                Content = (it == Value ? "✓ " : "    ") + it,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontFamily = Theme.Font,
                FontSize = 13,
                Foreground = it == Value ? Theme.Accent : Theme.Text,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 6, 8, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            var captured = it;
            b.Click += (_, _) =>
            {
                Value = captured;
                Content = Value + "   ▾";
                _popup.IsOpen = false;
                RebuildList();
                _onChange(Value);
            };
            _list.Children.Add(b);
        }
    }
}
