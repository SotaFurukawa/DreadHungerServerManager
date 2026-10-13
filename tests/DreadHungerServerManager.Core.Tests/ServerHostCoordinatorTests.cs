using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;
using DreadHungerServerManager.Core.Services.Cloud;

namespace DreadHungerServerManager.Core.Tests;

public sealed class ServerHostCoordinatorTests
{
    private const string ServerId = "default";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly SimulatedServerProcessManager _manager = new();
    private readonly InMemoryServerControlStore _store = new();
    private readonly FakeHostEnvironment _environment = new();

    [Fact]
    public async Task Startup_AutoStartsWithTheLastSettings()
    {
        _environment.LastSettings = GameSettings.Default with { Map = GameMap.Approach, DaysBeforeBlizzard = 6 };

        await using var host = Run();
        var heartbeat = await WaitForHeartbeatAsync(h => h.IsReady);

        Assert.Equal(ServerState.Running, heartbeat.State);
        Assert.Equal(GameMap.Approach, heartbeat.Settings?.Map);
        Assert.Contains("daysbeforeblizzard=6", _manager.LastLaunch?.Arguments);
        Assert.Null(heartbeat.LastResult);
    }

    [Fact]
    public async Task Startup_RunsAFreshRequestInsteadOfAutoStart()
    {
        var request = Request(ServerControlAction.Start, GameSettings.Default with { Map = GameMap.Departure });
        await _store.PutRequestAsync(ServerId, request);

        await using var host = Run();
        var heartbeat = await WaitForHeartbeatAsync(h => h.LastResult?.RequestId == request.RequestId);

        Assert.True(heartbeat.LastResult!.Succeeded);
        Assert.Equal(GameMap.Departure, heartbeat.Settings?.Map);
        Assert.Equal(GameMap.Departure, _environment.LastSettings.Map);
        Assert.Equal(1, _manager.StartCount);
    }

    [Fact]
    public async Task Startup_IgnoresAnOldRequestAndReportsIt()
    {
        var old = Request(ServerControlAction.Stop) with { RequestedAt = DateTimeOffset.UtcNow.AddHours(-3) };
        await _store.PutRequestAsync(ServerId, old);

        await using var host = Run();
        var heartbeat = await WaitForHeartbeatAsync(h => h.IsReady);

        Assert.Equal(old.RequestId, heartbeat.LastResult?.RequestId);
        Assert.False(heartbeat.LastResult!.Succeeded);
        Assert.Contains("古い要求", heartbeat.LastResult.Message);
    }

    [Fact]
    public async Task Requests_AreProcessedOnceAndInOrder()
    {
        await using var host = Run();
        await WaitForHeartbeatAsync(h => h.IsReady);

        var client = new ServerControlClient(_store);
        var stop = await client.SendAsync(ServerId, Request(ServerControlAction.Stop), Timeout, pollInterval: TimeSpan.FromMilliseconds(20));
        Assert.True(stop.Succeeded);
        Assert.Equal(ServerState.Stopped, _manager.Status.State);

        var start = await client.SendAsync(ServerId, Request(ServerControlAction.Start, GameSettings.Default with { Map = GameMap.Approach }), Timeout, pollInterval: TimeSpan.FromMilliseconds(20));
        Assert.True(start.Succeeded);
        Assert.Contains("進入路付近", start.Message);

        await Task.Delay(200);
        Assert.Equal(2, _manager.StartCount);
        Assert.Equal(1, _manager.StopCount);
    }

    [Fact]
    public async Task StartWhileRunning_DoesNotRestart()
    {
        await using var host = Run();
        await WaitForHeartbeatAsync(h => h.IsReady);

        var result = await new ServerControlClient(_store).SendAsync(ServerId, Request(ServerControlAction.Start), Timeout, pollInterval: TimeSpan.FromMilliseconds(20));

        Assert.True(result.Succeeded);
        Assert.Contains("既に起動", result.Message);
        Assert.Equal(1, _manager.StartCount);
    }

    [Fact]
    public async Task Restart_UsesTheRequestedSettings()
    {
        await using var host = Run();
        await WaitForHeartbeatAsync(h => h.IsReady);

        var result = await new ServerControlClient(_store).SendAsync(
            ServerId,
            Request(ServerControlAction.Restart, GameSettings.Default with { Map = GameMap.Departure, Thralls = 1 }),
            Timeout,
            pollInterval: TimeSpan.FromMilliseconds(20));

        Assert.True(result.Succeeded);
        Assert.Equal(2, _manager.StartCount);
        Assert.Contains("Departure_Persistent", _manager.LastLaunch?.Arguments);
        Assert.Contains("thralls=1", _manager.LastLaunch?.Arguments);
    }

