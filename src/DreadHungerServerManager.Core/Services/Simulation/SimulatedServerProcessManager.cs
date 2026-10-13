using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services.Simulation;

/// <summary>
/// プロセスを起動せずに、起動処理中→起動完了→停止の状態遷移を再現する <see cref="IServerProcessManager"/>（テスト・画面確認用）。
/// </summary>
public sealed class SimulatedServerProcessManager : IServerProcessManager
{
    private readonly Lock _gate = new();
    private ServerStatus _status = ServerStatus.Stopped;

    public bool BecomeReady { get; set; } = true;

    public ServerStartException? FailNextStart { get; set; }

    public ServerLaunchRequest? LastLaunch { get; private set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public ServerStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public event EventHandler? StatusChanged;

    public event EventHandler<IReadOnlyList<ServerLogLine>>? LogReceived { add { } remove { } }

    public event EventHandler? ServerReady { add { } remove { } }

    public event EventHandler<ServerExitInfo>? ServerExited;

    public IReadOnlyList<RunningServerProcess> FindRunningServers() => [];

    public void Start(ServerLaunchRequest request)
    {
        if (FailNextStart is { } failure)
        {
            FailNextStart = null;
            throw failure;
        }

        StartCount++;
        LastLaunch = request;
        SetStatus(new ServerStatus { State = ServerState.Starting, StartedAt = DateTimeOffset.Now });
        if (BecomeReady)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(30);
                SetStatus(Status with { State = ServerState.Running, IsPortListening = true, ListeningPort = 7777, IsWaitingToStart = true, MatchState = MatchStates.WaitingToStart });
            });
        }
    }

    public RunningServerProcess? AttachToRunningServer(string? logFilePathOverride, LogSourceMode logSource) => null;

    public Task<StopOutcome> StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        if (Status.State == ServerState.Stopped)
        {
            return Task.FromResult(StopOutcome.AlreadyStopped);
        }

        StopCount++;
        Exit(ServerExitKind.Requested);
        return Task.FromResult(StopOutcome.Exited);
    }

    public void CancelStop()
    {
    }

    public Task<bool> KillAsync(TimeSpan timeout)
    {
        Exit(ServerExitKind.Requested);
        return Task.FromResult(true);
    }

    public void EndMatch()
    {
        SetStatus(Status with { MatchState = MatchStates.WaitingPostMatch, ServerRequestedExit = true });
        Exit(ServerExitKind.EndedByServer);
    }

    public void Detach()
    {
    }

    public void Dispose()
    {
    }

    private void Exit(ServerExitKind kind)
    {
        SetStatus(ServerStatus.Stopped);
        ServerExited?.Invoke(this, new ServerExitInfo(0, kind == ServerExitKind.Requested, false, null, kind));
    }

    private void SetStatus(ServerStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
