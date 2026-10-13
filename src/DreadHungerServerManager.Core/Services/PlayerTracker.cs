using System.Text.RegularExpressions;

namespace DreadHungerServerManager.Core.Services;

/// <summary>
/// サーバーログから接続中のプレイヤーを数える。Unreal Engine 標準の LogNet の行を使う（実機ログで確認済み）。
/// <list type="bullet">
/// <item>ログイン: <c>LogNet: Login request: ?Name=koucha userId: EOSPlus:... platform: EOSPlus</c></item>
/// <item>参加: <c>LogNet: Join succeeded: koucha</c></item>
/// <item>切断: <c>LogNet: UNetConnection::Close: [UNetConnection] ..., Driver: GameNetDriver ..., PC: BP_DreadPlayerController_C_..., ..., UniqueId: EOSPlus:...</c></item>
/// </list>
/// 参加までたどり着かなかった接続（PC: NULL）は数えない。スレッドセーフではない（呼び出し側で直列化する）。
/// </summary>
public sealed partial class PlayerTracker
{
    private readonly Dictionary<string, string> _pendingIdsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _joinedNamesById = new(StringComparer.Ordinal);

    public int Count => _joinedNamesById.Count;

    public IReadOnlyCollection<string> PlayerNames => _joinedNamesById.Values;

    /// <summary>1行を処理する。人数が変わったらtrue。</summary>
    public bool Process(string line)
    {
        if (line.Contains("Login request: ", StringComparison.Ordinal))
        {
            var login = LoginRegex().Match(line);
            if (login.Success)
            {
                _pendingIdsByName[TrimName(login.Groups["name"].Value)] = login.Groups["id"].Value;
            }

            return false;
        }

        var join = JoinRegex().Match(line);
        if (join.Success)
        {
            var name = TrimName(join.Groups["name"].Value);
            var id = _pendingIdsByName.Remove(name, out var pendingId) ? pendingId : $"name:{name}";
            var before = Count;
            _joinedNamesById[id] = name;
            return Count != before;
        }

        if (line.Contains("UNetConnection::Close: ", StringComparison.Ordinal)
            && line.Contains("Driver: GameNetDriver", StringComparison.Ordinal))
        {
            var close = CloseRegex().Match(line);
            if (!close.Success || close.Groups["pc"].Value == "NULL")
            {
                return false;
            }

            return _joinedNamesById.Remove(close.Groups["id"].Value);
        }

        return false;
    }

    // URLの残りの部分（?SplitscreenCount=1 など）は名前に含めない。
    private static string TrimName(string name) => name.Split('?')[0].Trim();

    [GeneratedRegex(@"Login request: \?Name=(?<name>.*?) userId: (?<id>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex LoginRegex();

    [GeneratedRegex(@"Join succeeded: (?<name>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex JoinRegex();

    [GeneratedRegex(@"PC: (?<pc>[^,\s]+),.*UniqueId: (?<id>[^,\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CloseRegex();
}
