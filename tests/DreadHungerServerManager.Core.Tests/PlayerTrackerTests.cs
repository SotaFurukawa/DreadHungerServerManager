using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;

namespace DreadHungerServerManager.Core.Tests;

public sealed class PlayerTrackerTests
{
    private static readonly string SoloMatchLogPath = Path.Combine(AppContext.BaseDirectory, "TestData", "RealServer-SoloMatch.log");

    private const string LoginA = "LogNet: Login request: ?Name=Alice userId: EOSPlus:1_+_|aaa platform: EOSPlus";
    private const string JoinA = "LogNet: Join succeeded: Alice";
    private const string CloseA = "LogNet: UNetConnection::Close: [UNetConnection] RemoteAddr: 203.0.113.1:1, Name: IpConnection_1, Driver: GameNetDriver IpNetDriver_1, IsServer: YES, PC: BP_DreadPlayerController_C_1, Owner: BP_DreadPlayerController_C_1, UniqueId: EOSPlus:1_+_|aaa, Channels: 15, Time: 2026.10.13-06.43.49";
    private const string LoginB = "LogNet: Login request: ?Name=Bob userId: EOSPlus:2_+_|bbb platform: EOSPlus";
    private const string JoinB = "LogNet: Join succeeded: Bob";

    [Fact]
    public void RealSoloMatchLog_CountsJoinAndLeaveOnce()
    {
        var tracker = new PlayerTracker();
        var counts = new List<(string Line, int Count)>();
        foreach (var line in File.ReadLines(SoloMatchLogPath))
        {
            if (tracker.Process(line))
            {
                counts.Add((line, tracker.Count));
            }
        }

        // 参加で1人、切断で0人。接続直後（PC: NULL）や UChannel::CleanUp / UChannel::Close の行では変わらない。
        Assert.Collection(
            counts,
            change => Assert.Equal((1, true), (change.Count, change.Line.Contains("Join succeeded: Player1"))),
            change => Assert.Equal((0, true), (change.Count, change.Line.Contains("UNetConnection::Close:"))));
    }

    [Fact]
    public void MultiplePlayers_AreTrackedByUniqueId()
    {
        var tracker = new PlayerTracker();
        foreach (var line in new[] { LoginA, JoinA, LoginB, JoinB })
        {
            tracker.Process(line);
        }

        Assert.Equal(2, tracker.Count);
        Assert.True(tracker.Process(CloseA));
        Assert.Equal(["Bob"], tracker.PlayerNames);

        // 同じ切断の行が繰り返されても二重に減らさない。
        Assert.False(tracker.Process(CloseA));
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void ConnectionThatNeverJoined_IsIgnored()
    {
        var tracker = new PlayerTracker();
        tracker.Process(LoginA);

        Assert.False(tracker.Process(CloseA.Replace("PC: BP_DreadPlayerController_C_1", "PC: NULL", StringComparison.Ordinal)));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void NameWithUrlOptions_IsTrimmed()
    {
        var tracker = new PlayerTracker();
        tracker.Process("LogNet: Login request: ?Name=Alice?SplitscreenCount=1 userId: EOSPlus:1_+_|aaa platform: EOSPlus");
        tracker.Process(JoinA);

        Assert.True(tracker.Process(CloseA));
    }

    [Fact]
    public void RealSoloMatchLog_MatchStatesAndServerExitAreDetected()
    {
        var lines = File.ReadAllLines(SoloMatchLogPath);

        var states = lines
            .Select(line => ServerLogAnalyzer.TryGetMatchState(line, out var state) ? state : null)
            .OfType<string>()
            .Distinct()
            .ToList();

        Assert.Equal([MatchStates.WaitingToStart, MatchStates.PokerGame, MatchStates.InProgress, MatchStates.WaitingPostMatch], states);
        Assert.Equal(2, lines.Count(ServerLogAnalyzer.IsServerExitRequest));
    }
}
