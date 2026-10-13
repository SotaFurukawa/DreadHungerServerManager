using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;

namespace DreadHungerServerManager.Core.Tests;

public sealed class GameServerControllerTests
{
    private static readonly TimeSpan Graceful = TimeSpan.FromSeconds(20);

    [Fact]
    public void CreateLaunchRequest_UsesServerCommandBuilder()
    {
        var settings = GameSettings.Default with { Map = GameMap.Departure, Thralls = 1 };

        var request = GameServerController.CreateLaunchRequest(@" C:\DH\DreadHungerServer.exe ", settings, "-port=7778", null, LogSourceMode.Auto);

        Assert.Equal(@"C:\DH\DreadHungerServer.exe", request.ExecutablePath);
        Assert.Equal(ServerCommandBuilder.BuildArguments(settings, "-port=7778"), request.Arguments);
        Assert.Equal(ServerPaths.GetDefaultLogFilePath(@"C:\DH\DreadHungerServer.exe"), request.LogFilePath);
    }

    [Fact]
    public void ValidateLaunch_ReportsMissingExecutableAndInvalidSettings()
    {
        var errors = GameServerController.ValidateLaunch(@"C:\does-not-exist\DreadHungerServer.exe", GameSettings.Default with { MaxPlayers = 2, Thralls = 2 });

        Assert.Contains(errors, error => error.Contains("見つかりません", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("人狼人数", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_GracefulExit_DoesNotAskToKill()
    {
        var manager = new StubServerProcessManager { NextStopOutcome = StopOutcome.Exited };
        var controller = new GameServerController(manager);

        var result = await controller.StopAsync(Graceful, _ => throw new InvalidOperationException("should not ask"));

        Assert.Equal(StopResult.Stopped, result);
        Assert.Equal(0, manager.KillCount);
    }

    [Fact]
    public async Task Stop_Timeout_KillsWhenConfirmed()
    {
        var manager = new StubServerProcessManager { NextStopOutcome = StopOutcome.TimedOut };
        var controller = new GameServerController(manager);
        StopOutcome? asked = null;

        var result = await controller.StopAsync(Graceful, outcome =>
        {
            asked = outcome;
            return Task.FromResult(true);
        });

        Assert.Equal(StopResult.Stopped, result);
        Assert.Equal(StopOutcome.TimedOut, asked);
        Assert.Equal(1, manager.KillCount);
    }

    [Fact]
    public async Task Stop_Timeout_CancelsWhenDeclined()
    {
        var manager = new StubServerProcessManager { NextStopOutcome = StopOutcome.TimedOut };
        var controller = new GameServerController(manager);

        var result = await controller.StopAsync(Graceful, _ => Task.FromResult(false));

        Assert.Equal(StopResult.Cancelled, result);
        Assert.Equal(1, manager.CancelStopCount);
        Assert.Equal(0, manager.KillCount);
    }

    [Fact]
    public async Task Stop_KillFailure_IsReported()
    {
        var manager = new StubServerProcessManager { NextStopOutcome = StopOutcome.SignalUnavailable, KillSucceeds = false };
        var controller = new GameServerController(manager);

        Assert.Equal(StopResult.Failed, await controller.StopAsync(Graceful, _ => Task.FromResult(true)));
    }

    [Fact]
    public async Task ConcurrentOperations_AreRejected()
    {
        var manager = new StubServerProcessManager { NextStopOutcome = StopOutcome.TimedOut };
        var controller = new GameServerController(manager);
        var confirmation = new TaskCompletionSource<bool>();

        var stopping = controller.StopAsync(Graceful, _ => confirmation.Task);
        await WaitUntilAsync(() => controller.Gate.CurrentOperation is not null);

        var error = await Assert.ThrowsAsync<OperationInProgressException>(() => controller.StartAsync(Request()));
        Assert.Equal("サーバー停止", error.RunningOperation);
        Assert.Contains("Server operation already in progress", error.Message);
        Assert.Equal(0, manager.StartCount);

        confirmation.SetResult(false);
        Assert.Equal(StopResult.Cancelled, await stopping);
        await controller.StartAsync(Request());
        Assert.Equal(1, manager.StartCount);
    }

    [Fact]
    public async Task WaitForReady_CompletesWhenReadyOrStopped()
    {
        var manager = new StubServerProcessManager();
        var controller = new GameServerController(manager);

        manager.SetStatus(new ServerStatus { State = ServerState.Starting });
        var ready = controller.WaitForReadyAsync(TimeSpan.FromSeconds(10));
        Assert.False(ready.IsCompleted);
        manager.SetStatus(new ServerStatus { State = ServerState.Running, IsPortListening = true, ListeningPort = 7777 });
        Assert.True(await ready);

        manager.SetStatus(new ServerStatus { State = ServerState.Starting });
        var failed = controller.WaitForReadyAsync(TimeSpan.FromSeconds(10));
        manager.SetStatus(ServerStatus.Stopped);
        Assert.False(await failed);

        manager.SetStatus(new ServerStatus { State = ServerState.Starting });
        Assert.False(await controller.WaitForReadyAsync(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void OperationGate_ReleasesAfterDispose()
    {
        var gate = new OperationGate();
        using (gate.Enter("EC2 起動"))
        {
            Assert.Equal("EC2 起動", gate.CurrentOperation);
            Assert.Throws<OperationInProgressException>(() => gate.Enter("EC2 停止"));
        }

        Assert.Null(gate.CurrentOperation);
        using var lease = gate.Enter("EC2 停止");
    }

    private static ServerLaunchRequest Request() => new() { ExecutablePath = @"C:\DH\DreadHungerServer.exe", Arguments = "Expanse_Persistent -log" };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class StubServerProcessManager : IServerProcessManager
    {
        private ServerStatus _status = new() { State = ServerState.Running, IsPortListening = true, ListeningPort = 7777 };

        public StopOutcome NextStopOutcome { get; set; } = StopOutcome.Exited;

        public bool KillSucceeds { get; set; } = true;

        public int StartCount { get; private set; }

        public int KillCount { get; private set; }

        public int CancelStopCount { get; private set; }

        public ServerStatus Status => _status;

        public event EventHandler? StatusChanged;

        public event EventHandler<IReadOnlyList<ServerLogLine>>? LogReceived { add { } remove { } }

        public event EventHandler? ServerReady { add { } remove { } }

        public event EventHandler<ServerExitInfo>? ServerExited { add { } remove { } }

        public void SetStatus(ServerStatus status)
        {
            _status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public IReadOnlyList<RunningServerProcess> FindRunningServers() => [];

        public void Start(ServerLaunchRequest request) => StartCount++;

        public RunningServerProcess? AttachToRunningServer(string? logFilePathOverride, LogSourceMode logSource) => null;

        public Task<StopOutcome> StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
        {
            if (NextStopOutcome is StopOutcome.Exited or StopOutcome.AlreadyStopped)
            {
                SetStatus(ServerStatus.Stopped);
            }
            else
            {
                SetStatus(_status with { State = ServerState.Stopping });
            }

            return Task.FromResult(NextStopOutcome);
        }

        public void CancelStop()
        {
            CancelStopCount++;
            SetStatus(_status with { State = ServerState.Running });
        }

        public Task<bool> KillAsync(TimeSpan timeout)
        {
            KillCount++;
            if (KillSucceeds)
            {
                SetStatus(ServerStatus.Stopped);
            }

            return Task.FromResult(KillSucceeds);
        }

        public void Detach()
        {
        }

        public void Dispose()
        {
        }
    }
}
