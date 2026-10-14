using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using StockClient.App.ViewModels;
using StockClient.Core.Contracts;
using StockClient.Core.MarketWatch;

namespace StockClient.App.Views;

public partial class BoardWatchView : UserControl, IDisposable
{
    private BoardWatchViewModel? _vm;
    private readonly DispatcherTimer _selectionTimer;
    private MarketColumnLayout? _gainersLayout, _losersLayout;
    private BoardMarketQuote? _selected, _pending;
    private Window? _window;
    private bool _updatingSelection;
    public event Action<Contract>? OpenContract;

    public BoardWatchView()
    {
        InitializeComponent();
        foreach (var grid in new[] { GainersGrid, LosersGrid })
        {
            AddColumn(grid, "板块", nameof(BoardMarketQuote.Name), 135);
            AddColumn(grid, "涨跌幅(%)", nameof(BoardMarketQuote.Percent), 95, "N2", true);
            AddColumn(grid, "上涨家数", nameof(BoardMarketQuote.UpCount), 85, "N0");
            AddColumn(grid, "下跌家数", nameof(BoardMarketQuote.DownCount), 85, "N0");
            AddColumn(grid, "成交额", nameof(BoardMarketQuote.AmountDisplay), 115, "");
            AddColumn(grid, "主力净流入", nameof(BoardMarketQuote.InflowDisplay), 120, "", true);
            AddColumn(grid, "行情时间", nameof(BoardMarketQuote.TimeDisplay), 150);
            grid.MouseLeftButtonUp += (_, e) =>
            {
                if (_pending is null && !Details.IsExpanded && e.OriginalSource is DependencyObject source
                    && ItemsControl.ContainerFromElement(grid, source) is DataGridRow { Item: BoardMarketQuote row })
                    QueueBoard(row);
            };
        }
        _selectionTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(350) };
        _selectionTimer.Tick += (_, _) => ShowPending();
        Members.OpenContract += c => OpenContract?.Invoke(c);
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null) _window.StateChanged += Window_StateChanged;
            if (_gainersLayout is null)
            {
                _gainersLayout = new MarketColumnLayout(GainersGrid, "board-gainers");
                _losersLayout = new MarketColumnLayout(LosersGrid, "board-losers");
                AttachColumns(GainersGrid, _gainersLayout);
                AttachColumns(LosersGrid, _losersLayout);
            }
            UpdateActive();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null) _window.StateChanged -= Window_StateChanged;
            _selectionTimer.Stop();
            _vm?.SetActive(this, false);
            _gainersLayout?.Flush(); _losersLayout?.Flush();
        };
        IsVisibleChanged += (_, _) => UpdateActive();
    }

    public void Initialize(BoardWatchViewModel vm)
    {
        if (_vm is not null) throw new InvalidOperationException("Board view already initialized");
        _vm = vm;
        DataContext = vm;
        vm.RankingsChanging += RankingsChanging;
        vm.RankingsChanged += RestoreSelection;
        UpdateActive();
    }

    private void RankingsChanging() => _updatingSelection = true;

    private void AttachColumns(DataGrid grid, MarketColumnLayout layout)
    {
        var menu = new ContextMenu();
        var item = new MenuItem { Header = "列设置…" };
        item.Click += (_, _) => OpenColumns(grid, layout);
        menu.Items.Add(item);
        ColumnMenu.Attach(grid, menu);
    }

    private void OpenColumns(DataGrid grid, MarketColumnLayout? layout)
    {
        if (layout is null) return;
        new ColumnSettingsWindow(grid, layout.Reset) { Owner = Window.GetWindow(this) }.ShowDialog();
        layout.Flush();
    }
    private void GainersColumns_Click(object sender, RoutedEventArgs e) => OpenColumns(GainersGrid, _gainersLayout);
    private void LosersColumns_Click(object sender, RoutedEventArgs e) => OpenColumns(LosersGrid, _losersLayout);

    private void AddColumn(DataGrid grid, string title, string property, double width, string? format = null, bool signed = false)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, format is null ? TextAlignment.Left : TextAlignment.Right));
        if (signed) style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(
            property == nameof(BoardMarketQuote.InflowDisplay) ? nameof(BoardMarketQuote.MainInflow) : property)
            { Converter = (IValueConverter)FindResource("SignToBrush") }));
        grid.Columns.Add(new DataGridTextColumn { Header = title, SortMemberPath = property, Width = width, MinWidth = 45, ElementStyle = style,
            Binding = new Binding(property) { StringFormat = string.IsNullOrEmpty(format) ? null : "{0:" + format + "}", TargetNullValue = "—" } });
    }

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateActive();
    private void UpdateActive()
    {
        var active = IsLoaded && IsVisible && _window?.WindowState != WindowState.Minimized;
        _vm?.SetActive(this, active);
        if (_selectionTimer is null) return;
        if (active && _pending is not null) _selectionTimer.Start(); else if (!active) _selectionTimer.Stop();
    }

    private void Ranking_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection || ((Selector)sender).SelectedItem is not BoardMarketQuote row) return;
        _updatingSelection = true;
        foreach (var selector in new Selector[] { GainersGrid, LosersGrid, NeutralPicker })
            if (!ReferenceEquals(sender, selector)) selector.SelectedItem = null;
        _updatingSelection = false;
        QueueBoard(row);
    }

    private void QueueBoard(BoardMarketQuote row)
    {
        _pending = row;
        _selectionTimer.Stop();
        // Stop the previous board immediately. Its in-memory snapshot and cooldown survive.
        Details.IsExpanded = false;
        Details.IsEnabled = false;
        Details.Header = $"{row.Name}（{row.Code}）· 正在展开…";
        _selectionTimer.Start();
    }

    private void ShowPending()
    {
        _selectionTimer.Stop();
        if (_pending is not { } row) return;
        _pending = null;
        _selected = row;
        if (_vm is null) return;
        Members.Initialize(_vm.GetMembers(this, row.Board));
        Details.Header = $"{row.Name}（{row.Code}）· 成分股行情";
        Details.IsEnabled = true;
        Details.IsExpanded = true;
    }

    private void RestoreSelection()
    {
        if (_vm is null) return;
        try
        {
            var code = (_pending ?? _selected)?.Code;
            var row = _vm.Gainers.Concat(_vm.Losers).Concat(_vm.Neutral).FirstOrDefault(r => r.Code == code);
            GainersGrid.SelectedItem = row is not null && _vm.Gainers.Contains(row) ? row : null;
            LosersGrid.SelectedItem = row is not null && _vm.Losers.Contains(row) ? row : null;
            NeutralPicker.SelectedItem = row is not null && _vm.Neutral.Contains(row) ? row : null;
            if (row is null)
            {
                _selectionTimer.Stop(); _pending = _selected = null;
                Members.ClearModel(); _vm.ReleaseMembers(this);
                Details.IsExpanded = false; Details.IsEnabled = false;
                Details.Header = "点击板块展开成分股行情";
            }
            else if (_pending is not null) _pending = row;
            else _selected = row;
        }
        finally { _updatingSelection = false; }
    }

    private void Details_Expanded(object sender, RoutedEventArgs e)
    { RanksRow.Height = new GridLength(2, GridUnitType.Star); DetailRow.Height = new GridLength(3, GridUnitType.Star); }
    private void Details_Collapsed(object sender, RoutedEventArgs e)
    { if (DetailRow is not null) DetailRow.Height = GridLength.Auto; }

    public void Dispose()
    {
        _selectionTimer.Stop();
        if (_window is not null) _window.StateChanged -= Window_StateChanged;
        Members.Dispose();
        _gainersLayout?.Dispose(); _losersLayout?.Dispose();
        if (_vm is not null)
        {
            _vm.SetActive(this, false);
            _vm.ReleaseMembers(this);
            _vm.RankingsChanging -= RankingsChanging;
            _vm.RankingsChanged -= RestoreSelection;
        }
    }
}
