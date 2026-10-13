using System.Globalization;
using System.Text.RegularExpressions;
using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services;

/// <summary>サーバーログの1行から、起動完了の目印やエラーを読み取る。</summary>
public static partial class ServerLogAnalyzer
{
    public const string ListeningMarker = "IpNetDriver listening on port";
    public const string WaitingToStartMarker = "Match State Changed from EnteringMap to WaitingToStart";

    /// <summary>
    /// 実際の行は「LogNet: GameNetDriver IpNetDriver_2147482476 IpNetDriver listening on port 7777」。
    /// ビーコン用のNetDriverが別ポートで待ち受ける場合があるため、Beaconの行は起動完了とみなさない。
    /// </summary>
    public static bool TryGetListeningPort(string line, out int port)
    {
        port = 0;
        if (line.Contains("Beacon", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = ListeningRegex().Match(line);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out port);
    }

    public static bool IsWaitingToStart(string line) =>
        line.Contains(WaitingToStartMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>「Match State Changed from InProgress to WaitingPostMatch」から変更後の状態を読む。</summary>
    public static bool TryGetMatchState(string line, out string state)
    {
        var match = MatchStateRegex().Match(line);
        state = match.Success ? match.Groups[1].Value : "";
        return match.Success;
    }

    /// <summary>
    /// サーバー自身による終了要求か。試合終了後、サーバーは「FPlatformMisc::RequestExit(0)」を出して自分で終了する。
    /// Ctrl+Cによる終了（reason: ConsoleCtrl RequestExit）は含まない。
    /// </summary>
    public static bool IsServerExitRequest(string line) =>
        line.Contains("FPlatformMisc::RequestExit(0)", StringComparison.Ordinal)
        || line.Contains("Engine exit requested (reason: GenericPlatform RequestExit)", StringComparison.Ordinal);

    public static LogLineKind Classify(string line)
    {
        if (TryGetListeningPort(line, out _) || IsWaitingToStart(line))
        {
            return LogLineKind.Success;
        }

        if (line.Contains("Error:", StringComparison.Ordinal)
            || line.Contains("Fatal error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Critical error", StringComparison.OrdinalIgnoreCase))
        {
            return LogLineKind.Error;
        }

        return line.Contains("Warning:", StringComparison.Ordinal) ? LogLineKind.Warning : LogLineKind.Normal;
    }

    [GeneratedRegex(@"IpNetDriver listening on port (\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListeningRegex();

    [GeneratedRegex(@"Match State Changed from \w+ to (\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex MatchStateRegex();
}
