using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;
using DreadHungerServerManager.Core.Services.Cloud;
using DreadHungerServerManager.Core.Services.Simulation;

namespace DreadHungerServerManager.Bot.Tests;

/// <summary>
/// 疑似 EC2・メモリ上の受け渡し・疑似の常駐サービスをまとめたテスト環境。
/// 疑似 EC2 が稼働中になった時点で常駐サービスを動かし始め、実際の EC2 起動と同じ順番を再現する。
/// </summary>
internal sealed class TestHost : IAsyncDisposable
{
    public static readonly CloudInstanceRef Instance = new("ap-northeast-3", "i-0123456789abcdef0");

    private readonly CancellationTokenSource _stop = new();
    private Task? _host;

    public TestHost(TimeProvider? time = null)
    {
        Time = time ?? TimeProvider.System;
        Cloud = new FakeCloudInstanceService(Time) { StartDuration = TimeSpan.FromMilliseconds(150), StopDuration = TimeSpan.FromMilliseconds(100) };
        Options = new BotOptions
        {
            Instance = Instance,
            AllowedUserIds = new HashSet<string> { "100" },
            AllowedRoleIds = new HashSet<string> { "900" },
            StartInstanceTimeout = TimeSpan.FromSeconds(10),
            StartGameServerTimeout = TimeSpan.FromSeconds(10),
            RestartTimeout = TimeSpan.FromSeconds(10),
            StopGameServerTimeout = TimeSpan.FromSeconds(5),
            StopInstanceTimeout = TimeSpan.FromSeconds(10),
            PollInterval = TimeSpan.FromMilliseconds(20),
        };
        Workflows = new BotWorkflows(Cloud, Store, Options, Time, line =>
        {
            lock (Log)
            {
                Log.Add(line);
            }
        });
    }

    public static BotUser Member { get; } = new("100", "Alice", []);

    public static BotUser Stranger { get; } = new("200", "Bob", ["111"]);

    public TimeProvider Time { get; }

    public FakeCloudInstanceService Cloud { get; }

    public InMemoryServerControlStore Store { get; } = new();

    public SimulatedServerProcessManager Manager { get; } = new();

    public HostEnvironmentStub Environment { get; } = new();

    public BotOptions Options { get; }

    public BotWorkflows Workflows { get; }

    /// <summary>管理者向けのログ（詳しい原因はこちらにだけ出る）。</summary>
    public List<string> Log { get; } = [];

    /// <summary>常駐サービスが入っている状態にする（前回 EC2 が動いていたときの古い状態を置く）。</summary>
    public Task MarkHostInstalledAsync(GameSettings? lastSettings = null) => Store.PutHeartbeatAsync("default", new ServerHeartbeat
    {
        ServerId = "default",
        UpdatedAt = Time.GetUtcNow().AddHours(-1),
        State = ServerState.Stopped,
        Settings = lastSettings,
    });

    /// <summary>EC2 が稼働中になったら常駐サービスを動かし始める。</summary>
    public void RunHostWhenInstanceIsRunning(bool autoStart = true)
    {
        _host = Task.Run(async () =>
        {
            while ((await Cloud.GetStatusAsync(Instance)).State != CloudInstanceState.Running)
            {
                await Task.Delay(10, _stop.Token);
            }

            var coordinator = new ServerHostCoordinator(
                new GameServerController(Manager),
                Store,
                new ServerHostOptions
                {
                    ServerId = "default",
                    AutoStartGameServer = autoStart,
                    RequestPollInterval = TimeSpan.FromMilliseconds(20),
                    HeartbeatInterval = TimeSpan.FromMilliseconds(100),
                    ReadyTimeout = TimeSpan.FromSeconds(5),
                },
                Environment);
            await coordinator.RunAsync(_stop.Token);
        });
    }

    public async Task StartInstanceAndHostAsync()
    {
        Cloud.StartDuration = TimeSpan.Zero;
        await Cloud.StartAsync(Instance);
        await Cloud.GetStatusAsync(Instance);
        await MarkHostInstalledAsync();
        RunHostWhenInstanceIsRunning();
        for (var i = 0; i < 500; i++)
        {
            if (await Store.GetHeartbeatAsync("default") is { IsReady: true, UpdatedAt: var updated } && Time.GetUtcNow() - updated < TimeSpan.FromSeconds(5))
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("常駐サービスが起動しませんでした。");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_host is not null)
        {
            try
            {
                await _host.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    internal sealed class HostEnvironmentStub : IServerHostEnvironment
    {
        public GameSettings LastSettings { get; set; } = GameSettings.Default;

        public string? LaunchError { get; set; }

        public string? LogFilePathOverride => null;

        public LogSourceMode LogSource => LogSourceMode.Auto;

        public GameSettings LoadLastSettings() => LastSettings;

        public void SaveLastSettings(GameSettings settings) => LastSettings = settings;

        public ServerLaunchRequest? PrepareLaunch(GameSettings settings, out IReadOnlyList<string> errors)
        {
            errors = LaunchError is null ? [] : [LaunchError];
            return LaunchError is null ? GameServerController.CreateLaunchRequest(@"C:\DH\DreadHungerServer.exe", settings, null, null, LogSourceMode.Auto) : null;
        }

        public void Log(HostLogLevel level, string message)
        {
        }
    }
}
