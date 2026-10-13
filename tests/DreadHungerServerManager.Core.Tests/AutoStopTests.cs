using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;
using DreadHungerServerManager.Core.Services.Cloud;
using DreadHungerServerManager.Core.Services.Simulation;
using Microsoft.Extensions.Time.Testing;

namespace DreadHungerServerManager.Core.Tests;

public sealed class AutoStopTests
{
    private static readonly DateTimeOffset Boot = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly AutoStopConfig Config = AutoStopConfig.Default; // 誰もいない状態30分・最大6時間

    [Fact]
    public void NoGameServer_StopsAfterThirtyMinutesWithOneWarning()
    {
        var monitor = new AutoStopMonitor(Boot, Boot);

        var early = monitor.Evaluate(0, Config, Boot.AddMinutes(10));
        Assert.Equal(new AutoStopPlan(AutoStopReason.Idle, Boot.AddMinutes(30)), early.Plan);
        Assert.False(early.ShouldStop);
        Assert.Null(early.Warning);

        Assert.Equal(TimeSpan.FromMinutes(5), monitor.Evaluate(0, Config, Boot.AddMinutes(25)).Warning);
        Assert.Null(monitor.Evaluate(0, Config, Boot.AddMinutes(27)).Warning);
        Assert.True(monitor.Evaluate(0, Config, Boot.AddMinutes(30)).ShouldStop);
    }

    [Fact]
    public void Players_KeepTheServerAliveAndTheClockRestartsWhenTheyLeave()
    {
        var monitor = new AutoStopMonitor(Boot, Boot);

        var playing = monitor.Evaluate(4, Config, Boot.AddMinutes(50));
        Assert.Equal(AutoStopReason.MaxRuntime, playing.Plan?.Reason);
        Assert.False(playing.ShouldStop);

        // 50分の時点で全員抜けた → 80分に停止予定
        Assert.Equal(Boot.AddMinutes(80), monitor.Evaluate(0, Config, Boot.AddMinutes(51)).Plan?.StopAt);
        Assert.False(monitor.Evaluate(0, Config, Boot.AddMinutes(79)).ShouldStop);
        Assert.True(monitor.Evaluate(0, Config, Boot.AddMinutes(80)).ShouldStop);
    }

    [Fact]
    public void StartingANewServer_GivesAnotherThirtyMinutes()
    {
        var monitor = new AutoStopMonitor(Boot, Boot);
        Assert.NotNull(monitor.Evaluate(0, Config, Boot.AddMinutes(26)).Warning);

        monitor.RecordActivity(Boot.AddMinutes(28));

        var decision = monitor.Evaluate(0, Config, Boot.AddMinutes(29));
        Assert.Equal(Boot.AddMinutes(58), decision.Plan?.StopAt);
        Assert.Null(decision.Warning);

        // 予定が延びたので、5分前の警告はもう一度出る。
        Assert.Equal(TimeSpan.FromMinutes(5), monitor.Evaluate(0, Config, Boot.AddMinutes(53)).Warning);
    }