    [Fact]
    public async Task InvalidSettings_AreRejectedWithoutStarting()
    {
        _environment.AutoStart = false;
        await using var host = Run();

        var result = await new ServerControlClient(_store).SendAsync(
            ServerId,
            Request(ServerControlAction.Start, GameSettings.Default with { MaxPlayers = 2, Thralls = 2 }),
            Timeout,
            pollInterval: TimeSpan.FromMilliseconds(20));

        Assert.False(result.Succeeded);
        Assert.Contains("人狼人数", result.Message);
        Assert.Equal(0, _manager.StartCount);
    }

    [Fact]
    public async Task StartFailure_IsReported()
    {
        _environment.AutoStart = false;
        _manager.FailNextStart = new ServerStartException(ServerStartError.ExecutableNotFound, "サーバーの実行ファイルが見つかりません。");
        await using var host = Run();

        var result = await new ServerControlClient(_store).SendAsync(ServerId, Request(ServerControlAction.Start), Timeout, pollInterval: TimeSpan.FromMilliseconds(20));

        Assert.False(result.Succeeded);
        Assert.Contains("見つかりません", result.Message);
    }

    [Fact]
    public async Task ServerThatNeverBecomesReady_IsReportedAsFailure()
    {
        _environment.AutoStart = false;
        _manager.BecomeReady = false;
        await using var host = Run(readyTimeout: TimeSpan.FromMilliseconds(200));

        var result = await new ServerControlClient(_store).SendAsync(ServerId, Request(ServerControlAction.Start), Timeout, pollInterval: TimeSpan.FromMilliseconds(20));

        Assert.False(result.Succeeded);
        Assert.Contains("起動完了を確認できません", result.Message);
    }

