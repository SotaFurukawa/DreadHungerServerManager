using DreadHungerServerManager.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DreadHungerServerManager.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    public ServerViewModel ViewModel => App.ViewModel;
}
