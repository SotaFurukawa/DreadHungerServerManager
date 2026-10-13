using DreadHungerServerManager.Core.Models;

namespace DreadHungerServerManager.Core.Services.Cloud;

/// <summary>
/// クラウド上のインスタンス（EC2など）の操作。GUI・Host・Discord Botから共通で使う。
/// 実装（AWS SDK）は別プロジェクトに置き、ここにはサービス固有の型を持ち込まない。
/// 失敗時は <see cref="CloudOperationException"/> を投げる。
/// </summary>
public interface ICloudInstanceService
{
    Task<CloudInstanceStatus> GetStatusAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default);

    /// <summary>起動を要求する（完了は待たない）。起動中・稼働中なら何もしない。停止処理中なら <see cref="CloudErrorKind.InvalidState"/>。</summary>
    Task StartAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default);

    /// <summary>停止を要求する（完了は待たない）。停止中・停止処理中なら何もしない。</summary>
    Task StopAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default);

    /// <summary>再起動を要求する。稼働中でなければ <see cref="CloudErrorKind.InvalidState"/>。</summary>
    Task RebootAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default);

    /// <summary>現在のパブリックIPv4アドレス。稼働中でない、または割り当てがなければnull。</summary>
    Task<string?> GetPublicIpAsync(CloudInstanceRef instance, CancellationToken cancellationToken = default);
}

public enum CloudErrorKind
{
    /// <summary>リージョンやインスタンスIDなどの設定が正しくない。</summary>
    InvalidConfiguration,

    /// <summary>認証情報（IAMロール・プロファイル・環境変数）が見つからない、または無効。</summary>
    CredentialsNotFound,

    /// <summary>権限が足りない（IAMポリシー）。</summary>
    AccessDenied,

    /// <summary>インスタンスが見つからない。</summary>
    NotFound,

    /// <summary>今の状態ではその操作ができない（停止処理中に起動しようとした等）。</summary>
    InvalidState,

    /// <summary>呼び出し回数の制限に達した。</summary>
    Throttled,

    /// <summary>通信できない。</summary>
    Network,

    /// <summary>待機時間内に目的の状態にならなかった。</summary>
    Timeout,

    Unknown,
}

/// <summary>クラウド操作の失敗。<see cref="Exception.Message"/> は利用者にそのまま見せられる日本語の説明。</summary>
public sealed class CloudOperationException(CloudErrorKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public CloudErrorKind Kind { get; } = kind;
}
