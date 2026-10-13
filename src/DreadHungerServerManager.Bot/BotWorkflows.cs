using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services.Cloud;

namespace DreadHungerServerManager.Bot;

/// <summary>
/// /start・/stop・/restart・/status と見張り役の手順。Discord にも AWS SDK にも依存せず、
/// <see cref="ICloudInstanceService"/> と <see cref="IServerControlStore"/> を通して EC2 と常駐サービスを操作する。
/// 起動・停止・再起動は <see cref="ServerOperationLock"/> で1つずつに制限する（二重の /start など）。
/// 利用者は EC2 の場所を意識しない（接続先は <see cref="BotOptions.Instance"/> で管理者が1度だけ設定する）。
/// 返す文には EC2 のインスタンスID・リージョンなどを含めない（<see cref="BotRedaction"/>）。詳しい原因は <paramref name="log"/> へ出す。
/// </summary>
public sealed class BotWorkflows(ICloudInstanceService cloud, IServerControlStore store, BotOptions options, TimeProvider? timeProvider = null, Action<string>? log = null)
{
    private const string NotifiedValueName = "notified";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ServerOperationLock _lock = new(store, timeProvider);

    public async Task<BotStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var instance = await cloud.GetStatusAsync(options.Instance, cancellationToken).ConfigureAwait(false);
        ServerHeartbeat? heartbeat = null;
        try
        {
            heartbeat = await store.GetHeartbeatAsync(options.ServerId, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException)
        {
            // 状態が読めなくても EC2 の状態は表示する。
        }

        var alive = instance.State == CloudInstanceState.Running && heartbeat is not null && !heartbeat.IsStale(_time.GetUtcNow());
        var address = instance.State == CloudInstanceState.Running && instance.PublicIpAddress is { } ip ? $"{ip}:{options.GamePort}" : null;
        return new BotStatus(instance, heartbeat, alive, address);
    }

