using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services.Cloud;

public enum HostLogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>常駐サービス（Host）が動く環境。実行ファイルの場所・前回の設定の保存・ログを提供する。</summary>
public interface IServerHostEnvironment
{
    /// <summary>前回起動した設定（なければ既定）。</summary>
    GameSettings LoadLastSettings();

    void SaveLastSettings(GameSettings settings);

    /// <summary>実行ファイルの場所などを補って起動要求を作る。問題があれば <paramref name="errors"/> に理由を入れて null。</summary>
    ServerLaunchRequest? PrepareLaunch(GameSettings settings, out IReadOnlyList<string> errors);

    string? LogFilePathOverride { get; }

    LogSourceMode LogSource { get; }

    void Log(HostLogLevel level, string message);
}

public sealed class ServerHostOptions
{
    public required string ServerId { get; init; }

    /// <summary>常駐サービスの起動時（＝EC2 の起動時）に、ゲームサーバーを自動で起動する。</summary>
    public bool AutoStartGameServer { get; init; } = true;

    /// <summary>常駐サービスの終了時（Windows のシャットダウン時など）に、ゲームサーバーを安全に停止する。</summary>
    public bool StopServerOnShutdown { get; init; } = true;

    public TimeSpan RequestPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan GracefulStopTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>起動時に、これより古い要求は実行しない（前回の停止より前に出された要求を繰り返さないため）。</summary>
    public TimeSpan MaxRequestAgeAtStartup { get; init; } = TimeSpan.FromMinutes(30);

    public string? InstanceId { get; init; }

    public string? HostVersion { get; init; }
}

