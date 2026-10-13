using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services.Cloud;

/// <summary>
/// 要求を出す側（GUI・Discord Bot）の手順。要求を書き込み、Host がその要求の結果を状態に載せるまで待つ。
/// </summary>
public sealed class ServerControlClient(IServerControlStore store, TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task<ServerHeartbeat?> GetHeartbeatAsync(string serverId, CancellationToken cancellationToken = default) =>
        store.GetHeartbeatAsync(serverId, cancellationToken);

    /// <summary>
    /// 要求を送り、結果を待つ。Host が要求を拾わない（EC2 が止まっている等）まま時間が過ぎたら
    /// <see cref="CloudErrorKind.Timeout"/>。途中の状態は <paramref name="progress"/> に通知する。
    /// </summary>
    public async Task<ServerRequestResult> SendAsync(
        string serverId,
        ServerControlRequest request,
        TimeSpan timeout,
        IProgress<ServerHeartbeat>? progress = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        await store.PutRequestAsync(serverId, request, cancellationToken).ConfigureAwait(false);
        return await WaitForResultAsync(serverId, request.RequestId, timeout, progress, pollInterval, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>書き込み済みの要求の結果を待つ（EC2 の起動前に要求を書いておく場合など）。</summary>
    public async Task<ServerRequestResult> WaitForResultAsync(
        string serverId,
        string requestId,
        TimeSpan timeout,
        IProgress<ServerHeartbeat>? progress = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = _time.GetUtcNow();
        while (true)
        {
            await Task.Delay(pollInterval ?? DefaultPollInterval, _time, cancellationToken).ConfigureAwait(false);
            var heartbeat = await store.GetHeartbeatAsync(serverId, cancellationToken).ConfigureAwait(false);
            if (heartbeat is not null)
            {
                progress?.Report(heartbeat);
                if (heartbeat.LastResult is { } result && result.RequestId == requestId)
                {
                    return result;
                }
            }

            if (_time.GetUtcNow() - startedAt >= timeout)
            {
                var pending = await store.GetRequestAsync(serverId, cancellationToken).ConfigureAwait(false);
                var reason = pending?.RequestId != requestId
                    ? "別の要求で上書きされました。"
                    : heartbeat is null || heartbeat.IsStale(_time.GetUtcNow())
                        ? "EC2 上の常駐サービスが応答していません（EC2 が停止中・起動処理中か、常駐サービスが動いていません）。"
                        : "常駐サービスが要求を処理し終えていません。";
                throw new CloudOperationException(CloudErrorKind.Timeout, $"{timeout.TotalMinutes:0.#}分待ちましたが、結果が返ってきませんでした。{reason}");
            }
        }
    }
}
