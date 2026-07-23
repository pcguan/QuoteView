using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using StockClient.App.ViewModels;
using StockClient.Core.Contracts;
using Wpf.Ui.Controls;

namespace StockClient.App.Views;

public partial class QuotesView : UserControl
{
    public QuotesView()
    {
        InitializeComponent();

        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is QuotesViewModel vm)
                vm.SuggestionsChanged += has => SuggestPopup.IsOpen = has && CodeBox.IsKeyboardFocusWithin;
        };

        // Drag to reorder — groups in the list, contracts in the grid. Vm is read
        // lazily so it doesn't matter that the DataContext isn't set yet. The grid
        // only accepts drags in "原序" (unsorted) state, since a sorted view's order
        // doesn't match the underlying list.
        DragReorder.Enable(GroupList, (from, to) => Vm?.MoveGroup(from, to));
        DragReorder.Enable(QuoteGrid, (from, to) => Vm?.MoveCode(from, to), () => IsManualOrder);
    }

    // Three-state column sort: click cycles 正序 → 反序 → 原序 (the user's manual
    // drag order). "原序" clears the sort so the grid shows Quotes as-ordered, and
    // drag-reorder is re-enabled.
    private string? _sortPath;
    private ListSortDirection? _sortDir;

    /// <summary>True when no column sort is active, i.e. the manual drag order is shown.</summary>
    private bool IsManualOrder => _sortDir is null;

    private void QuoteGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // take over the default two-state sort

        var path = PathOf(e.Column);
        if (string.IsNullOrEmpty(path)) return; // e.g. the delete column

        if (path == _sortPath)
        {
            _sortDir = _sortDir switch
            {
                ListSortDirection.Ascending => ListSortDirection.Descending,
                ListSortDirection.Descending => null, // → 原序
                _ => ListSortDirection.Ascending,
            };
        }
        else
        {
            _sortPath = path;
            _sortDir = ListSortDirection.Ascending;
        }

        if (_sortDir is null) _sortPath = null;

        var view = CollectionViewSource.GetDefaultView(QuoteGrid.ItemsSource);
        if (view is not null)
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                if (_sortPath is not null && _sortDir is { } d)
                    view.SortDescriptions.Add(new SortDescription(_sortPath, d));
            }

        // Reflect state in the header glyph (null clears it → 原序 shows no arrow).
        foreach (var c in QuoteGrid.Columns) c.SortDirection = null;
        e.Column.SortDirection = _sortDir;
    }

    /// <summary>The property to sort by: the column's SortMemberPath, else its binding path.</summary>
    private static string? PathOf(DataGridColumn column) =>
        !string.IsNullOrEmpty(column.SortMemberPath) ? column.SortMemberPath
        : column is DataGridBoundColumn { Binding: Binding b } ? b.Path?.Path
        : null;

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "分组文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "导入分组",
            Content = "导入会替换当前的全部分组，确定继续？",
            PrimaryButtonText = "导入替换",
            CloseButtonText = "取消",
        };
        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        try
        {
            Vm.ImportGroups(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowError("导入失败", ex.Message);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"stock-groups-{DateTime.Now:yyyyMMdd}.json",
            Filter = "分组文件 (*.json)|*.json",
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            Vm.ExportGroups(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowError("导出失败", ex.Message);
        }
    }

    private static void ShowError(string title, string message) =>
        _ = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            CloseButtonText = "关闭",
        }.ShowDialogAsync();

    private QuotesViewModel? Vm => DataContext as QuotesViewModel;

    /// <summary>
    /// Raised with the double-clicked contract's code so the host window (which
    /// owns the repository and K-line client) can open its chart. Forwarded
    /// rather than handled here because this view has neither.
    /// </summary>
    public event Action<string>? OpenKlineRequested;

    private void QuoteGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Resolve the actual row under the pointer: a double-click on the header
        // or empty space must not reopen the last selection.
        var row = ClickedRow(e.OriginalSource as DependencyObject) as QuoteRow;
        StockClient.App.Probe.Log($"QuoteGrid_DoubleClick row={(row is null ? "none" : row.Code)}");

        if (row is not null && !string.IsNullOrWhiteSpace(row.Code))
            OpenKlineRequested?.Invoke(row.Code);
    }

    /// <summary>Walks up from the clicked element to the DataGridRow it belongs to.</summary>
    private static object? ClickedRow(DependencyObject? source)
    {
        while (source is not null and not DataGridRow)
            source = VisualTreeHelper.GetParent(source);

        return (source as DataGridRow)?.Item;
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selecting a group activates it: exactly one group polls at a time.
        if (GroupList.SelectedItem is GroupRow row) Vm?.SetActive(row);
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e) => Vm?.AddGroup();

    /// <summary>
    /// Toggling a group's "加入简洁面板" checkbox. The two-way binding already wrote
    /// the model; persist it, and mark the click handled so it doesn't also select
    /// (and thereby activate) the group.
    /// </summary>
    private void GroupInPanel_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Vm?.SaveConfig();
    }

    private async void RemoveGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GroupRow row }) return;

        // Wpf.Ui's MessageBox, not System.Windows.MessageBox: the system one is
        // the old Win32 chrome and looks nothing like the rest of the window.
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "删除分组",
            Content = $"确定删除分组「{row.Name}」？该分组下的合约会一并移除。",
            PrimaryButtonText = "删除",
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = "取消",
        };

        if (await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary)
            Vm?.RemoveGroup(row);
    }

    /// <summary>
    /// Double-click renames through a Fluent dialog.
    ///
    /// An inline box inside the ListBoxItem was a losing fight over focus: the
    /// item processes the same click and takes focus straight back, so the box
    /// raised LostFocus and closed before it was ever usable.
    /// </summary>
    private async void GroupName_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement { DataContext: GroupRow row }) return;

        e.Handled = true;

        var input = new Wpf.Ui.Controls.TextBox
        {
            Text = row.Name,
            PlaceholderText = "分组名称",
            MinWidth = 260,
        };

        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "重命名分组",
            Content = input,
            PrimaryButtonText = "保存",
            PrimaryButtonAppearance = ControlAppearance.Primary,
            CloseButtonText = "取消",
        };

        input.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        if (await dialog.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        var name = input.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        row.Name = name;
        Vm?.CommitRename();
    }

    private void AddCode_Click(object sender, RoutedEventArgs e) => Vm?.AddCode();

    private bool _columnsAttached;

    private void QuoteGrid_Loaded(object sender, RoutedEventArgs e)
    {
        // Loaded can fire again if the grid re-enters the visual tree; attach once
        // so the persistence listeners aren't stacked.
        if (_columnsAttached || sender is not System.Windows.Controls.DataGrid grid) return;
        _columnsAttached = true;

        // The header right-click opens the tiled column-settings window instead of
        // the old per-column checkbox menu: with ~20 columns that vertical menu
        // outgrew the screen and couldn't scroll.
        var menu = new ContextMenu();
        var settings = new System.Windows.Controls.MenuItem { Header = "列设置…" };
        settings.Click += (_, _) => OpenColumnSettings();
        menu.Items.Add(settings);

        ColumnMenu.Attach(grid, menu);
        if (Vm is { } vm) QuoteColumns.Attach(grid, vm.QuoteColumns, vm.SaveConfig);
    }

    private ColumnSettingsWindow? _columnSettings;

    private void ColumnSettings_Click(object sender, RoutedEventArgs e) => OpenColumnSettings();

    private void OpenColumnSettings()
    {
        if (_columnSettings is { } open)
        {
            open.Activate();
            return;
        }

        _columnSettings = new ColumnSettingsWindow(QuoteGrid) { Owner = Window.GetWindow(this) };
        _columnSettings.Closed += (_, _) => _columnSettings = null;
        _columnSettings.Show();
    }

    /// <summary>
    /// The dropdown is driven by the same repository search the 合约查询 tab uses,
    /// and renders code/name/type the same way. WPF UI's AutoSuggestBox was doing
    /// neither: it re-filtered by ToString() and, since Contract is a record,
    /// printed every property of every candidate into the list.
    /// </summary>
    private void Suggest_Click(object sender, MouseButtonEventArgs e)
    {
        if (SuggestList.SelectedItem is not Contract c)
        {
            // Click may land on the row before selection settles.
            if ((e.OriginalSource as FrameworkElement)?.DataContext is not Contract hit) return;
            c = hit;
        }

        Add(c.Code);
    }

    private void SuggestList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SuggestList.SelectedItem is Contract c)
        {
            Add(c.Code);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SuggestPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void CodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                // Enter takes the top-ranked match, same ordering the list shows.
                SuggestPopup.IsOpen = false;
                Vm?.AddCode();
                e.Handled = true;
                break;

            case Key.Down when SuggestList.Items.Count > 0:
                SuggestPopup.IsOpen = true;
                SuggestList.SelectedIndex = 0;
                SuggestList.Focus();
                e.Handled = true;
                break;

            case Key.Escape:
                SuggestPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Opening and closing is handled here rather than by StaysOpen="False":
    /// that treats the very click that focuses the box as a click outside the
    /// popup, so the list closed again the instant it opened.
    /// </summary>
    private void CodeBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var count = Vm?.Suggestions.Count ?? -1;
        Probe.Log($"GotFocus  suggestions={count} popupWas={SuggestPopup.IsOpen} focus={Probe.Focused()}");

        if (Vm is { Suggestions.Count: > 0 }) SuggestPopup.IsOpen = true;

        Probe.Log($"GotFocus  -> popupNow={SuggestPopup.IsOpen}");
    }

    /// <summary>
    /// Dismiss is driven by clicks, not focus.
    ///
    /// LostKeyboardFocus only fires when something else actually takes keyboard
    /// focus: clicking the group list dismissed the popup, but clicking empty
    /// space in the quote grid moved no focus at all, so the list stayed up.
    /// StaysOpen="False" isn't the answer either — it counts the very click that
    /// focuses the box as an outside click and closes the list as it opens.
    /// </summary>
    private void Root_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var src = (e.OriginalSource as FrameworkElement)?.Name ?? e.OriginalSource?.GetType().Name;
        var inside = e.OriginalSource is DependencyObject dd && IsWithin(dd, CodeBox, SuggestList);
        Probe.Log($"PreviewMouseDown src={src} insideBoxOrList={inside} popupWas={SuggestPopup.IsOpen}");

        // Clicking the box re-opens the list. This can't hang off
        // GotKeyboardFocus: the probe showed focus never leaves CodeBox in the
        // first place (clicking empty grid moves no focus), so returning to it
        // raises no focus event at all.
        if (inside)
        {
            if (!SuggestPopup.IsOpen && Vm is { Suggestions.Count: > 0 })
            {
                SuggestPopup.IsOpen = true;
                Probe.Log("PreviewMouseDown -> popup reopened");
            }

            return;
        }

        if (!SuggestPopup.IsOpen) return;

        SuggestPopup.IsOpen = false;
        Probe.Log("PreviewMouseDown -> popup closed");
    }

    private static bool IsWithin(DependencyObject node, params DependencyObject[] roots)
    {
        for (var n = node; n is not null; n = Parent(n))
        {
            if (roots.Any(r => ReferenceEquals(n, r))) return true;
        }

        return false;
    }

    /// <summary>
    /// Walks out of the popup too: a popup's content lives in its own visual
    /// tree, so VisualTreeHelper alone stops at its root.
    /// </summary>
    private static DependencyObject? Parent(DependencyObject node) =>
        node is FrameworkElement { Parent: { } logical } ? logical
        : node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node)
        : LogicalTreeHelper.GetParent(node);

    private void Add(string code)
    {
        SuggestPopup.IsOpen = false;
        Vm?.AddCode(code);
        CodeBox.Focus();
    }

    private void RemoveCode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QuoteRow row }) Vm?.RemoveCode(row);
    }
}
