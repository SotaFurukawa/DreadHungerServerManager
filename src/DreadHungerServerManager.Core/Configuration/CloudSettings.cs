using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Configuration;

/// <summary>
/// AWS（EC2）連携の設定。秘密情報（アクセスキー・Discord の Token など）はここに入れない。
/// 認証は AWS SDK の既定の方法（EC2 の IAM ロール、環境変数、共有認証情報ファイルのプロファイル）に任せる。
/// </summary>
public sealed partial record CloudSettings
{
    public const string DefaultServerId = "default";
    public const string DefaultRegion = "ap-northeast-1";
    public const int DefaultGamePort = 7777;
    public const int MinIdleTimeoutMinutes = 5;
    public const int MaxIdleTimeoutMinutes = 240;
    public const int MinMaxRuntimeHours = 1;
    public const int MaxMaxRuntimeHours = 24;

    /// <summary>サーバーの識別子。将来、複数のサーバーを管理するときに使う。</summary>
    public string ServerId { get; init; } = DefaultServerId;

    public string Region { get; init; } = DefaultRegion;

    /// <summary>空なら AWS 連携を使わない。</summary>
    public string InstanceId { get; init; } = "";

    /// <summary>
    /// AWS の共有認証情報ファイル（%USERPROFILE%\.aws\credentials）のプロファイル名。
    /// 空なら既定の方法（EC2 上なら IAM ロール、それ以外は環境変数や default プロファイル）で認証する。
    /// </summary>
    public string? ProfileName { get; init; }

    /// <summary>接続先として案内するゲームのポート（UDP）。</summary>
    public int GamePort { get; init; } = DefaultGamePort;

    /// <summary>EC2 の起動後に、Dread Hunger サーバーを自動で起動する（EC2 上の Host が行う）。</summary>
    public bool AutoStartGameServer { get; init; } = true;

    /// <summary>
    /// 誰も遊んでいない状態（ゲームサーバーが止まったまま、または参加者0人）が続いたら、サーバーと EC2 を停止する。
    /// GUI ではこの値を編集して EC2 に反映（Parameter Store）し、常駐サービスは反映された設定がなければこの値を使う。
    /// </summary>
    public bool IdleShutdownEnabled { get; init; } = true;

    public int IdleTimeoutMinutes { get; init; } = 30;

    /// <summary>料金事故の防止のため、EC2 の起動から一定時間で停止する。</summary>
    public bool MaxRuntimeEnabled { get; init; } = true;

    public int MaxRuntimeHours { get; init; } = 6;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(InstanceId);

    public static CloudSettings Default { get; } = new();

    public AutoStopConfig ToAutoStopConfig() => new AutoStopConfig
    {
        IdleShutdownEnabled = IdleShutdownEnabled,
        IdleTimeoutMinutes = IdleTimeoutMinutes,
        MaxRuntimeEnabled = MaxRuntimeEnabled,
        MaxRuntimeHours = MaxRuntimeHours,
    }.Normalize();

    /// <summary>設定済みなら操作対象を返す。未設定や形式の誤りがあればnull。</summary>
    public CloudInstanceRef? TryGetInstanceRef() =>
        IsConfigured && Validate().Count == 0 ? new CloudInstanceRef(Region, InstanceId) : null;

    /// <summary>入力の誤り。AWS 連携を使わない（インスタンスIDが空）場合は検証しない。</summary>
    public IReadOnlyList<string> Validate()
    {
        if (!IsConfigured)
        {
            return [];
        }

        var errors = new List<string?>
        {
            CloudInstanceRef.ValidateRegion(Region),
            CloudInstanceRef.ValidateInstanceId(InstanceId),
            ValidateProfileName(ProfileName),
            ValidateServerId(ServerId),
            GamePort is < 1 or > 65535 ? "ゲームのポートは 1～65535 で指定してください。" : null,
        };
        return errors.OfType<string>().ToList();
    }

    public static string? ValidateProfileName(string? profileName) =>
        string.IsNullOrWhiteSpace(profileName) || ProfileNameRegex().IsMatch(profileName.Trim())
            ? null
            : "プロファイル名に使えない文字が含まれています（英数字と _ - . のみ）。";

    public static string? ValidateServerId(string? serverId) =>
        !string.IsNullOrWhiteSpace(serverId) && ServerIdRegex().IsMatch(serverId.Trim())
            ? null
            : "サーバーIDは英小文字・数字・ハイフン（1～32文字）で指定してください。";

    /// <summary>手編集や古い版で壊れた値を補正する。</summary>
    public CloudSettings Normalize() => this with
    {
        ServerId = ValidateServerId(ServerId) is null ? ServerId.Trim() : DefaultServerId,
        Region = string.IsNullOrWhiteSpace(Region) ? DefaultRegion : Region.Trim(),
        InstanceId = InstanceId?.Trim() ?? "",
        ProfileName = string.IsNullOrWhiteSpace(ProfileName) ? null : ProfileName.Trim(),
        GamePort = GamePort is < 1 or > 65535 ? DefaultGamePort : GamePort,
        IdleTimeoutMinutes = Math.Clamp(IdleTimeoutMinutes, MinIdleTimeoutMinutes, MaxIdleTimeoutMinutes),
        MaxRuntimeHours = Math.Clamp(MaxRuntimeHours, MinMaxRuntimeHours, MaxMaxRuntimeHours),
    };

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileNameRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServerIdRegex();
}
