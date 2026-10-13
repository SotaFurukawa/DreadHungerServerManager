using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;
using DreadHungerServerManager.Core.Services.Cloud;
using Microsoft.Extensions.Time.Testing;

namespace DreadHungerServerManager.Bot.Tests;

public sealed class BotWorkflowsTests
{
    private static readonly GameSettings Departure = GameSettings.Default with { Map = GameMap.Departure, CoalBurnRate = 1.5 };

    [Fact]
    public async Task Start_WhenStopped_StartsEc2AndReportsTheAddress()
    {
        await using var host = new TestHost();
        await host.MarkHostInstalledAsync();
        host.RunHostWhenInstanceIsRunning();
        var progress = new List<string>();

        var result = await host.Workflows.StartAsync(Departure, TestHost.Member, new SyncProgress(progress.Add));

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("Dread Hunger Server Online", result.Message);
        Assert.Contains("山頂（Departure）", result.Message);
        Assert.Matches(@"Address:\*\* `203\.0\.113\.\d+:7777`", result.Message);
        Assert.Contains("Players:** 0 / 8", result.Message);
        AssertNoLocation(result.Message);
        Assert.Contains(progress, line => line.Contains("EC2 Starting", StringComparison.Ordinal));
        Assert.Contains("coalburnrate=1.5", host.Manager.LastLaunch?.Arguments);
        Assert.Equal("Discord:100 (Alice)", (await host.Store.GetRequestAsync("default"))?.RequestedBy);
    }

    [Fact]
    public async Task Start_WhenAlreadyRunning_DoesNotStartAgain()
    {
        await using var host = new TestHost();
        await host.StartInstanceAndHostAsync();

        var result = await host.Workflows.StartAsync(Departure, TestHost.Member);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("既に起動", result.Message);
        Assert.Contains("/restart", result.Message);
        Assert.Equal(1, host.Cloud.StartRequestCount);
        Assert.Equal(1, host.Manager.StartCount);
    }

