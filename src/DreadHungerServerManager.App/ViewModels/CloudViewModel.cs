using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;
using DreadHungerServerManager.Core.Services.Cloud;

namespace DreadHungerServerManager.App.ViewModels;

/// <summary>
/// 「AWS / Cloud」ページ。EC2 の状態表示と起動・停止、接続設定。
/// AWS 固有の処理は <see cref="ICloudInstanceService"/> の実装側にあり、ここには持ち込まない。
/// </summary>
public sealed class CloudViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RemoteStartTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan RemoteStopTimeout = TimeSpan.FromMinutes(2);

    private readonly ICloudInstanceService _cloud;
    private readonly IUserInteraction _ui;
    private readonly Func<IReadOnlyList<string>> _listProfiles;
    private readonly Func<Task<string?>> _getThisMachineInstanceId;
    private readonly Func<Task<bool>> _stopLocalGameServerAsync;
    private readonly Func<CloudSettings, IServerControlStore?> _createControlStore;
    private readonly Func<GameSettings?> _getGameSettings;
    private readonly string _requester;
    private ServerHeartbeat? _heartbeat;
    private string? _remoteError;
    private readonly OperationGate _gate = new();
    private readonly Timer _refreshTimer;
    private readonly SynchronizationContext _uiContext;
    private CloudSettings _settings;
    private CloudInstanceStatus? _status;
    private DateTimeOffset? _lastUpdated;
    private string? _errorMessage;
    private string? _operationText;
    private bool _isRefreshing;
    private IReadOnlyList<string> _availableProfiles = [];

    /// <param name="getThisMachineInstanceId">この PC が EC2 なら、そのインスタンスID。</param>
    /// <param name="stopLocalGameServerAsync">このアプリで管理しているゲームサーバーを止める（この PC 自体の EC2 を停止する前に呼ぶ）。</param>
    /// <param name="createControlStore">EC2 上の常駐サービスと要求・状態を受け渡す置き場所（未設定ならnull）。</param>
    /// <param name="getGameSettings">EC2 上のゲームサーバーの起動に使う設定（「サーバー」ページの設定。誤りがあればnull）。</param>
    /// <param name="requester">要求者として記録する名前（例: "GUI:MYPC"）。</param>
    public CloudViewModel(
        ICloudInstanceService cloud,
        CloudSettings settings,
        IUserInteraction ui,
        SynchronizationContext uiContext,
        Func<IReadOnlyList<string>> listProfiles,
        Func<Task<string?>> getThisMachineInstanceId,
        Func<Task<bool>> stopLocalGameServerAsync,
        Func<CloudSettings, IServerControlStore?> createControlStore,
        Func<GameSettings?> getGameSettings,
        string requester,
        bool isSimulated = false)
    {
        _cloud = cloud;
        _settings = settings;
        _ui = ui;
        _uiContext = uiContext;
        _listProfiles = listProfiles;
        _getThisMachineInstanceId = getThisMachineInstanceId;
        _stopLocalGameServerAsync = stopLocalGameServerAsync;
        _createControlStore = createControlStore;
        _getGameSettings = getGameSettings;
        _requester = requester;
        IsSimulated = isSimulated;

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(showErrors: true), () => CanOperate);
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanOperate && State is CloudInstanceState.Stopped or CloudInstanceState.Unknown);
        StopCommand = new AsyncRelayCommand(StopAsync, () => CanOperate && State is CloudInstanceState.Running or CloudInstanceState.Starting or CloudInstanceState.Unknown);
        CopyIpCommand = new RelayCommand(() => Copy(PublicIp, "IP アドレス"), () => PublicIp is not null);
        CopyAddressCommand = new RelayCommand(() => Copy(ConnectAddress, "接続先"), () => ConnectAddress is not null);
        ReloadProfilesCommand = new RelayCommand(ReloadProfiles);
        RemoteStartCommand = new AsyncRelayCommand(() => SendRemoteAsync(ServerControlAction.Start), () => CanOperateRemote && RemoteState == ServerState.Stopped);
        RemoteRestartCommand = new AsyncRelayCommand(() => SendRemoteAsync(ServerControlAction.Restart), () => CanOperateRemote && RemoteState != ServerState.Stopped);
        RemoteStopCommand = new AsyncRelayCommand(() => SendRemoteAsync(ServerControlAction.Stop), () => CanOperateRemote && RemoteState != ServerState.Stopped);

        ReloadProfiles();
        _refreshTimer = new Timer(_ => _uiContext.Post(_ => OnRefreshTick(), null), null, TimeSpan.FromSeconds(1), RefreshInterval);
    }

    /// <summary>保存すべき設定が変わった（App側でまとめて保存する）。</summary>
    public event EventHandler? SettingsChanged;

    public CloudSettings Settings => _settings;

    /// <summary>疑似 EC2（--fake-cloud）で動いている。</summary>
    public bool IsSimulated { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public RelayCommand CopyIpCommand { get; }

    public RelayCommand CopyAddressCommand { get; }

    public RelayCommand ReloadProfilesCommand { get; }

    public AsyncRelayCommand RemoteStartCommand { get; }

    public AsyncRelayCommand RemoteRestartCommand { get; }

    public AsyncRelayCommand RemoteStopCommand { get; }

    // ---- 接続設定 ----

    public string Region
    {
        get => _settings.Region;
        set => UpdateSettings(_settings with { Region = (value ?? "").Trim() });
    }

    public string InstanceId
    {
        get => _settings.InstanceId;
        set => UpdateSettings(_settings with { InstanceId = (value ?? "").Trim() });
    }

    public string ProfileName
    {
        get => _settings.ProfileName ?? "";
        set => UpdateSettings(_settings with { ProfileName = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
    }

    public double GamePort
    {
        get => _settings.GamePort;
        set
        {
            if (!double.IsNaN(value))
            {
                UpdateSettings(_settings with { GamePort = (int)Math.Round(value) });
            }
        }
    }

    public IReadOnlyList<string> AvailableProfiles
    {
        get => _availableProfiles;
        private set => SetProperty(ref _availableProfiles, value);
    }

    public string ProfilesText => AvailableProfiles.Count == 0
        ? "この PC にはプロファイルがありません（%USERPROFILE%\\.aws\\credentials）。空欄なら環境変数や EC2 の IAM ロールで認証します。"
        : $"この PC のプロファイル: {string.Join("、", AvailableProfiles)}";

    public string SettingsErrorText => string.Join(Environment.NewLine, _settings.Validate());

    public bool HasSettingsError => _settings.Validate().Count > 0;

    public bool IsConfigured => _settings.IsConfigured;

    public bool IsNotConfigured => !IsConfigured;

    // ---- 状態 ----

    public CloudInstanceState State => _status?.State ?? CloudInstanceState.Unknown;

    public bool IsBusy => _operationText is not null;

    public string StateText => !IsConfigured ? "未設定"
        : HasSettingsError ? "設定エラー"
        : _status is null && _errorMessage is not null ? "取得できません"
        : _status is null ? "確認中…"
        : CloudInstanceStatus.GetDisplayText(State);

    public string StateDetailText => _operationText
        ?? (!IsConfigured ? "下の「接続設定」でリージョンとインスタンスIDを入力してください。"
        : HasSettingsError ? "下の「接続設定」の誤りを直してください。"
        : _lastUpdated is null && _errorMessage is not null ? "上のエラーの内容を確認してください。"
        : _lastUpdated is { } updated ? $"最終更新 {updated.ToLocalTime():HH:mm:ss}（{RefreshInterval.TotalSeconds:0}秒ごとに更新）"
        : "状態を取得しています…");

    public string? PublicIp => State == CloudInstanceState.Running ? _status?.PublicIpAddress : null;

    public string PublicIpText => PublicIp ?? "—";

    public string? ConnectAddress => PublicIp is { } ip ? $"{ip}:{_settings.GamePort}" : null;

    public string ConnectAddressText => ConnectAddress ?? "—";

    public string LaunchTimeText => State is CloudInstanceState.Running or CloudInstanceState.Stopping && _status?.LaunchTime is { } launch
        ? launch.ToLocalTime().ToString("MM/dd HH:mm")
        : "—";

    public string InstanceText => IsConfigured ? $"{_settings.InstanceId}（{_settings.Region}）" : "—";

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public bool HasError => _errorMessage is not null;

    // ---- EC2 上のゲームサーバー（常駐サービスの状態） ----

    /// <summary>常駐サービスの状態が新しい（応答している）か。</summary>
    public bool IsRemoteAlive => _heartbeat is { } heartbeat && !heartbeat.IsStale(DateTimeOffset.UtcNow) && State == CloudInstanceState.Running;

    public ServerState RemoteState => IsRemoteAlive ? _heartbeat!.State : ServerState.Stopped;

    public string RemoteStateText
    {
        get
        {
            if (!IsConfigured || HasSettingsError)
            {
                return "—";
            }

            if (State != CloudInstanceState.Running)
            {
                return State == CloudInstanceState.Starting ? "EC2 起動待ち" : "EC2 が稼働していません";
            }

            if (_heartbeat is null)
            {
                return _remoteError is null ? "常駐サービス未確認" : "取得できません";
            }

            if (!IsRemoteAlive)
            {
                return "応答なし";
            }

            return (_heartbeat.State, _heartbeat.MatchState) switch
            {
                (ServerState.Running, MatchStates.PokerGame) => "起動中（開始準備中）",
                (ServerState.Running, MatchStates.InProgress) => "起動中（試合中）",
                (ServerState.Running, MatchStates.WaitingPostMatch) => "起動中（試合終了）",
                (ServerState.Running, _) when _heartbeat.IsReady => "起動中（参加待ち）",
                (ServerState.Running, _) => "起動中",
                (ServerState.Starting, _) => "起動処理中",
                (ServerState.Stopping, _) => "停止処理中",
                _ => "停止中",
            };
        }
    }

    public string RemoteDetailText
    {
        get
        {
            if (!IsConfigured || State != CloudInstanceState.Running)
            {
                return "EC2 が稼働すると、常駐サービスがゲームサーバーを自動で起動します（常駐サービスを入れた場合）。";
            }

            if (_heartbeat is not { } heartbeat)
            {
                return "常駐サービスの状態がまだありません。EC2 に常駐サービスを入れていない場合は、README の「EC2 の常駐サービス」を参照してください。";
            }

            var parts = new List<string>();
            if (heartbeat.Settings is { } settings)
            {
                parts.Add($"マップ: {GameMaps.Get(settings.Map).DisplayName}");
            }

            if (heartbeat.State != ServerState.Stopped)
            {
                parts.Add($"プレイヤー: {heartbeat.PlayerCount}人" + (heartbeat.Settings is { } running ? $" / {running.MaxPlayers}" : ""));
                if (heartbeat.ServerStartedAt is { } startedAt)
                {
                    var uptime = DateTimeOffset.UtcNow - startedAt;
                    parts.Add($"稼働: {(uptime < TimeSpan.Zero ? TimeSpan.Zero : uptime):hh\\:mm\\:ss}");
                }
            }
            else if (heartbeat.LastExitMessage is { } exit)
            {
                parts.Add(exit);
            }

            parts.Add($"更新 {heartbeat.UpdatedAt.ToLocalTime():HH:mm:ss}");
            if (!IsRemoteAlive)
            {
                parts.Add("常駐サービスからの更新が止まっています");
            }

            return string.Join("　", parts);
        }
    }

    /// <summary>最後の要求の結果。</summary>
    public string RemoteLastResultText => _heartbeat?.LastResult is { } result
        ? $"最後の操作（{result.CompletedAt.ToLocalTime():MM/dd HH:mm}）: {(result.Succeeded ? "成功" : "失敗")} - {result.Message}"
        : "";

    public string? RemoteError
    {
        get => _remoteError;
        private set
        {
            if (SetProperty(ref _remoteError, value))
            {
                OnPropertyChanged(nameof(HasRemoteError));
            }
        }
    }

    public bool HasRemoteError => _remoteError is not null;

    /// <summary>EC2 上のゲームサーバーの起動・再起動に使う設定の説明。</summary>
    public string RemoteLaunchSettingsText => _getGameSettings() is not null
        ? "「ゲームサーバー起動」「再起動」には、上の「起動に使うゲーム設定」を使います。"
        : "「起動に使うゲーム設定」に誤りがあるため、起動・再起動できません。";

    /// <summary>EC2 起動・ゲームサーバー起動に使う設定の要約。</summary>
    public string LaunchSettingsSummary => _getGameSettings() is { } settings
        ? $"{GameMaps.Get(settings.Map).DisplayName}・最大{settings.MaxPlayers}人・人狼{settings.Thralls}人・吹雪まで{settings.DaysBeforeBlizzard}日・1日{settings.DayMinutes}分"
        : "設定に誤りがあります（赤字の項目を直してください）";

    private bool CanOperate => IsConfigured && !HasSettingsError && !IsBusy;

    private bool CanOperateRemote => CanOperate && State == CloudInstanceState.Running;

    public void Dispose() => _refreshTimer.Dispose();

    /// <summary>状態を取得する。<paramref name="showErrors"/> が false なら、失敗してもダイアログを出さない（定期更新）。</summary>
    public async Task RefreshAsync(bool showErrors)
    {
        if (_settings.TryGetInstanceRef() is not { } instance || _isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            ApplyStatus(await _cloud.GetStatusAsync(instance));
            await RefreshRemoteAsync();
        }
        catch (CloudOperationException ex)
        {
            ErrorMessage = Describe(ex);
            if (showErrors)
            {
                await _ui.ShowErrorAsync("EC2 の状態を取得できませんでした", ErrorMessage);
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>常駐サービスが書き出した状態を読む。EC2 が稼働していなければ読まない。</summary>
    private async Task RefreshRemoteAsync()
    {
        if (State != CloudInstanceState.Running || _createControlStore(_settings) is not { } store)
        {
            ApplyHeartbeat(null);
            return;
        }

        try
        {
            ApplyHeartbeat(await store.GetHeartbeatAsync(_settings.ServerId));
            RemoteError = null;
        }
        catch (CloudOperationException ex)
        {
            RemoteError = Describe(ex);
            RaiseRemoteProperties();
        }
    }

    private async Task SendRemoteAsync(ServerControlAction action)
    {
        if (_createControlStore(_settings) is not { } store)
        {
            return;
        }

        GameSettings? settings = null;
        if (action != ServerControlAction.Stop)
        {
            settings = _getGameSettings();
            if (settings is null)
            {
                await _ui.ShowErrorAsync("ゲームサーバーを起動できません", "「サーバー」ページのゲーム設定に誤りがあります。直してからもう一度実行してください。");
                return;
            }
        }

        var (name, confirmTitle, confirmMessage, timeout) = action switch
        {
            ServerControlAction.Stop => ("ゲームサーバー停止", "EC2 上のゲームサーバーを停止しますか？", "接続中のプレイヤーは切断されます。EC2 は稼働したままです。", RemoteStopTimeout),
            ServerControlAction.Restart => ("ゲームサーバー再起動", "EC2 上のゲームサーバーを再起動しますか？", "接続中のプレイヤーは切断され、「サーバー」ページの設定で起動し直します。", RemoteStartTimeout),
            _ => ("ゲームサーバー起動", null, null, RemoteStartTimeout),
        };
        if (confirmTitle is not null && !await _ui.ConfirmAsync(confirmTitle, confirmMessage!, action == ServerControlAction.Stop ? "停止する" : "再起動する"))
        {
            return;
        }

        await RunOperationAsync(name, "EC2 上の常駐サービスへ要求を送っています…", async () =>
        {
            var request = ServerControlRequest.Create(action, settings, _requester, DateTimeOffset.UtcNow);
            SetOperationText($"{name}: 常駐サービスの応答を待っています…（数秒～数分）");
            var result = await new ServerControlClient(store).SendAsync(
                _settings.ServerId,
                request,
                timeout,
                progress: new Progress<ServerHeartbeat>(ApplyHeartbeat));
            await RefreshRemoteAsync();
            if (result.Succeeded)
            {
                _ui.ShowToast(result.Message);
            }
            else
            {
                await _ui.ShowErrorAsync($"{name}に失敗しました", result.Message);
            }
        });
    }

    private void ApplyHeartbeat(ServerHeartbeat? heartbeat)
    {
        _heartbeat = heartbeat;
        RaiseRemoteProperties();
    }

    private void RaiseRemoteProperties()
    {
        OnPropertyChanged(nameof(IsRemoteAlive));
        OnPropertyChanged(nameof(RemoteState));
        OnPropertyChanged(nameof(RemoteStateText));
        OnPropertyChanged(nameof(RemoteDetailText));
        OnPropertyChanged(nameof(RemoteLastResultText));
        OnPropertyChanged(nameof(RemoteLaunchSettingsText));
        RemoteStartCommand.NotifyCanExecuteChanged();
        RemoteRestartCommand.NotifyCanExecuteChanged();
        RemoteStopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 「起動に使うゲーム設定」を常駐サービスへの起動要求として先に書いてから EC2 を起動し、
    /// 常駐サービスがその設定でゲームサーバーを起動するまで待つ（<see cref="CloudServerLauncher"/>）。
    /// </summary>
    private async Task StartAsync()
    {
        if (_settings.TryGetInstanceRef() is not { } instance || _createControlStore(_settings) is not { } store)
        {
            return;
        }

        if (_getGameSettings() is not { } game)
        {
            await _ui.ShowErrorAsync("EC2 を起動できません", "「起動に使うゲーム設定」に誤りがあります。赤字の項目を直してからもう一度実行してください。");
            return;
        }

        await RunOperationAsync("EC2 起動", "ゲーム設定を送っています…", async () =>
        {
            var result = await new CloudServerLauncher(_cloud, store).StartAsync(
                instance,
                _settings.ServerId,
                game,
                _requester,
                new CloudStartOptions { InstanceTimeout = StartTimeout },
                new Progress<CloudStartProgress>(OnStartProgress));
            ApplyStatus(result.Instance);
            ApplyHeartbeat(result.Heartbeat ?? _heartbeat);

            var address = ConnectAddress is { } connect ? $"接続先: {connect}" : "パブリック IP はまだ割り当てられていません。";
            if (result.GameServer is { Succeeded: true } started)
            {
                _ui.ShowToast($"{started.Message}　{address}");
            }
            else if (result.GameServer is { } failed)
            {
                await _ui.ShowErrorAsync("ゲームサーバーを起動できませんでした", $"EC2 は起動しましたが、ゲームサーバーの起動に失敗しました。{Environment.NewLine}{failed.Message}");
            }
            else if (!result.IsHostKnown)
            {
                _ui.ShowToast($"EC2 が起動しました。{address}（常駐サービスの状態がまだないため、ゲームサーバーの起動は確認していません）", NoticeSeverity.Informational);
            }
            else
            {
                _ui.ShowToast($"EC2 は起動しましたが、まだゲームサーバーの起動を確認できていません。{address}　しばらくしてから ↻ で確認してください。", NoticeSeverity.Warning);
            }
        });
    }

    private void OnStartProgress(CloudStartProgress progress)
    {
        if (progress.Instance is { } instance)
        {
            ApplyStatus(instance);
        }

        if (progress.Heartbeat is { } heartbeat)
        {
            ApplyHeartbeat(heartbeat);
        }

        SetOperationText(progress.Stage switch
        {
            CloudStartStage.SendingRequest => "ゲーム設定を送っています…",
            CloudStartStage.StartingInstance => "EC2 の起動を要求しています…",
            CloudStartStage.WaitingForInstance => "EC2 の起動完了を待っています…（通常1～2分）",
            _ => "Windows とゲームサーバーの起動を待っています…（通常2～5分）",
        });
    }

    /// <summary>「サーバー」ページ（共通の設定欄）でゲーム設定が変わったときに呼ぶ。</summary>
    public void NotifyGameSettingsChanged()
    {
        OnPropertyChanged(nameof(LaunchSettingsSummary));
        RaiseRemoteProperties();
    }

    private async Task StopAsync()
    {
        if (_settings.TryGetInstanceRef() is not { } instance)
        {
            return;
        }

        var isThisMachine = string.Equals(await _getThisMachineInstanceId(), instance.InstanceId, StringComparison.Ordinal);
        var message = isThisMachine
            ? "この PC が対象の EC2 です。停止すると、この PC（Windows）がシャットダウンされます。先にこのアプリで管理しているゲームサーバーを安全に停止します。"
            : "EC2 上で Dread Hunger サーバーが動いている場合、接続中のプレイヤーは切断されます。停止すると、次に起動したときはパブリック IP が変わります。";
        if (!await _ui.ConfirmAsync("EC2 を停止しますか？", message, "停止する"))
        {
            return;
        }

        await RunOperationAsync("EC2 停止", "EC2 の停止を要求しています…", async () =>
        {
            if (isThisMachine)
            {
                SetOperationText("ゲームサーバーを停止しています…");
                if (!await _stopLocalGameServerAsync())
                {
                    await _ui.ShowErrorAsync("EC2 を停止できません", "ゲームサーバーを停止できなかったため、EC2 の停止を中止しました。");
                    return;
                }

                SetOperationText("EC2 の停止を要求しています…");
            }

            await _cloud.StopAsync(instance);
            SetOperationText("EC2 の停止完了を待っています…");
            var status = await _cloud.WaitForStateAsync(instance, CloudInstanceState.Stopped, StopTimeout, progress: new Progress<CloudInstanceStatus>(ApplyStatus));
            ApplyStatus(status);
            _ui.ShowToast("EC2 を停止しました。", NoticeSeverity.Informational);
        });
    }

    private async Task RunOperationAsync(string name, string initialText, Func<Task> operation)
    {
        try
        {
            using var lease = _gate.Enter(name);
            SetOperationText(initialText);
            try
            {
                await operation();
            }
            finally
            {
                SetOperationText(null);
            }
        }
        catch (OperationInProgressException ex)
        {
            _ui.ShowToast(ex.Message, NoticeSeverity.Warning);
        }
        catch (CloudOperationException ex)
        {
            ErrorMessage = Describe(ex);
            await _ui.ShowErrorAsync($"{name}に失敗しました", ErrorMessage);
            await RefreshAsync(showErrors: false);
        }
    }

    private void ApplyStatus(CloudInstanceStatus status)
    {
        _status = status;
        _lastUpdated = DateTimeOffset.Now;
        ErrorMessage = null;
        RaiseStatusProperties();
    }

    private void SetOperationText(string? text)
    {
        _operationText = text;
        RaiseStatusProperties();
    }

    private void UpdateSettings(CloudSettings settings)
    {
        if (settings == _settings)
        {
            return;
        }

        var targetChanged = settings.Region != _settings.Region || settings.InstanceId != _settings.InstanceId || settings.ProfileName != _settings.ProfileName;
        _settings = settings;
        OnPropertyChanged(nameof(Region));
        OnPropertyChanged(nameof(InstanceId));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(GamePort));
        OnPropertyChanged(nameof(SettingsErrorText));
        OnPropertyChanged(nameof(HasSettingsError));
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(IsNotConfigured));
        SettingsChanged?.Invoke(this, EventArgs.Empty);

        if (targetChanged)
        {
            _status = null;
            _heartbeat = null;
            RemoteError = null;
            _lastUpdated = null;
            ErrorMessage = null;
            RaiseStatusProperties();
            _ = RefreshAsync(showErrors: false);
        }
        else
        {
            RaiseStatusProperties();
        }
    }

    private void RaiseStatusProperties()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateDetailText));
        OnPropertyChanged(nameof(PublicIpText));
        OnPropertyChanged(nameof(ConnectAddressText));
        OnPropertyChanged(nameof(LaunchTimeText));
        OnPropertyChanged(nameof(InstanceText));
        RefreshCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        CopyIpCommand.NotifyCanExecuteChanged();
        CopyAddressCommand.NotifyCanExecuteChanged();
        RaiseRemoteProperties();
    }

    private void OnRefreshTick()
    {
        if (!IsBusy)
        {
            _ = RefreshAsync(showErrors: false);
        }
    }

    /// <summary>
    /// 利用者向けのエラー文。プロファイル名が空欄のまま認証情報が見つからない場合は、この PC にある
    /// プロファイルを選ぶよう案内する（空欄だと default プロファイルしか探さないため）。
    /// </summary>
    private string Describe(CloudOperationException exception) =>
        exception.Kind == CloudErrorKind.CredentialsNotFound && _settings.ProfileName is null && AvailableProfiles.Count > 0
            ? $"{exception.Message}{Environment.NewLine}この PC にはプロファイル（{string.Join("、", AvailableProfiles)}）があります。下の「接続設定」の「AWS プロファイル」で選んでください。"
            : exception.Message;

    private void ReloadProfiles()
    {
        AvailableProfiles = _listProfiles();
        OnPropertyChanged(nameof(ProfilesText));
    }

    private void Copy(string? text, string label)
    {
        if (text is not null && _ui.CopyToClipboard(text))
        {
            _ui.ShowToast($"{label}をコピーしました: {text}");
        }
    }
}
