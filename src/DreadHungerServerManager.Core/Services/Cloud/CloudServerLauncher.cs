using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services.Cloud;

public enum CloudStartStage
{
    /// <summary>起動に使うゲーム設定を、常駐サービス向けの要求として書いている。</summary>
    SendingRequest,

    /// <summary>EC2 の起動を要求している。</summary>
    StartingInstance,

    /// <summary>EC2 が稼働中になるのを待っている。</summary>
    WaitingForInstance,

    /// <summary>Windows と常駐サービスが起動し、ゲームサーバーの起動結果が返るのを待っている。</summary>
    WaitingForGameServer,
}

public sealed record CloudStartProgress(CloudStartStage Stage, CloudInstanceStatus? Instance = null, ServerHeartbeat? Heartbeat = null);

public sealed record CloudStartOptions
{
    public TimeSpan InstanceTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>EC2 が稼働中になってから、ゲームサーバーの起動結果を待つ時間（Windows の起動を含む）。</summary>
    public TimeSpan GameServerTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed record CloudStartResult
{
    public required CloudInstanceStatus Instance { get; init; }

    /// <summary>常駐サービスによるゲームサーバーの起動結果。確認できなかった場合はnull。</summary>
    public ServerRequestResult? GameServer { get; init; }

    public ServerHeartbeat? Heartbeat { get; init; }

    /// <summary>常駐サービスの状態が以前からあった（＝EC2 に常駐サービスが入っている）か。</summary>
    public bool IsHostKnown { get; init; }

    /// <summary>ゲームサーバーの起動結果を待ちきれなかった場合の説明。</summary>
    public string? GameServerTimeoutMessage { get; init; }

    public string? PublicIp => Instance.PublicIpAddress;
}

/// <summary>
/// 「指定したゲーム設定で、EC2 ごとゲームサーバーを起動する」手順。GUI の「EC2 起動」と Discord の /start で共通に使う。
/// <list type="number">
/// <item>ゲーム設定を、常駐サービス向けの起動要求として先に書いておく</item>
/// <item>EC2 を起動し、稼働中になるまで待つ（既に稼働中なら起動しない）</item>
/// <item>常駐サービスが起動直後に要求を拾い、その設定でゲームサーバーを起動した結果を待つ</item>
/// </list>
/// 常駐サービスの状態がこれまで一度もない（常駐サービスを入れていない）場合は、3. を待たずに終える。
/// </summary>
public sealed class CloudServerLauncher(ICloudInstanceService cloud, IServerControlStore store, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <exception cref="ArgumentException">ゲーム設定に誤りがある（何もせずに終わる）。</exception>
    /// <exception cref="CloudOperationException">AWS の操作に失敗した、EC2 が停止処理中などで起動できない。</exception>
    public async Task<CloudStartResult> StartAsync(
        CloudInstanceRef instance,
        string serverId,
        GameSettings settings,
        string requestedBy,
        CloudStartOptions? options = null,
        IProgress<CloudStartProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = GameSettingsValidator.Validate(settings);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors.Select(error => error.Message)), nameof(settings));
        }

        options ??= new CloudStartOptions();
        var current = await cloud.GetStatusAsync(instance, cancellationToken).ConfigureAwait(false);
        if (current.State == CloudInstanceState.Stopping)
        {
            throw new CloudOperationException(CloudErrorKind.InvalidState, "EC2 は停止処理中です。停止が終わってから起動してください。");
        }

        var previous = await store.GetHeartbeatAsync(serverId, cancellationToken).ConfigureAwait(false);
        progress?.Report(new CloudStartProgress(CloudStartStage.SendingRequest, current));
        var request = ServerControlRequest.Create(ServerControlAction.Start, settings, requestedBy, _time.GetUtcNow());
        await store.PutRequestAsync(serverId, request, cancellationToken).ConfigureAwait(false);

        if (current.State is not (CloudInstanceState.Running or CloudInstanceState.Starting))
        {
            progress?.Report(new CloudStartProgress(CloudStartStage.StartingInstance, current));
            await cloud.StartAsync(instance, cancellationToken).ConfigureAwait(false);
        }

        var running = await cloud.WaitForStateAsync(
            instance,
            CloudInstanceState.Running,
            options.InstanceTimeout,
            options.PollInterval,
            new CallbackProgress<CloudInstanceStatus>(status => progress?.Report(new CloudStartProgress(CloudStartStage.WaitingForInstance, status))),
            _time,
            cancellationToken).ConfigureAwait(false);

        if (previous is null)
        {
            return new CloudStartResult { Instance = running, IsHostKnown = false };
        }

        progress?.Report(new CloudStartProgress(CloudStartStage.WaitingForGameServer, running));
        try
        {
            var result = await new ServerControlClient(store, _time).WaitForResultAsync(
                serverId,
                request.RequestId,
                options.GameServerTimeout,
                new CallbackProgress<ServerHeartbeat>(heartbeat => progress?.Report(new CloudStartProgress(CloudStartStage.WaitingForGameServer, running, heartbeat))),
                options.PollInterval,
                cancellationToken).ConfigureAwait(false);

            // パブリック IP は稼働中になった直後にまだ付いていないことがあるため、取り直す。
            var latest = running.PublicIpAddress is null ? await cloud.GetStatusAsync(instance, cancellationToken).ConfigureAwait(false) : running;
            return new CloudStartResult
            {
                Instance = latest,
                GameServer = result,
                Heartbeat = await store.GetHeartbeatAsync(serverId, cancellationToken).ConfigureAwait(false),
                IsHostKnown = true,
            };
        }
        catch (CloudOperationException ex) when (ex.Kind == CloudErrorKind.Timeout)
        {
            return new CloudStartResult { Instance = running, IsHostKnown = true, GameServerTimeoutMessage = ex.Message };
        }
    }

    /// <summary>同期的に呼ぶ <see cref="IProgress{T}"/>（スレッドの切り替えは呼び出し側の Progress に任せる）。</summary>
    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