    [Fact]
    public async Task StoreFailures_DoNotStopTheHost()
    {
        var flaky = new FlakyStore(_store) { FailuresLeft = 5 };
        await using var host = Run(store: flaky);

        var heartbeat = await WaitForHeartbeatAsync(h => h.IsReady);

        Assert.Equal(ServerState.Running, heartbeat.State);
        Assert.Contains(_environment.Messages, message => message.Contains("再試行", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shutdown_StopsTheServerGracefully()
    {
        var host = Run();
        await WaitForHeartbeatAsync(h => h.IsReady);

        await host.DisposeAsync();

        Assert.Equal(ServerState.Stopped, _manager.Status.State);
        Assert.Equal(1, _manager.StopCount);
        Assert.Equal(ServerState.Stopped, (await _store.GetHeartbeatAsync(ServerId))?.State);
    }

    [Fact]
    public async Task MatchEnd_IsReportedAndTheServerStaysStopped()
    {
        await using var host = Run();
        await WaitForHeartbeatAsync(h => h.IsReady);

        _manager.EndMatch();
        var heartbeat = await WaitForHeartbeatAsync(h => h.State == ServerState.Stopped);
        await Task.Delay(200);

        Assert.Contains("試合の終了", heartbeat.LastExitMessage);
        Assert.Equal(1, _manager.StartCount);
    }

    [Fact]
    public async Task Client_TimesOutWhenNoHostIsRunning()
    {
        var client = new ServerControlClient(_store);

        var error = await Assert.ThrowsAsync<CloudOperationException>(() =>
            client.SendAsync(ServerId, Request(ServerControlAction.Start), TimeSpan.FromMilliseconds(100), pollInterval: TimeSpan.FromMilliseconds(20)));

        Assert.Equal(CloudErrorKind.Timeout, error.Kind);
        Assert.Contains("応答していません", error.Message);
    }

    [Fact]
    public void ControlJson_RoundTripsRequestsAndHeartbeats()
    {
        var request = Request(ServerControlAction.Restart, GameSettings.Default with { Map = GameMap.Departure, ColdIntensity = 1.25 });
        var heartbeat = new ServerHeartbeat
        {
            ServerId = ServerId,
            UpdatedAt = DateTimeOffset.UtcNow,
            State = ServerState.Running,
            MatchState = MatchStates.InProgress,
            PlayerCount = 4,
            Settings = request.Settings,
            LastResult = new ServerRequestResult { RequestId = request.RequestId, Action = request.Action, Succeeded = true, Message = "サーバーを起動しました。", CompletedAt = DateTimeOffset.UtcNow },
        };

        var requestJson = ServerControlJson.Serialize(request);
        var heartbeatJson = ServerControlJson.Serialize(heartbeat);

        Assert.Equal(request, ServerControlJson.Deserialize<ServerControlRequest>(requestJson));
        Assert.Equal(heartbeat, ServerControlJson.Deserialize<ServerHeartbeat>(heartbeatJson));
        Assert.Contains("\"action\":\"Restart\"", requestJson);
        Assert.True(heartbeatJson.Length < 4096, "SSM の標準パラメーター（4KB）に収まること");
        Assert.Null(ServerControlJson.Deserialize<ServerHeartbeat>("{ broken"));
    }

    private static ServerControlRequest Request(ServerControlAction action, GameSettings? settings = null) =>
        ServerControlRequest.Create(action, settings, "Test", DateTimeOffset.UtcNow);

    private RunningHost Run(IServerControlStore? store = null, TimeSpan? readyTimeout = null)
    {
        var coordinator = new ServerHostCoordinator(
            new GameServerController(_manager),
            store ?? _store,
            new ServerHostOptions
            {
                ServerId = ServerId,
                AutoStartGameServer = _environment.AutoStart,
                RequestPollInterval = TimeSpan.FromMilliseconds(20),
                HeartbeatInterval = TimeSpan.FromMilliseconds(100),
                ReadyTimeout = readyTimeout ?? TimeSpan.FromSeconds(5),
                GracefulStopTimeout = TimeSpan.FromSeconds(2),
            },
            _environment);
        return new RunningHost(coordinator);
    }

    private async Task<ServerHeartbeat> WaitForHeartbeatAsync(Func<ServerHeartbeat, bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await _store.GetHeartbeatAsync(ServerId) is { } heartbeat && condition(heartbeat))
            {
                return heartbeat;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("期待した状態になりませんでした。");
    }

    private sealed class RunningHost : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;

        public RunningHost(ServerHostCoordinator coordinator) => _run = Task.Run(() => coordinator.RunAsync(_stop.Token));

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _run.WaitAsync(Timeout);
            _stop.Dispose();
        }
    }

    private sealed class FakeHostEnvironment : IServerHostEnvironment
    {
        public GameSettings LastSettings { get; set; } = GameSettings.Default;

        public bool AutoStart { get; set; } = true;

        public List<string> Messages { get; } = [];

        public string? LogFilePathOverride => null;

        public LogSourceMode LogSource => LogSourceMode.Auto;

        public GameSettings LoadLastSettings() => LastSettings;

        public void SaveLastSettings(GameSettings settings) => LastSettings = settings;

        public ServerLaunchRequest? PrepareLaunch(GameSettings settings, out IReadOnlyList<string> errors)
        {
            errors = GameSettingsValidator.Validate(settings).Select(error => error.Message).ToList();
            return errors.Count > 0 ? null : GameServerController.CreateLaunchRequest(@"C:\DH\DreadHungerServer.exe", settings, null, null, LogSourceMode.Auto);
        }

        public void Log(HostLogLevel level, string message)
        {
            lock (Messages)
            {
                Messages.Add(message);
            }
        }
    }

    private sealed class FlakyStore(IServerControlStore inner) : IServerControlStore
    {
        public int FailuresLeft { get; set; }

        public Task<ServerControlRequest?> GetRequestAsync(string serverId, CancellationToken cancellationToken = default) =>
            MaybeFail(() => inner.GetRequestAsync(serverId, cancellationToken));

        public Task PutRequestAsync(string serverId, ServerControlRequest request, CancellationToken cancellationToken = default) =>
            inner.PutRequestAsync(serverId, request, cancellationToken);

        public Task<ServerHeartbeat?> GetHeartbeatAsync(string serverId, CancellationToken cancellationToken = default) =>
            MaybeFail(() => inner.GetHeartbeatAsync(serverId, cancellationToken));

        public Task PutHeartbeatAsync(string serverId, ServerHeartbeat heartbeat, CancellationToken cancellationToken = default) =>
            MaybeFail(async () =>
            {
                await inner.PutHeartbeatAsync(serverId, heartbeat, cancellationToken);
                return true;
            });

        public Task<AutoStopConfig?> GetAutoStopConfigAsync(string serverId, CancellationToken cancellationToken = default) =>
            MaybeFail(() => inner.GetAutoStopConfigAsync(serverId, cancellationToken));

        public Task PutAutoStopConfigAsync(string serverId, AutoStopConfig config, CancellationToken cancellationToken = default) =>
            inner.PutAutoStopConfigAsync(serverId, config, cancellationToken);

        private Task<T> MaybeFail<T>(Func<Task<T>> action)
        {
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new CloudOperationException(CloudErrorKind.Network, "通信できません（テスト）");
            }

            return action();
        }
    }
}

/// <summary>プロセスを起動せずに、起動処理中→起動完了→停止の状態遷移を再現する。</summary>
internal sealed class SimulatedServerProcessManager : IServerProcessManager
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
