using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Services;

namespace DreadHungerServerManager.App.ViewModels;

/// <summary>設定ページの項目（起動オプション・停止・終了時の動作・ログ）。</summary>
public sealed class OptionsViewModel : ObservableObject
{
    private readonly IUserInteraction _ui;
    private string _additionalArguments = "";
    private int _logSourceIndex;
    private string _logFilePathOverride = "";
    private string? _defaultLogFilePath;
    private bool _confirmBeforeStop = true;
    private double _gracefulStopTimeoutSeconds = AppSettings.Default.GracefulStopTimeoutSeconds;
    private int _closeBehaviorIndex;
    private double _maxLogLines = AppSettings.Default.MaxLogLines;

    public OptionsViewModel(AppPaths paths, IUserInteraction ui)
    {
        _ui = ui;
        Paths = paths;
        BrowseLogFileCommand = new AsyncRelayCommand(BrowseLogFileAsync);
        ResetLogFileCommand = new RelayCommand(() => LogFilePathOverride = "", () => _logFilePathOverride.Length > 0);
        OpenDataFolderCommand = new RelayCommand(() => _ui.OpenFolder(Paths.DataDirectory));
    }

    public event EventHandler? Changed;

    public AppPaths Paths { get; }

    public string AdditionalArguments
    {
        get => _additionalArguments;
        set
        {
            if (SetProperty(ref _additionalArguments, value ?? ""))
            {
                OnPropertyChanged(nameof(AdditionalArgumentsError));
                OnPropertyChanged(nameof(HasAdditionalArgumentsError));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string? AdditionalArgumentsError => ServerCommandBuilder.ValidateAdditionalArguments(_additionalArguments);

    public bool HasAdditionalArgumentsError => AdditionalArgumentsError is not null;

    /// <summary>0: 自動, 1: ログファイルのみ, 2: 標準出力のみ（<see cref="LogSourceMode"/> の順）。</summary>
    public int LogSourceIndex
    {
        get => _logSourceIndex;
        set
        {
            if (value >= 0 && SetProperty(ref _logSourceIndex, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public LogSourceMode LogSource => (LogSourceMode)_logSourceIndex;

    public string LogFilePathOverride
    {
        get => _logFilePathOverride;
        set
        {
            if (SetProperty(ref _logFilePathOverride, value?.Trim() ?? ""))
            {
                OnPropertyChanged(nameof(EffectiveLogFilePath));
                ResetLogFileCommand.NotifyCanExecuteChanged();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string? LogFilePathOverrideOrNull => _logFilePathOverride.Length == 0 ? null : _logFilePathOverride;

    public string EffectiveLogFilePath =>
        LogFilePathOverrideOrNull ?? _defaultLogFilePath ?? "（サーバー実行ファイルを指定すると決まります）";

    public bool ConfirmBeforeStop
    {
        get => _confirmBeforeStop;
        set
        {
            if (SetProperty(ref _confirmBeforeStop, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public double GracefulStopTimeoutSeconds
    {
        get => _gracefulStopTimeoutSeconds;
        set
        {
            var clamped = double.IsNaN(value)
                ? AppSettings.Default.GracefulStopTimeoutSeconds
                : Math.Clamp(Math.Round(value), AppSettings.MinGracefulStopTimeoutSeconds, AppSettings.MaxGracefulStopTimeoutSeconds);
            if (SetProperty(ref _gracefulStopTimeoutSeconds, clamped))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public TimeSpan GracefulStopTimeout => TimeSpan.FromSeconds(_gracefulStopTimeoutSeconds);

    /// <summary>0: 毎回確認, 1: 停止して終了, 2: 残して終了（<see cref="CloseBehavior"/> の順）。</summary>
    public int CloseBehaviorIndex
    {
        get => _closeBehaviorIndex;
        set
        {
            if (value >= 0 && SetProperty(ref _closeBehaviorIndex, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public CloseBehavior CloseBehavior => (CloseBehavior)_closeBehaviorIndex;

    public double MaxLogLines
    {
        get => _maxLogLines;
        set
        {
            var clamped = double.IsNaN(value)
                ? AppSettings.Default.MaxLogLines
                : Math.Clamp(Math.Round(value), AppSettings.MinLogLines, AppSettings.MaxLogLinesLimit);
            if (SetProperty(ref _maxLogLines, clamped))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public RelayCommand ResetLogFileCommand { get; }

    public AsyncRelayCommand BrowseLogFileCommand { get; }

    public RelayCommand OpenDataFolderCommand { get; }

    public void Apply(AppSettings settings)
    {
        _additionalArguments = settings.AdditionalArguments;
        _logSourceIndex = (int)settings.LogSource;
        _logFilePathOverride = settings.LogFilePathOverride ?? "";
        _confirmBeforeStop = settings.ConfirmBeforeStop;
        _gracefulStopTimeoutSeconds = settings.GracefulStopTimeoutSeconds;
        _closeBehaviorIndex = (int)settings.CloseBehavior;
        _maxLogLines = settings.MaxLogLines;
        OnPropertyChanged(string.Empty);
    }

    public void UpdateDefaultLogFilePath(string executablePath)
    {
        _defaultLogFilePath = ServerPaths.GetDefaultLogFilePath(executablePath);
        OnPropertyChanged(nameof(EffectiveLogFilePath));
    }

    private async Task BrowseLogFileAsync()
    {
        var path = await _ui.PickFileAsync(LogFilePathOverrideOrNull ?? _defaultLogFilePath, [".log", ".txt"]);
        if (path is not null)
        {
            LogFilePathOverride = path;
        }
    }
}
