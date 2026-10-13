using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services.Cloud;

namespace DreadHungerServerManager.Bot;

/// <summary>EC2 とゲームサーバーの状態（/status や起動結果の表示に使う）。</summary>
public sealed record BotStatus(CloudInstanceStatus Instance, ServerHeartbeat? Heartbeat, bool IsHostAlive, string? Address);

/// <summary>コマンドの結果。<see cref="Message"/> は Discord にそのまま出す文（Markdown）。</summary>
public sealed record BotResult(bool Succeeded, string Message)
{
    public static BotResult Ok(string message) => new(true, message);

    public static BotResult Fail(string message) => new(false, $"⚠️ {message}");
}

/// <summary>
/// Discord に出す文面。時刻は Discord のタイムスタンプ記法（&lt;t:…&gt;）にして、見る人の時間帯で表示させる。
/// </summary>
public static class BotMessages
{
    public static string Status(BotStatus status, DateTimeOffset now)
    {
        var lines = new List<string>
        {
            $"**EC2:** {InstanceText(status.Instance.State)}",
            $"**Server:** {ServerText(status)}",
        };

        if (status.IsHostAlive && status.Heartbeat is { } heartbeat)
        {
            if (heartbeat.Settings is { } settings)
            {
                lines.Add($"**Map:** {MapText(settings.Map)}");
            }

            if (heartbeat.State != ServerState.Stopped)
            {
                lines.Add($"**Players:** {heartbeat.PlayerCount} / {heartbeat.Settings?.MaxPlayers.ToString() ?? "?"}");
            }
        }

        if (status.Address is { } address)
        {
            lines.Add($"**Address:** `{address}`");
        }

        if (status.IsHostAlive && status.Heartbeat is { ServerStartedAt: { } startedAt, State: not ServerState.Stopped })
        {
            lines.Add($"**Uptime:** {Duration(now - startedAt)}");
        }

        if (status.IsHostAlive && status.Heartbeat is { AutoStopAt: { } stopAt } scheduled && !scheduled.IsAutoStopping)
        {
            lines.Add($"**自動停止予定:** {Time(stopAt)}（{Relative(stopAt)}・{scheduled.AutoStopReason}）");
        }

        if (status.Instance.State == CloudInstanceState.Running && !status.IsHostAlive)
        {
            lines.Add("※ EC2 上の常駐サービスが応答していません（Windows の起動中か、常駐サービスが止まっています）。");
        }

        return string.Join('\n', lines);
    }

    /// <summary>起動・再起動の成功（ご要望の「Dread Hunger Server Online」形式）。</summary>
    public static string Online(BotStatus status, GameSettings settings, DateTimeOffset now, string title = "Dread Hunger Server Online")
    {
        var players = status.Heartbeat is { } heartbeat ? heartbeat.PlayerCount : 0;
        var uptime = status.Heartbeat?.ServerStartedAt is { } startedAt ? Duration(now - startedAt) : "00:00:00";
        return $"""
            🟢 **{title}**
            **Map:** {MapText(settings.Map)}
            **Players:** {players} / {settings.MaxPlayers}
            **Address:** `{status.Address ?? "（取得できませんでした）"}`
            **Uptime:** {uptime}
            {LaunchPanel.Summary(settings)}
            """;
    }

    public static string StartProgress(CloudStartProgress progress, GameSettings settings)
    {
        var step = progress.Stage switch
        {
            CloudStartStage.SendingRequest => "ゲーム設定を送っています…",
            CloudStartStage.StartingInstance => "EC2 を起動しています…",
            CloudStartStage.WaitingForInstance => "EC2 Starting…（通常1～2分）",
            _ => "Windows とゲームサーバーの起動を待っています…（通常2～5分）",
        };
        return $"⏳ {step}\n{LaunchPanel.Summary(settings)}";
    }

    public static string InstanceText(CloudInstanceState state) => state switch
    {
        CloudInstanceState.Running => "Running（稼働中）",
        CloudInstanceState.Starting => "Starting（起動処理中）",
        CloudInstanceState.Stopping => "Stopping（停止処理中）",
        CloudInstanceState.Stopped => "Stopped（停止中）",
        CloudInstanceState.Error => "Error（使用できない状態）",
        _ => "Unknown（不明）",
    };

    public static string MapText(GameMap map) => $"{GameMaps.Get(map).DisplayName}（{map}）";

    public static string Duration(TimeSpan span) =>
        span < TimeSpan.Zero ? "00:00:00" : $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

    public static string Time(DateTimeOffset time) => $"<t:{time.ToUnixTimeSeconds()}:t>";

    public static string Relative(DateTimeOffset time) => $"<t:{time.ToUnixTimeSeconds()}:R>";

    private static string ServerText(BotStatus status)
    {
        if (status.Instance.State != CloudInstanceState.Running)
        {
            return "Offline（EC2 が稼働していません）";
        }

        if (!status.IsHostAlive || status.Heartbeat is not { } heartbeat)
        {
            return "Unknown（常駐サービスの応答待ち）";
        }

        if (heartbeat.IsAutoStopping)
        {
            return "Stopping（自動停止中）";
        }

        return (heartbeat.State, heartbeat.MatchState) switch
        {
            (ServerState.Running, MatchStates.InProgress) => "Online（試合中）",
            (ServerState.Running, MatchStates.PokerGame) => "Online（開始準備中）",
            (ServerState.Running, MatchStates.WaitingPostMatch) => "Online（試合終了）",
            (ServerState.Running, _) => "Online（参加待ち）",
            (ServerState.Starting, _) => "Starting（起動処理中）",
            (ServerState.Stopping, _) => "Stopping（停止処理中）",
            _ => "Offline（ゲームサーバーは停止中）" + (heartbeat.LastExitMessage is { } exit ? $" - {exit}" : ""),
        };
    }
}
