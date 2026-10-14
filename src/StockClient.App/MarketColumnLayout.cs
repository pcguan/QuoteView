using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StockClient.Core.Groups;

namespace StockClient.App;

/// <summary>Machine-local layout shared by the market tab and standalone window.</summary>
internal sealed class MarketColumnLayout : IDisposable
{
    private readonly DataGrid _grid;
    private readonly string _key;
    private readonly DispatcherTimer _timer;
    private readonly List<QuoteColumnState> _defaults;
    private readonly List<(DataGridColumn Column, DependencyPropertyDescriptor Descriptor)> _watches = new();
    private bool _applying, _dirty;

    public MarketColumnLayout(DataGrid grid, string key = "market")
    {
        _grid = grid;
        _key = key;
        _defaults = Snapshot();
        _timer = new DispatcherTimer(DispatcherPriority.Background, grid.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) => Flush();
        Apply(AppPrefs.GetColumns(_key));
        foreach (var column in grid.Columns)
        foreach (var property in new[] { DataGridColumn.WidthProperty, DataGridColumn.VisibilityProperty, DataGridColumn.DisplayIndexProperty, DataGridColumn.HeaderProperty })
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(property, typeof(DataGridColumn));
            if (descriptor is null) continue;
            descriptor.AddValueChanged(column, Changed);
            _watches.Add((column, descriptor));
        }
        AppPrefs.ColumnsChanged += Restore;
    }

    private void Changed(object? sender, EventArgs e)
    {
        if (_applying) return;
        _dirty = true;
        _timer.Stop();
        _timer.Start();
    }

    private List<QuoteColumnState> Snapshot() => _grid.Columns.Select(c => new QuoteColumnState
    {
        Key = c.SortMemberPath, Title = c.Header as string, Order = c.DisplayIndex,
        Width = c.Width.IsAbsolute ? c.Width.Value : c.ActualWidth,
        Visible = c.Visibility == Visibility.Visible,
    }).ToList();

    private void Restore(string key)
    {
        if (key != _key) return;
        _timer.Stop(); _dirty = false;
        Apply(AppPrefs.GetColumns(_key));
    }

    private void Apply(IReadOnlyList<QuoteColumnState> saved)
    {
        _applying = true;
        try
        {
            var byKey = saved.Where(s => !string.IsNullOrEmpty(s.Key)).GroupBy(s => s.Key)
                .ToDictionary(g => g.Key, g => g.First());
            foreach (var c in _grid.Columns)
            {
                if (!byKey.TryGetValue(c.SortMemberPath, out var state)) continue;
                if (!string.IsNullOrWhiteSpace(state.Title)) c.Header = state.Title;
                if (double.IsFinite(state.Width) && state.Width >= 40) c.Width = Math.Min(2000, state.Width);
                c.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
            }
            var index = 0;
            foreach (var state in byKey.Values.OrderBy(s => s.Order))
            {
                var c = _grid.Columns.FirstOrDefault(c => c.SortMemberPath == state.Key);
                if (c is not null) c.DisplayIndex = index++;
            }
        }
        finally { _applying = false; }
    }

    public void Reset()
    {
        Apply(_defaults);
        _dirty = true;
        Flush();
    }

    public void Flush()
    {
        _timer.Stop();
        if (!_dirty) return;
        _dirty = false;
        AppPrefs.SaveColumns(_key, Snapshot());
    }

    public void Dispose()
    {
        Flush();
        AppPrefs.ColumnsChanged -= Restore;
        foreach (var (column, descriptor) in _watches) descriptor.RemoveValueChanged(column, Changed);
        _watches.Clear();
    }
}
