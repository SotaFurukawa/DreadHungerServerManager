using DreadHungerServerManager.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DreadHungerServerManager.App.Views;

public sealed partial class CloudPage : Page
{
    public CloudPage()
    {
        InitializeComponent();
    }

    public CloudViewModel Cloud => App.CloudViewModel;

    public ServerViewModel Server => App.ViewModel;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = Cloud.RefreshAsync(showErrors: false);
    }
}
