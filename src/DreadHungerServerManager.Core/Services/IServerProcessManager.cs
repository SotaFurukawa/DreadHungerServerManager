using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services;

public sealed record ServerLaunchRequest
{
    public required string ExecutablePath { get; init; }

    public required string Arguments { get; init; }

    public string? LogFilePath { get; init; }

    public LogSourceMode LogSource { get; init; } = LogSourceMode.Auto;
}

public enum StopOutcome
{
    AlreadyStopped,
    Exited,
    TimedOut,

    /// <summary>Ctrl+Cを送れるコンソールもウィンドウも見つからなかった。</summary>
    SignalUnavailable,
}

public enum ServerStartError
{
    ExecutableNotFound,
    AlreadyRunning,
    LaunchFailed,
}

public sealed class ServerStartException(ServerStartError error, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ServerStartError Error { get; } = error;
}

/// <summary>
/// 1台のDread Hungerサーバーのプロセスを管理する。UIスレッドを前提にせず、イベントは任意のスレッドから発生する。
/// 複数サーバー管理に拡張する場合は、サーバーごとにこのクラスのインスタンスを持つ。
/// </summary>
public interface IServerProcessManager : IDisposable
{
    ServerStatus Status { get; }

    event EventHandler? StatusChanged;

    event EventHandler<IReadOnlyList<ServerLogLine>>? LogReceived;

    /// <summary>起動処理中から起動完了（ポート待受またはWaitingToStartを検出）に変わったとき。</summary>
    event EventHandler? ServerReady;

    event EventHandler<ServerExitInfo>? ServerExited;

    IReadOnlyList<RunningServerProcess> FindRunningServers();

    /// <summary>サーバーを起動する。失敗時は <see cref="ServerStartException"/>。</summary>
    void Start(ServerLaunchRequest request);

    /// <summary>このアプリ以外で起動済みのサーバーを検出して管理下に置く。見つからなければnull。</summary>
    RunningServerProcess? AttachToRunningServer(string? logFilePathOverride, LogSourceMode logSource);

    /// <summary>Ctrl+Cで安全な終了を要求し、終了を待つ。TimedOutの場合は停止処理中のまま。</summary>
    Task<StopOutcome> StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);

    /// <summary>TimedOut後に停止を取りやめ、稼働中の状態へ戻す。</summary>
    void CancelStop();

    /// <summary>プロセスツリーごと強制終了する。</summary>
    Task<bool> KillAsync(TimeSpan timeout);

    /// <summary>サーバーを止めずに管理だけをやめる（アプリ終了時に「残して終了」を選んだ場合）。</summary>
    void Detach();
}
