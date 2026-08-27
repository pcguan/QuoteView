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
            {
                vm.SuggestionsChanged += has => SuggestPopup.IsOpen = has && CodeBox.IsKeyboardFocusWithin;
                GroupCol.Width = new GridLength(vm.GroupPaneWidth);   // restore the dragged width
                TryAttachColumnPersistence();   // the grid usually loaded first
            }
        };

        // Drag to reorder — groups in the list, contracts in the grid. Vm is read
        // lazily so it doesn't matter that the DataContext isn't set yet. The grid
        // only accepts drags in "原序" (unsorted) state, since a sorted view's order
        // doesn't match the underlying list.
        DragReorder.Enable(GroupList, (from, to) => Vm?.MoveGroup(from, to));
        DragReorder.Enable(QuoteGrid, (from, to) => Vm?.MoveCode(from, to), beforeDrag: (_, from) =>
        {
            // Dragging while a column sort is on used to die SILENTLY — the
            // "reordering randomly stops working" report. Still blocked (a move
            // inside a sorted view has no stable meaning), but now it says so:
            // a self-dismissing toast, not a click-through-killing dialog.
            if (IsManualOrder) return from;

            ShowToast("当前为列排序视图，无法拖拽调整顺序\n再点击排序列的标题（至第三次）恢复原序后即可拖拽");
            return -1;
        });
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

    private DispatcherTimer? _toastTimer;

    /// <summary>
    /// Bottom-right transient notice: non-interactive (hit-testing off), fades
    /// out on its own — feedback for gestures that must be refused, without a
    /// dialog stealing the mouse.
    /// </summary>
    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.BeginAnimation(OpacityProperty, null);
        Toast.Opacity = 1;
        Toast.Visibility = Visibility.Visible;

        if (_toastTimer is null)
        {
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer!.Stop();
                var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350));
                fade.Completed += (_, _) => Toast.Visibility = Visibility.Collapsed;
                Toast.BeginAnimation(OpacityProperty, fade);
            };
        }

        _toastTimer.Stop();
        _toastTimer.Start();
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
    // The weighting toggle behaves like radio buttons: the menu reflects the
    // current mode on open, and clicking either side sets it outright.
    private void AggMenu_Opened(object sender, RoutedEventArgs e)
    {
        AggCapItem.IsChecked = Vm is { AggEqualWeight: false };
        AggEqualItem.IsChecked = Vm is { AggEqualWeight: true };
    }

    private void AggCap_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.AggEqualWeight = false;
    }

    private void AggEqual_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.AggEqualWeight = true;
    }

    /// <summary>Persists the sidebar width once the splitter drag ends.</summary>
    /// <summary>Sets the group sidebar width, for a mid-session settings pull.</summary>
    public void SetGroupPaneWidth(double width) => GroupCol.Width = new GridLength(width);

    /// <summary>Re-applies the saved column layout mid-session — used after an
    /// account-settings pull replaced the layout under a running grid.</summary>
    public void ReapplyColumnLayout()
    {
        if (Vm is { } vm) QuoteColumns.Restore(QuoteGrid, vm.QuoteColumns);
    }

    private void GroupSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (Vm is { } vm) vm.GroupPaneWidth = GroupCol.ActualWidth;
    }

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

    private bool _gridWired;
    private bool _persistenceAttached;

    /// <summary>
    /// Wires column persistence (restore saved layout + watch changes) once BOTH
    /// the grid is loaded AND the view model exists. The two arrive in either
    /// order: since the login pull became async, the grid renders (and Loaded
    /// fires) while Vm is still null — a Loaded-only attach silently never ran,
    /// and every column width/visibility/order change died with the session.
    /// The latch is set only after a successful attach.
    /// </summary>
    private void TryAttachColumnPersistence()
    {
        if (_persistenceAttached || !QuoteGrid.IsLoaded || Vm is not { } vm) return;
        _persistenceAttached = true;

        // The list accessor resolves per call: signing in as a different user
        // swaps the whole config object (ReloadFromStore), and a captured list
        // would leave the watcher writing into the orphaned copy forever.
        QuoteColumns.Attach(QuoteGrid, () => vm.QuoteColumns, vm.SaveConfig);
        UpdateFundFlowActive();
    }

    // On-demand: the EastMoney fund-flow poll runs only while one of its columns
    // is actually visible — no one looking, no extra request.
    private static readonly HashSet<string> FundFlowHeaders =
        new() { "涨速", "主力净流入", "主力占比", "超大单", "大单", "中单", "小单" };

    private void UpdateFundFlowActive() =>
        Vm?.SetFundFlowActive(QuoteGrid.Columns.Any(c =>
            FundFlowHeaders.Contains(c.Header as string ?? "") && c.Visibility == Visibility.Visible));

    private void QuoteGrid_Loaded(object sender, RoutedEventArgs e)
    {
        // Loaded can fire again if the grid re-enters the visual tree; wire once
        // so the menus and watchers aren't stacked.
        if (!_gridWired && sender is System.Windows.Controls.DataGrid grid)
        {
            _gridWired = true;

            // The header right-click opens the tiled column-settings window instead
            // of the old per-column checkbox menu: with ~20 columns that vertical
            // menu outgrew the screen and couldn't scroll.
            var menu = new ContextMenu();
            var settings = new System.Windows.Controls.MenuItem { Header = "列设置…" };
            settings.Click += (_, _) => OpenColumnSettings();
            menu.Items.Add(settings);

            ColumnMenu.Attach(grid, menu);
            AttachRowMenu(grid);

            foreach (var col in grid.Columns.Where(c =>
                FundFlowHeaders.Contains(c.Header as string ?? "")))
                System.ComponentModel.DependencyPropertyDescriptor
                    .FromProperty(DataGridColumn.VisibilityProperty, typeof(DataGridColumn))
                    ?.AddValueChanged(col, (_, _) => UpdateFundFlowActive());

            UpdateFundFlowActive();

            // The remove rail follows the rows: vertical scroll moves them,
            // items changing replaces them, resize changes how many fit.
            grid.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((_, _) => SyncRemoveRail()));
            grid.ItemContainerGenerator.ItemsChanged += (_, _) =>
                Dispatcher.BeginInvoke(new Action(SyncRemoveRail), DispatcherPriority.Loaded);
            grid.SizeChanged += (_, _) => SyncRemoveRail();
            Dispatcher.BeginInvoke(new Action(SyncRemoveRail), DispatcherPriority.Loaded);
        }

        TryAttachColumnPersistence();
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

    private async void RemoveCode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QuoteRow row } && Vm is { } vm)
            await RemoveWithConfirmAsync(vm, new[] { row });
    }

    // ---- floating per-row remove rail -------------------------------------
    //
    // The ✕ used to be a real trailing column — parked at the END of the
    // scrollable width, so with many columns visible deleting a row meant
    // scrolling all the way right first. These buttons live OUTSIDE the scroll
    // content, pinned to the viewport's right edge (same idea as the header
    // gear), repositioned per visible row whenever scroll/rows/size change.

    private void SyncRemoveRail()
    {
        if (Vm is null || !QuoteGrid.IsLoaded) return;

        RemoveRail.Children.Clear();
        var headerBottom = FindDescendant<
            System.Windows.Controls.Primitives.DataGridColumnHeadersPresenter>(QuoteGrid)
            ?.ActualHeight ?? 0;

        foreach (var item in QuoteGrid.Items)
        {
            if (QuoteGrid.ItemContainerGenerator.ContainerFromItem(item)
                is not DataGridRow row || !row.IsVisible) continue;   // virtualized away

            double y;
            try { y = row.TranslatePoint(default, QuoteGrid).Y; }
            catch (InvalidOperationException) { continue; }   // detached mid-layout

            if (y < headerBottom - 1 || y + row.ActualHeight > QuoteGrid.ActualHeight + 1)
                continue;   // scrolled (partially) out of the viewport

            var button = new Wpf.Ui.Controls.Button
            {
                Height = 24,
                Width = 28,
                Padding = new Thickness(0),
                Icon = new SymbolIcon { Symbol = SymbolRegular.Delete24 },
                ToolTip = "从分组移除",
                DataContext = row.DataContext,   // RemoveCode_Click reads the row here
            };
            button.Click += RemoveCode_Click;
            Canvas.SetLeft(button, 3);
            Canvas.SetTop(button, y + (row.ActualHeight - 24) / 2);
            RemoveRail.Children.Add(button);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } deep) return deep;
        }
        return null;
    }

    /// <summary>
    /// Every removal path lands here: confirm, then remove. A slip on the per-row
    /// ✕ or the bulk menu item is otherwise silent data loss. 今日不再提示 mutes
    /// the dialog until the date rolls over, on this machine only.
    /// </summary>
    private async Task RemoveWithConfirmAsync(QuotesViewModel vm, IReadOnlyList<QuoteRow> targets)
    {
        if (targets.Count == 0) return;

        if (!vm.RemoveConfirmSuppressed)
        {
            var what = targets.Count == 1
                ? $"{targets[0].Name} {targets[0].Code}"
                : $"这 {targets.Count} 个合约";

            var skip = new System.Windows.Controls.CheckBox
            {
                Content = "今日不再提示",
                Margin = new Thickness(0, 12, 0, 0),
            };
            var panel = new StackPanel();
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"确定从分组「{vm.ActiveGroup?.Name}」移除 {what}？",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(skip);

            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = "移除合约",
                Content = panel,
                PrimaryButtonText = "移除",
                PrimaryButtonAppearance = ControlAppearance.Danger,
                CloseButtonText = "取消",
            };
            if (await dialog.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

            if (skip.IsChecked == true) vm.SuppressRemoveConfirmToday();
        }

        vm.RemoveCodes(targets);
    }

    /// <summary>
    /// Right-click menu on contract rows: move / copy / remove, in bulk.
    ///
    /// Hung off the ROW style, not the grid, so it stays clear of the header's
    /// column menu — right-clicking a header must still open 列设置. The items are
    /// rebuilt on every open because both the group list and the selection change
    /// underneath.
    /// </summary>
    private void AttachRowMenu(System.Windows.Controls.DataGrid grid)
    {
        var rowMenu = new ContextMenu();
        rowMenu.Opened += (_, _) => BuildRowMenu(rowMenu);

        var style = new Style(typeof(DataGridRow),
            grid.RowStyle ?? grid.TryFindResource(typeof(DataGridRow)) as Style);

        style.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty, rowMenu));

        // Right-clicking INSIDE an existing multi-selection keeps it; right-clicking
        // elsewhere selects that one row first. Without this, right-clicking a
        // selected row would collapse the selection to it and silently turn a bulk
        // action into a single one.
        style.Setters.Add(new EventSetter(
            PreviewMouseRightButtonDownEvent,
            new MouseButtonEventHandler((s, _) =>
            {
                if (s is DataGridRow { IsSelected: false } row) row.IsSelected = true;
            })));

        grid.RowStyle = style;
    }

    /// <summary>
    /// The rows the menu acts on: the whole selection when the clicked row is part
    /// of it, otherwise just that row.
    /// </summary>
    private IReadOnlyList<QuoteRow> MenuTargets(ContextMenu menu)
    {
        var clicked = (menu.PlacementTarget as FrameworkElement)?.DataContext as QuoteRow;
        var selected = QuoteGrid.SelectedItems.OfType<QuoteRow>().ToArray();

        if (clicked is null) return selected;
        return selected.Contains(clicked) ? selected : new[] { clicked };
    }

    private void BuildRowMenu(ContextMenu menu)
    {
        menu.Items.Clear();

        if (Vm is not { } vm) return;

        var targets = MenuTargets(menu);
        if (targets.Count == 0) return;

        var many = targets.Count > 1;
        var what = many ? $"选中的 {targets.Count} 个合约" : targets[0].Name;

        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = what,
            IsEnabled = false,
        });
        menu.Items.Add(new Separator());

        var others = vm.Groups.Where(g => !ReferenceEquals(g, vm.ActiveGroup)).ToArray();

        menu.Items.Add(GroupTargets("移动到分组", others, targets,
            target => vm.MoveCodesTo(targets, target), markExisting: true));
        menu.Items.Add(GroupTargets("添加到分组", others, targets,
            target => vm.CopyCodesTo(targets, target), markExisting: false));

        menu.Items.Add(new Separator());

        // Single row only: a note is about one contract, and the dialog can only
        // show one text.
        if (!many)
        {
            var note = new System.Windows.Controls.MenuItem
            {
                Header = vm.GetNote(targets[0].Code).Length > 0 ? "编辑备注…" : "添加备注…",
            };
            note.Click += (_, _) => _ = EditNoteAsync(targets[0]);
            menu.Items.Add(note);
        }

        // Single row only: opening N chart windows at once from a menu click is
        // not what anyone means by it.
        if (!many)
        {
            var chart = new System.Windows.Controls.MenuItem { Header = "打开图表" };
            chart.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(targets[0].Code)) OpenKlineRequested?.Invoke(targets[0].Code);
            };
            menu.Items.Add(chart);
        }

        var remove = new System.Windows.Controls.MenuItem
        {
            Header = many ? $"从本分组移除这 {targets.Count} 个" : "从本分组移除",
        };
        remove.Click += (_, _) => _ = RemoveWithConfirmAsync(vm, targets);
        menu.Items.Add(remove);
    }

    /// <summary>
    /// Prompts for this contract's note and stores it.
    ///
    /// The note is keyed by contract code rather than by group, so editing it here
    /// changes it everywhere that contract appears. Saving also reveals the 备注
    /// column if it was hidden — otherwise the text is written and nothing visibly
    /// happens.
    /// </summary>
    private async Task EditNoteAsync(QuoteRow row)
    {
        if (Vm is not { } vm) return;

        var input = new System.Windows.Controls.TextBox
        {
            Text = vm.GetNote(row.Code),
            MinWidth = 320,
            MaxLength = 200,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"备注 · {row.Name} {row.Code}",
            Content = input,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
        };

        input.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };

        if (await dialog.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        vm.SetNote(row.Code, input.Text);
        ShowNoteColumn();
    }

    private void ShowNoteColumn()
    {
        var column = QuoteGrid.Columns.FirstOrDefault(c => (c.Header as string) == "备注");
        if (column is not null) column.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// One submenu listing every other group.
    /// </summary>
    /// <param name="markExisting">
    /// How a target that already holds ALL the selected codes is treated. A copy
    /// there would do nothing, so it is disabled; a move still has work to do
    /// (drop them from this group), so it stays clickable and is just labelled.
    /// </param>
    private static System.Windows.Controls.MenuItem GroupTargets(
        string header, IReadOnlyList<GroupRow> targets, IReadOnlyList<QuoteRow> rows,
        Action<GroupRow> apply, bool markExisting)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };

        if (targets.Count == 0)
        {
            item.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "（没有其它分组）",
                IsEnabled = false,
            });
            return item;
        }

        foreach (var target in targets)
        {
            // "已有" only when every selected contract is already there; a partial
            // overlap still has something to add.
            var missing = rows.Count(r =>
                !target.Model.Codes.Contains(r.Code, StringComparer.OrdinalIgnoreCase));
            var has = missing == 0;

            var label = has ? $"{target.Name}（已有）"
                : rows.Count > 1 && missing < rows.Count ? $"{target.Name}（缺 {missing} 个）"
                : target.Name;

            var entry = new System.Windows.Controls.MenuItem
            {
                Header = label,
                IsEnabled = !has || markExisting,
            };

            var captured = target;
            entry.Click += (_, _) => apply(captured);
            item.Items.Add(entry);
        }

        return item;
    }
}
