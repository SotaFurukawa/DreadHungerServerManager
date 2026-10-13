using System.Reflection;
using DreadHungerServerManager.Core.Configuration;

namespace DreadHungerServerManager.Host;

public static class HostInfo
{
    /// <summary>Windows サービス名（インストーラーと GUI の検出で同じ名前を使う）。</summary>
    public const string ServiceName = HostService.Name;

    public static string Version { get; } =
        typeof(HostInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    /// <summary>既定の設定・ログの場所（%ProgramData%\DreadHungerServerManager）。サービス（LocalSystem）からも読み書きできる場所。</summary>
    public static string DefaultDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        AppPaths.AppFolderName);
}

public sealed record HostCommandLine(string DataDirectory, bool UseLocalStore)
{
    public static HostCommandLine Parse(IReadOnlyList<string> args)
    {
        var dataDirectory = HostInfo.DefaultDataDirectory;
        var useLocalStore = false;
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                dataDirectory = args[++i];
            }
            else if (string.Equals(args[i], "--local", StringComparison.OrdinalIgnoreCase))
            {
                useLocalStore = true;
            }
        }

        return new HostCommandLine(Path.GetFullPath(dataDirectory), useLocalStore);
    }
}
