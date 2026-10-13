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

    public string? LogFilePath { get; init; }

    public static ServerStatus Stopped { get; } = new();
}

/// <summary>サーバープロセスが終了したときの情報。</summary>
public sealed record ServerExitInfo(int? ExitCode, bool WasRequested, bool ExitedDuringStartup, TimeSpan? Uptime)
{
    public bool IsAbnormal => !WasRequested;
}

/// <summary>実行中のDreadHungerServerプロセス。</summary>
public sealed record RunningServerProcess(int ProcessId, int ParentProcessId, string Name, string? ExecutablePath);
