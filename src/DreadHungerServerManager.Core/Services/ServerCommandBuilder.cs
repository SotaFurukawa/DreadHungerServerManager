using DreadHungerServerManager.Core.Configuration;
using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services;

/// <summary>
/// <c>DreadHungerServer.exe &lt;MAP&gt;?&lt;OPTIONS&gt; -log</c> 形式のコマンドラインを組み立てる。
/// </summary>
public static class ServerCommandBuilder
{
    public const string LogSwitch = "-log";

    /// <summary>例: <c>Expanse_Persistent?maxplayers=8?daysbeforeblizzard=3?...?thralls=2</c></summary>
    public static string BuildTravelUrl(GameSettings settings)
    {
        var options = GameSettingDefinitions.CommandLineOrder
            .Select(definition => $"{definition.QueryName}={definition.FormatValue(definition.Read(settings))}");
        return string.Join('?', [GameMaps.Get(settings.Map).MapName, .. options]);
    }

    public static string BuildArguments(GameSettings settings, string? additionalArguments)
    {
        var arguments = $"{BuildTravelUrl(settings)} {LogSwitch}";
        var extra = additionalArguments?.Trim();
        return string.IsNullOrEmpty(extra) ? arguments : $"{arguments} {extra}";
    }

    /// <summary>画面表示用。実行ファイルはファイル名だけを表示する。</summary>
    public static string BuildPreview(string? executablePath, GameSettings settings, string? additionalArguments)
    {
        var fileName = string.IsNullOrWhiteSpace(executablePath)
            ? ServerPaths.ExecutableFileName
            : Path.GetFileName(executablePath.Trim());
        return $"{fileName} {BuildArguments(settings, additionalArguments)}";
    }

    /// <summary>batファイル等へそのまま貼り付けられる、フルパス付きのコマンドライン。</summary>
    public static string BuildFullCommandLine(string executablePath, GameSettings settings, string? additionalArguments)
    {
        var path = executablePath.Trim();
        var quoted = path.Contains(' ') ? $"\"{path}\"" : path;
        return $"{quoted} {BuildArguments(settings, additionalArguments)}";
    }

    /// <summary>追加引数として受け付けられるならnull。改行などはコマンドラインを壊すため拒否する。</summary>
    public static string? ValidateAdditionalArguments(string? additionalArguments) =>
        additionalArguments is not null && additionalArguments.Any(char.IsControl)
            ? "追加引数に改行やタブなどの制御文字は使えません。"
            : null;
}
