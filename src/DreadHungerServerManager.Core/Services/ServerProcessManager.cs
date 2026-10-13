using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Interop;
using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services;

public sealed record ServerLaunchRequest
{
    public required string ExecutablePath { get; init; }

    public required string Arguments { get; init; }

    public string? LogFilePath { get; init; }

    public LogSourceMode LogSource { get; init; } = LogSourceMode.Auto;
}

public enum StopOutcome
{
    AlreadyStopped,
    Exited,
    TimedOut,

    /// <summary>Ctrl+Cを送れるコンソールもウィンドウも見つからなかった。</summary>
    SignalUnavailable,
}

public enum ServerStartError
{
    ExecutableNotFound,
    AlreadyRunning,
    LaunchFailed,
}

public sealed class ServerStartException(ServerStartError error, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ServerStartError Error { get; } = error;
}

public sealed class ServerProcessManagerOptions
{
    /// <summary>
    /// サーバーとみなすプロセス名。名前そのもの、または「名前-」で始まるもの
    /// （DreadHungerServer-Win64-Shipping）が対象。このアプリ（DreadHungerServerManager）は含まない。
    /// </summary>
    public IReadOnlyList<string> ServerProcessBaseNames { get; init; } = ["DreadHungerServer"];

    public TimeSpan MonitorInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan LogPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>起動済みサーバーを検出したとき、ログ画面に表示する直近の行数。</summary>
    public int AttachReplayLineCount { get; init; } = 200;
}

/// <summary>
/// 1台のDread Hungerサーバーのプロセスを管理する。UIスレッドを前提にせず、イベントは任意のスレッドから発生する。
/// 複数サーバー管理に拡張する場合は、サーバーごとにこのクラスのインスタンスを持つ。
/// </summary>
public interface IServerProcessManager : IDisposable
{
    ServerStatus Status { get; }

    event EventHandler? StatusChanged;

    event EventHandler<IReadOnlyList<ServerLogLine>>? LogReceived;

    /// <summary>起動処理中から起動完了（ポート待受またはWaitingToStartを検出）に変わったとき。</summary>
    event EventHandler? ServerReady;

    event EventHandler<ServerExitInfo>? ServerExited;

    IReadOnlyList<RunningServerProcess> FindRunningServers();

    /// <summary>サーバーを起動する。失敗時は <see cref="ServerStartException"/>。</summary>
    void Start(ServerLaunchRequest request);

    /// <summary>このアプリ以外で起動済みのサーバーを検出して管理下に置く。見つからなければnull。</summary>
    RunningServerProcess? AttachToRunningServer(string? logFilePathOverride, LogSourceMode logSource);

    /// <summary>Ctrl+Cで安全な終了を要求し、終了を待つ。TimedOutの場合は停止処理中のまま。</summary>
    Task<StopOutcome> StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);

    /// <summary>TimedOut後に停止を取りやめ、稼働中の状態へ戻す。</summary>
    void CancelStop();

    /// <summary>プロセスツリーごと強制終了する。</summary>
    Task<bool> KillAsync(TimeSpan timeout);

    /// <summary>サーバーを止めずに管理だけをやめる（アプリ終了時に「残して終了」を選んだ場合）。</summary>
    void Detach();
}

public sealed class ServerProcessManager : IServerProcessManager
{
    private readonly ServerProcessManagerOptions _options;
    private readonly Lock _gate = new();
    private ServerStatus _status = ServerStatus.Stopped;
    private Session? _session;

    public ServerProcessManager(ServerProcessManagerOptions? options = null)
    {
        _options = options ?? new ServerProcessManagerOptions();
    }

    public event EventHandler? StatusChanged;

    public event EventHandler<IReadOnlyList<ServerLogLine>>? LogReceived;

    public event EventHandler? ServerReady;

    public event EventHandler<ServerExitInfo>? ServerExited;

