using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services.Cloud;

public enum FakeCloudOperation
{
    GetStatus,
    Start,
    Stop,
    Reboot,
    GetPublicIp,
}

/// <summary>
/// 実AWSを使わずにEC2の状態遷移を再現する（テスト・画面確認用）。時間は <see cref="TimeProvider"/> で進める。
/// <list type="bullet">
/// <item>停止中 →（起動）→ 起動処理中 →（<see cref="StartDuration"/> 後）→ 稼働中</item>
/// <item>稼働中 →（停止）→ 停止処理中 →（<see cref="StopDuration"/> 後）→ 停止中</item>
/// </list>
/// 起動のたびに別のパブリックIPを割り当てる（Elastic IP がない EC2 と同じ）。
/// </summary>
public sealed class FakeCloudInstanceService : ICloudInstanceService
{
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<FakeCloudOperation, Queue<CloudOperationException>> _failures = [];
    private CloudInstanceState _state;
    private DateTimeOffset _transitionEndsAt;
    private string? _publicIp;
    private DateTimeOffset? _launchTime;
    private string? _stateReason;
    private int _ipCounter;

    public FakeCloudInstanceService(TimeProvider? timeProvider = null, CloudInstanceState initialState = CloudInstanceState.Stopped)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _state = initialState;
        if (initialState == CloudInstanceState.Running)
        {
            _publicIp = NextPublicIp();
            _launchTime = _timeProvider.GetUtcNow();
        }
    }

    public TimeSpan StartDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan StopDuration { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>設定すると、このID以外は「見つからない」になる。</summary>
    public string? KnownInstanceId { get; set; }

    /// <summary>trueなら、起動処理の後に稼働中にならず停止中へ戻る（容量不足などによる起動失敗の再現）。</summary>
    public bool FailStartTransition { get; set; }

    /// <summary>trueなら、稼働中でもパブリックIPが割り当てられない。</summary>
    public bool PublicIpUnavailable { get; set; }

    /// <summary>設定すると、起動・停止をまたいでこのIPを使い続ける（Elastic IP の再現）。</summary>
    public string? ElasticIp { get; set; }

    public int StartRequestCount { get; private set; }

    public int StopRequestCount { get; private set; }

    public int RebootRequestCount { get; private set; }

    /// <summary>次の1回の呼び出しを失敗させる（呼ぶたびに積み重なる）。</summary>
    public void FailNext(FakeCloudOperation operation, CloudOperationException exception)
    {
        lock (_gate)
        {
            if (!_failures.TryGetValue(operation, out var queue))
            {
                _failures[operation] = queue = new Queue<CloudOperationException>();
            }

            queue.Enqueue(exception);
        }
    }

    public Task<CloudInstanceStatus> GetStatusAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Prepare(FakeCloudOperation.GetStatus, instance);
            return Task.FromResult(new CloudInstanceStatus
            {
                State = _state,
                RawState = ToRawState(_state),
                PublicIpAddress = CurrentPublicIp(),
                LaunchTime = _launchTime,
                StateReason = _stateReason,
            });
        }
    }

    public Task StartAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Prepare(FakeCloudOperation.Start, instance);
            StartRequestCount++;
            switch (_state)
            {
                case CloudInstanceState.Stopped:
                    _state = CloudInstanceState.Starting;
                    _transitionEndsAt = _timeProvider.GetUtcNow() + StartDuration;
                    _stateReason = null;
                    break;
                case CloudInstanceState.Stopping:
                    throw new CloudOperationException(CloudErrorKind.InvalidState, "EC2 は停止処理中のため起動できません。停止が終わってから起動してください。");
                case CloudInstanceState.Error:
                    throw new CloudOperationException(CloudErrorKind.InvalidState, "EC2 は起動できない状態です。");
            }

            return Task.CompletedTask;
        }
    }

    public Task StopAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Prepare(FakeCloudOperation.Stop, instance);
            StopRequestCount++;
            switch (_state)
            {
                case CloudInstanceState.Running:
                    _state = CloudInstanceState.Stopping;
                    _transitionEndsAt = _timeProvider.GetUtcNow() + StopDuration;
                    break;
                case CloudInstanceState.Starting:
                    throw new CloudOperationException(CloudErrorKind.InvalidState, "EC2 は起動処理中のため停止できません。起動が終わってから停止してください。");
                case CloudInstanceState.Error:
                    throw new CloudOperationException(CloudErrorKind.InvalidState, "EC2 は停止できない状態です。");
            }

            return Task.CompletedTask;
        }
    }

    public Task RebootAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Prepare(FakeCloudOperation.Reboot, instance);
            if (_state != CloudInstanceState.Running)
            {
                throw new CloudOperationException(CloudErrorKind.InvalidState, "EC2 が稼働中でないため再起動できません。");
            }

            // 再起動ではインスタンスは稼働中のままで、パブリックIPも変わらない。
            RebootRequestCount++;
            return Task.CompletedTask;
        }
    }

    public Task<string?> GetPublicIpAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Prepare(FakeCloudOperation.GetPublicIp, instance);
            return Task.FromResult(_state == CloudInstanceState.Running ? CurrentPublicIp() : null);
        }
    }

    private void Prepare(FakeCloudOperation operation, CloudInstanceRef instance)
    {
        if (_failures.TryGetValue(operation, out var queue) && queue.TryDequeue(out var failure))
        {
            throw failure;
        }

        if (KnownInstanceId is { } known && !string.Equals(known, instance.InstanceId, StringComparison.Ordinal))
        {
            throw new CloudOperationException(CloudErrorKind.NotFound, $"EC2 インスタンス {instance.InstanceId} が見つかりません。");
        }

        AdvanceLocked();
    }

    private void AdvanceLocked()
    {
        if (_state is not (CloudInstanceState.Starting or CloudInstanceState.Stopping) || _timeProvider.GetUtcNow() < _transitionEndsAt)
        {
            return;
        }

        if (_state == CloudInstanceState.Stopping)
        {
            _state = CloudInstanceState.Stopped;
            _publicIp = null;
            return;
        }

        if (FailStartTransition)
        {
            _state = CloudInstanceState.Stopped;
            _stateReason = "Server.InsufficientInstanceCapacity: 要求したインスタンスタイプの容量が不足しています（疑似）";
            return;
        }

        _state = CloudInstanceState.Running;
        _launchTime = _transitionEndsAt;
        _publicIp = NextPublicIp();
    }

    private string? CurrentPublicIp() =>
        PublicIpUnavailable || _state is not (CloudInstanceState.Running or CloudInstanceState.Stopping) ? null : ElasticIp ?? _publicIp;

    // ドキュメント用のアドレス範囲（RFC 5737）を使う。
    private string NextPublicIp() => $"203.0.113.{++_ipCounter % 254 + 1}";

    private static string ToRawState(CloudInstanceState state) => state switch
    {
        CloudInstanceState.Stopped => "stopped",
        CloudInstanceState.Starting => "pending",
        CloudInstanceState.Running => "running",
        CloudInstanceState.Stopping => "stopping",
        CloudInstanceState.Error => "terminated",
        _ => "unknown",
    };
}