    [Fact]
    public async Task Start_WhenEc2FailsToStart_ReportsTheErrorWithoutTheLocation()
    {
        await using var host = new TestHost();
        host.Cloud.FailNext(FakeCloudOperation.Start, new CloudOperationException(
            CloudErrorKind.AccessDenied,
            "User: arn:aws:sts::111122223333:assumed-role/dhsm-bot/x is not authorized to perform ec2:StartInstances on i-0123456789abcdef0"));

        var result = await host.Workflows.StartAsync(Departure, TestHost.Member);

        Assert.False(result.Succeeded);
        Assert.Contains("権限がない", result.Message);
        Assert.Contains("管理者", result.Message);
        AssertNoLocation(result.Message);
        Assert.Contains(host.Log, line => line.Contains("i-0123456789abcdef0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_WhenTheInstanceIsMissing_HidesTheIdAndRegion()
    {
        await using var host = new TestHost();
        host.Cloud.KnownInstanceId = "i-0fffffffffffffff0";

        var result = await host.Workflows.StatusAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("設定に問題", result.Message);
        AssertNoLocation(result.Message);
    }

    [Fact]
    public async Task Start_WhenCapacityIsLacking_ReportsTheReason()
    {
        await using var host = new TestHost();
        host.Cloud.FailStartTransition = true;

        var result = await host.Workflows.StartAsync(Departure, TestHost.Member);

        Assert.False(result.Succeeded);
        Assert.Contains("InsufficientInstanceCapacity", result.Message);
    }

    [Fact]
    public async Task Start_WhenPublicIpIsMissing_ReportsIt()
    {
        await using var host = new TestHost();
        host.Cloud.PublicIpUnavailable = true;
        await host.MarkHostInstalledAsync();
        host.RunHostWhenInstanceIsRunning();

        var result = await host.Workflows.StartAsync(Departure, TestHost.Member);

        Assert.False(result.Succeeded);
        Assert.Contains("パブリック IP を取得できませんでした", result.Message);
    }

    [Fact]
    public async Task Start_WhenTheGameServerFails_ReportsIt()
    {
        await using var host = new TestHost();
        host.Environment.LaunchError = "DreadHungerServer.exe が見つかりません: C:\\DH\\DreadHungerServer.exe";
        await host.MarkHostInstalledAsync();
        host.RunHostWhenInstanceIsRunning(autoStart: false);

        var result = await host.Workflows.StartAsync(Departure, TestHost.Member);

        Assert.False(result.Succeeded);
        Assert.Contains("ゲームサーバーを起動できませんでした", result.Message);
        Assert.Contains("見つかりません", result.Message);
    }

    [Fact]
    public async Task Start_WhileAnotherOperationRuns_IsRejected()
    {
        await using var host = new TestHost();
        var other = await new ServerOperationLock(host.Store).TryAcquireAsync("default", "停止", "Discord:300 (Carol)", TimeSpan.FromMinutes(15), "Carol");
        Assert.True(other.IsAcquired);

        var result = await host.Workflows.StartAsync(Departure, TestHost.Member);

        Assert.False(result.Succeeded);
        Assert.Contains("Server operation already in progress", result.Message);
        Assert.Contains("停止、Carol", result.Message);
        Assert.DoesNotContain("300", result.Message);
        Assert.Equal(0, host.Cloud.StartRequestCount);
    }

    [Fact]
    public async Task ConcurrentStarts_OnlyOneRuns()
    {
        await using var host = new TestHost();
        await host.MarkHostInstalledAsync();
        host.RunHostWhenInstanceIsRunning();

        var results = await Task.WhenAll(
            host.Workflows.StartAsync(Departure, TestHost.Member),
            Task.Run(async () =>
            {
                await Task.Delay(30);
                return await host.Workflows.StartAsync(Departure, TestHost.Member);
            }));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => result.Message.Contains("Server operation already in progress", StringComparison.Ordinal));
        Assert.Equal(1, host.Cloud.StartRequestCount);
    }

    [Fact]
    public async Task InvalidSettings_AreRejected()
    {
        await using var host = new TestHost();

        var result = await host.Workflows.StartAsync(GameSettings.Default with { MaxPlayers = 2, Thralls = 3 }, TestHost.Member);

        Assert.False(result.Succeeded);
        Assert.Contains("人狼人数", result.Message);
        Assert.Equal(0, host.Cloud.StartRequestCount);
    }

    [Fact]
    public async Task Stop_StopsTheGameServerAndThenEc2()
    {
        await using var host = new TestHost();
        await host.StartInstanceAndHostAsync();
        var progress = new List<string>();

        var result = await host.Workflows.StopAsync(TestHost.Member, new SyncProgress(progress.Add));

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("Stopped", result.Message);
        Assert.Equal(1, host.Manager.StopCount);
        Assert.Equal(CloudInstanceState.Stopped, (await host.Cloud.GetStatusAsync(TestHost.Instance)).State);
        Assert.Contains(progress, line => line.Contains("安全に停止", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_WhenAlreadyStopped_SaysSo()
    {
        await using var host = new TestHost();

        var result = await host.Workflows.StopAsync(TestHost.Member);

        Assert.True(result.Succeeded);
        Assert.Contains("既に停止", result.Message);
        Assert.Equal(0, host.Cloud.StopRequestCount);
    }

    [Fact]
    public async Task Restart_UsesTheNewSettings()
    {
        await using var host = new TestHost();
        await host.StartInstanceAndHostAsync();

        var result = await host.Workflows.RestartAsync(Departure, TestHost.Member);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("Restarted", result.Message);
        Assert.Contains("Departure_Persistent", host.Manager.LastLaunch?.Arguments);
    }

    [Fact]
    public async Task Restart_WhenEc2IsStopped_SuggestsStart()
    {
        await using var host = new TestHost();

        var result = await host.Workflows.RestartAsync(Departure, TestHost.Member);

        Assert.False(result.Succeeded);
        Assert.Contains("/start", result.Message);
    }

    [Fact]
    public async Task Status_ShowsEc2AndServerSeparately()
    {
        await using var host = new TestHost();
        await host.StartInstanceAndHostAsync();

        var result = await host.Workflows.StatusAsync();

        Assert.Contains("**EC2:** Running", result.Message);
        Assert.Contains("**Server:** Online（参加待ち）", result.Message);
        Assert.Contains("**Address:** `203.0.113.", result.Message);
        Assert.Contains("**Uptime:**", result.Message);
        AssertNoLocation(result.Message);
    }

    [Fact]
    public async Task LastSettings_ComeFromTheLastRunningServer()
    {
        await using var host = new TestHost();
        await host.MarkHostInstalledAsync(Departure);

        Assert.Equal(Departure, await host.Workflows.GetLastSettingsAsync());
    }

    [Fact]
    public async Task Watchdog_WarnsOnceBeforeAnIdleStop()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero));
        await using var host = new TestHost(time);
        await host.Cloud.StartAsync(TestHost.Instance);
        time.Advance(TimeSpan.FromMinutes(1));
        await PutAliveHeartbeatAsync(host, time, stopAt: time.GetUtcNow().AddMinutes(4), AutoStopReason.Idle);

        var first = await host.Workflows.RunWatchdogAsync();
        var second = await host.Workflows.RunWatchdogAsync();

        var warning = Assert.Single(first);
        Assert.Contains("自動停止します", warning);
        Assert.Contains("延長", warning);
        Assert.Empty(second);
    }

    [Fact]
    public async Task Watchdog_WarnsThirtyMinutesBeforeTheMaxRuntime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero));
        await using var host = new TestHost(time);
        await host.Cloud.StartAsync(TestHost.Instance);
        time.Advance(TimeSpan.FromMinutes(1));
        await PutAliveHeartbeatAsync(host, time, stopAt: time.GetUtcNow().AddMinutes(27), AutoStopReason.MaxRuntime);

