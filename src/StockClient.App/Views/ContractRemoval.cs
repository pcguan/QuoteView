using System.Windows;
using System.Windows.Controls;
using StockClient.App.ViewModels;
using Wpf.Ui.Controls;

namespace StockClient.App.Views;

internal static class ContractRemoval
{
    public static async Task ConfirmAsync(QuotesViewModel vm, IReadOnlyList<QuoteRow> targets, Window? owner)
    {
        var group = vm.ActiveGroup;
        if (targets.Count == 0 || group is null) return;
        if (!vm.RemoveConfirmSuppressed)
        {
            var what = targets.Count == 1
                ? $"{targets[0].Name} {targets[0].Code}" : $"这 {targets.Count} 个合约";
            var skip = new System.Windows.Controls.CheckBox
                { Content = "今日不再提示", Margin = new Thickness(0, 12, 0, 0) };
            var panel = new StackPanel();
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"确定从分组「{group.Name}」移除 {what}？",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(skip);
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Owner = owner,
                Topmost = owner?.Topmost == true,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Title = "移除合约",
                Content = panel,
                PrimaryButtonText = "移除",
                PrimaryButtonAppearance = ControlAppearance.Danger,
                CloseButtonText = "取消",
            };
            if (await dialog.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
            if (!ReferenceEquals(group, vm.ActiveGroup)) return;
            if (skip.IsChecked == true) vm.SuppressRemoveConfirmToday();
        }
        if (ReferenceEquals(group, vm.ActiveGroup)) vm.RemoveCodes(targets);
    }
}
