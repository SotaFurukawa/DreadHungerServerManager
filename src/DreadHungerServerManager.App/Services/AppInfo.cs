using System.Reflection;

namespace DreadHungerServerManager.App.Services;

public static class AppInfo
{
    public const string Title = "Dread Hunger Server Manager";

    /// <summary>Directory.Build.props の Version（例: 0.1.0-beta）。</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
