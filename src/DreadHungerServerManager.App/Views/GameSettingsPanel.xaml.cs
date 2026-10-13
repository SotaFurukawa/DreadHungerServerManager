using System.Globalization;
using DreadHungerServerManager.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DreadHungerServerManager.App.Views;

/// <summary>プリセットとマップ・ゲーム設定の欄（「サーバー」ページと「AWS / Cloud」ページで共通）。</summary>
public sealed partial class GameSettingsPanel : UserControl
{
    public GameSettingsPanel()
    {
        InitializeComponent();
    }

    public ServerViewModel ViewModel => App.ViewModel;

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
}
