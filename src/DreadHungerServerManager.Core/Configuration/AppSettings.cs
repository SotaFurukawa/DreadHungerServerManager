using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Configuration;

public enum LogSourceMode
{
    /// <summary>ログファイルを優先し、ログファイルから行が来るまでは標準出力を表示する。</summary>
    Auto,
    LogFile,
    StandardOutput,
}

public enum CloseBehavior
{
    Ask,
    StopServer,
    LeaveRunning,
}

/// <summary>settings.json に保存するアプリ設定。</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultServerExecutablePath = @"C:\DreadHunger\WindowsServer\DreadHungerServer.exe";
    public const int MinGracefulStopTimeoutSeconds = 5;
    public const int MaxGracefulStopTimeoutSeconds = 300;
    public const int MinLogLines = 500;
    public const int MaxLogLinesLimit = 100_000;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string ServerExecutablePath { get; init; } = DefaultServerExecutablePath;

    public GameSettings Game { get; init; } = GameSettings.Default;

    public string? LastPresetName { get; init; }

    /// <summary><c>-log</c> の後ろに追加する引数（例: <c>-port=7777</c>）。</summary>
    public string AdditionalArguments { get; init; } = "";

    public LogSourceMode LogSource { get; init; } = LogSourceMode.Auto;

    /// <summary>空なら <see cref="ServerPaths.GetDefaultLogFilePath"/> を使う。</summary>
    public string? LogFilePathOverride { get; init; }

    public bool ConfirmBeforeStop { get; init; } = true;

    public int GracefulStopTimeoutSeconds { get; init; } = 20;

    public CloseBehavior CloseBehavior { get; init; } = CloseBehavior.Ask;

    public bool AutoScrollLog { get; init; } = true;

    public int MaxLogLines { get; init; } = 5000;

    /// <summary>AWS（EC2）連携。古い settings.json にはないため、無ければ既定値（連携しない）になる。</summary>
    public CloudSettings Cloud { get; init; } = CloudSettings.Default;

    public static AppSettings Default { get; } = new();

    /// <summary>
    /// 手編集や古い版で壊れた値を補正する。ゲーム設定の範囲外は補正せず、画面上でエラーとして見せる。
    /// </summary>
    public AppSettings Normalize() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        ServerExecutablePath = string.IsNullOrWhiteSpace(ServerExecutablePath) ? DefaultServerExecutablePath : ServerExecutablePath.Trim(),
        Game = Game ?? GameSettings.Default,
        AdditionalArguments = AdditionalArguments ?? "",
        LogSource = Enum.IsDefined(LogSource) ? LogSource : LogSourceMode.Auto,
        LogFilePathOverride = string.IsNullOrWhiteSpace(LogFilePathOverride) ? null : LogFilePathOverride.Trim(),
        GracefulStopTimeoutSeconds = Math.Clamp(GracefulStopTimeoutSeconds, MinGracefulStopTimeoutSeconds, MaxGracefulStopTimeoutSeconds),
        CloseBehavior = Enum.IsDefined(CloseBehavior) ? CloseBehavior : CloseBehavior.Ask,
        MaxLogLines = Math.Clamp(MaxLogLines, MinLogLines, MaxLogLinesLimit),
        Cloud = (Cloud ?? CloudSettings.Default).Normalize(),
    };
}
