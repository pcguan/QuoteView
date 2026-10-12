using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StockClient.App.ViewModels;
using StockClient.Core;
using StockClient.Core.Contracts;

namespace StockClient.App.Views;

/// <summary>Independent search state; all mutations go through the shared quotes VM.</summary>
public partial class AddContractWindow : Window
{
    private readonly QuotesViewModel _vm;
    private GroupRow? _group;
    private bool _creatingGroup;

    public AddContractWindow(QuotesViewModel vm)
    {
        _vm = vm;
        _group = vm.ActiveGroup;
        InitializeComponent();
        GroupText.Text = _group is null ? "新增合约" : $"添加到「{_group.Name}」";
        _vm.PropertyChanged += VmChanged;
        Closed += (_, _) => _vm.PropertyChanged -= VmChanged;
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void VmChanged(object? sender, PropertyChangedEventArgs e)
    {
        // An account/group switch must never redirect an open editor's writes.
        if (!_creatingGroup && e.PropertyName == nameof(QuotesViewModel.ActiveGroup)
            && !ReferenceEquals(_group, _vm.ActiveGroup)) Close();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (Matches is null || AddButton is null || ResultText is null) return;
        var query = SearchBox.Text.Trim();
        var matches = _vm.SearchContracts(query);
        Matches.ItemsSource = matches;
        Matches.SelectedIndex = matches.Count > 0 ? 0 : -1;
        AddButton.IsEnabled = matches.Count > 0 || CodeMapper.IsValid(query);
        ResultText.Text = query.Length > 0 && !AddButton.IsEnabled ? "没有匹配的合约" : "";
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddSelected();

    private void AddSelected()
    {
        if (!ReferenceEquals(_group, _vm.ActiveGroup)) { Close(); return; }
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return;
        var contract = Matches.SelectedItem as Contract;
        var code = contract?.Code ?? query.ToUpperInvariant();
        if (!CodeMapper.IsValid(code)) { ResultText.Text = "请选择合约或输入完整市场代码"; return; }
        if (_group is null)
        {
            _creatingGroup = true;
            try { _vm.AddGroup(); _group = _vm.ActiveGroup; }
            finally { _creatingGroup = false; }
            GroupText.Text = $"添加到「{_group?.Name}」";
        }
        _vm.AddCode(code);
        if (_vm.HasError) { ResultText.Text = _vm.Error; return; }
        _vm.StealthSelectContract(code);
        SearchBox.Clear();
        ResultText.Text = $"已添加 {contract?.Name ?? code}";
        SearchBox.Focus();
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddSelected(); e.Handled = true; }
        else if (e.Key == Key.Down && Matches.Items.Count > 0)
        {
            Matches.SelectedIndex = Math.Max(0, Matches.SelectedIndex);
            ((ListBoxItem?)Matches.ItemContainerGenerator.ContainerFromIndex(Matches.SelectedIndex))?.Focus();
            e.Handled = true;
        }
    }

    private void Matches_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddSelected(); e.Handled = true; }
    }

    private void Matches_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(Matches, source) is ListBoxItem)
        { AddSelected(); e.Handled = true; }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
