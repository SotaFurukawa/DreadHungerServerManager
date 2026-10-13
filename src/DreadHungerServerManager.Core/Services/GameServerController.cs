using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services;

public enum StopResult
{
    Stopped,

    /// <summary>強制終了の確認で「やめる」が選ばれ、サーバーは稼働を続けている。</summary>
    Cancelled,

    /// <summary>強制終了もできなかった（権限不足など）。</summary>
    Failed,
}

/// <summary>
/// ゲームサーバーの起動・停止の手順。GUI（確認ダイアログあり）と Host・Bot（確認なし）で共通に使う。
/// 同時に複数の操作が来た場合は <see cref="OperationInProgressException"/> で断る。
/// </summary>
public sealed class GameServerController(IServerProcessManager manager, OperationGate? gate = null)
{
    public static readonly TimeSpan DefaultKillTimeout = TimeSpan.FromSeconds(15);

    public IServerProcessManager Manager { get; } = manager;

    public OperationGate Gate { get; } = gate ?? new OperationGate();

    /// <summary>設定から起動要求を作る（コマンドラインは <see cref="ServerCommandBuilder"/> で組み立てる）。</summary>
    public static ServerLaunchRequest CreateLaunchRequest(
        string executablePath,
        GameSettings settings,
        string? additionalArguments,
        string? logFilePathOverride,
        LogSourceMode logSource) => new()
        {
            ExecutablePath = executablePath.Trim(),
            Arguments = ServerCommandBuilder.BuildArguments(settings, additionalArguments),
            LogFilePath = ServerPaths.ResolveLogFilePath(executablePath, logFilePathOverride),
            LogSource = logSource,
        };

    /// <summary>画面を介さずに起動する場合（Host・Bot）の事前確認。問題がなければ空。</summary>
    public static IReadOnlyList<string> ValidateLaunch(string executablePath, GameSettings settings, string? additionalArguments = null)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            errors.Add("DreadHungerServer.exe の場所が設定されていません。");
        }
        else if (!File.Exists(executablePath.Trim()))
        {
            errors.Add($"DreadHungerServer.exe が見つかりません: {executablePath.Trim()}");
        }

        errors.AddRange(GameSettingsValidator.Validate(settings).Select(error => error.Message));
        if (ServerCommandBuilder.ValidateAdditionalArguments(additionalArguments) is { } argumentsError)
        {
            errors.Add(argumentsError);
        }

        return errors;
    }

    /// <summary>起動する。失敗時は <see cref="ServerStartException"/>、操作中なら <see cref="OperationInProgressException"/>。</summary>
    public Task StartAsync(ServerLaunchRequest request) =>
        Gate.RunAsync("サーバー起動", () => Task.Run(() => Manager.Start(request)));

    /// <summary>
    /// 安全な終了（Ctrl+C）を要求し、時間内に終わらなければ <paramref name="confirmForceKill"/> に強制終了してよいかを聞く。
    /// 確認なしで強制終了する場合は <c>_ =&gt; Task.FromResult(true)</c> を渡す。
    /// </summary>
    public Task<StopResult> StopAsync(
        TimeSpan gracefulTimeout,
        Func<StopOutcome, Task<bool>> confirmForceKill,
        TimeSpan? killTimeout = null,
        CancellationToken cancellationToken = default) =>
        Gate.RunAsync("サーバー停止", async () =>
        {
            var outcome = await Manager.StopAsync(gracefulTimeout, cancellationToken).ConfigureAwait(false);
            if (outcome is StopOutcome.Exited or StopOutcome.AlreadyStopped || Manager.Status.State == ServerState.Stopped)
            {
                return StopResult.Stopped;
            }

            var force = await confirmForceKill(outcome).ConfigureAwait(false);
            if (Manager.Status.State == ServerState.Stopped)
            {
                return StopResult.Stopped;
            }

            if (!force)
            {
                Manager.CancelStop();
                return StopResult.Cancelled;
            }

            return await Manager.KillAsync(killTimeout ?? DefaultKillTimeout).ConfigureAwait(false) ? StopResult.Stopped : StopResult.Failed;
        });

    /// <summary>
    /// 起動完了（ポート待ち受けかWaitingToStartの検出）を待つ。完了したらtrue、
    /// 先にサーバーが終了した、または時間切れならfalse。
    /// </summary>
    public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, EventArgs e)
        {
            var status = Manager.Status;
            if (IsReady(status))
            {
                completion.TrySetResult(true);
            }
            else if (status.State == ServerState.Stopped)
            {
                completion.TrySetResult(false);
            }
        }

        Manager.StatusChanged += Check;
        try
        {
            Check(null, EventArgs.Empty);
            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return finished == completion.Task && completion.Task.Result;
        }
        finally
        {
            Manager.StatusChanged -= Check;
        }
    }

    public static bool IsReady(ServerStatus status) =>
        status.State == ServerState.Running && (status.IsPortListening || status.IsWaitingToStart);
}
