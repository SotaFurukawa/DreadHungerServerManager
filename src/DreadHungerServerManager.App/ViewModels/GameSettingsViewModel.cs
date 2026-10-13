using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;

namespace DreadHungerServerManager.App.ViewModels;

/// <summary>数値設定1項目。NumberBoxと双方向に結び付き、空欄（NaN）や範囲外もそのまま保持してエラーを表示する。</summary>
public sealed class NumericSettingViewModel : ObservableObject
{
    private double _value;
    private string? _error;

    public NumericSettingViewModel(GameSettingDefinition definition)
    {
        Definition = definition;
        _value = definition.DefaultValue;
    }

    public event EventHandler? ValueChanged;

    public GameSettingDefinition Definition { get; }

    public string Label => Definition.Label;

    public string Caption => $"{Definition.QueryName}　範囲 {Definition.RangeText}";

    public double SmallChange => Definition.Step;

    public double LargeChange => Definition.IsInteger ? 5 : 0.5;

    public IReadOnlyList<double> QuickValues => Definition.QuickValues;

    public double Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;
}

public sealed class GameSettingsViewModel : ObservableObject
{
    private readonly Dictionary<GameSettingKey, NumericSettingViewModel> _settings;
    private GameMapInfo _selectedMap = GameMaps.Get(GameSettings.Default.Map);
    private IReadOnlyList<string> _errors = [];
    private bool _suppressChanged;

    public GameSettingsViewModel()
    {
        _settings = GameSettingDefinitions.CommandLineOrder.ToDictionary(definition => definition.Key, definition => new NumericSettingViewModel(definition));
        foreach (var setting in _settings.Values)
        {
            setting.ValueChanged += (_, _) => OnSettingChanged();
        }

        PlayerSettings = [_settings[GameSettingKey.MaxPlayers], _settings[GameSettingKey.Thralls]];
        TimeSettings = [_settings[GameSettingKey.DaysBeforeBlizzard], _settings[GameSettingKey.DayMinutes]];
        MultiplierSettings =
        [
            _settings[GameSettingKey.PredatorDamage],
            _settings[GameSettingKey.ColdIntensity],
            _settings[GameSettingKey.HungerRate],
            _settings[GameSettingKey.CoalBurnRate],
        ];
        Validate();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<GameMapInfo> Maps => GameMaps.All;

    /// <summary>ComboBox.SelectedItemとの双方向バインド用（x:Bindはobject型同士でないと双方向にできない）。</summary>
    public object? SelectedMapItem
    {
        get => _selectedMap;
        set
        {
            if (value is GameMapInfo map && map != _selectedMap)
            {
                _selectedMap = map;
                OnPropertyChanged();
                OnSettingChanged();
            }
        }
    }

    public GameMap SelectedMap => _selectedMap.Map;

    public IReadOnlyList<NumericSettingViewModel> PlayerSettings { get; }

    public IReadOnlyList<NumericSettingViewModel> TimeSettings { get; }

    public IReadOnlyList<NumericSettingViewModel> MultiplierSettings { get; }

    public IReadOnlyList<string> Errors => _errors;

    public bool HasErrors => _errors.Count > 0;

    /// <summary>すべて妥当なら設定を、誤りがあればnullを返す。</summary>
    public GameSettings? TryGetSettings() => HasErrors ? null : Build();

    /// <summary>保存用。空欄は既定値に置き換え、範囲外の値はそのまま残す（次回起動時もエラーとして見せる）。</summary>
    public GameSettings BuildForPersistence() => Build();

    public void Apply(GameSettings settings)
    {
        _suppressChanged = true;
        try
        {
            SelectedMapItem = GameMaps.All.FirstOrDefault(map => map.Map == settings.Map) ?? GameMaps.Get(GameSettings.Default.Map);
            foreach (var definition in GameSettingDefinitions.CommandLineOrder)
            {
                _settings[definition.Key].Value = definition.Read(settings);
            }
        }
        finally
        {
            _suppressChanged = false;
        }

        OnSettingChanged();
    }

    private GameSettings Build()
    {
        var settings = GameSettings.Default with { Map = _selectedMap.Map };
        foreach (var definition in GameSettingDefinitions.CommandLineOrder)
        {
            var value = _settings[definition.Key].Value;
            settings = definition.Write(settings, double.IsNaN(value) ? definition.DefaultValue : value);
        }

        return settings;
    }

    private void OnSettingChanged()
    {
        if (_suppressChanged)
        {
            return;
        }

        Validate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Validate()
    {
        var errors = new List<string>();
        foreach (var definition in GameSettingDefinitions.CommandLineOrder)
        {
            var setting = _settings[definition.Key];
            setting.Error = definition.Validate(setting.Value);
            if (setting.Error is not null)
            {
                errors.Add(setting.Error);
            }
        }

        var maxPlayers = _settings[GameSettingKey.MaxPlayers];
        var thralls = _settings[GameSettingKey.Thralls];
        if (!maxPlayers.HasError && !thralls.HasError)
        {
            var crossError = GameSettingsValidator.ValidateThralls(maxPlayers.Value, thralls.Value);
            if (crossError is not null)
            {
                thralls.Error = crossError;
                errors.Add(crossError);
            }
        }

        _errors = errors;
        OnPropertyChanged(nameof(Errors));
        OnPropertyChanged(nameof(HasErrors));
    }
}
