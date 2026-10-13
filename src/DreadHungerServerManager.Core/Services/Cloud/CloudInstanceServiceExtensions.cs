using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services.Cloud;

public static class CloudInstanceServiceExtensions
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 起動要求の直後は、状態の取得結果がまだ「停止中」のことがある（結果整合）。
    /// この時間を過ぎても停止中のままなら、起動に失敗したとみなす。
    /// </summary>
    public static readonly TimeSpan StartAcceptanceGracePeriod = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 目的の状態になるまで状態を取得し続ける（起動完了待機・停止完了待機）。
    /// 時間内にならなければ <see cref="CloudErrorKind.Timeout"/>、起動に失敗して停止中に戻った場合や
    /// 終了済みになった場合は <see cref="CloudErrorKind.InvalidState"/> の <see cref="CloudOperationException"/>。
    /// </summary>
    public static async Task<CloudInstanceStatus> WaitForStateAsync(
        this ICloudInstanceService service,
        CloudInstanceRef instance,
        CloudInstanceState target,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        IProgress<CloudInstanceStatus>? progress = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        timeProvider ??= TimeProvider.System;
        var interval = pollInterval ?? DefaultPollInterval;
        var startedAt = timeProvider.GetUtcNow();
        var sawStarting = false;
        while (true)
        {
            var status = await service.GetStatusAsync(instance, cancellationToken).ConfigureAwait(false);
            progress?.Report(status);
            if (status.State == target)
            {
                return status;
            }

            if (status.State == CloudInstanceState.Error)
            {
                throw new CloudOperationException(
                    CloudErrorKind.InvalidState,
                    $"EC2 が使用できない状態（{status.RawState}）になりました。{status.StateReason}".TrimEnd());
            }

            var elapsed = timeProvider.GetUtcNow() - startedAt;
            sawStarting |= status.State == CloudInstanceState.Starting;
            if (target == CloudInstanceState.Running
                && status.State == CloudInstanceState.Stopped
                && (sawStarting || elapsed >= StartAcceptanceGracePeriod))
            {
                var reason = string.IsNullOrWhiteSpace(status.StateReason) ? "" : $"（理由: {status.StateReason}）";
                throw new CloudOperationException(CloudErrorKind.InvalidState, $"EC2 の起動に失敗し、停止中に戻りました{reason}。");
            }

            if (elapsed >= timeout)
            {
                throw new CloudOperationException(
                    CloudErrorKind.Timeout,
                    $"{FormatDuration(timeout)}待ちましたが、EC2 が「{CloudInstanceStatus.GetDisplayText(target)}」になりませんでした"
                    + $"（現在: {CloudInstanceStatus.GetDisplayText(status.State)}）。");
            }

            await Task.Delay(interval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 1 ? $"{duration.TotalMinutes:0.#}分" : $"{duration.TotalSeconds:0}秒";
}
