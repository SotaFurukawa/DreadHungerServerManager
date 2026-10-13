using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;
using DreadHungerServerManager.Core.Services.Cloud;
using DreadHungerServerManager.Core.Services.Simulation;

namespace DreadHungerServerManager.Core.Tests;

/// <summary>
/// 「設定を添えて EC2 ごと起動する」流れ。疑似 EC2 が稼働中になった時点で疑似の常駐サービスを動かし始め、
/// 実際の EC2 起動（Windows の起動後に常駐サービスが開始する）と同じ順番を再現する。
/// </summary>
public sealed class CloudServerLauncherTests : IAsyncDisposable
{
    private const string ServerId = "default";
    private static readonly CloudInstanceRef Instance = new("ap-northeast-3", "i-0123456789abcdef0");
    private static readonly CloudStartOptions FastOptions = new()
    {
        InstanceTimeout = TimeSpan.FromSeconds(10),
        GameServerTimeout = TimeSpan.FromSeconds(10),
        PollInterval = TimeSpan.FromMilliseconds(20),
    };

    private readonly FakeCloudInstanceService _cloud = new() { StartDuration = TimeSpan.FromMilliseconds(150), StopDuration = TimeSpan.FromMilliseconds(50) };
    private readonly InMemoryServerControlStore _store = new();
    private readonly SimulatedServerProcessManager _manager = new();
    private readonly CancellationTokenSource _hostStop = new();
    private Task? _host;

    public async ValueTask DisposeAsync()
    {
        await _hostStop.CancelAsync();
        if (_host is not null)
        {
            await _host.WaitAsync(TimeSpan.FromSeconds(10));
        }

        _hostStop.Dispose();
    }

    [Fact]
    public async Task StoppedInstance_StartsWithTheRequestedSettings()
    {
        await MarkHostAsInstalledAsync();
        StartHostWhenInstanceIsRunning();
        var stages = new List<CloudStartStage>();

        var result = await new CloudServerLauncher(_cloud, _store).StartAsync(
            Instance,
            ServerId,
            GameSettings.Default with { Map = GameMap.Departure, DaysBeforeBlizzard = 6 },
            "Test",
            FastOptions,
            new SyncProgress<CloudStartProgress>(progress => stages.Add(progress.Stage)));

        Assert.Equal(CloudInstanceState.Running, result.Instance.State);
        Assert.NotNull(result.PublicIp);
        Assert.True(result.GameServer?.Succeeded);
        Assert.Contains("山頂", result.GameServer!.Message);
        Assert.Equal(GameMap.Departure, result.Heartbeat?.Settings?.Map);
        Assert.Contains("daysbeforeblizzard=6", _manager.LastLaunch?.Arguments);

        // 自動起動（前回の設定）ではなく、要求の設定で1回だけ起動している。
        Assert.Equal(1, _manager.StartCount);
        Assert.Equal(
            [CloudStartStage.SendingRequest, CloudStartStage.StartingInstance, CloudStartStage.WaitingForInstance, CloudStartStage.WaitingForGameServer],
            stages.Distinct().ToList());
    }

    [Fact]
    public async Task RunningInstance_IsNotStartedAgain()
    {
        _cloud.StartDuration = TimeSpan.Zero;
        await _cloud.StartAsync(Instance);
        await MarkHostAsInstalledAsync();
        StartHostWhenInstanceIsRunning();

        var result = await new CloudServerLauncher(_cloud, _store).StartAsync(Instance, ServerId, GameSettings.Default, "Test", FastOptions);

        Assert.True(result.GameServer?.Succeeded);
        Assert.Equal(1, _cloud.StartRequestCount);
    }

    [Fact]
    public async Task WithoutHost_ReturnsAfterTheInstanceIsRunning()
    {
        var result = await new CloudServerLauncher(_cloud, _store).StartAsync(Instance, ServerId, GameSettings.Default, "Test", FastOptions);

        Assert.Equal(CloudInstanceState.Running, result.Instance.State);
        Assert.False(result.IsHostKnown);
        Assert.Null(result.GameServer);
    }

    [Fact]
    public async Task HostThatDoesNotRespond_IsReportedWithoutFailingTheStart()
    {
        await MarkHostAsInstalledAsync();

        var result = await new CloudServerLauncher(_cloud, _store).StartAsync(
            Instance,
            ServerId,
            GameSettings.Default,
            "Test",
            FastOptions with { GameServerTimeout = TimeSpan.FromMilliseconds(200) });

        Assert.Equal(CloudInstanceState.Running, result.Instance.State);
        Assert.Null(result.GameServer);
        Assert.Contains("応答していません", result.GameServerTimeoutMessage);
    }

    [Fact]
    public async Task InvalidSettings_DoNothing()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new CloudServerLauncher(_cloud, _store).StartAsync(Instance, ServerId, GameSettings.Default with { MaxPlayers = 2, Thralls = 2 }, "Test", FastOptions));

        Assert.Contains("人狼人数", error.Message);
        Assert.Equal(0, _cloud.StartRequestCount);
        Assert.Null(await _store.GetRequestAsync(ServerId));
    }

    [Fact]
    public async Task StoppingInstance_IsRejected()
    {
        _cloud.StartDuration = TimeSpan.Zero;
        _cloud.StopDuration = TimeSpan.FromMinutes(5);
        await _cloud.StartAsync(Instance);
        await _cloud.GetStatusAsync(Instance);
        await _cloud.StopAsync(Instance);

        var error = await Assert.ThrowsAsync<CloudOperationException>(() =>
            new CloudServerLauncher(_cloud, _store).StartAsync(Instance, ServerId, GameSettings.Default, "Test", FastOptions));

        Assert.Equal(CloudErrorKind.InvalidState, error.Kind);
        Assert.Null(await _store.GetRequestAsync(ServerId));
    }

    /// <summary>前回 EC2 が動いていたときの状態（古い）を置き、常駐サービスが入っている状況にする。</summary>
    private Task MarkHostAsInstalledAsync() => _store.PutHeartbeatAsync(ServerId, new ServerHeartbeat
    {
        ServerId = ServerId,
        UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        State = ServerState.Stopped,
    });

    private void StartHostWhenInstanceIsRunning()
    {
        _host = Task.Run(async () =>
        {
            while ((await _cloud.GetStatusAsync(Instance)).State != CloudInstanceState.Running)
            {
                await Task.Delay(10, _hostStop.Token);
            }

            var coordinator = new ServerHostCoordinator(
                new GameServerController(_manager),
                _store,
                new ServerHostOptions
                {
                    ServerId = ServerId,
                    RequestPollInterval = TimeSpan.FromMilliseconds(20),
                    HeartbeatInterval = TimeSpan.FromMilliseconds(100),
                    ReadyTimeout = TimeSpan.FromSeconds(5),
                },
                new HostEnvironmentStub());
            await coordinator.RunAsync(_hostStop.Token);
        });
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class HostEnvironmentStub : IServerHostEnvironment
    {
        private GameSettings _last = GameSettings.Default with { Map = GameMap.Approach };

        public string? LogFilePathOverride => null;

        public LogSourceMode LogSource => LogSourceMode.Auto;

        public GameSettings LoadLastSettings() => _last;

        public void SaveLastSettings(GameSettings settings) => _last = settings;

        public ServerLaunchRequest? PrepareLaunch(GameSettings settings, out IReadOnlyList<string> errors)
        {
            errors = [];
            return GameServerController.CreateLaunchRequest(@"C:\DH\DreadHungerServer.exe", settings, null, null, LogSourceMode.Auto);
        }

        public void Log(HostLogLevel level, string message)
        {
        }
    }
}
