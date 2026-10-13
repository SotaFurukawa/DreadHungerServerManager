using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;

namespace DreadHungerServerManager.Core.Tests;

public sealed class ServerLogAnalyzerTests
{
    [Fact]
    public void ListeningLine_YieldsPort()
    {
        const string line = "[2026.10.13-03.12.45:123][  0]LogNet: GameNetDriver IpNetDriver_2147482476 IpNetDriver listening on port 7777";

        Assert.True(ServerLogAnalyzer.TryGetListeningPort(line, out var port));
        Assert.Equal(7777, port);
        Assert.Equal(LogLineKind.Success, ServerLogAnalyzer.Classify(line));
    }

    [Fact]
    public void BeaconListeningLine_IsNotTreatedAsServerReady()
    {
        const string line = "LogNet: BeaconNetDriver IpNetDriver_1 IpNetDriver listening on port 15000";

        Assert.False(ServerLogAnalyzer.TryGetListeningPort(line, out _));
    }

    [Fact]
    public void WaitingToStart_IsDetected()
    {
        Assert.True(ServerLogAnalyzer.IsWaitingToStart("LogGameMode: Display: Match State Changed from EnteringMap to WaitingToStart"));
        Assert.False(ServerLogAnalyzer.IsWaitingToStart("LogGameMode: Display: Match State Changed from WaitingToStart to InProgress"));
    }

    [Theory]
    [InlineData("[2026.10.13-06.43.59:245][775]LogGenericPlatformMisc: FPlatformMisc::RequestExit(0)", true)]
    [InlineData("[2026.10.13-06.43.59:247][775]LogCore: Engine exit requested (reason: GenericPlatform RequestExit)", true)]
    [InlineData("[2026.10.13-06.39.11:670][156]LogCore: Engine exit requested (reason: ConsoleCtrl RequestExit)", false)]
    [InlineData("LogCore: Engine exit requested (reason: EngineExit() was called; note: exit was already requested)", false)]
    public void ServerExitRequest_ExcludesCtrlC(string line, bool expected)
    {
        Assert.Equal(expected, ServerLogAnalyzer.IsServerExitRequest(line));
    }

    [Fact]
    public void MatchState_IsTheStateAfterTheChange()
    {
        Assert.True(ServerLogAnalyzer.TryGetMatchState("LogGameMode: Display: Match State Changed from InProgress to WaitingPostMatch", out var state));
        Assert.Equal(MatchStates.WaitingPostMatch, state);
        Assert.False(ServerLogAnalyzer.TryGetMatchState("LogGameMode: GameMode returned ReadyToEndMatch", out _));
    }

    [Theory]
    [InlineData("LogWindows: Error: === Critical error: ===", LogLineKind.Error)]
    [InlineData("LogStreaming: Warning: Failed to read file", LogLineKind.Warning)]
    [InlineData("LogInit: Display: Running engine", LogLineKind.Normal)]
    public void Lines_AreClassified(string line, LogLineKind expected)
    {
        Assert.Equal(expected, ServerLogAnalyzer.Classify(line));
    }
}