/// <summary>
/// EC2 上の常駐サービスの中身。要求（起動・停止・再起動）を順に処理し、ゲームサーバーの状態を書き出す。
/// <list type="bullet">
/// <item>起動時: 未処理の新しい要求があればそれを、なければ（自動起動が有効なら）前回の設定で起動する</item>
/// <item>以降: 要求を定期的に確認し、状態が変わるたび・一定間隔ごとに状態を書き出す</item>
/// <item>終了時: ゲームサーバーを安全に停止する</item>
/// </list>
/// 要求の置き場所との通信に失敗しても止まらず、次の周期でやり直す。
/// </summary>
public sealed class ServerHostCoordinator(
    GameServerController controller,
    IServerControlStore store,
    ServerHostOptions options,
    IServerHostEnvironment environment,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private DateTimeOffset _hostStartedAt;
    private DateTimeOffset _lastPublishedAt = DateTimeOffset.MinValue;
    private volatile bool _dirty = true;
    private string? _activeRequestId;
    private ServerRequestResult? _lastResult;
    private GameSettings? _currentSettings;
    private string? _lastExitMessage;

    private IServerProcessManager Manager => controller.Manager;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        _hostStartedAt = _time.GetUtcNow();
        Manager.StatusChanged += (_, _) => _dirty = true;
        Manager.ServerExited += (_, info) => _lastExitMessage = DescribeExit(info);
        environment.Log(HostLogLevel.Info, $"常駐サービスを開始しました（サーバーID: {options.ServerId}、インスタンス: {options.InstanceId ?? "不明"}）。");

        var previous = await TryStoreAsync(() => store.GetHeartbeatAsync(options.ServerId, stoppingToken), "前回の状態の読み込み");
        _lastResult = previous?.LastResult;

        // 常駐サービスだけが再起動した場合など、既に動いているサーバーは管理下に置く。
        if (Manager.AttachToRunningServer(environment.LogFilePathOverride, environment.LogSource) is { } attached)
        {
            environment.Log(HostLogLevel.Info, $"起動済みのサーバー（PID {attached.ProcessId}）を管理下に置きました。");
        }

        await RunStartupActionAsync(stoppingToken);
        await PublishAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(options.RequestPollInterval, _time, stoppingToken);
                var request = await TryStoreAsync(() => store.GetRequestAsync(options.ServerId, stoppingToken), "要求の確認");
                if (request is not null && request.RequestId != _lastResult?.RequestId)
                {
                    await ProcessAsync(request, stoppingToken);
                }

                if (_dirty || _time.GetUtcNow() - _lastPublishedAt >= options.HeartbeatInterval)
                {
                    await PublishAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await ShutdownAsync();
    }

    private async Task RunStartupActionAsync(CancellationToken cancellationToken)
    {
        var request = await TryStoreAsync(() => store.GetRequestAsync(options.ServerId, cancellationToken), "要求の確認");
        if (request is not null && request.RequestId != _lastResult?.RequestId)
        {
            if (_time.GetUtcNow() - request.RequestedAt <= options.MaxRequestAgeAtStartup)
            {
                // Discord の /start などは、EC2 を起動する前に要求を書いておく。ここで拾って実行する。
                await ProcessAsync(request, cancellationToken);
                return;
            }

            _lastResult = Result(request, false, "古い要求のため実行しませんでした。");
            environment.Log(HostLogLevel.Warning, $"古い要求（{request.Action}、{request.RequestedBy}、{request.RequestedAt:yyyy-MM-dd HH:mm}）は実行しませんでした。");
        }

        if (options.AutoStartGameServer && Manager.Status.State == ServerState.Stopped)
        {
            var result = await StartCoreAsync(environment.LoadLastSettings(), cancellationToken);
            environment.Log(result.Succeeded ? HostLogLevel.Info : HostLogLevel.Error, $"自動起動: {result.Message}");
        }
    }

    private async Task ProcessAsync(ServerControlRequest request, CancellationToken cancellationToken)
    {
        environment.Log(HostLogLevel.Info, $"要求を受け付けました: {request.Action}（{request.RequestedBy}、ID {request.RequestId}）");
        _activeRequestId = request.RequestId;
        await PublishAsync(cancellationToken);

        (bool Succeeded, string Message) outcome;
        try
        {
            outcome = request.Action switch
            {
                ServerControlAction.Start => await StartForRequestAsync(request, cancellationToken),
                ServerControlAction.Stop => await StopCoreAsync(),
                ServerControlAction.Restart => await RestartAsync(request, cancellationToken),
                _ => (false, $"不明な要求です: {request.Action}"),
            };
        }
        catch (OperationInProgressException ex)
        {
            outcome = (false, ex.Message);
        }

        _lastResult = Result(request, outcome.Succeeded, outcome.Message);
        _activeRequestId = null;
        environment.Log(
            outcome.Succeeded ? HostLogLevel.Info : HostLogLevel.Error,
            $"要求の結果: {request.Action}（{request.RequestedBy}）{(outcome.Succeeded ? "成功" : "失敗")} - {outcome.Message}");
        await PublishAsync(cancellationToken);
    }

    private async Task<(bool, string)> StartForRequestAsync(ServerControlRequest request, CancellationToken cancellationToken)
    {
        var status = Manager.Status;
        if (status.State == ServerState.Running)
        {
            var map = _currentSettings is { } current ? $"（マップ: {GameMaps.Get(current.Map).DisplayName}）" : "";
            return (true, $"サーバーは既に起動しています{map}。設定を変える場合は再起動してください。");
        }

        if (status.State == ServerState.Starting)
        {
            return await controller.WaitForReadyAsync(options.ReadyTimeout, cancellationToken)
                ? (true, "起動処理中だったサーバーの起動が完了しました。")
                : (false, "起動処理中のサーバーの起動完了を確認できませんでした。");
        }

        return await StartCoreAsync(request.Settings ?? environment.LoadLastSettings(), cancellationToken);
    }

    private async Task<(bool, string)> RestartAsync(ServerControlRequest request, CancellationToken cancellationToken)
    {
        var settings = request.Settings ?? _currentSettings ?? environment.LoadLastSettings();
        if (environment.PrepareLaunch(settings, out var errors) is null)
        {
            return (false, string.Join(" ", errors));
        }

        var stopped = await StopCoreAsync();
        return stopped.Item1 ? await StartCoreAsync(settings, cancellationToken) : stopped;
    }

    private async Task<(bool Succeeded, string Message)> StartCoreAsync(GameSettings settings, CancellationToken cancellationToken)
    {
        if (environment.PrepareLaunch(settings, out var errors) is not { } launch)
        {
            return (false, string.Join(" ", errors));
        }

        try
        {
            await controller.StartAsync(launch);
        }
        catch (ServerStartException ex)
        {
            return (false, ex.Message);
        }

        _currentSettings = settings;
        _lastExitMessage = null;
        _dirty = true;
        if (await controller.WaitForReadyAsync(options.ReadyTimeout, cancellationToken))
        {
            environment.SaveLastSettings(settings);
            return (true, $"サーバーを起動しました（マップ: {GameMaps.Get(settings.Map).DisplayName}）。");
        }

        return Manager.Status.State == ServerState.Stopped
            ? (false, $"起動中にサーバーが終了しました。{_lastExitMessage}".TrimEnd())
            : (false, $"{options.ReadyTimeout.TotalMinutes:0}分待ちましたが、起動完了を確認できませんでした。サーバーは動いている可能性があります。");
    }

    private async Task<(bool, string)> StopCoreAsync()
    {
        if (Manager.Status.State == ServerState.Stopped)
        {
            return (true, "サーバーは既に停止しています。");
        }

        var result = await controller.StopAsync(options.GracefulStopTimeout, _ => Task.FromResult(true));
        return result == StopResult.Stopped
            ? (true, "サーバーを停止しました。")
            : (false, "サーバーを停止できませんでした（強制終了にも失敗しました）。");
    }

    private async Task ShutdownAsync()
    {
        if (options.StopServerOnShutdown && Manager.Status.State != ServerState.Stopped)
        {
            environment.Log(HostLogLevel.Info, "常駐サービスの終了に合わせて、サーバーを停止します。");
            try
            {
                await StopCoreAsync();
            }
            catch (OperationInProgressException ex)
            {
                environment.Log(HostLogLevel.Warning, ex.Message);
            }
        }

        // 終了処理中のため、待たずに打ち切れるよう短い時間だけ試す。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await PublishAsync(timeout.Token);
        environment.Log(HostLogLevel.Info, "常駐サービスを終了しました。");
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        _dirty = false;
        var status = Manager.Status;
        var heartbeat = new ServerHeartbeat
        {
            ServerId = options.ServerId,
            UpdatedAt = _time.GetUtcNow(),
            HostStartedAt = _hostStartedAt,
            HostVersion = options.HostVersion,
            InstanceId = options.InstanceId,
            State = status.State,
            IsReady = GameServerController.IsReady(status),
            MatchState = status.State == ServerState.Stopped ? null : status.MatchState,
            PlayerCount = status.PlayerCount,
            Port = status.ListeningPort,
            ServerStartedAt = status.State == ServerState.Stopped ? null : status.StartedAt,
            Settings = _currentSettings,
            LastExitMessage = _lastExitMessage,
            ActiveRequestId = _activeRequestId,
            LastResult = _lastResult,
        };
        if (await TryStoreAsync(async () =>
            {
                await store.PutHeartbeatAsync(options.ServerId, heartbeat, cancellationToken);
                return true;
            }, "状態の書き出し"))
        {
            _lastPublishedAt = heartbeat.UpdatedAt;
        }
        else
        {
            _dirty = true;
        }
    }

    private async Task<T?> TryStoreAsync<T>(Func<Task<T>> action, string what)
    {
        try
        {
            return await action();
        }
        catch (CloudOperationException ex)
        {
            environment.Log(HostLogLevel.Warning, $"{what}に失敗しました（次の周期で再試行します）: {ex.Message}");
            return default;
        }
        catch (OperationCanceledException)
        {
            return default;
        }
    }

    private ServerRequestResult Result(ServerControlRequest request, bool succeeded, string message) => new()
    {
        RequestId = request.RequestId,
        Action = request.Action,
        Succeeded = succeeded,
        Message = message,
        CompletedAt = _time.GetUtcNow(),
    };

    private static string DescribeExit(ServerExitInfo info) => info.Kind switch
    {
        ServerExitKind.Requested => "要求により停止しました。",
        ServerExitKind.EndedByServer => "試合の終了などにより、サーバーが自分で終了しました。",
        _ => $"サーバーが予期せず終了しました（終了コード {info.ExitCode?.ToString() ?? "不明"}）。",
    };
}
