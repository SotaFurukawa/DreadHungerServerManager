namespace DreadHungerServerManager.Core.Models;

public enum ServerControlAction
{
    Start,
    Stop,
    Restart,
}

/// <summary>
/// GUI・Discord Bot から EC2 上の常駐サービス（Host）への要求。最新の1件だけを保持し、
/// Host は <see cref="RequestId"/> がまだ処理していないものなら実行する。
/// </summary>
public sealed record ServerControlRequest
{
    public required string RequestId { get; init; }

    public required ServerControlAction Action { get; init; }

    /// <summary>起動・再起動に使う設定。nullなら Host が前回の設定を使う。</summary>
    public GameSettings? Settings { get; init; }

    /// <summary>要求した人・場所（例: "GUI:MYPC"、"Discord:123456789 (name)"）。監査ログに残す。</summary>
    public required string RequestedBy { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public static ServerControlRequest Create(ServerControlAction action, GameSettings? settings, string requestedBy, DateTimeOffset now) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Action = action,
        Settings = settings,
        RequestedBy = requestedBy,
        RequestedAt = now,
    };
}

/// <summary>要求の処理結果。</summary>
public sealed record ServerRequestResult
{
    public required string RequestId { get; init; }

    public required ServerControlAction Action { get; init; }

    public required bool Succeeded { get; init; }

    /// <summary>利用者にそのまま見せられる日本語の説明。</summary>
    public required string Message { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }
}

/// <summary>
/// EC2 上の常駐サービス（Host）が定期的に書き出すゲームサーバーの状態。
/// GUI・Discord Bot はこれを読んで表示する（<see cref="UpdatedAt"/> が古ければ Host が動いていない）。
/// </summary>
public sealed record ServerHeartbeat
{
    public required string ServerId { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset HostStartedAt { get; init; }

    public string? HostVersion { get; init; }

    public string? InstanceId { get; init; }

    public ServerState State { get; init; }

    /// <summary>起動完了（ポート待ち受けかWaitingToStart）を確認済みか。</summary>
    public bool IsReady { get; init; }

    public string? MatchState { get; init; }

    public int PlayerCount { get; init; }

    public int? Port { get; init; }

    public DateTimeOffset? ServerStartedAt { get; init; }

    /// <summary>起動中（または最後に起動した）ゲーム設定。</summary>
    public GameSettings? Settings { get; init; }

    /// <summary>最後にサーバーが終了した理由（試合終了・異常終了など）。</summary>
    public string? LastExitMessage { get; init; }

    /// <summary>処理中の要求。なければnull。</summary>
    public string? ActiveRequestId { get; init; }

    /// <summary>最後に処理した要求の結果。</summary>
    public ServerRequestResult? LastResult { get; init; }

    /// <summary>この時間より古い状態は、Host が止まっている（EC2 停止中・起動処理中など）とみなす。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);

    public bool IsStale(DateTimeOffset now) => now - UpdatedAt > StaleAfter;
}
