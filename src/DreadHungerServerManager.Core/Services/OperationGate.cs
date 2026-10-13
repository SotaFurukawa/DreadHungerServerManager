namespace DreadHungerServerManager.Core.Services;

/// <summary>別の操作が実行中のため、新しい操作を受け付けなかった。</summary>
public sealed class OperationInProgressException(string runningOperation)
    : InvalidOperationException($"「{runningOperation}」を実行中です。終わってからもう一度実行してください。（Server operation already in progress）")
{
    public string RunningOperation { get; } = runningOperation;
}

/// <summary>
/// 同じ対象への操作（起動・停止など）を1つずつに制限する。実行中に別の操作が来たら待たずに
/// <see cref="OperationInProgressException"/> を投げる（Discord と GUI から同時に操作された場合など）。
/// 同じプロセス内の排他。プロセスをまたぐ排他は別途行う。
/// </summary>
public sealed class OperationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private volatile string? _current;

    /// <summary>実行中の操作の名前。なければnull。</summary>
    public string? CurrentOperation => _current;

    public async Task RunAsync(string operation, Func<Task> action)
    {
        using var lease = Enter(operation);
        await action().ConfigureAwait(false);
    }

    public async Task<T> RunAsync<T>(string operation, Func<Task<T>> action)
    {
        using var lease = Enter(operation);
        return await action().ConfigureAwait(false);
    }

    /// <summary>操作を開始する。戻り値を Dispose すると終了する。</summary>
    public IDisposable Enter(string operation)
    {
        if (!_semaphore.Wait(0))
        {
            throw new OperationInProgressException(_current ?? "別の操作");
        }

        _current = operation;
        return new Lease(this);
    }

    private void Exit()
    {
        _current = null;
        _semaphore.Release();
    }

    private sealed class Lease(OperationGate owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Exit();
            }
        }
    }
}
