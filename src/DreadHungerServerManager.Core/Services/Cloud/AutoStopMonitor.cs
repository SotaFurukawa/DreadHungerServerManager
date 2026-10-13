using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services.Cloud;

/// <summary>判定の結果。<see cref="Warning"/> は今回はじめて知らせるべき警告（残り時間）。</summary>
public sealed record AutoStopDecision(AutoStopPlan? Plan, bool ShouldStop, TimeSpan? Warning)
{
    public static AutoStopDecision None { get; } = new(null, false, null);
}

/// <summary>
/// EC2 を自動で停止するかを決める（時間と状態だけを見る純粋なロジック。常駐サービスが定期的に呼ぶ）。
/// <list type="bullet">
/// <item>誰も遊んでいない: 参加者0人（ゲームサーバーが止まっている場合を含む）の状態が設定時間続いたら停止。
/// 参加者がいる間・ゲームサーバーの起動や要求があったときに数え直す</item>
/// <item>最大稼働時間: EC2 の起動から設定時間で停止（試合中でも停止する。30分前・10分前・5分前に警告）</item>
/// </list>
/// </summary>
public sealed class AutoStopMonitor(DateTimeOffset instanceStartedAt, DateTimeOffset now)
{
    public static readonly IReadOnlyList<TimeSpan> MaxRuntimeWarnings = [TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5)];

    public static readonly IReadOnlyList<TimeSpan> IdleWarnings = [TimeSpan.FromMinutes(5)];

    private readonly HashSet<(AutoStopReason Reason, DateTimeOffset StopAt, TimeSpan Threshold)> _sentWarnings = [];
    private DateTimeOffset _lastActivityAt = now;

    public DateTimeOffset InstanceStartedAt { get; } = instanceStartedAt;

    public DateTimeOffset LastActivityAt => _lastActivityAt;

    /// <summary>ゲームサーバーの起動・要求の処理など、遊ぶための操作があった。</summary>
    public void RecordActivity(DateTimeOffset now)
    {
        if (now > _lastActivityAt)
        {
            _lastActivityAt = now;
        }
    }

    public AutoStopDecision Evaluate(int playerCount, AutoStopConfig config, DateTimeOffset now)
    {
        if (playerCount > 0)
        {
            RecordActivity(now);
        }

        var plans = new List<AutoStopPlan>();
        if (config.IdleShutdownEnabled && playerCount == 0)
        {
            plans.Add(new AutoStopPlan(AutoStopReason.Idle, _lastActivityAt + config.IdleTimeout));
        }

        if (config.MaxRuntimeEnabled)
        {
            plans.Add(new AutoStopPlan(AutoStopReason.MaxRuntime, InstanceStartedAt + config.MaxRuntime));
        }

        if (plans.Count == 0)
        {
            return AutoStopDecision.None;
        }

        var next = plans.MinBy(plan => plan.StopAt)!;
        var remaining = next.StopAt - now;
        if (remaining <= TimeSpan.Zero)
        {
            return new AutoStopDecision(next, true, null);
        }

        var thresholds = next.Reason == AutoStopReason.MaxRuntime ? MaxRuntimeWarnings : IdleWarnings;
        var due = thresholds.Where(threshold => remaining <= threshold).ToList();
        if (due.Count == 0)
        {
            return new AutoStopDecision(next, false, null);
        }

        // 同じ停止予定について、同じ段階の警告は1回だけ。参加や操作で予定が延びたら、また警告する。
        var smallest = due.Min();
        var isNew = false;
        foreach (var threshold in due)
        {
            isNew |= _sentWarnings.Add((next.Reason, next.StopAt, threshold));
        }

        return new AutoStopDecision(next, false, isNew ? Ceiling(remaining, smallest) : null);
    }

    // 「残り4分59秒」は「5分」として知らせる。
    private static TimeSpan Ceiling(TimeSpan remaining, TimeSpan threshold) =>
        TimeSpan.FromMinutes(Math.Min(Math.Ceiling(remaining.TotalMinutes), threshold.TotalMinutes));
}

/// <summary>自動停止の予告・実行を知らせる先（Discord の Webhook など）。</summary>
public interface IAutoStopNotifier
{
    Task NotifyWarningAsync(AutoStopPlan plan, TimeSpan remaining, CancellationToken cancellationToken);

    Task NotifyStoppingAsync(AutoStopPlan plan, CancellationToken cancellationToken);
}
