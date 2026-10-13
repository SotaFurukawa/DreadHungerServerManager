using System.Globalization;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services;

namespace DreadHungerServerManager.Bot;

/// <summary>設定パネルの1項目。</summary>
public sealed record PanelField(string Key, string Label, GameSettingKey? SettingKey);

/// <summary>プルダウンの選択肢。</summary>
public sealed record PanelOption(string Label, string Value, bool IsSelected);

/// <summary>
/// Discord の設定パネル（/start）の状態と選択肢。Discord に依存しない。
/// パネルの状態はボタン・プルダウンの custom_id に埋め込む（<see cref="Encode"/>）ため、サーバー側に何も保存しない。
/// </summary>
public static class LaunchPanel
{
    public static readonly PanelField Map = new("map", "マップ", null);

    public static IReadOnlyList<PanelField> Fields { get; } =
    [
        Map,
        new("players", "最大人数", GameSettingKey.MaxPlayers),
        new("thralls", "人狼人数", GameSettingKey.Thralls),
        new("days", "吹雪までの日数", GameSettingKey.DaysBeforeBlizzard),
        new("dayminutes", "1日の長さ", GameSettingKey.DayMinutes),
        new("predator", "動物ダメージ", GameSettingKey.PredatorDamage),
        new("cold", "寒さ", GameSettingKey.ColdIntensity),
        new("hunger", "空腹速度", GameSettingKey.HungerRate),
        new("coal", "石炭消費", GameSettingKey.CoalBurnRate),
    ];

    private static readonly int[] DaysChoices = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 15, 20, 25, 30];
    private static readonly int[] DayMinutesChoices = [3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 18, 20, 25, 30, 40, 45, 60];
    private static readonly double[] MultiplierChoices = [0, 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0];

    public static PanelField? FindField(string key) => Fields.FirstOrDefault(field => field.Key == key);

    /// <summary>例: <c>E.8.2.3.9.1.1.1.1p5</c>（マップの頭文字と各値を「.」で区切る。小数点は p。custom_id の100文字に収まる）。</summary>
    public static string Encode(GameSettings settings) => string.Join('.',
        MapCode(settings.Map),
        Number(settings.MaxPlayers),
        Number(settings.Thralls),
        Number(settings.DaysBeforeBlizzard),
        Number(settings.DayMinutes),
        Number(settings.PredatorDamage),
        Number(settings.ColdIntensity),
        Number(settings.HungerRate),
        Number(settings.CoalBurnRate));

    public static GameSettings? Decode(string state)
    {
        var parts = state.Split('.', 2);
        if (parts.Length != 2 || ParseMap(parts[0]) is not { } map)
        {
            return null;
        }

        var values = ParseNumbers(parts[1]);
        if (values is not { Count: 8 })
        {
            return null;
        }

        return GameSettings.Default with
        {
            Map = map,
            MaxPlayers = (int)values[0],
            Thralls = (int)values[1],
            DaysBeforeBlizzard = (int)values[2],
            DayMinutes = (int)values[3],
            PredatorDamage = values[4],
            ColdIntensity = values[5],
            HungerRate = values[6],
            CoalBurnRate = values[7],
        };
    }

    /// <summary>プルダウンで選んだ値を反映する。不正な値なら元の設定のまま。</summary>
    public static GameSettings Apply(GameSettings settings, string fieldKey, string value)
    {
        if (fieldKey == Map.Key)
        {
            return ParseMap(value) is { } map ? settings with { Map = map } : settings;
        }

        if (FindField(fieldKey)?.SettingKey is not { } key
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return settings;
        }

        var definition = GameSettingDefinitions.Get(key);
        return definition.Validate(number) is null ? definition.Write(settings, number) : settings;
    }

    /// <summary>プルダウンの選択肢（25個まで）。今の値が標準の選択肢にない場合も選択肢に加える。</summary>
    public static IReadOnlyList<PanelOption> Options(PanelField field, GameSettings settings)
    {
        if (field.SettingKey is not { } key)
        {
            return GameMaps.All
                .Select(map => new PanelOption($"{map.DisplayName}（{map.Map}）", MapCode(map.Map), map.Map == settings.Map))
                .ToList();
        }

        var definition = GameSettingDefinitions.Get(key);
        var current = definition.Read(settings);
        IEnumerable<double> values = key switch
        {
            GameSettingKey.MaxPlayers => Enumerable.Range(1, 8).Select(value => (double)value),
            GameSettingKey.Thralls => Enumerable.Range(0, 8).Select(value => (double)value),
            GameSettingKey.DaysBeforeBlizzard => DaysChoices.Select(value => (double)value),
            GameSettingKey.DayMinutes => DayMinutesChoices.Select(value => (double)value),
            _ => MultiplierChoices,
        };
        return values
            .Append(current)
            .Where(value => definition.Validate(value) is null)
            .Distinct()
            .Order()
            .Take(25)
            .Select(value => new PanelOption(FormatValue(field, value), definition.FormatValue(value), Math.Abs(value - current) < 1e-9))
            .ToList();
    }

    /// <summary>パネルや結果の表示用（例: 「×1.50」「3日」「8人」）。</summary>
    public static string FormatValue(PanelField field, double value) => field.SettingKey switch
    {
        GameSettingKey.MaxPlayers or GameSettingKey.Thralls => $"{value:0}人",
        GameSettingKey.DaysBeforeBlizzard => $"{value:0}日",
        GameSettingKey.DayMinutes => $"{value:0}分",
        _ => "×" + value.ToString("0.00", CultureInfo.InvariantCulture),
    };

    /// <summary>設定の要約（1行）。</summary>
    public static string Summary(GameSettings settings) =>
        $"{GameMaps.Get(settings.Map).DisplayName}・最大{settings.MaxPlayers}人・人狼{settings.Thralls}人・吹雪まで{settings.DaysBeforeBlizzard}日・1日{settings.DayMinutes}分"
        + $"・動物×{Multiplier(settings.PredatorDamage)}・寒さ×{Multiplier(settings.ColdIntensity)}・空腹×{Multiplier(settings.HungerRate)}・石炭×{Multiplier(settings.CoalBurnRate)}";

    /// <summary>起動できない設定ならその理由。問題なければnull。</summary>
    public static string? Validate(GameSettings settings)
    {
        var errors = GameSettingsValidator.Validate(settings);
        return errors.Count == 0 ? null : string.Join(" ", errors.Select(error => error.Message));
    }

    public static string MapCode(GameMap map) => map switch
    {
        GameMap.Approach => "A",
        GameMap.Departure => "D",
        _ => "E",
    };

    public static GameMap? ParseMap(string code) => code switch
    {
        "A" => GameMap.Approach,
        "E" => GameMap.Expanse,
        "D" => GameMap.Departure,
        _ => Enum.TryParse<GameMap>(code, ignoreCase: true, out var map) && Enum.IsDefined(map) ? map : null,
    };

    private static string Multiplier(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', 'p');

    private static List<double>? ParseNumbers(string text)
    {
        var values = new List<double>();
        foreach (var part in text.Split('.'))
        {
            if (!double.TryParse(part.Replace('p', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            values.Add(value);
        }

        return values;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
