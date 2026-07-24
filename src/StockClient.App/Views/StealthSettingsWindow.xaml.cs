using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using StockClient.Core.Groups;

namespace StockClient.App.Views;

/// <summary>
/// One place to configure the stealth panel — row count, opacity, and each
/// field's visibility and colour — replacing the old right-click submenus.
/// Changes apply live (the panel redraws) and persist immediately.
/// </summary>
public partial class StealthSettingsWindow : Window
{
    private static readonly (StealthField Field, string Name)[] FieldNames =
    {
        (StealthField.Code, "合约编码"),
        (StealthField.Name, "合约名称"),
        (StealthField.Price, "最新价"),
        (StealthField.Change, "涨跌额"),
        (StealthField.Percent, "涨跌幅"),
        (StealthField.Open, "今开"),
        (StealthField.High, "最高"),
        (StealthField.Low, "最低"),
        (StealthField.Yesterday, "昨收"),
        (StealthField.Time, "时间"),
        (StealthField.Volume, "成交量"),
        (StealthField.Amount, "成交额"),
        (StealthField.TotalCap, "总市值"),
        (StealthField.FloatCap, "流通市值"),
        (StealthField.TurnoverRate, "换手率"),
        (StealthField.VolumeRatio, "量比"),
        (StealthField.Amplitude, "振幅"),
        (StealthField.AvgPrice, "均价"),
        (StealthField.PeTtm, "市盈TTM"),
        (StealthField.Pb, "市净率"),
    };

    private static readonly (string Name, string Hex)[] Palette =
    {
        ("白", "#FFFFFF"), ("红", "#EF5350"), ("绿", "#26A69A"), ("黄", "#FFC107"),
        ("蓝", "#4C8DFF"), ("灰", "#8B93A3"), ("黑", "#000000"),
    };

    private readonly StealthConfig _config;
    private readonly Action _save;
    private readonly Action _onChanged;

    public StealthSettingsWindow(StealthConfig config, Action save, Action onChanged)
    {
        InitializeComponent();

        _config = config;
        _save = save;
        _onChanged = onChanged;

        // Set initial values BEFORE wiring events so seeding doesn't fire Apply.
        RowsSlider.Value = config.Rows;
        RowsValue.Text = config.Rows.ToString();
        RowGapSlider.Value = config.RowGap;
        RowGapValue.Text = config.RowGap.ToString();
        ShadeSlider.Value = config.Shade;
        ShadeValue.Text = config.Shade.ToString();

        RowsSlider.ValueChanged += (_, e) =>
        {
            var v = (int)e.NewValue;
            RowsValue.Text = v.ToString();
            _config.Rows = v;
            Apply();
        };

        RowGapSlider.ValueChanged += (_, e) =>
        {
            var v = (int)e.NewValue;
            RowGapValue.Text = v.ToString();
            _config.RowGap = v;
            UpdateGapPreview();
            Apply();
        };

        BuildGapPreview();

        ShadeSlider.ValueChanged += (_, e) =>
        {
            var v = (int)e.NewValue;
            ShadeValue.Text = v.ToString();
            _config.Shade = v;
            Apply();
        };

        TrendCheck.IsChecked = config.ShowTrend;
        TrendCheck.Checked += (_, _) => { _config.ShowTrend = true; Apply(); };
        TrendCheck.Unchecked += (_, _) => { _config.ShowTrend = false; Apply(); };

        BuildFields();
    }

    private void BuildFields()
    {
        foreach (var field in _config.Fields)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());                              // name (stretch)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // colour(s)

            var captured = field;

