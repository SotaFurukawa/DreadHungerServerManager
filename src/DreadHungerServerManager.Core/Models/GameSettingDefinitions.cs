using System.Globalization;

namespace DreadHungerServerManager.Core.Models;

public enum GameSettingKey
{
    MaxPlayers,
    DaysBeforeBlizzard,
    DayMinutes,
    PredatorDamage,
    ColdIntensity,
    HungerRate,
    CoalBurnRate,
    Thralls,
}

/// <summary>数値設定1項目分の定義（表示名・範囲・刻み・コマンドライン名）。</summary>
public sealed class GameSettingDefinition
{
    public required GameSettingKey Key { get; init; }

    /// <summary>コマンドライン上の名前（例: <c>maxplayers</c>）。</summary>
    public required string QueryName { get; init; }

    public required string Label { get; init; }

    public required double Minimum { get; init; }

    public required double Maximum { get; init; }

    public required double Step { get; init; }

    public required bool IsInteger { get; init; }

    /// <summary>ドロップダウンから素早く選べる値。空なら選択肢を出さない。</summary>
    public IReadOnlyList<double> QuickValues { get; init; } = [];

    public required Func<GameSettings, double> Read { get; init; }

    public required Func<GameSettings, double, GameSettings> Write { get; init; }

    public double DefaultValue => Read(GameSettings.Default);

    public string RangeText => $"{FormatValue(Minimum)}～{FormatValue(Maximum)}";

    /// <summary>コマンドライン用の表記。1.0は「1」、0.75は「0.75」のように末尾の0を省く。</summary>
    public string FormatValue(double value) =>
        IsInteger
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>単独の値として妥当かを判定する。妥当ならnull、そうでなければ利用者向けのエラー文。</summary>
    public string? Validate(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return $"{Label}に数値を入力してください。";
        }

        if (IsInteger && Math.Abs(value - Math.Round(value)) > 1e-9)
        {
            return $"{Label}は整数で入力してください。";
        }

        if (value < Minimum - 1e-9 || value > Maximum + 1e-9)
        {
            return $"{Label}は{RangeText}の範囲で入力してください（現在: {FormatValue(value)}）。";
        }

        return null;
    }
}

public static class GameSettingDefinitions
{
    public static IReadOnlyList<double> MultiplierQuickValues { get; } = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0];

    public static GameSettingDefinition MaxPlayers { get; } = new()
    {
        Key = GameSettingKey.MaxPlayers,
        QueryName = "maxplayers",
        Label = "最大人数",
        Minimum = 1,
        Maximum = 8,
        Step = 1,
        IsInteger = true,
        Read = s => s.MaxPlayers,
        Write = (s, v) => s with { MaxPlayers = (int)Math.Round(v) },
    };

    public static GameSettingDefinition DaysBeforeBlizzard { get; } = new()
    {
        Key = GameSettingKey.DaysBeforeBlizzard,
        QueryName = "daysbeforeblizzard",
        Label = "吹雪までの日数",
        Minimum = 1,
        Maximum = 30,
        Step = 1,
        IsInteger = true,
        Read = s => s.DaysBeforeBlizzard,
        Write = (s, v) => s with { DaysBeforeBlizzard = (int)Math.Round(v) },
    };

    public static GameSettingDefinition DayMinutes { get; } = new()
    {
        Key = GameSettingKey.DayMinutes,
        QueryName = "dayminutes",
        Label = "1日の長さ（分）",
        Minimum = 1,
        Maximum = 60,
        Step = 1,
        IsInteger = true,
        Read = s => s.DayMinutes,
        Write = (s, v) => s with { DayMinutes = (int)Math.Round(v) },
    };

    public static GameSettingDefinition PredatorDamage { get; } = Multiplier(
        GameSettingKey.PredatorDamage, "predatordamage", "動物ダメージ倍率",
        s => s.PredatorDamage, (s, v) => s with { PredatorDamage = v });

    public static GameSettingDefinition ColdIntensity { get; } = Multiplier(
        GameSettingKey.ColdIntensity, "coldintensity", "寒さ倍率",
        s => s.ColdIntensity, (s, v) => s with { ColdIntensity = v });

    public static GameSettingDefinition HungerRate { get; } = Multiplier(
        GameSettingKey.HungerRate, "hungerrate", "空腹速度倍率",
        s => s.HungerRate, (s, v) => s with { HungerRate = v });

    public static GameSettingDefinition CoalBurnRate { get; } = Multiplier(
        GameSettingKey.CoalBurnRate, "coalburnrate", "石炭消費倍率",
        s => s.CoalBurnRate, (s, v) => s with { CoalBurnRate = v });

    public static GameSettingDefinition Thralls { get; } = new()
    {
        Key = GameSettingKey.Thralls,
        QueryName = "thralls",
        Label = "人狼人数",
        Minimum = 0,
        Maximum = 7,
        Step = 1,
        IsInteger = true,
        Read = s => s.Thralls,
        Write = (s, v) => s with { Thralls = (int)Math.Round(v) },
    };

    /// <summary>コマンドラインへ並べる順序。既存のbatファイルと同じ並びにしている。</summary>
    public static IReadOnlyList<GameSettingDefinition> CommandLineOrder { get; } =
    [
        MaxPlayers,
        DaysBeforeBlizzard,
        DayMinutes,
        PredatorDamage,
        ColdIntensity,
        HungerRate,
        CoalBurnRate,
        Thralls,
    ];

    public static GameSettingDefinition Get(GameSettingKey key) =>
        CommandLineOrder.First(definition => definition.Key == key);

    private static GameSettingDefinition Multiplier(
        GameSettingKey key,
        string queryName,
        string label,
        Func<GameSettings, double> read,
        Func<GameSettings, double, GameSettings> write) => new()
        {
            Key = key,
            QueryName = queryName,
            Label = label,
            Minimum = 0,
            Maximum = 10,
            Step = 0.25,
            IsInteger = false,
            QuickValues = MultiplierQuickValues,
            Read = read,
            Write = write,
        };
}
