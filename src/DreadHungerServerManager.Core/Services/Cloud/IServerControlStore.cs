using System.Collections.Concurrent;
using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services.Cloud;

/// <summary>
/// GUI・Discord Bot と EC2 上の常駐サービス（Host）の間で、要求と状態を受け渡す置き場所。
/// Host から取りに行く方式のため、EC2 に受信用のポートを開ける必要がない。
/// 実装は AWS SSM Parameter Store（別プロジェクト）と、テスト用の <see cref="InMemoryServerControlStore"/>。
/// 失敗時は <see cref="CloudOperationException"/>。
/// </summary>
public interface IServerControlStore
{
    Task<ServerControlRequest?> GetRequestAsync(string serverId, CancellationToken cancellationToken = default);

    /// <summary>要求を書き込む（前の要求は上書きされる）。</summary>
    Task PutRequestAsync(string serverId, ServerControlRequest request, CancellationToken cancellationToken = default);

    Task<ServerHeartbeat?> GetHeartbeatAsync(string serverId, CancellationToken cancellationToken = default);

    Task PutHeartbeatAsync(string serverId, ServerHeartbeat heartbeat, CancellationToken cancellationToken = default);

    /// <summary>自動停止の設定（GUI が書き、常駐サービスが読む）。なければnull。</summary>
    Task<AutoStopConfig?> GetAutoStopConfigAsync(string serverId, CancellationToken cancellationToken = default);

    Task PutAutoStopConfigAsync(string serverId, AutoStopConfig config, CancellationToken cancellationToken = default);
}

/// <summary>メモリ上の <see cref="IServerControlStore"/>（テスト・画面確認用）。</summary>
public sealed class InMemoryServerControlStore : IServerControlStore
{
    private readonly ConcurrentDictionary<string, ServerControlRequest> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ServerHeartbeat> _heartbeats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AutoStopConfig> _autoStopConfigs = new(StringComparer.Ordinal);

    public Task<ServerControlRequest?> GetRequestAsync(string serverId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_requests.TryGetValue(serverId, out var request) ? request : null);

    public Task PutRequestAsync(string serverId, ServerControlRequest request, CancellationToken cancellationToken = default)
    {
        _requests[serverId] = request;
        return Task.CompletedTask;
    }

    public Task<ServerHeartbeat?> GetHeartbeatAsync(string serverId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_heartbeats.TryGetValue(serverId, out var heartbeat) ? heartbeat : null);

    public Task PutHeartbeatAsync(string serverId, ServerHeartbeat heartbeat, CancellationToken cancellationToken = default)
    {
        _heartbeats[serverId] = heartbeat;
        return Task.CompletedTask;
    }

    public Task<AutoStopConfig?> GetAutoStopConfigAsync(string serverId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_autoStopConfigs.TryGetValue(serverId, out var config) ? config : null);

    public Task PutAutoStopConfigAsync(string serverId, AutoStopConfig config, CancellationToken cancellationToken = default)
    {
        _autoStopConfigs[serverId] = config;
        return Task.CompletedTask;
    }
}
