namespace DreadHungerServerManager.Core.Services.Cloud;

/// <summary>実行中の操作の印。期限（<see cref="ExpiresAt"/>）を過ぎたら無効（途中で落ちても残り続けないため）。</summary>
public sealed record OperationLease
{
    public required string LeaseId { get; init; }

    /// <summary>操作の名前（例: 「起動」）。</summary>
    public required string Operation { get; init; }

    /// <summary>操作した人（例: "Discord:123 (name)"）。記録用。</summary>
    public required string Owner { get; init; }

    /// <summary>ほかの人に見せる名前（例: Discord の表示名）。IDを見せないために <see cref="Owner"/> と分ける。</summary>
    public string? OwnerName { get; init; }

    public required DateTimeOffset AcquiredAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed record OperationLockResult(OperationLease? Acquired, OperationLease? Holder)
{
    public bool IsAcquired => Acquired is not null;

    /// <summary>取れなかったときの説明（利用者向け）。</summary>
    public string BusyMessage => Holder is { } holder
        ? $"ほかの操作（{holder.Operation}、{holder.OwnerName ?? holder.Owner}）を実行中です。終わってからもう一度実行してください。（Server operation already in progress）"
        : "ほかの操作を実行中です。終わってからもう一度実行してください。（Server operation already in progress）";
}

/// <summary>
/// Discord の /start と /stop、GUI の操作などが同時に動かないようにする（別のプロセス・別の PC の間の排他）。
/// <see cref="IServerControlStore"/> の補助値（operation）に期限付きの印を置く。
/// 書いた直後に読み直して自分の印か確かめるため、ほぼ同時の取得でも片方だけが成功する。
/// </summary>
public sealed class ServerOperationLock(IServerControlStore store, TimeProvider? timeProvider = null)
{
    public const string ValueName = "operation";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<OperationLockResult> TryAcquireAsync(string serverId, string operation, string owner, TimeSpan ttl, string? ownerName = null, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var current = ServerControlJson.Deserialize<OperationLease>(await store.GetValueAsync(serverId, ValueName, cancellationToken).ConfigureAwait(false));
        if (current is not null && current.ExpiresAt > now)
        {
            return new OperationLockResult(null, current);
        }

        var lease = new OperationLease
        {
            LeaseId = Guid.NewGuid().ToString("N"),
            Operation = operation,
            Owner = owner,
            OwnerName = ownerName,
            AcquiredAt = now,
            ExpiresAt = now + ttl,
        };
        await store.PutValueAsync(serverId, ValueName, ServerControlJson.Serialize(lease), cancellationToken).ConfigureAwait(false);

        var written = ServerControlJson.Deserialize<OperationLease>(await store.GetValueAsync(serverId, ValueName, cancellationToken).ConfigureAwait(false));
        return written?.LeaseId == lease.LeaseId ? new OperationLockResult(lease, null) : new OperationLockResult(null, written);
    }

    /// <summary>自分の印なら期限切れにして手放す（ほかの人の印は消さない）。</summary>
    public async Task ReleaseAsync(string serverId, OperationLease lease, CancellationToken cancellationToken = default)
    {
        var current = ServerControlJson.Deserialize<OperationLease>(await store.GetValueAsync(serverId, ValueName, cancellationToken).ConfigureAwait(false));
        if (current?.LeaseId == lease.LeaseId)
        {
            await store.PutValueAsync(serverId, ValueName, ServerControlJson.Serialize(lease with { ExpiresAt = _time.GetUtcNow() }), cancellationToken).ConfigureAwait(false);
        }
    }
}
