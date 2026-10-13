using System.Runtime.InteropServices;
using DreadHungerServerManager.App.Services;
using Microsoft.UI.Xaml.Controls;

namespace DreadHungerServerManager.App.Views;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
    }

    public string Title => AppInfo.Title;

    public string VersionText => $"バージョン {AppInfo.Version}";

    public string EnvironmentText =>
        $"このPC: {RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）/ {RuntimeInformation.FrameworkDescription}";
}
