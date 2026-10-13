using System.Runtime.InteropServices;
using DreadHungerServerManager.App.Services;
using DreadHungerServerManager.App.ViewModels;
using DreadHungerServerManager.App.Views;
using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DreadHungerServerManager.App;

/// <summary>
/// 構成ルート。設定・プリセット・プロセス管理のサービスを組み立て、ViewModelとウィンドウへ渡す。
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private Mutex? _instanceMutex;
    private MainWindow? _window;
    private IAppSettingsStore? _settingsStore;
    private IServerProcessManager? _manager;
    private DispatcherQueueTimer? _saveTimer;

    public App()
    {
        try
        {
            // 英語版Windows Serverでも、標準コントロールの文言を日本語にそろえる。
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "ja-JP";
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
        }

        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public static ServerViewModel ViewModel { get; private set; } = null!;

    public static FileAppLog Log { get; private set; } = new(AppPaths.CreateDefault().LogsDirectory);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var paths = AppPaths.FromCommandLine(Environment.GetCommandLineArgs());
        Log = new FileAppLog(paths.LogsDirectory);
        if (!SingleInstance.TryAcquire(paths.DataDirectory, out _instanceMutex))
        {
            SingleInstance.ActivateExistingWindow(AppInfo.Title);
            Exit();
            return;
        }

        Log.Info($"起動 {AppInfo.Title} {AppInfo.Version}（データ: {paths.DataDirectory}）");
        _settingsStore = new JsonAppSettingsStore(paths.SettingsFile);
        var loaded = _settingsStore.Load();
        var presets = new PresetService(new JsonPresetStore(paths.PresetsFile));
        _manager = new ServerProcessManager();
        var interaction = new UserInteractionService();
        ViewModel = new ServerViewModel(new GameServerController(_manager), presets, interaction, paths, loaded.Value, SynchronizationContext.Current!);

        _window = new MainWindow(ViewModel);
        interaction.Attach(_window);
        AsyncRelayCommand.UnhandledExceptionHandler = ex =>
        {
            Log.Error("コマンドの実行中にエラーが発生しました", ex);
            interaction.ShowToast($"エラーが発生しました: {ex.Message}", NoticeSeverity.Error);
        };
        ViewModel.SettingsChanged += (_, _) => ScheduleSave();
        _window.Closed += (_, _) => Shutdown();
        _window.Activate();

        ViewModel.ReportStartupWarnings([loaded.Warning, presets.LoadWarning]);
        ViewModel.AttachToRunningServer();
    }

    private void ScheduleSave()
    {
        if (_saveTimer is null)
        {
            _saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _saveTimer.Interval = SaveDelay;
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => SaveSettings();
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSettings()
    {
        if (_settingsStore is null)
        {
            return;
        }

        try
        {
            _settingsStore.Save(ViewModel.CreateSettingsSnapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("設定を保存できませんでした", ex);
            ViewModel.SetNotice(NoticeSeverity.Error, "設定を保存できませんでした", ex.Message);
        }
    }

    private void Shutdown()
    {
        _saveTimer?.Stop();
        SaveSettings();
        _manager?.Dispose();
        ViewModel.Dispose();
        Log.Info("終了");
        _instanceMutex?.Dispose();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // サーバー管理アプリが落ちるとサーバーの監視も途切れるため、記録して動作を続ける。
        Log.Error("予期しないエラー", e.Exception);
        e.Handled = true;
        _window?.ShowToast($"予期しないエラーが発生しました: {e.Exception.Message}", NoticeSeverity.Error);
    }
}