    public ServerStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public IReadOnlyList<RunningServerProcess> FindRunningServers()
    {
        var self = Environment.ProcessId;
        return ProcessSnapshot.Take()
            .Where(entry => entry.ProcessId != self && IsServerProcessName(entry.ExeName))
            .Select(entry => new RunningServerProcess(
                entry.ProcessId,
                entry.ParentProcessId,
                Path.GetFileNameWithoutExtension(entry.ExeName),
                ProcessSnapshot.TryGetImagePath(entry.ProcessId)))
            .ToList();
    }

    public void Start(ServerLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var executablePath = request.ExecutablePath.Trim();
        Session session;
        lock (_gate)
        {
            if (_session is not null)
            {
                throw new ServerStartException(ServerStartError.AlreadyRunning, "このアプリで管理しているサーバーが既に起動しています。");
            }

            if (!File.Exists(executablePath))
            {
                throw new ServerStartException(
                    ServerStartError.ExecutableNotFound,
                    $"サーバーの実行ファイルが見つかりません。{Environment.NewLine}{executablePath}");
            }

            var running = FindRunningServers();
            if (running.Count > 0)
            {
                throw new ServerStartException(
                    ServerStartError.AlreadyRunning,
                    $"DreadHungerServer.exe は既に起動しています（PID: {string.Join(", ", running.Select(process => process.ProcessId))}）。二重起動はできません。");
            }

            session = new Session(request.LogSource, isAttached: false);
            _session = session;
            _status = new ServerStatus
            {
                State = ServerState.Starting,
                StartedAt = DateTimeOffset.Now,
                LogFilePath = request.LogFilePath,
            };
        }

        RaiseStatusChanged();
        EmitManagerLine($"サーバーを起動します: {Path.GetFileName(executablePath)} {request.Arguments}");

        if (session.LogSource != LogSourceMode.StandardOutput && !string.IsNullOrWhiteSpace(request.LogFilePath))
        {
            session.Tailer = new LogFileTailer(request.LogFilePath, _options.LogPollInterval);
            session.Tailer.LinesReceived += (lines, _) => OnServerLines(session, lines, LogLineSource.LogFile, displayLimit: null);
            session.Tailer.Start(readExistingContent: false);
        }

        // DreadHungerServer.exeはGUIアプリのため、本体は自前のコンソールへログを出す（パイプでは取れない
        // ことが多い）。標準出力は取れた場合の補助として扱い、ログファイルを主に使う。
        var redirect = session.LogSource != LogSourceMode.LogFile;
        var startInfo = new ProcessStartInfo(executablePath, request.Arguments)
        {
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            StandardOutputEncoding = redirect ? Encoding.UTF8 : null,
            StandardErrorEncoding = redirect ? Encoding.UTF8 : null,
        };

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AbandonSession(session);
            EmitManagerLine($"サーバーの起動に失敗しました: {ex.Message}", LogLineKind.Error);
            throw new ServerStartException(ServerStartError.LaunchFailed, $"サーバーの起動に失敗しました。{Environment.NewLine}{ex.Message}", ex);
        }

        if (redirect)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    OnServerLines(session, [e.Data], LogLineSource.StandardOutput, displayLimit: null);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    OnServerLines(session, [e.Data], LogLineSource.StandardError, displayLimit: null);
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => session.Wake();
        session.Root = process;
        session.AddTracked(new TrackedProcess(process, process.Id, Path.GetFileNameWithoutExtension(executablePath), Depth: 0));

        lock (_gate)
        {
            if (_session == session)
            {
                _status = _status with
                {
                    ProcessId = process.Id,
                    MainProcessId = process.Id,
                    StartedAt = TryGetStartTime(process) ?? _status.StartedAt,
                };
            }
        }