        var messages = await host.Workflows.RunWatchdogAsync();

        Assert.Contains("最大稼働時間", Assert.Single(messages));
    }

    [Fact]
    public async Task Watchdog_StopsEc2WhenTheHostIsSilent()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero));
        await using var host = new TestHost(time);
        host.Cloud.StartDuration = TimeSpan.Zero;
        await host.Cloud.StartAsync(TestHost.Instance);
        await host.Cloud.GetStatusAsync(TestHost.Instance);
        time.Advance(TimeSpan.FromMinutes(45));

        var messages = await host.Workflows.RunWatchdogAsync();

        Assert.Contains("応答しない", Assert.Single(messages));
        Assert.Equal(1, host.Cloud.StopRequestCount);
    }

    [Fact]
    public async Task Watchdog_ReportsAnAutoStopOnce()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero));
        await using var host = new TestHost(time);
        await host.Store.PutHeartbeatAsync("default", new ServerHeartbeat
        {
            ServerId = "default",
            UpdatedAt = time.GetUtcNow(),
            IsAutoStopping = true,
            AutoStopAt = time.GetUtcNow(),
            AutoStopReason = "誰も遊んでいない状態が続いたため",
        });

        var first = await host.Workflows.RunWatchdogAsync();
        var second = await host.Workflows.RunWatchdogAsync();

        Assert.Contains("自動停止しました", Assert.Single(first));
        Assert.Empty(second);
    }

    /// <summary>利用者に EC2 の場所（インスタンスID・リージョン・アカウント）が見えない。</summary>
    private static void AssertNoLocation(string message)
    {
        Assert.DoesNotContain("i-0", message);
        Assert.DoesNotContain("ap-northeast-3", message);
        Assert.DoesNotContain("111122223333", message);
        Assert.DoesNotContain("arn:", message);
    }

    private static Task PutAliveHeartbeatAsync(TestHost host, TimeProvider time, DateTimeOffset stopAt, AutoStopReason reason) =>
        host.Store.PutHeartbeatAsync("default", new ServerHeartbeat
        {
            ServerId = "default",
            UpdatedAt = time.GetUtcNow(),
            State = ServerState.Running,
            IsReady = true,
            AutoStopAt = stopAt,
            AutoStopKind = reason,
            AutoStopReason = new AutoStopPlan(reason, stopAt).ReasonText,
        });

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
