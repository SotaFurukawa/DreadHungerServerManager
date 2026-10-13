namespace DreadHungerServerManager.Core.Models;

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

public sealed record ServerStatus
{
    public ServerState State { get; init; } = ServerState.Stopped;

    /// <summary>起動した（または検出した）DreadHungerServer.exeのPID。</summary>
    public int? ProcessId { get; init; }

    /// <summary>
    /// 実際にゲームを動かしているプロセスのPID。DreadHungerServer.exeはブートストラップで、
    /// 本体の DreadHungerServer-Win64-Shipping.exe を子プロセスとして起動する。
    /// </summary>
    public int? MainProcessId { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>このアプリ以外で起動されたサーバーを検出して管理しているか。</summary>
    public bool IsAttached { get; init; }

    /// <summary>「IpNetDriver listening on port」を検出済みか。</summary>
    public bool IsPortListening { get; init; }

    public int? ListeningPort { get; init; }

    /// <summary>「Match State Changed from EnteringMap to WaitingToStart」を検出済みか。</summary>
    public bool IsWaitingToStart { get; init; }

    /// <summary>
    /// ログから読み取った最新の試合状態（<see cref="MatchStates"/>）。未検出ならnull。
    /// 待機中は WaitingToStart、開始前のポーカー卓は PokerGame、試合中は InProgress、試合終了後は WaitingPostMatch。
    /// </summary>
    public string? MatchState { get; init; }

    /// <summary>接続中のプレイヤー数（ログの参加・切断から数えた値）。</summary>
    public int PlayerCount { get; init; }

    /// <summary>サーバー自身が終了を要求した（試合終了後など。Ctrl+Cによる終了は含まない）。</summary>
    public bool ServerRequestedExit { get; init; }

    public string? LogFilePath { get; init; }

    public static ServerStatus Stopped { get; } = new();
}

/// <summary>Dread Hunger がログに出す試合状態の名前。</summary>
public static class MatchStates
{
    public const string WaitingToStart = "WaitingToStart";
    public const string PokerGame = "PokerGame";
    public const string InProgress = "InProgress";
    public const string WaitingPostMatch = "WaitingPostMatch";
}

public enum ServerExitKind
{
    /// <summary>このアプリ（または利用者）が停止を要求した。</summary>
    Requested,

    /// <summary>試合の終了などで、サーバー自身が終了した（終了コード0）。</summary>
    EndedByServer,

    /// <summary>要求していないのに終了した（クラッシュなど）。</summary>
    Unexpected,
}

/// <summary>サーバープロセスが終了したときの情報。</summary>
public sealed record ServerExitInfo(int? ExitCode, bool WasRequested, bool ExitedDuringStartup, TimeSpan? Uptime, ServerExitKind Kind)
{
    public bool IsAbnormal => Kind == ServerExitKind.Unexpected;
}

/// <summary>実行中のDreadHungerServerプロセス。</summary>
public sealed record RunningServerProcess(int ProcessId, int ParentProcessId, string Name, string? ExecutablePath);
