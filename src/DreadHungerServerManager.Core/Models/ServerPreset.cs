namespace DreadHungerServerManager.Core.Models;

/// <summary>
/// 名前付きのゲーム設定。標準プリセットはマップを持たず（<see cref="IncludesMap"/> = false）、
/// 読み込んでも現在のマップを変えない。利用者が保存したプリセットはマップも含めて復元する。
/// </summary>
public sealed record ServerPreset(string Name, GameSettings Settings, bool IsBuiltIn = false, bool IncludesMap = true)
{
    public GameSettings ApplyTo(GameSettings current) =>
        IncludesMap ? Settings : Settings with { Map = current.Map };
}
