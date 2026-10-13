namespace DreadHungerServerManager.Core.Models;

public enum GameMap
{
    Approach,
    Expanse,
    Departure,
}

/// <summary>画面に表示する名前と、コマンドラインに渡すマップ名の対応。</summary>
public sealed record GameMapInfo(GameMap Map, string DisplayName, string MapName)
{
    // ComboBoxの項目名（スクリーンリーダー等）に使われる。
    public override string ToString() => DisplayName;
}

public static class GameMaps
{
    public static IReadOnlyList<GameMapInfo> All { get; } =
    [
        new(GameMap.Approach, "進入路付近", "Approach_Persistent"),
        new(GameMap.Expanse, "広漠の地", "Expanse_Persistent"),
        new(GameMap.Departure, "山頂", "Departure_Persistent"),
    ];

    public static GameMapInfo Get(GameMap map) =>
        All.FirstOrDefault(info => info.Map == map)
        ?? throw new ArgumentOutOfRangeException(nameof(map), map, "Unknown map.");
}