    [Fact]
    public void MaxRuntime_StopsEvenDuringAMatchWithWarnings()
    {
        var monitor = new AutoStopMonitor(Boot, Boot);
        var warnings = new List<TimeSpan>();
        for (var minute = 300; minute < 360; minute++)
        {
            if (monitor.Evaluate(6, Config, Boot.AddMinutes(minute)).Warning is { } warning)
            {
                warnings.Add(warning);
            }
        }

        Assert.Equal([TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5)], warnings);
        var stop = monitor.Evaluate(6, Config, Boot.AddHours(6));
        Assert.True(stop.ShouldStop);
        Assert.Equal(AutoStopReason.MaxRuntime, stop.Plan?.Reason);
    }

    [Fact]
    public void DisabledRules_NeverStop()
    {
        var monitor = new AutoStopMonitor(Boot, Boot);
        var disabled = Config with { IdleShutdownEnabled = false, MaxRuntimeEnabled = false };

        Assert.Equal(AutoStopDecision.None, monitor.Evaluate(0, disabled, Boot.AddHours(48)));
        Assert.Equal(AutoStopReason.MaxRuntime, monitor.Evaluate(0, Config with { IdleShutdownEnabled = false }, Boot.AddHours(1)).Plan?.Reason);
    }

    [Fact]
    public async Task Host_StopsTheGameServerAndThenItsOwnInstance()
    {
        var time = new FakeTimeProvider(Boot);
        var cloud = new FakeCloudInstanceService(time, CloudInstanceState.Running);
        var self = new CloudInstanceRef("ap-northeast-3", "i-0123456789abcdef0");
        var store = new InMemoryServerControlStore();
        var manager = new SimulatedServerProcessManager();
        var notifier = new RecordingNotifier();
        var log = new List<string>();

        // GUI から「誰もいない状態が5分続いたら停止」を反映した状態
        await store.PutAutoStopConfigAsync("default", AutoStopConfig.Default with { IdleTimeoutMinutes = 5, UpdatedBy = "GUI:TEST" });

        var coordinator = new ServerHostCoordinator(
            new GameServerController(manager),
            store,
            new ServerHostOptions { ServerId = "default", RequestPollInterval = TimeSpan.FromSeconds(5), InstanceStartedAt = Boot },
            new LoggingEnvironment(log),
            time,
            cloud,
            self,
            notifier);
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => coordinator.RunAsync(stop.Token));

        // 自動起動したゲームサーバーが起動完了するまで待ち、参加者0人のまま時間を進める。
        await WaitUntilAsync(() => manager.Status.State == ServerState.Running);
        for (var i = 0; i < 80 && cloud.StopRequestCount == 0; i++)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(15);
        }

        Assert.Equal(1, cloud.StopRequestCount);
        Assert.Equal(1, manager.StopCount);
        Assert.Contains(notifier.Warnings, warning => warning == TimeSpan.FromMinutes(5));
        Assert.Single(notifier.Stops);
        Assert.Contains(log, line => line.Contains("自動停止の設定を反映しました", StringComparison.Ordinal));
        var heartbeat = await store.GetHeartbeatAsync("default");
        Assert.True(heartbeat?.IsAutoStopping);
        Assert.Equal(5, heartbeat?.AutoStop?.IdleTimeoutMinutes);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void CloudSettings_DefaultsMatchTheAgreedRules()
    {
        var config = CloudSettings.Default.ToAutoStopConfig();

        Assert.True(config.IdleShutdownEnabled);
        Assert.Equal(30, config.IdleTimeoutMinutes);
        Assert.True(config.MaxRuntimeEnabled);
        Assert.Equal(6, config.MaxRuntimeHours);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class RecordingNotifier : IAutoStopNotifier
    {
        public List<TimeSpan> Warnings { get; } = [];

        public List<AutoStopPlan> Stops { get; } = [];

        public Task NotifyWarningAsync(AutoStopPlan plan, TimeSpan remaining, CancellationToken cancellationToken)
        {
            Warnings.Add(remaining);
            return Task.CompletedTask;
        }

        public Task NotifyStoppingAsync(AutoStopPlan plan, CancellationToken cancellationToken)
        {
            Stops.Add(plan);
            return Task.CompletedTask;
        }
    }

    private sealed class LoggingEnvironment(List<string> log) : IServerHostEnvironment
    {
        public string? LogFilePathOverride => null;

        public LogSourceMode LogSource => LogSourceMode.Auto;

        public GameSettings LoadLastSettings() => GameSettings.Default;

        public void SaveLastSettings(GameSettings settings)
        {
        }

        public ServerLaunchRequest? PrepareLaunch(GameSettings settings, out IReadOnlyList<string> errors)
        {
            errors = [];
            return GameServerController.CreateLaunchRequest(@"C:\DH\DreadHungerServer.exe", settings, null, null, LogSourceMode.Auto);
        }

        public void Log(HostLogLevel level, string message)
        {
            lock (log)
            {
                log.Add(message);
            }
        }
    }
}
