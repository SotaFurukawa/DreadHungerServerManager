using System.Collections.ObjectModel;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;

namespace DreadHungerServerManager.App.ViewModels;

public sealed class PresetItemViewModel(ServerPreset preset)
{
    public ServerPreset Preset { get; } = preset;

    public string Name => Preset.Name;

    public string DisplayName => Preset.IsBuiltIn ? $"{Preset.Name}（標準）" : Preset.Name;

    public string Description => Preset.IsBuiltIn
        ? $"マップは変更しません　吹雪まで{Preset.Settings.DaysBeforeBlizzard}日・1日{Preset.Settings.DayMinutes}分"
        : $"{GameMaps.Get(Preset.Settings.Map).DisplayName}・{Preset.Settings.MaxPlayers}人・人狼{Preset.Settings.Thralls}人";

    public override string ToString() => DisplayName;
}

public sealed class PresetsViewModel : ObservableObject
{
    private readonly PresetService _service;
    private readonly GameSettingsViewModel _game;
    private readonly IUserInteraction _ui;
    private PresetItemViewModel? _selected;

    public PresetsViewModel(PresetService service, GameSettingsViewModel game, IUserInteraction ui)
    {
        _service = service;
        _game = game;
        _ui = ui;
        LoadCommand = new RelayCommand(Load, () => _selected is not null);
        SaveAsCommand = new AsyncRelayCommand(SaveAsAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => _selected is { Preset.IsBuiltIn: false });
        Refresh(selectName: null);
    }

    public event EventHandler? SelectionChanged;

    public ObservableCollection<PresetItemViewModel> Items { get; } = [];

    public object? SelectedItem
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value as PresetItemViewModel))
            {
                LoadCommand.NotifyCanExecuteChanged();
                DeleteCommand.NotifyCanExecuteChanged();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string? SelectedName => _selected?.Name;

    public RelayCommand LoadCommand { get; }

    public AsyncRelayCommand SaveAsCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    public void Select(string? name) =>
        SelectedItem = Items.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

    private void Load()
    {
        if (_selected is null)
        {
            return;
        }

        _game.Apply(_selected.Preset.ApplyTo(GameSettings.Default with { Map = _game.SelectedMap }));
        _ui.ShowToast($"プリセット「{_selected.Name}」を読み込みました。");
    }

    private async Task SaveAsAsync()
    {
        var settings = _game.TryGetSettings();
        if (settings is null)
        {
            await _ui.ShowErrorAsync("プリセットを保存できません", "設定値に誤りがあります。" + Environment.NewLine + string.Join(Environment.NewLine, _game.Errors));
            return;
        }

        var initialName = _selected is { Preset.IsBuiltIn: false } ? _selected.Name : "";
        var name = await _ui.PromptTextAsync(
            "プリセットに名前を付けて保存",
            "現在のマップとゲーム設定を保存します。",
            initialName,
            PresetService.ValidateName);
        if (name is null)
        {
            return;
        }

        if (_service.UserPresetExists(name)
            && !await _ui.ConfirmAsync("プリセットを上書きしますか？", $"「{name}」は既にあります。現在の設定で上書きします。", "上書き"))
        {
            return;
        }

        try
        {
            var saved = _service.SaveUserPreset(name, settings);
            Refresh(saved.Name);
            _ui.ShowToast($"プリセット「{saved.Name}」を保存しました。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _ui.ShowErrorAsync("プリセットを保存できませんでした", ex.Message);
        }
    }

    private async Task DeleteAsync()
    {
        if (_selected is not { Preset.IsBuiltIn: false } target
            || !await _ui.ConfirmAsync("プリセットを削除しますか？", $"「{target.Name}」を削除します。元に戻せません。", "削除"))
        {
            return;
        }

        try
        {
            _service.DeleteUserPreset(target.Name);
            Refresh(selectName: null);
            _ui.ShowToast($"プリセット「{target.Name}」を削除しました。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _ui.ShowErrorAsync("プリセットを削除できませんでした", ex.Message);
        }
    }

    private void Refresh(string? selectName)
    {
        Items.Clear();
        foreach (var preset in _service.All)
        {
            Items.Add(new PresetItemViewModel(preset));
        }

        Select(selectName);
    }
}
