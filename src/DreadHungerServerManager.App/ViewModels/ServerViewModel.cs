using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;

namespace DreadHungerServerManager.App.ViewModels;

/// <summary>
/// サーバー1台分の画面状態。複数サーバー管理に拡張する場合は、サーバーごとにこのViewModelと
/// <see cref="IServerProcessManager"/> の組を持たせる。
/// </summary>
public sealed class ServerViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan StartupWarningDelay = TimeSpan.FromMinutes(3);
    private const int HostCheckIntervalSeconds = 5;

    private readonly GameServerController _controller;
    private readonly IServerProcessManager _manager;
    private readonly IUserInteraction _ui;
    private readonly SynchronizationContext _uiContext;
    private readonly Timer _clock;
    private string _executablePath;
    private ServerStatus _status = ServerStatus.Stopped;
    private string _executableStatusText = "";
    private bool _executableExists;
    private string _commandPreview = "";
    private string? _fullCommandLine;
    private bool _isNoticeOpen;
    private string _noticeTitle = "";
    private string _noticeMessage = "";
    private NoticeSeverity _noticeSeverity;
    private bool _startupWarningShown;
    private bool _noticeIsStartValidation;

    // 画面で編集しない項目（AWS連携など）も保存時に失わないよう、読み込んだ設定を土台にする。
    private readonly AppSettings _baseSettings;
    private readonly Func<bool> _isHostServiceRunning;
    private bool _isHostManaged;
    private int _hostCheckCountdown;

    public ServerViewModel(
        GameServerController controller,
        PresetService presets,
        IUserInteraction ui,
        AppPaths paths,
        AppSettings settings,
        SynchronizationContext uiContext,
        Func<bool>? isHostServiceRunning = null)
    {
        _controller = controller;
        _manager = controller.Manager;
        _baseSettings = settings;
        _isHostServiceRunning = isHostServiceRunning ?? (() => false);
        _isHostManaged = _isHostServiceRunning();
        _ui = ui;
        _uiContext = uiContext;
        _executablePath = settings.ServerExecutablePath;

        Game = new GameSettingsViewModel();
        Game.Apply(settings.Game);
        Presets = new PresetsViewModel(presets, Game, ui);
        Presets.Select(settings.LastPresetName);
        Log = new LogViewModel(uiContext) { AutoScroll = settings.AutoScrollLog, MaxLines = settings.MaxLogLines };
        Options = new OptionsViewModel(paths, ui);
        Options.Apply(settings);
        Options.UpdateDefaultLogFilePath(_executablePath);

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsHostManaged && State == ServerState.Stopped);
        StopCommand = new AsyncRelayCommand(StopAsync, () => !IsHostManaged && State is ServerState.Starting or ServerState.Running);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => !IsHostManaged && State is ServerState.Starting or ServerState.Running);
        BrowseExecutableCommand = new AsyncRelayCommand(BrowseExecutableAsync);
        CopyCommandLineCommand = new RelayCommand(CopyCommandLine, () => _fullCommandLine is not null);
        ClearLogCommand = new RelayCommand(Log.Clear);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);

        Game.Changed += (_, _) => OnSettingsEdited();
        Presets.SelectionChanged += (_, _) => RaiseSettingsChanged();
        Log.SettingsChanged += (_, _) => RaiseSettingsChanged();
        Options.Changed += (_, _) =>
        {
            Log.MaxLines = (int)Options.MaxLogLines;
            OnPropertyChanged(nameof(LogSourceText));
            OnSettingsEdited();
        };

        _manager.StatusChanged += (_, _) => _uiContext.Post(_ => ApplyStatus(_manager.Status), null);
        _manager.LogReceived += (_, lines) => Log.Enqueue(lines);
        _manager.ServerReady += (_, _) => _uiContext.Post(_ => OnServerReady(), null);
        _manager.ServerExited += (_, info) => _uiContext.Post(_ => OnServerExited(info), null);

        UpdateExecutableStatus();
        UpdatePreview();
        ApplyStatus(_manager.Status);
        _clock = new Timer(_ => _uiContext.Post(_ => OnClockTick(), null), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>保存すべき設定が変わった（App側でまとめて保存する）。</summary>
    public event EventHandler? SettingsChanged;

    public GameSettingsViewModel Game { get; }

    public PresetsViewModel Presets { get; }

    public LogViewModel Log { get; }

    public OptionsViewModel Options { get; }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand RestartCommand { get; }

    public AsyncRelayCommand BrowseExecutableCommand { get; }

    public RelayCommand CopyCommandLineCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    // ---- サーバー実行ファイル ----

    public string ExecutablePath
    {
        get => _executablePath;
        set
        {
            if (SetProperty(ref _executablePath, value ?? ""))
            {
                UpdateExecutableStatus();
                Options.UpdateDefaultLogFilePath(_executablePath);
                OnPropertyChanged(nameof(LogSourceText));
                OnSettingsEdited();
            }
        }
    }

    public bool ExecutableExists
    {
        get => _executableExists;
        private set
        {
            if (SetProperty(ref _executableExists, value))
            {
                OnPropertyChanged(nameof(IsExecutableMissing));
            }
        }
    }

    public bool IsExecutableMissing => !_executableExists;

    public string ExecutableStatusText
    {
        get => _executableStatusText;
        private set => SetProperty(ref _executableStatusText, value);
    }

    // ---- 状態表示 ----

    public ServerState State => _status.State;

    public bool IsBusy => State is ServerState.Starting or ServerState.Stopping;

    /// <summary>
    /// この PC で常駐サービス（Host）が動いていて、ゲームサーバーの起動・停止を任せている。
    /// この画面から直接操作すると常駐サービスと競合するため、操作は「AWS / Cloud」ページから行い、ここではログを表示する。
    /// </summary>
    public bool IsHostManaged
    {
        get => _isHostManaged;
        private set
        {
            if (SetProperty(ref _isHostManaged, value))
            {
                StartCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
                RestartCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StateText => State switch
    {
        ServerState.Starting => "起動処理中",
        ServerState.Running => "起動中",
        ServerState.Stopping => "停止処理中",
        _ => "停止中",
    };

    public string StateDetailText => State switch
    {
        ServerState.Starting => "起動完了のログを待っています…",
        ServerState.Running when _status.IsAttached && !IsReadyConfirmed => "既に起動していたサーバーを管理しています（起動完了ログは未確認）",
        ServerState.Running when MatchDetailText is { } detail => detail,
        ServerState.Running when _status.IsAttached => "既に起動していたサーバーを管理しています",
        ServerState.Running => "サーバー起動完了",
        ServerState.Stopping => "サーバーの終了を待っています…",
        _ => "「サーバー起動」で起動します",
    };

    public bool IsReadyConfirmed => _status.IsPortListening || _status.IsWaitingToStart;

    public bool IsPortListening => _status.IsPortListening;

    public bool IsWaitingToStart => _status.IsWaitingToStart;

    public string ProcessIdText => _status.ProcessId switch
    {
        null => "—",
        { } id when _status.MainProcessId is { } main && main != id => $"{id}（本体 {main}）",
        { } id => id.ToString(),
    };

    public string StartedAtText => State != ServerState.Stopped && _status.StartedAt is { } startedAt
        ? startedAt.ToString("MM/dd HH:mm:ss")
        : "—";

    public string UptimeText
    {
        get
        {
            if (State == ServerState.Stopped || _status.StartedAt is not { } startedAt)
            {
                return "—";
            }

            var uptime = DateTimeOffset.Now - startedAt;
            if (uptime < TimeSpan.Zero)
            {
                uptime = TimeSpan.Zero;
            }

            return uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays}日 {uptime:hh\\:mm\\:ss}" : uptime.ToString(@"hh\:mm\:ss");
        }
    }

    public string PortText => _status.ListeningPort?.ToString() ?? "—";

    /// <summary>ログの参加・切断から数えた接続中の人数。</summary>
    public string PlayerCountText => State == ServerState.Stopped ? "—" : $"{_status.PlayerCount}人";

    private string? MatchDetailText => _status.MatchState switch
    {
        MatchStates.PokerGame => "試合の開始準備中（ポーカー）",
        MatchStates.InProgress => "試合中",
        MatchStates.WaitingPostMatch => "試合終了（まもなくサーバーが自動で終了します）",
        _ => null,
    };

    public string LogSourceText
    {
        get
        {
            var mode = Options.LogSource switch
            {
                LogSourceMode.LogFile => "ログファイル",
                LogSourceMode.StandardOutput => "標準出力",
                _ => "自動（ログファイル優先）",
            };
            var path = _status.LogFilePath ?? ServerPaths.ResolveLogFilePath(ExecutablePath, Options.LogFilePathOverrideOrNull);
            return Options.LogSource == LogSourceMode.StandardOutput || path is null ? $"取得元: {mode}" : $"取得元: {mode}　{path}";
        }
    }

    // ---- コマンドプレビュー ----

    public string CommandPreview
    {
        get => _commandPreview;
        private set => SetProperty(ref _commandPreview, value);
    }

    public string WorkingDirectoryText
    {
        get
        {
            try
            {
                var directory = Path.GetDirectoryName(ExecutablePath.Trim());
                return string.IsNullOrEmpty(directory) ? "" : $"作業フォルダー: {directory}";
            }
            catch (ArgumentException)
            {
                return "";
            }
        }
    }

    public bool HasValidationErrors => Game.HasErrors || Options.HasAdditionalArgumentsError;

    // ---- 通知（InfoBar） ----

    public bool IsNoticeOpen
    {
        get => _isNoticeOpen;
        set => SetProperty(ref _isNoticeOpen, value);
    }

    public string NoticeTitle
    {
        get => _noticeTitle;
        private set => SetProperty(ref _noticeTitle, value);
    }

    public string NoticeMessage
    {
        get => _noticeMessage;
        private set => SetProperty(ref _noticeMessage, value);
    }

    public NoticeSeverity NoticeSeverity
    {
        get => _noticeSeverity;
        private set => SetProperty(ref _noticeSeverity, value);
    }

    public void SetNotice(NoticeSeverity severity, string title, string message)
    {
        _noticeIsStartValidation = false;
        NoticeSeverity = severity;
        NoticeTitle = title;
        NoticeMessage = message;
        IsNoticeOpen = true;
    }

    public void ReportStartupWarnings(IEnumerable<string?> warnings)
    {
        var messages = warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).ToList();
        if (messages.Count > 0)
        {
            SetNotice(NoticeSeverity.Warning, "設定ファイルを読み込めませんでした", string.Join(Environment.NewLine, messages));
        }
    }

    /// <summary>アプリ起動時、既に動いているサーバーがあれば管理下に置く。</summary>
    public void AttachToRunningServer()
    {
        var attached = _manager.AttachToRunningServer(Options.LogFilePathOverrideOrNull, Options.LogSource);
        if (attached is null)
        {
            return;
        }

        var message = $"{attached.Name}.exe（PID {attached.ProcessId}）は既に起動していました。このアプリから監視・停止できます。";
        if (attached.ExecutablePath is { } path && !string.Equals(path, ExecutablePath.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            message += $"{Environment.NewLine}実行ファイル: {path}";
        }

        SetNotice(NoticeSeverity.Informational, "起動中のサーバーを検出しました", message);
    }

    public AppSettings CreateSettingsSnapshot() => _baseSettings with
    {
        ServerExecutablePath = ExecutablePath.Trim(),
        Game = Game.BuildForPersistence(),
        LastPresetName = Presets.SelectedName,
        AdditionalArguments = Options.AdditionalArguments,
        LogSource = Options.LogSource,
        LogFilePathOverride = Options.LogFilePathOverrideOrNull,
        ConfirmBeforeStop = Options.ConfirmBeforeStop,
        GracefulStopTimeoutSeconds = (int)Options.GracefulStopTimeoutSeconds,
        CloseBehavior = Options.CloseBehavior,
        AutoScrollLog = Log.AutoScroll,
        MaxLogLines = (int)Options.MaxLogLines,
    };

    /// <summary>
    /// この PC（EC2）を停止する前に、確認なしでサーバーを止める（時間内に終わらなければ強制終了）。
    /// 止まっている、または止められたらtrue。
    /// </summary>
    public async Task<bool> StopServerForShutdownAsync()
    {
        // 常駐サービスが管理している場合は、Windows のシャットダウン時に常駐サービスが安全に停止する。
        if (State == ServerState.Stopped || IsHostManaged)
        {
            return true;
        }

        try
        {
            return await _controller.StopAsync(Options.GracefulStopTimeout, _ => Task.FromResult(true)) == StopResult.Stopped;
        }
        catch (OperationInProgressException)
        {
            return false;
        }
    }

    /// <summary>ウィンドウを閉じる前に呼ぶ。閉じてよければtrue。</summary>
    public async Task<bool> PrepareForExitAsync()
    {
        if (State == ServerState.Stopped)
        {
            return true;
        }

        var behavior = Options.CloseBehavior;
        if (behavior == CloseBehavior.Ask)
        {
            var choice = await _ui.ChooseAsync(
                "サーバーが起動中です",
                "アプリを終了する前に、サーバーをどうするか選んでください。"
                + Environment.NewLine
                + "「サーバーを残して終了」を選ぶと、サーバーは動き続け、次にこのアプリを開いたときに自動で管理を再開します。",
                "停止して終了",
                "サーバーを残して終了");
            if (choice == DialogChoice.Cancel)
            {
                return false;
            }

            behavior = choice == DialogChoice.Primary ? CloseBehavior.StopServer : CloseBehavior.LeaveRunning;
        }

        if (behavior == CloseBehavior.StopServer)
        {
            return await StopCoreAsync();
        }

        _manager.Detach();
        return true;
    }

    public void CopyLog(IReadOnlyList<LogLineViewModel> selectedLines)
    {
        var lines = selectedLines.Count > 0 ? selectedLines : Log.Lines.ToList();
        if (lines.Count == 0)
        {
            _ui.ShowToast("コピーするログがありません。", NoticeSeverity.Informational);
            return;
        }

        if (_ui.CopyToClipboard(LogViewModel.Format(lines)))
        {
            _ui.ShowToast(selectedLines.Count > 0 ? $"選択した {lines.Count:N0} 行をコピーしました。" : $"ログ {lines.Count:N0} 行をコピーしました。");
        }
    }

    public void Dispose() => _clock.Dispose();

    private async Task StartAsync()
    {
        var errors = CollectStartErrors();
        if (errors.Count > 0)
        {
            SetNotice(NoticeSeverity.Error, "サーバーを起動できません", string.Join(Environment.NewLine, errors));
            _noticeIsStartValidation = true;
            await _ui.ShowErrorAsync("サーバーを起動できません", string.Join(Environment.NewLine, errors));
            return;
        }

        var request = GameServerController.CreateLaunchRequest(
            ExecutablePath,
            Game.TryGetSettings()!,
            Options.AdditionalArguments,
            Options.LogFilePathOverrideOrNull,
            Options.LogSource);

        IsNoticeOpen = false;
        _startupWarningShown = false;
        try
        {
            await _controller.StartAsync(request);
        }
        catch (OperationInProgressException ex)
        {
            SetNotice(NoticeSeverity.Warning, "サーバーを起動できません", ex.Message);
        }
        catch (ServerStartException ex)
        {
            var title = ex.Error switch
            {
                ServerStartError.ExecutableNotFound => "DreadHungerServer.exe が見つかりません",
                ServerStartError.AlreadyRunning => "サーバーは既に起動しています",
                _ => "サーバーの起動に失敗しました",
            };
            SetNotice(NoticeSeverity.Error, title, ex.Message);
            if (ex.Error == ServerStartError.AlreadyRunning && State == ServerState.Stopped)
            {
                if (await _ui.ConfirmAsync(title, ex.Message + Environment.NewLine + Environment.NewLine + "起動済みのサーバーをこのアプリで管理しますか？", "管理する", "閉じる"))
                {
                    AttachToRunningServer();
                }

                return;
            }

            await _ui.ShowErrorAsync(title, ex.Message);
        }
    }

    private async Task StopAsync()
    {
        if (Options.ConfirmBeforeStop
            && !await _ui.ConfirmAsync("サーバーを停止しますか？", "接続中のプレイヤーは切断されます。", "停止する"))
        {
            return;
        }

        await StopCoreAsync();
    }

    private async Task RestartAsync()
    {
        // 起動できない設定のまま止めてしまわないよう、先に確認する。
        var errors = CollectStartErrors();
        if (errors.Count > 0)
        {
            await _ui.ShowErrorAsync("サーバーを再起動できません", string.Join(Environment.NewLine, errors));
            return;
        }

        if (Options.ConfirmBeforeStop
            && !await _ui.ConfirmAsync("サーバーを再起動しますか？", "接続中のプレイヤーは切断され、現在の設定で起動し直します。", "再起動する"))
        {
            return;
        }

        if (await StopCoreAsync())
        {
            await StartAsync();
        }
    }

    /// <summary>安全な終了を要求し、終わらなければ強制終了するか確認する。停止できたらtrue。</summary>
    private async Task<bool> StopCoreAsync()
    {
        StopResult result;
        try
        {
            result = await _controller.StopAsync(Options.GracefulStopTimeout, ConfirmForceKillAsync);
        }
        catch (OperationInProgressException ex)
        {
            SetNotice(NoticeSeverity.Warning, "サーバーを停止できません", ex.Message);
            return false;
        }

        if (result != StopResult.Failed)
        {
            return result == StopResult.Stopped;
        }

        await _ui.ShowErrorAsync(
            "サーバーを停止できませんでした",
            "プロセスを終了できませんでした。このアプリを管理者として実行するか、タスク マネージャーから DreadHungerServer を終了してください。");
        return false;
    }

    private Task<bool> ConfirmForceKillAsync(StopOutcome outcome)
    {
        var reason = outcome == StopOutcome.SignalUnavailable
            ? "サーバーのコンソールが見つからないため、安全な終了を要求できませんでした。"
            : $"{Options.GracefulStopTimeoutSeconds:0}秒待ちましたが、サーバーが終了していません。";
        return _ui.ConfirmAsync(
            "サーバーを強制終了しますか？",
            reason + Environment.NewLine + "強制終了すると、進行中のゲームは失われます。",
            "強制終了",
            "停止をやめる");
    }

    private List<string> CollectStartErrors()
    {
        var errors = new List<string>();
        var path = ExecutablePath.Trim();
        if (path.Length == 0)
        {
            errors.Add("DreadHungerServer.exe の場所を指定してください。");
        }
        else if (!File.Exists(path))
        {
            errors.Add($"DreadHungerServer.exe が見つかりません: {path}");
        }

        errors.AddRange(Game.Errors);
        if (Options.AdditionalArgumentsError is { } argumentsError)
        {
            errors.Add(argumentsError);
        }

        return errors;
    }

    private async Task BrowseExecutableAsync()
    {
        var path = await _ui.PickFileAsync(ExecutablePath, [".exe"]);
        if (path is not null)
        {
            ExecutablePath = path;
        }
    }

    private void CopyCommandLine()
    {
        if (_fullCommandLine is not null && _ui.CopyToClipboard(_fullCommandLine))
        {
            _ui.ShowToast("コマンドをコピーしました（フルパス付き）。");
        }
    }

    private void OpenLogFolder()
    {
        var directory = Path.GetDirectoryName(_status.LogFilePath ?? ServerPaths.ResolveLogFilePath(ExecutablePath, Options.LogFilePathOverrideOrNull) ?? "");
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            _ui.ShowToast("ログフォルダーがまだありません（サーバーを一度起動すると作成されます）。", NoticeSeverity.Warning);
            return;
        }

        _ui.OpenFolder(directory);
    }

    private void OnSettingsEdited()
    {
        UpdatePreview();
        RaiseSettingsChanged();

        // 入力の誤りで起動できなかった通知は、誤りを直した時点で閉じる。
        if (_noticeIsStartValidation && IsNoticeOpen && CollectStartErrors().Count == 0)
        {
            IsNoticeOpen = false;
        }
    }

    private void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);

    private void UpdateExecutableStatus()
    {
        var path = _executablePath.Trim();
        if (path.Length == 0)
        {
            ExecutableExists = false;
            ExecutableStatusText = "DreadHungerServer.exe の場所を指定してください。";
        }
        else if (!File.Exists(path))
        {
            ExecutableExists = false;
            ExecutableStatusText = "ファイルが見つかりません。「参照」から DreadHungerServer.exe を選んでください。";
        }
        else
        {
            ExecutableExists = true;
            ExecutableStatusText = string.Equals(Path.GetFileName(path), ServerPaths.ExecutableFileName, StringComparison.OrdinalIgnoreCase)
                ? "見つかりました。"
                : $"見つかりました（ファイル名が {ServerPaths.ExecutableFileName} ではありません）。";
        }

        OnPropertyChanged(nameof(WorkingDirectoryText));
    }

    private void UpdatePreview()
    {
        var settings = Game.TryGetSettings();
        if (settings is null || Options.HasAdditionalArgumentsError)
        {
            _fullCommandLine = null;
            CommandPreview = "（設定値に誤りがあるため、コマンドを生成できません）";
        }
        else
        {
            _fullCommandLine = ServerCommandBuilder.BuildFullCommandLine(ExecutablePath, settings, Options.AdditionalArguments);
            CommandPreview = ServerCommandBuilder.BuildPreview(ExecutablePath, settings, Options.AdditionalArguments);
        }

        OnPropertyChanged(nameof(HasValidationErrors));
        CopyCommandLineCommand.NotifyCanExecuteChanged();
    }

    private void ApplyStatus(ServerStatus status)
    {
        _status = status;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateDetailText));
        OnPropertyChanged(nameof(IsReadyConfirmed));
        OnPropertyChanged(nameof(IsPortListening));
        OnPropertyChanged(nameof(IsWaitingToStart));
        OnPropertyChanged(nameof(ProcessIdText));
        OnPropertyChanged(nameof(StartedAtText));
        OnPropertyChanged(nameof(UptimeText));
        OnPropertyChanged(nameof(PortText));
        OnPropertyChanged(nameof(PlayerCountText));
        OnPropertyChanged(nameof(LogSourceText));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
    }

    private void OnClockTick()
    {
        if (--_hostCheckCountdown <= 0)
        {
            _hostCheckCountdown = HostCheckIntervalSeconds;
            IsHostManaged = _isHostServiceRunning();

            // 常駐サービスが起動したサーバーのログをここに表示するため、起動したら管理下（監視のみ）に置く。
            if (IsHostManaged && State == ServerState.Stopped)
            {
                _manager.AttachToRunningServer(Options.LogFilePathOverrideOrNull, Options.LogSource);
            }
        }

        if (State == ServerState.Stopped)
        {
            return;
        }

        OnPropertyChanged(nameof(UptimeText));
        if (State == ServerState.Starting
            && !_startupWarningShown
            && _status.StartedAt is { } startedAt
            && DateTimeOffset.Now - startedAt > StartupWarningDelay)
        {
            _startupWarningShown = true;
            SetNotice(
                NoticeSeverity.Warning,
                "起動完了のログを検出できていません",
                $"起動から{StartupWarningDelay.TotalMinutes:0}分以上「{ServerLogAnalyzer.ListeningMarker}」が見つかりません。サーバー自体は動いている可能性があります。設定ページでログファイルの場所を確認してください。");
        }
    }

    private void OnServerReady()
    {
        // 状態カードにも「サーバー起動完了」と出るため、通知は一時的なトーストだけにする。
        var detail = _status.ListeningPort is { } port ? $"ポート {port} で接続を待ち受けています。" : "マップの読み込みが完了しました。";
        IsNoticeOpen = false;
        _ui.ShowToast($"サーバー起動完了　{detail}");
    }

    private void OnServerExited(ServerExitInfo info)
    {
        if (info.Kind == ServerExitKind.EndedByServer)
        {
            SetNotice(
                NoticeSeverity.Informational,
                "サーバーが終了しました",
                "試合の終了などにより、サーバーが自分で終了しました。続けて遊ぶ場合は「サーバー起動」を押してください。");
            return;
        }

        if (!info.IsAbnormal)
        {
            IsNoticeOpen = false;
            _ui.ShowToast("サーバーを停止しました。", NoticeSeverity.Informational);
            return;
        }

        var code = info.ExitCode is { } value ? $"終了コード: {value}（0x{value:X8}）" : "終了コード: 不明";
        string title;
        string message;
        if (info.ExitedDuringStartup)
        {
            title = "サーバーの起動に失敗しました";
            message = $"起動処理中に DreadHungerServer.exe が終了しました。{code}{Environment.NewLine}サーバーログで原因を確認してください。";
        }
        else
        {
            title = "サーバーが異常終了しました";
            var uptime = info.Uptime is { } span ? $"{Environment.NewLine}稼働時間: {span:hh\\:mm\\:ss}" : "";
            message = $"DreadHungerServer.exe が予期せず終了しました。{code}{uptime}";
        }

        SetNotice(NoticeSeverity.Error, title, message);
        _ = _ui.ShowErrorAsync(title, message);
    }
}
