using System.Text.RegularExpressions;

namespace DreadHungerServerManager.Core.Models;

/// <summary>クラウド上のインスタンス（EC2など）の状態。サービス固有の状態名はここへ変換する。</summary>
public enum CloudInstanceState
{
    Unknown,
    Stopped,
    Starting,
    Running,
    Stopping,

    /// <summary>終了済み（terminated）など、起動も停止もできない状態。</summary>
    Error,
}

/// <summary>
/// 操作対象のインスタンス。複数サーバーに拡張するときは、サーバーごとにこれを持たせる。
/// </summary>
public sealed partial record CloudInstanceRef
{
    public CloudInstanceRef(string region, string instanceId)
    {
        if (ValidateRegion(region) is { } regionError)
        {
            throw new ArgumentException(regionError, nameof(region));
        }

        if (ValidateInstanceId(instanceId) is { } instanceError)
        {
            throw new ArgumentException(instanceError, nameof(instanceId));
        }

        Region = region.Trim();
        InstanceId = instanceId.Trim();
    }

    /// <summary>例: ap-northeast-1</summary>
    public string Region { get; }

    /// <summary>例: i-0123456789abcdef0</summary>
    public string InstanceId { get; }

    public static string? ValidateRegion(string? region) =>
        string.IsNullOrWhiteSpace(region) ? "リージョンを入力してください（例: ap-northeast-1）。"
        : RegionRegex().IsMatch(region.Trim()) ? null
        : $"リージョンの形式が正しくありません: {region.Trim()}（例: ap-northeast-1）";

    public static string? ValidateInstanceId(string? instanceId) =>
        string.IsNullOrWhiteSpace(instanceId) ? "インスタンスIDを入力してください（例: i-0123456789abcdef0）。"
        : InstanceIdRegex().IsMatch(instanceId.Trim()) ? null
        : $"インスタンスIDの形式が正しくありません: {instanceId.Trim()}（例: i-0123456789abcdef0）";

    public override string ToString() => $"{InstanceId} ({Region})";

    [GeneratedRegex(@"^[a-z]{2}(-[a-z]+)+-\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex RegionRegex();

    [GeneratedRegex(@"^i-([0-9a-f]{8}|[0-9a-f]{17})$", RegexOptions.CultureInvariant)]
    private static partial Regex InstanceIdRegex();
}

public sealed record CloudInstanceStatus
{
    public CloudInstanceState State { get; init; } = CloudInstanceState.Unknown;

    /// <summary>サービス固有の状態名（EC2なら pending / running / stopping / stopped / shutting-down / terminated）。</summary>
    public string? RawState { get; init; }

    /// <summary>パブリックIPv4アドレス。停止中や割り当てがない場合はnull。EC2は停止・起動のたびに変わる（Elastic IPを除く）。</summary>
    public string? PublicIpAddress { get; init; }

    /// <summary>最後に起動した日時。</summary>
    public DateTimeOffset? LaunchTime { get; init; }

    /// <summary>状態が変わった理由（起動に失敗した理由など）。</summary>
    public string? StateReason { get; init; }

    public static CloudInstanceStatus Unknown { get; } = new();

    public static string GetDisplayText(CloudInstanceState state) => state switch
    {
        CloudInstanceState.Stopped => "停止中",
        CloudInstanceState.Starting => "起動処理中",
        CloudInstanceState.Running => "稼働中",
        CloudInstanceState.Stopping => "停止処理中",
        CloudInstanceState.Error => "エラー",
        _ => "不明",
    };
}
