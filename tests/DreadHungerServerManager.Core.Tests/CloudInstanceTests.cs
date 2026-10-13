using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services.Cloud;
using Microsoft.Extensions.Time.Testing;

namespace DreadHungerServerManager.Core.Tests;

public sealed class CloudInstanceTests
{
    private static readonly CloudInstanceRef Instance = new("ap-northeast-1", "i-0123456789abcdef0");
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 6, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData("ap-northeast-1", "i-0123456789abcdef0", true)]
    [InlineData("us-gov-west-1", "i-01234567", true)]
    [InlineData("ap-northeast-1", "i-XYZ", false)]
    [InlineData("Tokyo", "i-0123456789abcdef0", false)]
    [InlineData("", "i-0123456789abcdef0", false)]
    public void InstanceRef_ValidatesRegionAndInstanceId(string region, string instanceId, bool valid)
    {
        var errors = new[] { CloudInstanceRef.ValidateRegion(region), CloudInstanceRef.ValidateInstanceId(instanceId) };

        Assert.Equal(valid, errors.All(error => error is null));
        if (!valid)
        {
            Assert.Throws<ArgumentException>(() => new CloudInstanceRef(region, instanceId));
        }
    }

    [Fact]
    public async Task Fake_StoppedToStartingToRunning_AssignsANewPublicIpEachStart()
    {
        var cloud = new FakeCloudInstanceService(_time) { StartDuration = TimeSpan.FromSeconds(30), StopDuration = TimeSpan.FromSeconds(20) };

        Assert.Equal(CloudInstanceState.Stopped, (await cloud.GetStatusAsync(Instance)).State);
        Assert.Null(await cloud.GetPublicIpAsync(Instance));

        await cloud.StartAsync(Instance);
        Assert.Equal(CloudInstanceState.Starting, (await cloud.GetStatusAsync(Instance)).State);
        _time.Advance(TimeSpan.FromSeconds(30));
        var running = await cloud.GetStatusAsync(Instance);
        Assert.Equal(CloudInstanceState.Running, running.State);
        Assert.Equal("running", running.RawState);
        var firstIp = await cloud.GetPublicIpAsync(Instance);
        Assert.NotNull(firstIp);

        await cloud.StopAsync(Instance);
        Assert.Equal(CloudInstanceState.Stopping, (await cloud.GetStatusAsync(Instance)).State);
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(CloudInstanceState.Stopped, (await cloud.GetStatusAsync(Instance)).State);
        Assert.Null(await cloud.GetPublicIpAsync(Instance));

        await cloud.StartAsync(Instance);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.NotEqual(firstIp, await cloud.GetPublicIpAsync(Instance));
    }

    [Fact]
    public async Task Fake_ElasticIp_IsKeptAcrossRestarts()
    {
        var cloud = new FakeCloudInstanceService(_time, CloudInstanceState.Running) { ElasticIp = "198.51.100.7", StopDuration = TimeSpan.Zero, StartDuration = TimeSpan.Zero };

        Assert.Equal("198.51.100.7", await cloud.GetPublicIpAsync(Instance));
        await cloud.StopAsync(Instance);
        await cloud.StartAsync(Instance);
        Assert.Equal("198.51.100.7", await cloud.GetPublicIpAsync(Instance));
    }

    [Fact]
    public async Task Fake_RejectsOperationsThatAwsWouldReject()
    {
        var cloud = new FakeCloudInstanceService(_time, CloudInstanceState.Running);
        await cloud.StopAsync(Instance);

        var startWhileStopping = await Assert.ThrowsAsync<CloudOperationException>(() => cloud.StartAsync(Instance));
        Assert.Equal(CloudErrorKind.InvalidState, startWhileStopping.Kind);
        await Assert.ThrowsAsync<CloudOperationException>(() => cloud.RebootAsync(Instance));

        // 停止済み・停止処理中への停止要求や、稼働中への起動要求は何もしない（AWS と同じく冪等）。
        await cloud.StopAsync(Instance);
        Assert.Equal(CloudInstanceState.Stopping, (await cloud.GetStatusAsync(Instance)).State);
    }

    [Fact]
    public async Task Fake_UnknownInstance_IsNotFound()
    {
        var cloud = new FakeCloudInstanceService(_time) { KnownInstanceId = "i-0fedcba9876543210" };

        var error = await Assert.ThrowsAsync<CloudOperationException>(() => cloud.GetStatusAsync(Instance));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task WaitForRunning_ReturnsWhenRunning()
    {
        var cloud = new FakeCloudInstanceService(_time) { StartDuration = TimeSpan.FromSeconds(40) };
        await cloud.StartAsync(Instance);
        var reported = new List<CloudInstanceState>();

        var wait = cloud.WaitForStateAsync(Instance, CloudInstanceState.Running, TimeSpan.FromMinutes(5), Poll, new SyncProgress(status => reported.Add(status.State)), _time);
        var status = await AdvanceUntilCompletedAsync(wait);

        Assert.Equal(CloudInstanceState.Running, status.State);
        Assert.NotNull(status.PublicIpAddress);
        Assert.Equal(CloudInstanceState.Starting, reported[0]);
        Assert.Equal(CloudInstanceState.Running, reported[^1]);
    }

    [Fact]
    public async Task WaitForRunning_WhenStartFails_ReportsTheReason()
    {
        var cloud = new FakeCloudInstanceService(_time) { FailStartTransition = true };
        await cloud.StartAsync(Instance);

        var wait = cloud.WaitForStateAsync(Instance, CloudInstanceState.Running, TimeSpan.FromMinutes(5), Poll, timeProvider: _time);
        var error = await Assert.ThrowsAsync<CloudOperationException>(() => AdvanceUntilCompletedAsync(wait));

        Assert.Equal(CloudErrorKind.InvalidState, error.Kind);
        Assert.Contains("InsufficientInstanceCapacity", error.Message);
    }

    [Fact]
    public async Task WaitForStopped_TimesOut()
    {
        var cloud = new FakeCloudInstanceService(_time, CloudInstanceState.Running) { StopDuration = TimeSpan.FromMinutes(10) };
        await cloud.StopAsync(Instance);

        var wait = cloud.WaitForStateAsync(Instance, CloudInstanceState.Stopped, TimeSpan.FromMinutes(2), Poll, timeProvider: _time);
        var error = await Assert.ThrowsAsync<CloudOperationException>(() => AdvanceUntilCompletedAsync(wait));

        Assert.Equal(CloudErrorKind.Timeout, error.Kind);
        Assert.Contains("停止処理中", error.Message);
    }

    [Fact]
    public async Task ApiFailures_ArePropagated()
    {
        var cloud = new FakeCloudInstanceService(_time);
        cloud.FailNext(FakeCloudOperation.Start, new CloudOperationException(CloudErrorKind.AccessDenied, "権限がありません。"));

        var error = await Assert.ThrowsAsync<CloudOperationException>(() => cloud.StartAsync(Instance));

        Assert.Equal(CloudErrorKind.AccessDenied, error.Kind);
        Assert.Equal(CloudInstanceState.Stopped, (await cloud.GetStatusAsync(Instance)).State);
        await cloud.StartAsync(Instance);
        Assert.Equal(1, cloud.StartRequestCount);
    }

    [Fact]
    public async Task Fake_PublicIpUnavailable_ReturnsNull()
    {
        var cloud = new FakeCloudInstanceService(_time, CloudInstanceState.Running) { PublicIpUnavailable = true };

        Assert.Equal(CloudInstanceState.Running, (await cloud.GetStatusAsync(Instance)).State);
        Assert.Null(await cloud.GetPublicIpAsync(Instance));
    }

    /// <summary>待機中のタスクが終わるまで、疑似時計を少しずつ進める。</summary>
    private async Task<T> AdvanceUntilCompletedAsync<T>(Task<T> task)
    {
        for (var i = 0; i < 1000 && !task.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class SyncProgress(Action<CloudInstanceStatus> report) : IProgress<CloudInstanceStatus>
    {
        public void Report(CloudInstanceStatus value) => report(value);
    }
}
