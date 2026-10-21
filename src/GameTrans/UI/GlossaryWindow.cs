using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameTrans.Core;

namespace GameTrans.UI;

/// <summary>黑话 / 术语对照表编辑器：手动指定「某个词在游戏里是什么意思」，会注入提示词。</summary>
public sealed class GlossaryWindow : Window
{
    private sealed class Row
    {
        public required Grid Root;
        public required TextBox Term;
        public required TextBox Meaning;
    }

    private readonly List<Row> _rows = new();
    private readonly StackPanel _list;
    private readonly TextBlock _count;
    private readonly TextBox _batch;
    private readonly Border _batchPanel;

    public List<GlossaryEntry>? Result { get; private set; }

    public GlossaryWindow(List<GlossaryEntry>? current)
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        Title = "GameTrans 术语 / 黑话对照表";
        Width = 760;
        Height = 620;
        MinWidth = 620;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Bg;
        FontFamily = Theme.Font;
        Foreground = Theme.Text;
        ShowInTaskbar = true;

        _list = new StackPanel { Orientation = Orientation.Vertical };
        _count = Theme.L( "", 12, Theme.Sub);

        var head = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(Theme.L("术语 / 黑话对照表", 16, Theme.Text, true));
        head.Children.Add(new Border { Height = 8 });
        head.Children.Add(Theme.L(
            "左列为游戏内出现的词条（缩写、黑话、道具或点位名称），右列为该词条在游戏语境中的实际含义。\n" +
            "本表随提示词一并发送给模型；超过 60 项时仅前 60 项参与推理。",
            12, Theme.Sub));

        var listBox = new Border
        {
            Background = Theme.CardBg,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = new ScrollViewer
            {
                Content = _list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };

        var addBtn = Theme.Btn("添加词条", AddRow, false, 110);

        var clearBtn = Theme.Btn("清空", () =>
        {
            _rows.Clear();
            _list.Children.Clear();
            RefreshCount();
        }, false, 72);

        _batch = Theme.T("", double.NaN, true, 4);
        _batch.FontFamily = Theme.Mono;
        var importBtn = Theme.Btn("导入", ImportBatch, true, 110);
        _batchPanel = new Border
        {
            Background = Theme.CardBg,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children =
                {
                    Theme.L("每行一个词条，格式：词条=含义（亦支持 Tab 或 | 分隔）", 12, Theme.Sub),
                    new Border { Height = 6 },
                    _batch,
                    new Border { Height = 8 },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { importBtn },
                    },
                },
            },
        };

        var batchBtn = Theme.Btn("批量导入", () => _batchPanel.Visibility =
            _batchPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible, false, 96);

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
                Children = { _count, cancelBtn, saveBtn },
            },
        };
        _count.Margin = new Thickness(0, 0, 14, 0);
        _count.VerticalAlignment = VerticalAlignment.Center;

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
            Children = { addBtn, batchBtn, clearBtn },
        };

        // DockPanel：工具条/批量面板贴顶，列表占满剩余高度
        var main = new DockPanel();
        DockPanel.SetDock(tools, Dock.Top);
        DockPanel.SetDock(_batchPanel, Dock.Top);
        main.Children.Add(tools);
        main.Children.Add(_batchPanel);
        main.Children.Add(listBox);
        root.Children.Add(main);
        root.Background = Theme.Bg;
        Content = root;

        foreach (var g in current ?? new List<GlossaryEntry>())
            AddRow(g.Term, g.Meaning);

        if (_rows.Count == 0) AddRow("", "");

        RefreshCount();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void AddRow() => AddRow("", "");

    private void AddRow(string term, string meaning)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var termBox = Theme.T(term, double.NaN);
        termBox.FontFamily = Theme.Mono;
        termBox.ToolTip = "游戏内出现的词条，例如 GG、push、first blood";
        var eq = Theme.L("=", 13, Theme.Sub);
        eq.HorizontalAlignment = HorizontalAlignment.Center;
        var meanBox = Theme.T(meaning, double.NaN);
        meanBox.ToolTip = "该词条在游戏语境中的含义，例如“比赛结束时的致意用语”";

        var del = Theme.Btn("删除", () =>
        {
            _list.Children.Remove(g);
            _rows.RemoveAll(r => ReferenceEquals(r.Root, g));
            RefreshCount();
        }, false, 72);
        del.Margin = new Thickness(8, 0, 0, 0);

        Grid.SetColumn(termBox, 0);
        Grid.SetColumn(eq, 1);
        Grid.SetColumn(meanBox, 2);
        Grid.SetColumn(del, 3);
        g.Children.Add(termBox);
        g.Children.Add(eq);
        g.Children.Add(meanBox);
        g.Children.Add(del);

        _list.Children.Add(g);
        _rows.Add(new Row { Root = g, Term = termBox, Meaning = meanBox });

        termBox.TextChanged += (_, _) => RefreshCount();
        RefreshCount();
    }

    private void RefreshCount()
    {
        int n = _rows.Count(r => !string.IsNullOrWhiteSpace(r.Term.Text));
        _count.Text = $"已填写 {n} 项（上限 60 项参与推理）";
    }

    private void ImportBatch()
    {
        int ok = 0, bad = 0;
        foreach (var raw in (_batch.Text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            string? term = null, meaning = null;
            foreach (var sep in new[] { "=", "\t", "|", "＝" })
            {
                int idx = line.IndexOf(sep, StringComparison.Ordinal);
                if (idx > 0)
                {
                    term = line[..idx].Trim();
                    meaning = line[(idx + sep.Length)..].Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(term))
            {
                bad++;
                continue;
            }

            AddRow(term, meaning ?? "");
            ok++;
        }

        _batch.Text = "";
        _batchPanel.Visibility = Visibility.Collapsed;
        ToastWindow.ShowToast($"已导入 {ok} 项" + (bad > 0 ? $"；{bad} 行格式无效，已跳过" : ""),
            bad > 0);
    }

    private void Save()
    {
        Result = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Term.Text))
            .Select(r => new GlossaryEntry
            {
                Term = r.Term.Text.Trim(),
                Meaning = (r.Meaning.Text ?? "").Trim(),
            })
            .Take(200)
            .ToList();
        Close();
    }

    /// <summary>打开编辑器，返回新列表（取消返回 null）。</summary>
    public static List<GlossaryEntry>? Edit(List<GlossaryEntry>? current, Window? owner = null)
    {
        var w = new GlossaryWindow(current);
        if (owner != null) w.Owner = owner;
        w.ShowDialog();
        return w.Result;
    }
}
