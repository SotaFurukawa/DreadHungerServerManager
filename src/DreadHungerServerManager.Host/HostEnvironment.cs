using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;
using DreadHungerServerManager.Core.Services.Cloud;

namespace DreadHungerServerManager.Host;

/// <summary>
/// 常駐サービスの設定（%ProgramData%\DreadHungerServerManager\settings.json。GUI と同じ形式）とログ。
/// 起動に成功したゲーム設定を「前回の設定」として保存し、次回の自動起動に使う。
/// </summary>
public sealed class HostEnvironment(IAppSettingsStore store, AppSettings settings, FileAppLog log) : IServerHostEnvironment
{
    private readonly Lock _gate = new();
    private AppSettings _settings = settings;

    public AppSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    public string? LogFilePathOverride => Settings.LogFilePathOverride;

    public LogSourceMode LogSource => Settings.LogSource;

    public GameSettings LoadLastSettings() => Settings.Game;

    public void SaveLastSettings(GameSettings game)
    {
        AppSettings updated;
        lock (_gate)
        {
            _settings = updated = _settings with { Game = game };
        }

        try
        {
            store.Save(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error("前回の設定を保存できませんでした", ex);
        }
    }

    public ServerLaunchRequest? PrepareLaunch(GameSettings game, out IReadOnlyList<string> errors)
    {
        var current = Settings;
        errors = GameServerController.ValidateLaunch(current.ServerExecutablePath, game, current.AdditionalArguments);
        return errors.Count > 0
            ? null
            : GameServerController.CreateLaunchRequest(current.ServerExecutablePath, game, current.AdditionalArguments, current.LogFilePathOverride, current.LogSource);
    }

    public void Log(HostLogLevel level, string message)
    {
        switch (level)
        {
            case HostLogLevel.Error:
                log.Error(message);
                break;
            case HostLogLevel.Warning:
                log.Warning(message);
                break;
            default:
                log.Info(message);
                break;
        }

        if (Environment.UserInteractive)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} [{level}] {message}");
        }
    }
}
