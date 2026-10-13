using System.Globalization;
using DreadHungerServerManager.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DreadHungerServerManager.App.Views;

public sealed partial class ServerPage : Page
{
    public ServerPage()
    {
        InitializeComponent();
        ViewModel.Log.LinesAppended += (_, _) => ScrollLogToEnd();

        // 通知の開閉などでログ欄の高さが変わっても、最新行が見える位置を保つ。
        LogListView.SizeChanged += (_, _) => ScrollLogToEnd();
    }

    public ServerViewModel ViewModel => App.ViewModel;

    private void ScrollLogToEnd()
    {
        var lines = ViewModel.Log.Lines;
        if (ViewModel.Log.AutoScroll && lines.Count > 0)
        {
            LogListView.ScrollIntoView(lines[^1]);
        }
    }

    private void MultiplierNumberBox_Loaded(object sender, RoutedEventArgs e) =>
        ((NumberBox)sender).NumberFormatter = ViewHelpers.CreateMultiplierFormatter();

    private void QuickValueButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton { DataContext: NumericSettingViewModel setting, Flyout: null } button)
        {
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var value in setting.QuickValues)
        {
            var item = new MenuFlyoutItem { Text = value.ToString("0.00", CultureInfo.InvariantCulture) };
            item.Click += (_, _) => setting.Value = value;
            flyout.Items.Add(item);
        }

        button.Flyout = flyout;
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) => CopySelectedLog();

    private void CopyLogAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CopySelectedLog();
    }

    // SelectedItemsは選択した順に並ぶため、表示順を保つよう選択範囲（インデックス）から取り出す。
    private void CopySelectedLog()
    {
        var lines = ViewModel.Log.Lines;
        var selected = LogListView.SelectedRanges
            .OrderBy(range => range.FirstIndex)
            .SelectMany(range => Enumerable.Range(range.FirstIndex, (int)range.Length))
            .Where(index => index < lines.Count)
            .Select(index => lines[index])
            .ToList();
        ViewModel.CopyLog(selected);
    }
}
