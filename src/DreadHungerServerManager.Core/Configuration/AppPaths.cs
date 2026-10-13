namespace DreadHungerServerManager.Core.Configuration;

/// <summary>アプリが読み書きするファイルの場所。既定は %LOCALAPPDATA%\DreadHungerServerManager。</summary>
public sealed class AppPaths
{
    public const string AppFolderName = "DreadHungerServerManager";

    public AppPaths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public string PresetsFile => Path.Combine(DataDirectory, "presets.json");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static AppPaths CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName));

    /// <summary>コマンドライン引数 <c>--data-dir &lt;path&gt;</c> があればその場所を使う（動作確認・ポータブル運用用）。</summary>
    public static AppPaths FromCommandLine(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                return new AppPaths(args[i + 1]);
            }
        }

        return CreateDefault();
    }
}

public static class ServerPaths
{
    public const string ExecutableFileName = "DreadHungerServer.exe";

    /// <summary>
    /// Unreal Engineの専用サーバーは、実行ファイルと同じ階層の
    /// <c>DreadHunger\Saved\Logs\DreadHunger.log</c> にログを書き出す。
    /// </summary>
    public static string? GetDefaultLogFilePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath.Trim()));
            return directory is null ? null : Path.Combine(directory, "DreadHunger", "Saved", "Logs", "DreadHunger.log");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static string? ResolveLogFilePath(string? executablePath, string? overridePath) =>
        string.IsNullOrWhiteSpace(overridePath) ? GetDefaultLogFilePath(executablePath) : overridePath.Trim();
}
