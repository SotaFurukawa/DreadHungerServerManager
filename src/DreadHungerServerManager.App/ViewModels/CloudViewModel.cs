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

    private readonly ICloudInstanceService _cloud;
    private readonly IUserInteraction _ui;
    private readonly Func<IReadOnlyList<string>> _listProfiles;
    private readonly Func<Task<string?>> _getThisMachineInstanceId;
    private readonly Func<Task<bool>> _stopLocalGameServerAsync;
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
    public CloudViewModel(
        ICloudInstanceService cloud,
        CloudSettings settings,
        IUserInteraction ui,
        SynchronizationContext uiContext,
        Func<IReadOnlyList<string>> listProfiles,
        Func<Task<string?>> getThisMachineInstanceId,
        Func<Task<bool>> stopLocalGameServerAsync,
        bool isSimulated = false)
    {
        _cloud = cloud;
        _settings = settings;
        _ui = ui;
        _uiContext = uiContext;
        _listProfiles = listProfiles;
        _getThisMachineInstanceId = getThisMachineInstanceId;
        _stopLocalGameServerAsync = stopLocalGameServerAsync;
        IsSimulated = isSimulated;

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(showErrors: true), () => CanOperate);
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanOperate && State is CloudInstanceState.Stopped or CloudInstanceState.Unknown);
        StopCommand = new AsyncRelayCommand(StopAsync, () => CanOperate && State is CloudInstanceState.Running or CloudInstanceState.Starting or CloudInstanceState.Unknown);
        CopyIpCommand = new RelayCommand(() => Copy(PublicIp, "IP アドレス"), () => PublicIp is not null);
        CopyAddressCommand = new RelayCommand(() => Copy(ConnectAddress, "接続先"), () => ConnectAddress is not null);
        ReloadProfilesCommand = new RelayCommand(ReloadProfiles);

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

    private bool CanOperate => IsConfigured && !HasSettingsError && !IsBusy;

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
        }
        catch (CloudOperationException ex)
        {
            ErrorMessage = ex.Message;
            if (showErrors)
            {
                await _ui.ShowErrorAsync("EC2 の状態を取得できませんでした", ex.Message);
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task StartAsync()
    {
        if (_settings.TryGetInstanceRef() is not { } instance)
        {
            return;
        }

        await RunOperationAsync("EC2 起動", "EC2 の起動を要求しています…", async () =>
        {
            await _cloud.StartAsync(instance);
            SetOperationText("EC2 の起動完了を待っています…（通常1～2分）");
            var status = await _cloud.WaitForStateAsync(instance, CloudInstanceState.Running, StartTimeout, progress: new Progress<CloudInstanceStatus>(ApplyStatus));
            ApplyStatus(status);
            _ui.ShowToast(ConnectAddress is { } address
                ? $"EC2 が起動しました。接続先: {address}（Windows とゲームサーバーの起動にはさらに数分かかります）"
                : "EC2 が起動しました。パブリック IP はまだ割り当てられていません。");
        });
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
            ErrorMessage = ex.Message;
            await _ui.ShowErrorAsync($"{name}に失敗しました", ex.Message);
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
    }

    private void OnRefreshTick()
    {
        if (!IsBusy)
        {
            _ = RefreshAsync(showErrors: false);
        }
    }

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