    public async Task<BotResult> StatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return BotResult.Ok(BotMessages.Status(await GetStatusAsync(cancellationToken).ConfigureAwait(false), _time.GetUtcNow()));
        }
        catch (CloudOperationException ex)
        {
            return Fail(ex);
        }
    }

    /// <summary>前回の設定（最後に起動したゲーム設定）。分からなければ既定値。設定パネルの初期値に使う。</summary>
    public async Task<GameSettings> GetLastSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if ((await store.GetHeartbeatAsync(options.ServerId, cancellationToken).ConfigureAwait(false))?.Settings is { } running)
            {
                return running;
            }

            return (await store.GetRequestAsync(options.ServerId, cancellationToken).ConfigureAwait(false))?.Settings ?? GameSettings.Default;
        }
        catch (CloudOperationException)
        {
            return GameSettings.Default;
        }
    }

    /// <summary>/start: 指定の設定で EC2 ごとゲームサーバーを起動する（<see cref="CloudServerLauncher"/>）。</summary>
    public async Task<BotResult> StartAsync(GameSettings settings, BotUser user, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (LaunchPanel.Validate(settings) is { } error)
        {
            return BotResult.Fail(error);
        }

        return await WithLockAsync("起動", user, async () =>
        {
            var launcher = new CloudServerLauncher(cloud, store, _time);
            var result = await launcher.StartAsync(
                options.Instance,
                options.ServerId,
                settings,
                user.AuditName,
                new CloudStartOptions
                {
                    InstanceTimeout = options.StartInstanceTimeout,
                    GameServerTimeout = options.StartGameServerTimeout,
                    PollInterval = options.PollInterval,
                },
                new CallbackProgress<CloudStartProgress>(step => progress?.Report(BotMessages.StartProgress(step, settings))),
                cancellationToken).ConfigureAwait(false);

            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (result.GameServer is { Succeeded: true } started)
            {
                if (status.Address is null)
                {
                    return BotResult.Fail($"ゲームサーバーは起動しましたが、パブリック IP を取得できませんでした。少し待ってから /status で確認してください。\n{started.Message}");
                }

                // 既に動いていた場合は、常駐サービスの説明（「既に起動しています」）を添える。
                var note = started.Message.Contains("既に起動", StringComparison.Ordinal) ? $"\nℹ️ {started.Message} 設定を変える場合は /restart を使ってください。" : "";
                return BotResult.Ok(BotMessages.Online(status, status.Heartbeat?.Settings ?? settings, _time.GetUtcNow()) + note);
            }

            if (result.GameServer is { } failed)
            {
                return BotResult.Fail($"EC2 は起動しましたが、ゲームサーバーを起動できませんでした。\n{failed.Message}");
            }

            if (!result.IsHostKnown)
            {
                return BotResult.Ok($"🟡 EC2 は起動しました（常駐サービスの状態がまだないため、ゲームサーバーの起動は確認していません）。\n**Address:** `{status.Address ?? "（取得できませんでした）"}`");
            }

            return BotResult.Ok($"🟡 EC2 は起動しましたが、まだゲームサーバーの起動を確認できていません。数分後に /status で確認してください。\n**Address:** `{status.Address ?? "（取得できませんでした）"}`");
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>/stop: ゲームサーバーを安全に止めてから EC2 を停止する。参加者がいる間は断る（<see cref="RefuseWhilePlaying"/>）。</summary>
    public Task<BotResult> StopAsync(BotUser user, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        WithLockAsync("停止", user, async () =>
        {
            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            switch (status.Instance.State)
            {
                case CloudInstanceState.Stopped:
                    return BotResult.Ok("⚪ EC2 は既に停止しています。");
                case CloudInstanceState.Starting:
                    return BotResult.Fail("EC2 は起動処理中のため、まだ停止できません。1～2分後にもう一度 /stop を実行してください。");
            }

            if (RefuseWhilePlaying(status, "停止", "/stop") is { } refused)
            {
                return refused;
            }

            var note = "";
            if (status.Instance.State == CloudInstanceState.Running && status.IsHostAlive && status.Heartbeat?.State != ServerState.Stopped)
            {
                progress?.Report("⏳ ゲームサーバーを安全に停止しています…");
                try
                {
                    var stopped = await new ServerControlClient(store, _time).SendAsync(
                        options.ServerId,
                        ServerControlRequest.Create(ServerControlAction.Stop, null, user.AuditName, _time.GetUtcNow()),
                        options.StopGameServerTimeout,
                        pollInterval: options.PollInterval,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    note = stopped.Succeeded ? "" : $"\n（ゲームサーバーの停止に失敗したため、EC2 の停止に合わせて終了させました: {stopped.Message}）";
                }
                catch (CloudOperationException ex) when (ex.Kind == CloudErrorKind.Timeout)
                {
                    note = "\n（常駐サービスの応答がなかったため、EC2 の停止に合わせてゲームサーバーを終了させました）";
                }
            }

            progress?.Report("⏳ EC2 を停止しています…（通常1～2分）");
            if (status.Instance.State != CloudInstanceState.Stopping)
            {
                await cloud.StopAsync(options.Instance, cancellationToken).ConfigureAwait(false);
            }

            await cloud.WaitForStateAsync(options.Instance, CloudInstanceState.Stopped, options.StopInstanceTimeout, options.PollInterval, timeProvider: _time, cancellationToken: cancellationToken).ConfigureAwait(false);
            return BotResult.Ok($"⚪ **Dread Hunger Server Stopped**\nゲームサーバーと EC2 を停止しました。{note}");
        }, cancellationToken);

    /// <summary>/restart: EC2 は稼働したまま、指定の設定でゲームサーバーを起動し直す。参加者がいる間は断る。</summary>
    public async Task<BotResult> RestartAsync(GameSettings settings, BotUser user, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (LaunchPanel.Validate(settings) is { } error)
        {
            return BotResult.Fail(error);
        }

        return await WithLockAsync("再起動", user, async () =>
        {
            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.Instance.State != CloudInstanceState.Running)
            {
                return BotResult.Fail("EC2 が稼働していません。/start で起動してください。");
            }

            if (!status.IsHostAlive)
            {
                return BotResult.Fail("EC2 上の常駐サービスが応答していません。Windows の起動中なら、数分後にもう一度実行してください。");
            }

            if (RefuseWhilePlaying(status, "再起動", "/restart") is { } refused)
            {
                return refused;
            }

            progress?.Report($"⏳ ゲームサーバーを再起動しています…\n{LaunchPanel.Summary(settings)}");
            var result = await new ServerControlClient(store, _time).SendAsync(
                options.ServerId,
                ServerControlRequest.Create(ServerControlAction.Restart, settings, user.AuditName, _time.GetUtcNow()),
                options.RestartTimeout,
                pollInterval: options.PollInterval,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return BotResult.Fail($"ゲームサーバーを再起動できませんでした。\n{result.Message}");
            }

            var after = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return BotResult.Ok(BotMessages.Online(after, settings, _time.GetUtcNow(), "Dread Hunger Server Restarted"));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 見張り役（5分ごと）。Discord へ送る通知の文を返す。
    /// <list type="bullet">
    /// <item>自動停止の予告（誰も遊んでいない: 5分前、最大稼働時間: 30分・10分・5分前）と、自動停止したことの通知</item>
    /// <item>EC2 が稼働中なのに常駐サービスが長く応答しない場合は、料金事故を防ぐため EC2 を停止する</item>
    /// </list>
    /// </summary>
    public async Task<IReadOnlyList<string>> RunWatchdogAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var notified = await LoadNotifiedAsync(cancellationToken).ConfigureAwait(false);
        var messages = new List<string>();
        var heartbeat = status.Heartbeat;

        if (status.Instance.State is CloudInstanceState.Stopped or CloudInstanceState.Stopping && heartbeat is { IsAutoStopping: true })
        {
            if (notified.Add($"stopped:{heartbeat.AutoStopAt?.ToUnixTimeSeconds()}"))
            {
                messages.Add($"⚪ EC2 を自動停止しました（{heartbeat.AutoStopReason}）。また遊ぶときは /start で起動してください。");
            }
        }
        else if (status.Instance.State == CloudInstanceState.Running)
        {
            var staleFor = heartbeat is null ? (TimeSpan?)null : now - heartbeat.UpdatedAt;
            var runningFor = status.Instance.LaunchTime is { } launched ? now - launched : TimeSpan.Zero;
            var limit = TimeSpan.FromMinutes(options.WatchdogStaleStopMinutes);
            if (options.WatchdogStaleStopMinutes > 0 && runningFor > limit && (staleFor is null || staleFor > limit))
            {
                await cloud.StopAsync(options.Instance, cancellationToken).ConfigureAwait(false);
                messages.Add($"⚠️ EC2 上の常駐サービスが{options.WatchdogStaleStopMinutes}分以上応答しないため、料金の発生を防ぐために EC2 を停止しました。EC2 の常駐サービスとログ（C:\\ProgramData\\DreadHungerServerManager\\logs）を確認してください。");
            }
            else if (status.IsHostAlive && heartbeat is { AutoStopAt: { } stopAt, IsAutoStopping: false })
            {
                var remaining = stopAt - now;
                IReadOnlyList<int> thresholds = heartbeat.AutoStopKind == AutoStopReason.MaxRuntime ? [30, 10, 5] : [5];
                foreach (var minutes in thresholds)
                {
                    // 5分ごとに見るので、各段階の5分の幅に入ったら1回だけ知らせる。
                    if (remaining <= TimeSpan.FromMinutes(minutes) && remaining > TimeSpan.FromMinutes(minutes - 5)
                        && notified.Add($"warn:{stopAt.ToUnixTimeSeconds()}:{minutes}"))
                    {
                        var hint = heartbeat.AutoStopKind == AutoStopReason.MaxRuntime
                            ? "最大稼働時間です。続けて遊ぶ場合は、停止後にもう一度 /start してください。"
                            : "誰かが参加するか、ゲームサーバーを起動すると延長されます。";
                        messages.Add($"⏰ {BotMessages.Relative(stopAt)}（{BotMessages.Time(stopAt)}）に EC2 を自動停止します（{heartbeat.AutoStopReason}）。{hint}");
                    }
                }
            }
        }

        if (messages.Count > 0)
        {
            await SaveNotifiedAsync(notified, cancellationToken).ConfigureAwait(false);
        }

        return messages;
    }

    private async Task<BotResult> WithLockAsync(string operation, BotUser user, Func<Task<BotResult>> action, CancellationToken cancellationToken)
    {
        OperationLockResult acquired;
        try
        {
            acquired = await _lock.TryAcquireAsync(options.ServerId, operation, user.AuditName, options.OperationLeaseDuration, user.DisplayName, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudOperationException ex)
        {
            return Fail(ex);
        }

        if (acquired.Acquired is not { } lease)
        {
            return BotResult.Fail(acquired.BusyMessage);
        }

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (CloudOperationException ex)
        {
            return Fail(ex);
        }
        catch (ArgumentException ex)
        {
            return BotResult.Fail(BotRedaction.Redact(ex.Message, options.Instance));
        }
        finally
        {
            try
            {
                await _lock.ReleaseAsync(options.ServerId, lease, CancellationToken.None).ConfigureAwait(false);
            }
            catch (CloudOperationException)
            {
                // 解放できなくても期限で自然に無効になる。
            }
        }
    }

    /// <summary>
    /// 誰でも使える設定でも試合を壊されないよう、参加者がいる間は停止・再起動を断る。
    /// 人数は常駐サービスの最新の状態で判断する。常駐サービスが応答していない（人数が分からない）ときは断らない（止められなくなるのを防ぐ）。
    /// 管理者は GUI の「AWS / Cloud」ページからなら、参加者がいても停止できる。
    /// </summary>
    private static BotResult? RefuseWhilePlaying(BotStatus status, string operation, string command)
    {
        if (!status.IsHostAlive || status.Heartbeat is not { State: not ServerState.Stopped, PlayerCount: > 0 and var players } heartbeat)
        {
            return null;
        }

        var idle = heartbeat.AutoStop is { IdleShutdownEnabled: true, IdleTimeoutMinutes: var minutes } ? $"（誰もいない状態が{minutes}分続くと自動で停止します）" : "";
        return BotResult.Fail($"参加者が{players}人いるため{operation}できません。全員が抜けてから、もう一度 {command} を実行してください。{idle}");
    }

    /// <summary>詳しい原因は管理者向けのログへ、利用者には場所を伏せた説明を返す。</summary>
    private BotResult Fail(CloudOperationException exception)
    {
        log?.Invoke($"cloud operation failed: {exception.Kind}: {exception.Message}" + (exception.InnerException is { } inner ? $" ({inner.GetType().Name}: {inner.Message})" : ""));
        return BotResult.Fail(BotRedaction.PublicMessage(exception, options.Instance));
    }

    private async Task<HashSet<string>> LoadNotifiedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await store.GetValueAsync(options.ServerId, NotifiedValueName, cancellationToken).ConfigureAwait(false);
            return new HashSet<string>(ServerControlJson.Deserialize<List<string>>(json) ?? [], StringComparer.Ordinal);
        }
        catch (CloudOperationException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private Task SaveNotifiedAsync(HashSet<string> notified, CancellationToken cancellationToken) =>
        store.PutValueAsync(options.ServerId, NotifiedValueName, ServerControlJson.Serialize(notified.TakeLast(30).ToList()), cancellationToken);

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