        RaiseStatusChanged();
        EmitManagerLine($"プロセスを起動しました（PID {process.Id}）。起動完了のログを待っています…");
        session.MonitorTask = Task.Run(() => MonitorAsync(session));
    }

    public RunningServerProcess? AttachToRunningServer(string? logFilePathOverride, LogSourceMode logSource)
    {
        lock (_gate)
        {
            if (_session is not null)
            {
                return null;
            }
        }

        var servers = FindRunningServers();
        if (servers.Count == 0)
        {
            return null;
        }

        // 親がサーバーでないもの（＝ブートストラップ側）を管理の起点にする。
        var root = servers.FirstOrDefault(candidate => servers.All(other => other.ProcessId != candidate.ParentProcessId)) ?? servers[0];
        var process = TryOpenProcess(root.ProcessId);
        var logFilePath = string.IsNullOrWhiteSpace(logFilePathOverride)
            ? ServerPaths.GetDefaultLogFilePath(root.ExecutablePath)
            : logFilePathOverride.Trim();
        var session = new Session(logSource, isAttached: true) { Root = process };
        session.AddTracked(new TrackedProcess(process, root.ProcessId, root.Name, Depth: 0));

        lock (_gate)
        {
            if (_session is not null)
            {
                process?.Dispose();
                return null;
            }

            _session = session;
            _status = new ServerStatus
            {
                State = ServerState.Running,
                IsAttached = true,
                ProcessId = root.ProcessId,
                MainProcessId = root.ProcessId,
                StartedAt = TryGetStartTime(process),
                LogFilePath = logFilePath,
            };
        }

        RaiseStatusChanged();
        EmitManagerLine($"既に起動している {root.Name}.exe（PID {root.ProcessId}）を検出しました。このアプリから監視・停止できます。");
        if (process is not null)
        {
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => session.Wake();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // 終了通知を受け取れない場合も、定期的な監視で終了を検出できる。
            }
        }

        RefreshTrackedProcesses(session);
        if (logSource != LogSourceMode.StandardOutput && !string.IsNullOrWhiteSpace(logFilePath))
        {
            session.Tailer = new LogFileTailer(logFilePath, _options.LogPollInterval);
            session.Tailer.LinesReceived += (lines, isCatchUp) =>
                OnServerLines(session, lines, LogLineSource.LogFile, isCatchUp ? _options.AttachReplayLineCount : null);
            session.Tailer.Start(readExistingContent: true);
        }

        session.MonitorTask = Task.Run(() => MonitorAsync(session));
        return root;
    }

    public async Task<StopOutcome> StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            if (session is null)
            {
                return StopOutcome.AlreadyStopped;
            }

            BeginStopLocked(session);
        }

        RaiseStatusChanged();
        EmitManagerLine("サーバーに終了を要求しています（Ctrl+C）…");
        var signaled = await Task.Run(
            () =>
            {
                // 本体（子プロセス）をまだ検出していなければ、Ctrl+Cの送り先を決める前に探す。
                RefreshTrackedProcesses(session);
                return SendGracefulSignal(session);
            },
            cancellationToken).ConfigureAwait(false);
        if (!signaled)
        {
            EmitManagerLine("安全な終了の要求を送れませんでした（サーバーのコンソールが見つかりません）。", LogLineKind.Warning);
            return session.Ended.Task.IsCompleted ? StopOutcome.Exited : StopOutcome.SignalUnavailable;
        }

        session.Wake();
        var completed = await Task.WhenAny(session.Ended.Task, Task.Delay(gracefulTimeout, cancellationToken)).ConfigureAwait(false);
        if (completed == session.Ended.Task)
        {
            return StopOutcome.Exited;
        }

        EmitManagerLine($"{gracefulTimeout.TotalSeconds:0}秒待ちましたが、サーバーが終了していません。", LogLineKind.Warning);
        return StopOutcome.TimedOut;
    }

    public void CancelStop()
    {
        lock (_gate)
        {
            if (_session is not { StopRequested: true } session || _status.State != ServerState.Stopping)
            {
                return;
            }

            session.StopRequested = false;
            var ready = _status.IsPortListening || _status.IsWaitingToStart;
            _status = _status with
            {
                State = session.StateBeforeStop == ServerState.Starting && !ready ? ServerState.Starting : ServerState.Running,
            };
        }

        RaiseStatusChanged();
        EmitManagerLine("停止を取りやめました。サーバーは稼働を続けています。");
    }

    public async Task<bool> KillAsync(TimeSpan timeout)
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            if (session is null)
            {
                return true;
            }

            BeginStopLocked(session);
        }

        RaiseStatusChanged();
        EmitManagerLine("サーバーを強制終了しています…", LogLineKind.Warning);
        var failures = new List<string>();
        foreach (var tracked in session.GetTracked().OrderBy(tracked => tracked.Depth))
        {
            try
            {
                if (tracked.Process is { HasExited: false } process)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                failures.Add($"PID {tracked.ProcessId}: {ex.Message}");
            }
        }

        session.Wake();
        var completed = await Task.WhenAny(session.Ended.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed == session.Ended.Task)
        {
            return true;
        }

        var detail = failures.Count > 0 ? string.Join(" / ", failures) : "応答がありません";
        EmitManagerLine($"強制終了できませんでした（{detail}）。管理者として実行する必要があるかもしれません。", LogLineKind.Error);
        return false;
    }

    public void Detach()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
            _status = ServerStatus.Stopped;
        }

        if (session is null)
        {
            return;
        }

        session.Cancel();
        session.Tailer?.Dispose();
        session.DisposeProcesses();
        session.Ended.TrySetResult();
        RaiseStatusChanged();
    }

    public void Dispose() => Detach();

    private void BeginStopLocked(Session session)
    {
        if (!session.StopRequested)
        {
            session.StateBeforeStop = _status.State;
        }

        session.StopRequested = true;
        _status = _status with { State = ServerState.Stopping };
    }

    private async Task MonitorAsync(Session session)
    {
        try
        {
            while (!session.IsCancelled)
            {
                var alive = RefreshTrackedProcesses(session);
                if (alive is not null && !session.GetTracked().Any(tracked => IsAlive(tracked, alive)))
                {
                    FinishSession(session);
                    return;
                }

                if (!await session.WaitForWakeAsync(_options.MonitorInterval).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            EmitManagerLine($"プロセス監視でエラーが発生しました: {ex.Message}", LogLineKind.Error);
        }
    }

    /// <summary>
    /// 管理中のプロセスの子孫からサーバー本体（DreadHungerServer-Win64-Shipping.exe等）を探して追跡に加える。
    /// 戻り値はスナップショット時点で存在したPIDの集合（取得失敗時はnull）。
    /// </summary>
    private HashSet<int>? RefreshTrackedProcesses(Session session)
    {
        IReadOnlyList<ProcessEntry> snapshot;
        try
        {
            snapshot = ProcessSnapshot.Take();
        }
        catch (Win32Exception)
        {
            return null;
        }

        var alive = snapshot.Select(entry => entry.ProcessId).ToHashSet();
        var tracked = session.GetTracked();
        var known = tracked.Select(item => item.ProcessId).ToHashSet();
        var queue = new Queue<TrackedProcess>(tracked.Where(item => IsAlive(item, alive)));
        TrackedProcess? discovered = null;
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var entry in snapshot)
            {
                if (entry.ParentProcessId != parent.ProcessId || known.Contains(entry.ProcessId) || !IsServerProcessName(entry.ExeName))
                {
                    continue;
                }

                var process = TryOpenProcess(entry.ProcessId);
                if (process is not null && !StartedAfter(process, parent.Process))
                {
                    // PIDが再利用された無関係のプロセス。
                    process.Dispose();
                    continue;
                }

                var child = new TrackedProcess(process, entry.ProcessId, Path.GetFileNameWithoutExtension(entry.ExeName), parent.Depth + 1);
                session.AddTracked(child);
                known.Add(entry.ProcessId);
                queue.Enqueue(child);
                discovered = child;
            }
        }

        if (discovered is not null)
        {
            var changed = false;
            lock (_gate)
            {
                if (_session == session)
                {
                    _status = _status with { MainProcessId = discovered.ProcessId };
                    changed = true;
                }
            }

            if (changed)
            {
                RaiseStatusChanged();
                EmitManagerLine($"サーバー本体のプロセスを検出しました（{discovered.Name}.exe, PID {discovered.ProcessId}）。");
            }
        }

        return alive;
    }

    private void FinishSession(Session session)
    {
        // 終了直前に書かれたクラッシュ情報なども表示するため、先にログを読み切る。
        session.Tailer?.Stop();
        var exitCode = TryGetExitCode(session.Root)
            ?? session.GetTracked().Select(tracked => TryGetExitCode(tracked.Process)).LastOrDefault(code => code is not null);

        ServerExitInfo info;
        lock (_gate)
        {
            if (_session != session)
            {
                return;
            }

            var startedDuringStartup = _status.State == ServerState.Starting
                || (session.StopRequested && session.StateBeforeStop == ServerState.Starting);
            var uptime = _status.StartedAt is { } startedAt ? DateTimeOffset.Now - startedAt : (TimeSpan?)null;
            info = new ServerExitInfo(exitCode, session.StopRequested, startedDuringStartup, uptime);
            _session = null;
            _status = ServerStatus.Stopped;
        }

        session.DisposeProcesses();
        session.Ended.TrySetResult();
        if (info.WasRequested)
        {
            EmitManagerLine("サーバーを停止しました。", LogLineKind.Manager);
        }
        else
        {
            var code = exitCode is { } value ? $"終了コード {value}" : "終了コード不明";
            EmitManagerLine($"サーバーが予期せず終了しました（{code}）。", LogLineKind.Error);
        }

        RaiseStatusChanged();
        ServerExited?.Invoke(this, info);
    }

    /// <summary>起動に失敗したセッションを片付ける（終了イベントは出さない）。</summary>
    private void AbandonSession(Session session)
    {
        lock (_gate)
        {
            if (_session == session)
            {
                _session = null;
                _status = ServerStatus.Stopped;
            }
        }

        session.Cancel();
        session.Tailer?.Dispose();
        session.Ended.TrySetResult();
        RaiseStatusChanged();
    }

    private void OnServerLines(Session session, IReadOnlyList<string> lines, LogLineSource source, int? displayLimit)
    {
        var display = new List<ServerLogLine>();
        bool statusChanged;
        var becameReady = false;
        lock (_gate)
        {
            if (_session != session)
            {
                return;
            }

            var status = _status;
            foreach (var line in lines)
            {
                if (!status.IsPortListening && ServerLogAnalyzer.TryGetListeningPort(line, out var port))
                {
                    status = status with { IsPortListening = true, ListeningPort = port };
                }

                if (!status.IsWaitingToStart && ServerLogAnalyzer.IsWaitingToStart(line))
                {
                    status = status with { IsWaitingToStart = true };
                }
            }

            if (status.State == ServerState.Starting && (status.IsPortListening || status.IsWaitingToStart))
            {
                status = status with { State = ServerState.Running };
                becameReady = true;
            }

            statusChanged = status != _status;
            _status = status;

            if (source == LogLineSource.LogFile)
            {
                session.FileLinesSeen = true;
            }

            // 自動: ログファイルを優先し、ログファイルから行が届くまでは標準出力を表示する。
            var show = session.LogSource switch
            {
                LogSourceMode.LogFile => source == LogLineSource.LogFile,
                LogSourceMode.StandardOutput => source != LogLineSource.LogFile,
                _ => source == LogLineSource.LogFile || !session.FileLinesSeen,
            };
            if (show)
            {
                var now = DateTimeOffset.Now;
                var start = displayLimit is { } limit ? Math.Max(0, lines.Count - limit) : 0;
                for (var i = start; i < lines.Count; i++)
                {
                    display.Add(new ServerLogLine(now, lines[i], source, ServerLogAnalyzer.Classify(lines[i])));
                }
            }
        }

        if (statusChanged)
        {
            RaiseStatusChanged();
        }

        if (display.Count > 0)
        {
            LogReceived?.Invoke(this, display);
        }

        if (becameReady)
        {
            var status = Status;
            var detail = status.ListeningPort is { } port ? $"ポート {port} で待ち受け中" : "マップの読み込みを確認";
            EmitManagerLine($"サーバー起動完了（{detail}）。", LogLineKind.Success);
            ServerReady?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool SendGracefulSignal(Session session)
    {
        // 子（サーバー本体）から順に試す。本体は -log 指定時に自前のコンソールを持つ。
        var tracked = session.GetTracked();
        var serverProcessIds = tracked.Select(item => item.ProcessId).ToHashSet();
        var candidates = tracked
            .Where(item => item.Process is null || !HasExitedSafe(item.Process))
            .OrderByDescending(item => item.Depth)
            .ToList();
        foreach (var candidate in candidates)
        {
            if (ConsoleControl.TrySendCtrlC(candidate.ProcessId, serverProcessIds))
            {
                return true;
            }
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (candidate.Process is { } process && process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow())
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        return false;
    }

    private void EmitManagerLine(string message, LogLineKind kind = LogLineKind.Manager)
    {
        var now = DateTimeOffset.Now;
        LogReceived?.Invoke(this, [new ServerLogLine(now, $"[{now:HH:mm:ss}] {message}", LogLineSource.Manager, kind)]);
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private bool IsServerProcessName(string exeOrProcessName)
    {
        var name = exeOrProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exeOrProcessName[..^4]
            : exeOrProcessName;
        return _options.ServerProcessBaseNames.Any(baseName =>
            string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(baseName + "-", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAlive(TrackedProcess tracked, HashSet<int> alivePids)
    {
        if (tracked.Process is not null)
        {
            try
            {
                return !tracked.Process.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // ハンドルを開けない（権限不足など）場合はプロセス一覧で判定する。
            }
        }

        return alivePids.Contains(tracked.ProcessId);
    }

    private static bool HasExitedSafe(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static Process? TryOpenProcess(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            try
            {
                // 終了後に終了コードを読めるよう、生きているうちにハンドルを開いておく。
                _ = process.Handle;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
            }

            return process;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool StartedAfter(Process child, Process? parent)
    {
        if (parent is null)
        {
            return true;
        }

        try
        {
            return child.StartTime >= parent.StartTime.AddSeconds(-1);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return true;
        }
    }

    private static DateTimeOffset? TryGetStartTime(Process? process)
    {
        try
        {
            return process is null ? null : new DateTimeOffset(process.StartTime);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static int? TryGetExitCode(Process? process)
    {
        try
        {
            return process is { HasExited: true } ? process.ExitCode : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record TrackedProcess(Process? Process, int ProcessId, string Name, int Depth);

    private sealed class Session(LogSourceMode logSource, bool isAttached)
    {
        private readonly List<TrackedProcess> _tracked = [];
        private readonly CancellationTokenSource _cancellation = new();
        private readonly SemaphoreSlim _wake = new(0);

        public LogSourceMode LogSource { get; } = logSource;

        public bool IsAttached { get; } = isAttached;

        public LogFileTailer? Tailer { get; set; }

        public Process? Root { get; set; }

        public Task? MonitorTask { get; set; }

        public bool StopRequested { get; set; }

        public ServerState StateBeforeStop { get; set; }

        public bool FileLinesSeen { get; set; }

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsCancelled => _cancellation.IsCancellationRequested;

        public void AddTracked(TrackedProcess tracked)
        {
            lock (_tracked)
            {
                _tracked.Add(tracked);
            }
        }

        public IReadOnlyList<TrackedProcess> GetTracked()
        {
            lock (_tracked)
            {
                return _tracked.ToList();
            }
        }

        public void Wake()
        {
            if (!IsCancelled)
            {
                _wake.Release();
            }
        }

        /// <summary>指定時間か、Wake()まで待つ。キャンセルされたらfalse。</summary>
        public async Task<bool> WaitForWakeAsync(TimeSpan timeout)
        {
            try
            {
                await _wake.WaitAsync(timeout, _cancellation.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public void Cancel() => _cancellation.Cancel();

        public void DisposeProcesses()
        {
            foreach (var tracked in GetTracked())
            {
                tracked.Process?.Dispose();
            }
        }
    }
}