            var check = new CheckBox
            {
                Content = FieldName(field.Field),
                IsChecked = field.Visible,
                Foreground = Frozen("#EDF1F7"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            check.Checked += (_, _) => { captured.Visible = true; Apply(); };
            check.Unchecked += (_, _) =>
            {
                // Keep at least one field visible: an empty line would show nothing.
                if (_config.Fields.Count(f => f.Visible) <= 1)
                {
                    check.IsChecked = true;
                    return;
                }

                captured.Visible = false;
                Apply();
            };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);

            // Signed fields (price/change/percent/open/high/low) get a rise colour
            // and a fall colour; the rest get one colour. Setting both the same is
            // how you'd make a signed field single-colour.
            var colours = new StackPanel { Orientation = Orientation.Horizontal };
            if (StealthFields.IsSigned(field.Field))
            {
                colours.Children.Add(ColorCombo(field.PositiveColor, h => captured.PositiveColor = h, "上涨颜色", 74));
                colours.Children.Add(ColorCombo(field.NegativeColor, h => captured.NegativeColor = h, "下跌颜色", 74, leftMargin: 6));
            }
            else
            {
                colours.Children.Add(ColorCombo(field.Color, h => captured.Color = h, "颜色", 96));
            }

            Grid.SetColumn(colours, 1);
            grid.Children.Add(colours);

            FieldsPanel.Children.Add(grid);
        }
    }

    /// <summary>A palette dropdown that writes the chosen hex back via <paramref name="setColor"/>.</summary>
    private ComboBox ColorCombo(string currentHex, Action<string> setColor, string tooltip, double width, double leftMargin = 0)
    {
        var combo = new ComboBox
        {
            Width = width,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tooltip,
            Margin = new Thickness(leftMargin, 0, 0, 0),
        };

        foreach (var (name, hex) in Palette)
            combo.Items.Add(new ComboBoxItem { Content = ColorItem(name, hex), Tag = hex });

        // Select current before wiring the handler so seeding doesn't fire Apply.
        combo.SelectedIndex = Array.FindIndex(Palette, p =>
            string.Equals(p.Hex, currentHex, StringComparison.OrdinalIgnoreCase));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string hex })
            {
                setColor(hex);
                Apply();
            }
        };

        return combo;
    }

    private static UIElement ColorItem(string name, string hex)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Rectangle
        {
            Width = 12, Height = 12, Fill = Frozen(hex),
            Stroke = Brushes.Gray, StrokeThickness = 1,
            Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void Apply()
    {
        _save();
        _onChanged();
    }

    /// <summary>Sample rows that mirror the panel's layout, for the gap preview.</summary>
    private static readonly (string Code, string Name, string Pct, string Color)[] GapSample =
    {
        ("600519", "贵州茅台", "+5.95%", "#EF5350"),
        ("000651", "格力电器", "+1.58%", "#EF5350"),
        ("920992", "中科美菱", "-3.76%", "#26A69A"),
    };

    private void BuildGapPreview()
    {
        foreach (var (code, name, pct, color) in GapSample)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Cell(code, "#FFFFFF", "Consolas"));
            row.Children.Add(Cell(name, "#FFFFFF", "Microsoft YaHei", 7));
            row.Children.Add(Cell(pct, color, "Consolas", 7));
            GapPreview.Children.Add(row);
        }

        UpdateGapPreview();
    }

    /// <summary>Applies the current gap to the preview, the same way the panel does.</summary>
    private void UpdateGapPreview()
    {
        var gap = _config.RowGap;
        for (var i = 0; i < GapPreview.Children.Count; i++)
            if (GapPreview.Children[i] is FrameworkElement fe)
                fe.Margin = new Thickness(0, i == 0 ? 0 : gap, 0, 0);
    }

    private static TextBlock Cell(string text, string hex, string font, double left = 0) => new()
    {
        Text = text,
        FontSize = 12,
        FontFamily = new FontFamily(font),
        Foreground = Frozen(hex),
        Margin = new Thickness(left, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static string FieldName(StealthField field) =>
        FieldNames.FirstOrDefault(f => f.Field == field).Name is { Length: > 0 } n ? n : field.ToString();

    private static Brush Frozen(string hex)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.White;
        }
    }
}
