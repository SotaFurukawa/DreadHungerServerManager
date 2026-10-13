using System.Text.RegularExpressions;
using DreadHungerServerManager.Core.Models;
using DreadHungerServerManager.Core.Services.Cloud;

namespace DreadHungerServerManager.Bot;

/// <summary>
/// Discord に出す文から、EC2 の場所（インスタンスID・リージョン・AWS アカウント・ARN）が見えないようにする。
/// 利用者に見せるのはゲームに入るためのアドレス（IP:ポート）だけにする。詳しい原因は管理者向けのログ（CloudWatch Logs）に出す。
/// </summary>
public static partial class BotRedaction
{
    public const string Hidden = "（非公開）";

    /// <summary>クラウド操作の失敗を、利用者に見せる説明に変える。設定や権限の問題は中身を出さずに管理者への連絡を促す。</summary>
    public static string PublicMessage(CloudOperationException exception, CloudInstanceRef instance) => exception.Kind switch
    {
        CloudErrorKind.InvalidConfiguration or CloudErrorKind.NotFound =>
            "Bot の接続先サーバーの設定に問題があるため操作できませんでした。サーバーの管理者に連絡してください。",
        CloudErrorKind.CredentialsNotFound or CloudErrorKind.AccessDenied =>
            "Bot にサーバーを操作する権限がないため操作できませんでした。サーバーの管理者に連絡してください。",
        CloudErrorKind.Unknown =>
            "サーバーの操作に失敗しました。少し待ってからもう一度実行し、続くようならサーバーの管理者に連絡してください。",
        _ => Redact(exception.Message, instance),
    };

    /// <summary>文中のインスタンスID・リージョン・ARN・AWS アカウントIDを伏せる。</summary>
    public static string Redact(string text, CloudInstanceRef instance)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = ArnPattern().Replace(text, Hidden);
        result = InstanceIdPattern().Replace(result, Hidden);
        result = AccountIdPattern().Replace(result, Hidden);
        if (instance.InstanceId.Length > 0)
        {
            result = result.Replace(instance.InstanceId, Hidden, StringComparison.OrdinalIgnoreCase);
        }

        if (instance.Region.Length > 0)
        {
            result = result.Replace(instance.Region, Hidden, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    [GeneratedRegex(@"arn:aws[a-z\-]*:[^\s`'""）)]*", RegexOptions.IgnoreCase)]
    private static partial Regex ArnPattern();

    [GeneratedRegex(@"\bi-[0-9a-f]{8,17}\b", RegexOptions.IgnoreCase)]
    private static partial Regex InstanceIdPattern();

    // AWS アカウントIDは12桁。Discord のIDは17桁以上、時刻は10桁なので対象にならない。
    [GeneratedRegex(@"(?<!\d)\d{12}(?!\d)")]
    private static partial Regex AccountIdPattern();
}
