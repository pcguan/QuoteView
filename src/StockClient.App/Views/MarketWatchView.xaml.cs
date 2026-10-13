using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using StockClient.App.ViewModels;
using StockClient.Core.Contracts;
using StockClient.Core.MarketWatch;

namespace StockClient.App.Views;

public partial class MarketWatchView : UserControl
{
    private MarketWatchViewModel? _vm;
    private Window? _window;
    private string? _selectedCode;
    public event Action<Contract>? OpenContract;

    public MarketWatchView()
    {
        InitializeComponent();
        Column("代码", nameof(MarketQuote.Code), 95);
        Column("名称", nameof(MarketQuote.Name), 120);
        Column("市场", nameof(MarketQuote.MarketName), 65);
        Column("上市板块", nameof(MarketQuote.BoardName), 85);
        foreach (var field in MarketFields.All)
        {
            var col = Column(field.Name, field.Key, field.Name.Length > 8 ? 140 : 110, field.Format);
            if (field.Key is nameof(MarketQuote.Percent) or nameof(MarketQuote.Change) or nameof(MarketQuote.Speed)
                or nameof(MarketQuote.MainInflow) or nameof(MarketQuote.SuperInflow) or nameof(MarketQuote.BigInflow)
                or nameof(MarketQuote.MidInflow) or nameof(MarketQuote.SmallInflow))
            {
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
                style.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                    new Binding(field.Key) { Converter = (IValueConverter)FindResource("SignToBrush") }));
                col.ElementStyle = style;
            }
        }
        Column("行业", nameof(MarketQuote.Industry), 130);
        Column("地区", nameof(MarketQuote.Region), 100);
        Column("概念", nameof(MarketQuote.Concepts), 280);
        Column("上市日期", nameof(MarketQuote.ListedOn), 105);
        Column("行情时间(北京时间)", nameof(MarketQuote.TimeDisplay), 170).SortMemberPath = nameof(MarketQuote.QuoteTime);
        Loaded += (_, _) =>
        {
            if (_vm is null)
            {
                _vm = new MarketWatchViewModel(Dispatcher);
                DataContext = _vm;
                _vm.ResultsChanging += () => _selectedCode = (QuoteGrid.SelectedItem as MarketQuote)?.Code;
                _vm.ResultsChanged += RestoreSelection;
            }
            _window = Window.GetWindow(this);
            if (_window is not null) _window.StateChanged += Window_StateChanged;
            UpdateActive();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null) _window.StateChanged -= Window_StateChanged;
            _vm?.SetActive(false);
        };
        IsVisibleChanged += (_, _) => UpdateActive();
    }

    private DataGridTextColumn Column(string title, string property, double width, string? format = null)
    {
        var column = new DataGridTextColumn
        {
            Header = title, SortMemberPath = property, Width = width,
            Binding = new Binding(property) { StringFormat = format is null ? null : "{0:" + format + "}", TargetNullValue = "—" },
        };
        QuoteGrid.Columns.Add(column);
        if (format is not null)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            column.ElementStyle = style;
        }
        return column;
    }

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateActive();
    private void UpdateActive() => _vm?.SetActive(IsLoaded && IsVisible && _window?.WindowState != WindowState.Minimized);

    private void RestoreSelection()
    {
        if (_vm is null) return;
        QuoteGrid.SelectedItem = _vm.Rows.FirstOrDefault(r => r.Code == _selectedCode);
        foreach (var column in QuoteGrid.Columns)
            column.SortDirection = column.SortMemberPath == _vm.SortKey
                ? (_vm.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending) : null;
    }

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (_vm is null) return;
        var descending = _vm.SortKey == e.Column.SortMemberPath && !_vm.Descending;
        _vm.Sort(e.Column.SortMemberPath, descending);
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(QuoteGrid, source) is DataGridRow { Item: MarketQuote row })
            OpenContract?.Invoke(row.Contract);
    }

    private void Value_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm?.AddConditionCommand.Execute(null); e.Handled = true; }
    }

    public void Dispose() => _vm?.Dispose();
}
