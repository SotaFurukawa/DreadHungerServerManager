namespace DreadHungerServerManager.Core.Models;

/// <summary>
/// EC2 の自動停止の設定。GUI で編集して Parameter Store（/dhsm/{サーバーID}/config）に置き、EC2 上の常駐サービスが読む。
/// </summary>
public sealed record AutoStopConfig
{
    public const int MinIdleTimeoutMinutes = 5;
    public const int MaxIdleTimeoutMinutes = 240;
    public const int MinMaxRuntimeHours = 1;
    public const int MaxMaxRuntimeHours = 24;

    /// <summary>
    /// 誰も遊んでいない状態（ゲームサーバーが止まったまま、または参加者0人）が続いたら停止する。
    /// ゲームサーバーの起動・要求・参加があるたびに数え直す。
    /// </summary>
    public bool IdleShutdownEnabled { get; init; } = true;

    public int IdleTimeoutMinutes { get; init; } = 30;

    /// <summary>料金事故の防止のため、EC2 の起動から一定時間で停止する（30分前・10分前・5分前に警告）。</summary>
    public bool MaxRuntimeEnabled { get; init; } = true;

    public int MaxRuntimeHours { get; init; } = 6;

    public DateTimeOffset? UpdatedAt { get; init; }

    public string? UpdatedBy { get; init; }

    public static AutoStopConfig Default { get; } = new();

    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(Math.Clamp(IdleTimeoutMinutes, MinIdleTimeoutMinutes, MaxIdleTimeoutMinutes));

    public TimeSpan MaxRuntime => TimeSpan.FromHours(Math.Clamp(MaxRuntimeHours, MinMaxRuntimeHours, MaxMaxRuntimeHours));

    public AutoStopConfig Normalize() => this with
    {
        IdleTimeoutMinutes = Math.Clamp(IdleTimeoutMinutes, MinIdleTimeoutMinutes, MaxIdleTimeoutMinutes),
        MaxRuntimeHours = Math.Clamp(MaxRuntimeHours, MinMaxRuntimeHours, MaxMaxRuntimeHours),
    };

    public string Describe()
    {
        var idle = IdleShutdownEnabled ? $"誰も遊んでいない状態が{IdleTimeoutMinutes}分続いたら停止" : "誰も遊んでいなくても停止しない";
        var max = MaxRuntimeEnabled ? $"起動から{MaxRuntimeHours}時間で停止" : "最大稼働時間なし";
        return $"{idle}・{max}";
    }
}

public enum AutoStopReason
{
    /// <summary>誰も遊んでいない状態が続いた。</summary>
    Idle,

    /// <summary>最大稼働時間に達した。</summary>
    MaxRuntime,
}

/// <summary>次に予定されている自動停止。</summary>
public sealed record AutoStopPlan(AutoStopReason Reason, DateTimeOffset StopAt)
{
    public string ReasonText => Reason switch
    {
        AutoStopReason.MaxRuntime => "最大稼働時間に達するため",
        _ => "誰も遊んでいない状態が続いたため",
    };
}
