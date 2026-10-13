using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;

namespace DreadHungerServerManager.Core.Tests;

[CollectionDefinition(nameof(ServerProcessCollection), DisableParallelization = true)]
public sealed class ServerProcessCollection;

/// <summary>
/// 疑似サーバー（tools\FakeDreadHungerServer）を実際に起動して、起動完了検出・安全な停止・
/// 二重起動防止・異常終了検出・強制終了・起動済みサーバーの検出を確認する結合テスト。
/// </summary>
[Collection(nameof(ServerProcessCollection))]
public sealed class ServerProcessManagerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _serverDirectory;
    private readonly List<ServerProcessManager> _managers = [];

    public ServerProcessManagerTests()
    {
        _serverDirectory = Path.Combine(Path.GetTempPath(), "dhsm-tests", Guid.NewGuid().ToString("N"), "WindowsServer");
        CopyFakeServer(_serverDirectory);
    }

    private string ExecutablePath => Path.Combine(_serverDirectory, "DreadHungerServer.exe");

    private string LogFilePath => ServerPaths.GetDefaultLogFilePath(ExecutablePath)!;

    public void Dispose()
    {
        foreach (var manager in _managers)
        {
            manager.KillAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            manager.Dispose();
        }

        KillLeftovers();
        try
        {
            Directory.Delete(Path.GetDirectoryName(_serverDirectory)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Start_DetectsReadyFromLogFile_TracksChildAndStopsGracefully()
    {
        var manager = CreateManager();
        var lines = CollectLines(manager);
        var ready = WaitForReady(manager);
        var exited = WaitForExit(manager);

        manager.Start(Request("-fakehidden"));
        await ready.WaitAsync(Timeout);

        var status = manager.Status;
        Assert.Equal(ServerState.Running, status.State);
        Assert.True(status.IsPortListening);
        Assert.Equal(7777, status.ListeningPort);
        await WaitUntilAsync(() => manager.Status.IsWaitingToStart && manager.Status.MainProcessId != manager.Status.ProcessId);
        Assert.Contains(lines, line => line.Source == LogLineSource.LogFile && line.Text.Contains("IpNetDriver listening on port 7777"));

        var outcome = await manager.StopAsync(TimeSpan.FromSeconds(15));
        var exitInfo = await exited.WaitAsync(Timeout);

        Assert.Equal(StopOutcome.Exited, outcome);
        Assert.True(exitInfo.WasRequested);
        Assert.Equal(0, exitInfo.ExitCode);
        Assert.Equal(ServerState.Stopped, manager.Status.State);
        Assert.Contains("LogExit: Exiting.", File.ReadAllText(LogFilePath));
    }

    [Fact]
    public async Task SingleProcessServer_IsReadFromStandardOutputWhenLogFileIsDisabled()
    {
        var manager = CreateManager();
        var lines = CollectLines(manager);
        var ready = WaitForReady(manager);

        manager.Start(Request("-fakesingle") with { LogSource = LogSourceMode.StandardOutput });
        await ready.WaitAsync(Timeout);
        await WaitUntilAsync(() => lines.Any(line => line.Source == LogLineSource.StandardOutput && line.Text.Contains("WaitingToStart")));

        Assert.True(manager.Status.IsWaitingToStart);
        Assert.DoesNotContain(lines, line => line.Source == LogLineSource.LogFile);
        Assert.Equal(StopOutcome.Exited, await manager.StopAsync(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task Start_WhenServerIsAlreadyRunning_IsRejected()
    {
        var first = CreateManager();
        var ready = WaitForReady(first);
        first.Start(Request("-fakehidden"));
        await ready.WaitAsync(Timeout);

        var second = CreateManager();
        var error = Assert.Throws<ServerStartException>(() => second.Start(Request("-fakehidden")));

        Assert.Equal(ServerStartError.AlreadyRunning, error.Error);
        Assert.Equal(ServerState.Stopped, second.Status.State);
        Assert.Throws<ServerStartException>(() => first.Start(Request("-fakehidden")));
    }

    [Fact]
    public void Start_WithMissingExecutable_IsRejected()
    {
        var manager = CreateManager();

        var error = Assert.Throws<ServerStartException>(() => manager.Start(new ServerLaunchRequest
        {
            ExecutablePath = Path.Combine(_serverDirectory, "missing", "DreadHungerServer.exe"),
            Arguments = "Expanse_Persistent -log",
        }));

        Assert.Equal(ServerStartError.ExecutableNotFound, error.Error);
        Assert.Equal(ServerState.Stopped, manager.Status.State);
    }

    [Fact]
    public async Task UnexpectedExit_IsReportedWithExitCode()
    {
        var manager = CreateManager();
        var exited = WaitForExit(manager);

        manager.Start(Request("-fakehidden", "-fakecrash=2", "-fakedelay=200"));
        var info = await exited.WaitAsync(Timeout);

        Assert.True(info.IsAbnormal);
        Assert.Equal(ServerExitKind.Unexpected, info.Kind);
        Assert.Equal(3, info.ExitCode);
        Assert.Equal(ServerState.Stopped, manager.Status.State);
    }

    [Fact]
    public async Task MatchEnd_IsReportedAsEndedByServerAndPlayersAreCounted()
    {
        var manager = CreateManager();
        var exited = WaitForExit(manager);

        manager.Start(Request("-fakehidden", "-fakedelay=200", "-fakeplayer=Tester", "-fakematchend=3"));
        await WaitUntilAsync(() => manager.Status.PlayerCount == 1);
        Assert.Equal(MatchStates.WaitingToStart, manager.Status.MatchState);
        var info = await exited.WaitAsync(Timeout);

        Assert.Equal(ServerExitKind.EndedByServer, info.Kind);
        Assert.False(info.IsAbnormal);
        Assert.False(info.WasRequested);
        Assert.Equal(0, info.ExitCode);
        Assert.Equal(0, manager.Status.PlayerCount);
    }

    [Fact]
    public async Task ServerIgnoringCtrlC_TimesOutAndCanBeKilledWithItsChild()
    {
        var manager = CreateManager();
        var ready = WaitForReady(manager);
        manager.Start(Request("-fakehidden", "-fakeignorectrlc"));
        await ready.WaitAsync(Timeout);
        await WaitUntilAsync(() => manager.Status.MainProcessId != manager.Status.ProcessId);
        var childId = manager.Status.MainProcessId!.Value;

        var outcome = await manager.StopAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StopOutcome.TimedOut, outcome);
        Assert.Equal(ServerState.Stopping, manager.Status.State);

        Assert.True(await manager.KillAsync(Timeout));
        Assert.Equal(ServerState.Stopped, manager.Status.State);
        Assert.True(HasExited(childId));
    }

    [Fact]
    public async Task CancelStop_ReturnsToRunning()
    {
        var manager = CreateManager();
        var ready = WaitForReady(manager);
        manager.Start(Request("-fakehidden", "-fakeignorectrlc"));
        await ready.WaitAsync(Timeout);

        Assert.Equal(StopOutcome.TimedOut, await manager.StopAsync(TimeSpan.FromSeconds(1)));
        manager.CancelStop();

        Assert.Equal(ServerState.Running, manager.Status.State);
    }

    [Fact]
    public async Task DetachedServer_CanBeAttachedAndStoppedByAnotherManager()
    {
        var first = CreateManager();
        var ready = WaitForReady(first);
        first.Start(Request("-fakehidden"));
        await ready.WaitAsync(Timeout);
        await WaitUntilAsync(() => first.Status.MainProcessId != first.Status.ProcessId);
        first.Detach();

        var second = CreateManager();
        var lines = CollectLines(second);
        var exited = WaitForExit(second);
        var attached = second.AttachToRunningServer(logFilePathOverride: null, LogSourceMode.Auto);

        Assert.NotNull(attached);
        var status = second.Status;
        Assert.Equal(ServerState.Running, status.State);
        Assert.True(status.IsAttached);
        Assert.True(status.IsPortListening);
        Assert.Equal(LogFilePath, status.LogFilePath, ignoreCase: true);
        Assert.Contains(lines, line => line.Text.Contains("WaitingToStart"));

        Assert.Equal(StopOutcome.Exited, await second.StopAsync(TimeSpan.FromSeconds(15)));
        Assert.True((await exited.WaitAsync(Timeout)).WasRequested);
    }

    private ServerLaunchRequest Request(params string[] fakeOptions) => new()
    {
        ExecutablePath = ExecutablePath,
        Arguments = ServerCommandBuilder.BuildArguments(GameSettings.Default, string.Join(' ', fakeOptions)),
        LogFilePath = LogFilePath,
        LogSource = LogSourceMode.Auto,
    };

    private ServerProcessManager CreateManager()
    {
        var manager = new ServerProcessManager(new ServerProcessManagerOptions
        {
            MonitorInterval = TimeSpan.FromMilliseconds(200),
            LogPollInterval = TimeSpan.FromMilliseconds(100),
        });
        _managers.Add(manager);
        return manager;
    }

    private static ConcurrentBag<ServerLogLine> CollectLines(ServerProcessManager manager)
    {
        var lines = new ConcurrentBag<ServerLogLine>();
        manager.LogReceived += (_, received) =>
        {
            foreach (var line in received)
            {
                lines.Add(line);
            }
        };
        return lines;
    }

    private static Task WaitForReady(ServerProcessManager manager)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ServerReady += (_, _) => completion.TrySetResult();
        return completion.Task;
    }

    private static Task<ServerExitInfo> WaitForExit(ServerProcessManager manager)
    {
        var completion = new TaskCompletionSource<ServerExitInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ServerExited += (_, info) => completion.TrySetResult(info);
        return completion.Task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < Timeout, "Condition was not met in time.");
            await Task.Delay(100);
        }
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private void KillLeftovers()
    {
        foreach (var process in Process.GetProcessesByName("DreadHungerServer"))
        {
            try
            {
                if (process.MainModule?.FileName?.StartsWith(_serverDirectory, StringComparison.OrdinalIgnoreCase) == true)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void CopyFakeServer(string destination)
    {
        var source = typeof(ServerProcessManagerTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "FakeServerOutputDirectory")
            .Value!;
        Assert.True(File.Exists(Path.Combine(source, "DreadHungerServer.exe")), $"Fake server is not built: {source}");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }
}
