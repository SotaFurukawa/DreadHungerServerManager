namespace DreadHungerServerManager.Core.Models;

/// <summary>
/// サーバー起動URL（<c>MAP?key=value...</c>）に渡すゲーム設定。
/// 値の範囲・表示名・コマンドライン上の名前は <see cref="GameSettingDefinitions"/> が正本。
/// </summary>
public sealed record GameSettings
{
    public GameMap Map { get; init; } = GameMap.Expanse;

    public int MaxPlayers { get; init; } = 8;

    public int DaysBeforeBlizzard { get; init; } = 3;

    public int DayMinutes { get; init; } = 9;

    public double PredatorDamage { get; init; } = 1.0;

    public double ColdIntensity { get; init; } = 1.0;

    public double HungerRate { get; init; } = 1.0;

    public double CoalBurnRate { get; init; } = 1.0;

    public int Thralls { get; init; } = 2;

    public static GameSettings Default { get; } = new();
}
